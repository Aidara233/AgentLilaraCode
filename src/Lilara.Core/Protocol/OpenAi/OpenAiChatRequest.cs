using System.Text.Json;
using System.Text.Json.Nodes;
using Lilara.Core.Kernel;

namespace Lilara.Core.Protocol.OpenAi;

// canonical → OpenAI Chat 请求体。纯函数：同一 ModelRequest 必须构造出逐字节相同的 JSON；
// 用 JsonObject 显式按序插入，绝不依赖字典枚举序。
public static class OpenAiChatRequest
{
    public const string Path = "/v1/chat/completions";

    public static string Build(ModelRequest request)
    {
        // ⚠️ 判据与 Claude 那条**必须逐字相同**（空时间线 + 两槽位都空才早失败）——
        // 三条链路对「什么算合法的请求」各判一套的话，换个 provider 就会静默换一种行为。
        if (request.Timeline.Count == 0
            && request.Slots.System.Count == 0
            && request.Slots.Premier.Count == 0)
            throw new ArgumentException("时间线为空，且没有任何槽位内容，无法构造请求", nameof(request));

        var messages = new JsonArray();

        // 两块头槽位放**同一条** system 消息（Chat 协议没有独立 system 字段）。
        // ⚠️ **顺序是契约**：系统块在前、首要块在后（`01` §3.1）——见 Claude 那边同一条注释。
        // 判空必须落在拼接后的文本上，不能判块数：非空的输入若全是非文本块，
        // 拼接结果是空串，发出去就是一个空 system 消息（空串 vs 不存在）。
        var systemText = string.Concat(
            request.Slots.System.Concat(request.Slots.Premier)
                .OfType<Block.TextBlock>().Select(b => b.Text));
        if (systemText.Length > 0)
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = systemText });

        foreach (var evt in request.Timeline)
            messages.Add(ToMessage(evt, request.Protocol, request.Tools.Count > 0));

        // ---- 尾槽位：**拼进末尾那条 user 消息**（`01` §3.2 / §3.3）----
        //
        // ⚠️ **判据只有一条：末尾是不是 user。** 是 ⇒ 拼进去；不是 ⇒ 补一条 user 消息。
        // ⚠️ 但 Chat 这一条多一格：工具结果在 Chat 下是独立的 `role:"tool"` 消息，
        // 往 `role:"tool"` 里拼正文是**非法的**（那条消息的 `content` 是工具输出），
        // 所以这里补一条 user 消息——而 Claude 那边走的是「拼进去」。
        // ⚠️ **两条链路的形状差异是协议给的**，不是这里偷懒：同一个尾块在两条链路上
        // 落点不同，而模型看到的内容**逐字相同**（这是判据，「落在同一个位置」不是）。
        //
        // ⚠️ 2026-10-17：模块合成对（`floating_context` 的 tool_use + tool_result）之后，
        // 时间线末尾**通常**是那条 `role:"tool"` 的合成结果 ⇒ 这里给尾块另起一条 user 消息。
        // 这不改变判据，只是让「末尾不是 user」从例外变成常态。
        if (TailText(request.Slots.Tail) is { Length: > 0 } tail)
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = tail });

        var body = new JsonObject
        {
            ["model"] = request.ModelId,
            ["max_tokens"] = request.MaxTokens,
            ["messages"] = messages,
        };

        // ⚠️ **空工具表 ⇒ 不写这个键**（不是写一个空数组）：与 `temperature` 同一条判据，
        // 也是「没配工具的会话请求逐字节不变」的依据。
        if (request.Tools.Count > 0)
        {
            body["tools"] = new JsonArray(request.Tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    // ⚠️ `parameters` 是**对象**（不是字符串）⇒ 把原文解析回 JsonNode。
                    // 这是 Chat 与 Claude 那条链路的形状差异之一，别顺手抄。
                    ["parameters"] = JsonNode.Parse(t.ParametersSchemaJson),
                },
            }).ToArray());
        }

        // `null` ⇒ **不写这个键**（与「发一个默认值」不是一回事）。
        // ⚠️ 必须显式判空：手搓的 `JsonObject` 不走 `Json.Options` 的 `WhenWritingNull`，
        // 直接赋 `null` 会写成 JSON 的 `null`——那是**另一个**值，上游会 400。
        if (request.Temperature is { } temperature)
            body["temperature"] = temperature;

        return body.ToJsonString(Json.Options);
    }

    /// <summary>
    /// 尾槽位的块 → 一段正文。⚠️ <b>非文本块直接抛</b>，与两条 OpenAI 链路处理工具结果
    /// 时那条判据同源（静默丢一块 = 少一条信息，而那比抛坏）。
    ///
    /// <para>⚠️ <b>它住在 Chat 这个类里，而两条 OpenAI 协议共用它</b>——
    /// 理由是「块怎么念成文本」这件事**两条 OpenAI 协议完全一样**（Claude 那边不一样：
    /// 它发的是**块**、不是文本，所以那边没有对应的助手）。放在任一处都行，
    /// 放这里是随手挑的；**别为它另建一个文件**——五行的东西配一个类只会让下一个人
    /// 去猜它是不是还有别的用途。</para>
    /// </summary>
    internal static string? TailText(IReadOnlyList<Block> tail)
    {
        if (tail.Count == 0) return null;

        return string.Concat(tail.Select(b => b switch
        {
            Block.TextBlock t => t.Text,
            _ => throw new ArgumentException(
                $"尾槽位里有无法翻译的块（{b.GetType().Name}）", nameof(tail)),
        }));
    }

    /// <summary>
    /// 一个 canonical 事件 → 协议消息。⚠️ **两种事件在这里分派，形状完全不同**：
    /// 消息是 role/content，工具结果是 role/**tool** + `tool_call_id`（Chat 把结果
    /// 做成一个**独立消息**，而不是 Claude 那种塞进 user 消息的块）。
    /// </summary>
    private static JsonObject ToMessage(TimelineEvent evt, string protocol, bool carriesTools) => evt switch
    {
        TimelineEvent.MessageEvent m => MessageToJson(m, protocol, carriesTools),
        TimelineEvent.ToolResultEvent r => ResultToJson(r),
        _ => throw new ArgumentException($"时间线里有无法翻译的事件（{evt.GetType().Name}）", nameof(evt)),
    };

    /// <summary>
    /// 工具结果 → `{"role":"tool","tool_call_id":…,"content":…}`。
    ///
    /// <para>⚠️ <b>`tool_call_id` 是<b>协议 id</b>（<c>CallId</c>）</b>——上游按它配对。</para>
    ///
    /// <para>⚠️ 空的 `content` 在 Chat 下**不是** 400（它合法），但这里仍给占位串：
    /// 与 Claude 那条链路**保持同一种行为**——同一个工具结果在两条链路上应当让模型看到
    /// 同样的东西，否则「换个 provider」会静默改变模型对同一条历史的理解。</para>
    /// </summary>
    private static JsonObject ResultToJson(TimelineEvent.ToolResultEvent r)
    {
        // ⚠️ **`Ref` 到不了这里**（2026-10-06）：`fetch` 的结果今天带的是**内容**，
        // 而引用（若哪天真有）由 `TimelineRender` 在**进适配器之前**解掉
        // ⇒ 这里原先那条「`Ref` 非空 ⇒ 抛」的守卫已经**永远到不了**，删掉。
        // ⚠️ **留着它比删掉坏**：`fetch` 一上线，那句话会变成一条指着错方向的假诊断。
        // ⚠️ 若哪天真有引用漏到这里，下面 `switch` 的默认分支会响亮地抛——不需要额外守卫。
        var text = string.Concat(r.Content.Select(b => b switch
        {
            Block.TextBlock t => t.Text,
            _ => throw new ArgumentException(
                $"工具结果里有无法翻译的块（{b.GetType().Name}）", nameof(r)),
        }));

        if (text.Length == 0) text = "(无输出)";

        return new JsonObject
        {
            ["role"] = "tool",
            ["tool_call_id"] = r.CallId,
            ["content"] = text,
        };
    }

    // 一条 canonical 消息 → 协议消息。Chat 的 content **不暴露推理**（推理块不进 content，丢且不伪造）；
    // ⚠️ 但 `reasoning_content` 这个**字段**在带工具的请求里必须回传——见下面那段注释。
    private static JsonObject MessageToJson(TimelineEvent.MessageEvent message, string protocol, bool carriesTools)
    {
        var role = message.From == Speaker.User ? "user" : "assistant";

        // 先按协议能力过滤推理块——Chat 不承载，丢且不伪造。
        var blocks = ReasoningPolicy.FilterFor(message.Blocks, protocol);
        var texts = blocks.OfType<Block.TextBlock>().Select(b => b.Text).ToList();

        // 模型的工具调用走 `tool_calls`（Chat 把它挂在 assistant 消息上）。
        var calls = blocks.OfType<Block.ToolUseBlock>().ToList();

        // ⚠️ **`reasoning_content` 走的是另一条路**（2026-10-17）：上面过滤掉的是
        // **content 里的推理块**，而 DeepSeek 要回来的是那个**字段**。两者看着像同一件事，
        // 把这一条也按 CanCarry 丢掉就会得到
        // `The reasoning_content in the thinking mode must be passed back to the API.`（实测）。
        // 值是「原样回传」；记录里没有推理块时给空串（见 `ReasoningContentOf` 的注释）。
        var reasoning = ReasoningPolicy.ReasoningContentOf(message.Blocks, protocol, carriesTools);

        // ⚠️ 判据要**连调用一起看**：一个只调工具、不说话的 assistant 消息 content 是空的，
        // 而那是**常态**（模型先调再说）。只看 texts 会把它当成「空消息」拒掉。
        // ⚠️ 只带推理的 assistant 消息也放行——`reasoning_content` 就是它的内容。
        if (texts.Count == 0 && calls.Count == 0 && reasoning is null)
            throw new ArgumentException(
                $"时间线里有空消息（speaker={message.From}），无法构造请求", nameof(message));

        var obj = new JsonObject { ["role"] = role };

        if (reasoning is not null) obj["reasoning_content"] = reasoning;

        // ⚠️ 有 tool_calls 而 content 为空时，**发 `null` 而不是空串或省略**：
        // 省略 content 字段会被上游当成格式错误（它要求这个键存在）。
        if (texts.Count == 0) obj["content"] = null;
        else if (texts.Count == 1) obj["content"] = texts[0];
        else
            obj["content"] = new JsonArray(
                texts.Select(t => (JsonNode)new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = t,
                }).ToArray());

        if (calls.Count > 0)
        {
            obj["tool_calls"] = new JsonArray(calls.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.CallId,
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = c.Name,
                    // ⚠️ **`arguments` 在协议里是字符串**（不是对象）——而我们的 `Arguments`
                    // 本来就是一串 JSON 原文 ⇒ **逐字塞进去**，中间没有人解析过它。
                    // 这是两条链路里**唯一**一处参数不需要过类型的（Claude 那边必须过）。
                    ["arguments"] = c.Arguments,
                },
            }).ToArray());
        }

        return obj;
    }
}
