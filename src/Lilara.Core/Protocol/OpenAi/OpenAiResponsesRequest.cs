using System.Text.Json.Nodes;
using Lilara.Core.Kernel;

namespace Lilara.Core.Protocol.OpenAi;

// canonical → OpenAI Responses 请求体。纯函数：同一 ModelRequest 必须构造出逐字节相同的 JSON；
// 用 JsonObject 显式按序插入，绝不依赖字典枚举序。
// 两个容易漏的实测事实：字段名是 max_output_tokens（不是 max_tokens）；每个 input 项必须带 "type":"message"
// （少了上游报 upstream_error）。
// 与 OpenAiChatRequest 形状故意不同：Chat 拒收裸字符串数组，Responses 全都收——两条协议的 content 形状不可互换。
public static class OpenAiResponsesRequest
{
    public const string Path = "/v1/responses";

    public static string Build(ModelRequest request)
    {
        // ⚠️ 判据与另两条**必须逐字相同**（空时间线 + 两槽位都空才早失败）。
        if (request.Timeline.Count == 0
            && request.Slots.System.Count == 0
            && request.Slots.Premier.Count == 0)
            throw new ArgumentException("时间线为空，且没有任何槽位内容，无法构造请求", nameof(request));

        var input = new JsonArray();
        foreach (var evt in request.Timeline)
            foreach (var item in ToItems(evt, request.Protocol))
                input.Add(item);

        // ---- 尾槽位：**拼进末尾那条 user 消息**（`01` §3.2 / §3.3）----
        //
        // ⚠️ 与 Chat 那条链路**同一个形状、同一个理由**：Responses 把工具结果做成
        // 与消息并列的 `function_call_output` 项 ⇒ 末尾通常**不是** user，
        // 所以这里补一条 message 项。（Claude 那边是块、能拼进去，故那边不补。）
        // ⚠️ 项必须带 `"type":"message"`——少了它上游报 upstream_error（本文件开头那条实测）。
        if (OpenAiChatRequest.TailText(request.Slots.Tail) is { Length: > 0 } tail)
        {
            input.Add(new JsonObject
            {
                ["type"] = "message",
                ["role"] = "user",
                // ⚠️ 始终发 part 数组（单块也是）：与下面 MessageToItems 同一条判据——
                // 数组对单块/多块是同一条路径，少一处无覆盖的代码。
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "input_text",
                    ["text"] = tail,
                }),
            });
        }

        var body = new JsonObject
        {
            ["model"] = request.ModelId,
            ["max_output_tokens"] = request.MaxTokens,
        };

        // 同 Chat：`null` 不写这个键。⚠️ **字段名两条 OpenAI 协议都叫 `temperature`**——
        // 与 `max_tokens` → `max_output_tokens` 那次改名不同，这里不要照抄那个改法。
        if (request.Temperature is { } temperature)
            body["temperature"] = temperature;

        // 两块头槽位走顶层 instructions（不占一条消息）。空时不发这个字段，而不是发个空串：
        // ⚠️ **顺序是契约**：系统块在前、首要块在后（`01` §3.1）——见 Claude 那边同一条注释。
        // 判空必须落在拼接后的文本上，非空输入若全是非文本块会拼出空串。
        var systemText = string.Concat(
            request.Slots.System.Concat(request.Slots.Premier)
                .OfType<Block.TextBlock>().Select(b => b.Text));
        if (systemText.Length > 0)
            body["instructions"] = systemText;

        body["input"] = input;

        // ⚠️ **空工具表 ⇒ 不写这个键**（与 Chat / temperature 同一条判据）。
        // Responses 的工具是**扁平**的（Chat 多一层 `function`）——两条 OpenAI 协议**形状不同**，
        // 别互相抄（本文件开头那句「两条协议的 content 形状不可互换」同一个教训）。
        if (request.Tools.Count > 0)
        {
            body["tools"] = new JsonArray(request.Tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["parameters"] = JsonNode.Parse(t.ParametersSchemaJson),
            }).ToArray());
        }

        return body.ToJsonString(Json.Options);
    }

    /// <summary>
    /// 一个 canonical 事件 → **若干**协议 input 项。
    ///
    /// <para>⚠️ <b>为什么是「若干」而不是「一个」</b>：Responses 把模型的工具调用做成
    /// <b>与消息并列的项</b>（<c>type:"function_call"</c>），而不是挂在消息上的字段
    /// ——所以「模型说了句话 + 调了两个工具」是**三项**，不是一项。
    /// ⚠️ 这是三条链路里唯一需要「一变多」的地方（Chat 挂在 <c>tool_calls</c>、
    /// Claude 是同一个 content 数组里的块）。</para>
    /// </summary>
    private static List<JsonObject> ToItems(TimelineEvent evt, string protocol) => evt switch
    {
        TimelineEvent.MessageEvent m => MessageToItems(m, protocol),
        TimelineEvent.ToolResultEvent r => [ResultToItem(r)],
        _ => throw new ArgumentException($"时间线里有无法翻译的事件（{evt.GetType().Name}）", nameof(evt)),
    };

    /// <summary>
    /// 工具结果 → `{"type":"function_call_output","call_id":…,"output":…}`。
    ///
    /// <para>⚠️ 字段名是 <c>call_id</c> 与 <c>output</c>——**不是** Chat 的
    /// <c>tool_call_id</c> / <c>content</c>。两条 OpenAI 协议的这两个名字都不一样，
    /// 而写错了的表现是上游 400，不是静默降级。</para>
    /// </summary>
    private static JsonObject ResultToItem(TimelineEvent.ToolResultEvent r)
    {
        // ⚠️ **`Ref` 到不了这里**（2026-10-06）：理由与 Chat 那条链路**逐字相同**
        // （见 `OpenAiChatRequest.ResultToJson`）。⚠️ 这三条链路必须**一起**看待：
        // 只删一条会留下两条指着相反方向的假诊断，而它们分叉的那一刻没人会红。
        var text = string.Concat(r.Content.Select(b => b switch
        {
            Block.TextBlock t => t.Text,
            _ => throw new ArgumentException(
                $"工具结果里有无法翻译的块（{b.GetType().Name}）", nameof(r)),
        }));

        // 空输出给出占位串：与另两条链路同一种行为（换个 provider 不该改变模型看到的东西）。
        if (text.Length == 0) text = "(无输出)";

        return new JsonObject
        {
            ["type"] = "function_call_output",
            ["call_id"] = r.CallId,
            ["output"] = text,
        };
    }

    // 一条 canonical 消息 → 协议 input 项（有工具调用时是多项，见 ToItems）。
    // Responses 不暴露推理（返回体无推理字段）→ 丢且不伪造。
    private static List<JsonObject> MessageToItems(TimelineEvent.MessageEvent message, string protocol)
    {
        var role = message.From == Speaker.User ? "user" : "assistant";

        // 先按协议能力过滤推理块——Responses 不承载，丢且不伪造。
        var blocks = ReasoningPolicy.FilterFor(message.Blocks, protocol);
        var texts = blocks.OfType<Block.TextBlock>().Select(b => b.Text).ToList();

        // ⚠️ 与 Chat 同一条判据：一个只调工具、不说话的 assistant 消息 content 是空的，
        // 而那是常态 ⇒ 判据要连调用一起看。
        var calls = blocks.OfType<Block.ToolUseBlock>().ToList();

        // 过滤掉推理块后可能一条不剩（推理模型只思考不落笔），必须早失败。
        if (texts.Count == 0 && calls.Count == 0)
            throw new ArgumentException(
                $"时间线里有空消息（speaker={message.From}），无法构造请求", nameof(message));

        var items = new List<JsonObject>();

        // ⚠️ **没有正文时不发那条 message 项**（`content: []` 是另一回事，而且多半非法）。
        // 一条「只调工具」的 assistant 消息在 Responses 下**就是**若干 function_call 项，没有消息项。
        if (texts.Count > 0)
        {
            // 用户用 input_text、助手用 output_text（上游两者都收）。
            // 始终发 part 数组（单块也是）：数组对单块/多块是同一条路径，少一处无覆盖的代码。
            var blockType = message.From == Speaker.User ? "input_text" : "output_text";
            items.Add(new JsonObject
            {
                ["type"] = "message",   // 必须——少了它上游报 upstream_error
                ["role"] = role,
                ["content"] = new JsonArray(
                    texts.Select(t => (JsonNode)new JsonObject { ["type"] = blockType, ["text"] = t })
                        .ToArray()),
            });
        }

        // 模型的调用在 Responses 里是**并列的项**（这是它与 Chat 最不一样的地方）。
        foreach (var c in calls)
            items.Add(new JsonObject
            {
                ["type"] = "function_call",
                ["call_id"] = c.CallId,
                ["name"] = c.Name,
                // ⚠️ 与 Chat 同：`arguments` 是**字符串**，逐字塞原文。
                ["arguments"] = c.Arguments,
            });

        return items;
    }
}
