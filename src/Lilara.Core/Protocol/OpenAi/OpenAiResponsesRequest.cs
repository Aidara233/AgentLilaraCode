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

    public static string Build(
        ModelRequest request, bool explicitCache = false, bool sendEffort = false,
        bool includeReasoning = false, bool stream = false)
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

        // ---- 显式缓存断点（2026-10-28，只在 provider 声明 `cacheMode: explicit` 时）----
        //
        // ⚠️ **必须打在这里：尾槽位追加之前**（与 Chat 那条同一个理由，那处有完整解释）。
        // 简短版：尾块是每步都变的那一段，它一进 `input`，「最后一条能挂断点的 message 项」
        // 就可能选中它 ⇒ 断点划在变动内容上 ⇒ 每步 miss。
        // ⚠️ 旧注释断言「`function_call_output` 被跳过 ⇒ 断点天然在浮动对之前」——
        // 那句话**只在有 `floating_context` 工具时成立**（那种会话尾槽位被清空）；
        // 没有那个工具的会话，尾块会拼成一条 `type:"message"` 项追加在最后，而它**能**挂断点。
        var marked = explicitCache && OpenAiResponsesCache.Mark(input);

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

        // ---- 思考强度与推理回传（2026-10-28，两个都由 provider 打开，默认都不发）----
        //
        // ⚠️ **`reasoning.effort`**：与 Chat 那条同一条理由（`Axes.Effort` 是纯参数轴，
        // 而这条链路此前一次都没读过 `request.Effort`）。字段名**不一样**——Responses 是
        // 嵌套对象里的 `reasoning.effort`，Chat 是顶层 `reasoning_effort`。别互相抄。
        //
        // ⚠️ **`reasoning.context` 不写**：它是「历史推理要不要渲染进下一步」的开关，
        // 默认值随模型代际变（5.4/5.5 是 current_turn，5.6+ 是 all_turns）。
        // 写死任何一个都会把「升级模型后的默认行为」钉在某一代上，而那是上游的判据。
        //
        // ⚠️ **`include: ["reasoning.encrypted_content"]`**：本仓不用 `previous_response_id`
        // （全量重放自己的时间线）⇒ 服务端那份推理拿不到，推理回传**只能**靠客户端带着密文重放。
        // 没有这个字段，响应里的 reasoning 项就没有密文 ⇒ 下一轮发回去的只是一段摘要。
        if (sendEffort || includeReasoning)
        {
            var reasoning = new JsonObject();
            // ⚠️ **`off` 发 `none`**（2026-11-03，与 Chat 那条同一个判据 `EffortParam.IsOff`）：
            // `none` 是 OpenAI 自己的「关思考」档。⚠️ `includeReasoning` 那一支**不受影响**——
            // 它管的是「响应里的推理项要不要带密文回来」，与本档要不要请求思考无关。
            if (sendEffort) reasoning["effort"] = EffortParam.IsOff(request.Effort) ? "none" : request.Effort;
            if (reasoning.Count > 0) body["reasoning"] = reasoning;

            if (includeReasoning)
                body["include"] = new JsonArray("reasoning.encrypted_content");
        }

        // ---- 显式缓存断点（2026-10-28，只在 provider 声明 `cacheMode: explicit` 时）----
        //
        // ⚠️ 与 Chat 同一条纪律：`prompt_cache_options.mode = "explicit"` 会**关掉隐式缓存**，
        // 所以打不上历史断点就**不写**这个键（宁可退回隐式，而不是变成「什么都不缓存」）。
        // ⚠️ `instructions`（头槽位）是**字符串**、挂不了断点 ⇒ Responses 这一族只有历史那一个断点。
        // 这是协议形状的限制，不是漏做。
        if (marked)
            body["prompt_cache_options"] = new JsonObject { ["mode"] = "explicit" };

        if (stream) body["stream"] = true;

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
        // ⚠️ **带图的结果走片数组那条**（2026-10-29）：`function_call_output.output` 可以
        // 是字符串，也可以是一串内容片。⚠️ 只在真的有图时换形状 —— 纯文本逐字节不变。
        if (r.Content.Any(b => b is Block.ImageBlock image && ImagePayload.CanSend(image)))
        {
            var parts = new JsonArray();
            foreach (var b in r.Content)
            {
                if (b is Block.TextBlock tb)
                    parts.Add(new JsonObject { ["type"] = "input_text", ["text"] = tb.Text });
                else if (b is Block.ImageBlock image && ImagePayload.CanSend(image))
                    parts.Add(new JsonObject
                    {
                        ["type"] = "input_image",
                        ["image_url"] = ImagePayload.DataUrl(image),
                    });
                else
                    parts.Add(new JsonObject { ["type"] = "input_text", ["text"] = UnsupportedBlock.Note(b) });
            }

            return new JsonObject
            {
                ["type"] = "function_call_output",
                ["call_id"] = r.CallId,
                ["output"] = parts,
            };
        }

        var text = string.Concat(r.Content.Select(b => b switch
        {
            Block.TextBlock t => t.Text,

            // ⚠️ **不再抛**（2026-10-29，见 `UnsupportedBlock`）：三条链路同一条判据。
            _ => UnsupportedBlock.Note(b),
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
    // Responses 的 content **不暴露推理块**（它们进的是并列的 reasoning 项，见 ReasoningItem）。
    private static List<JsonObject> MessageToItems(TimelineEvent.MessageEvent message, string protocol)
    {
        var role = message.From == Speaker.User ? "user" : "assistant";

        // 先按协议能力过滤推理块——Responses 的 content 不承载它们（它们走 reasoning 项，见上）。
        var blocks = ReasoningPolicy.FilterFor(message.Blocks, protocol);
        var texts = blocks.OfType<Block.TextBlock>().Select(b => b.Text).ToList();

        // ⚠️ **翻译不了的块要留一句话**（2026-10-29，见 `UnsupportedBlock`）：与 Chat 那条
        // 逐字同一个理由——`OfType<TextBlock>()` 的静默丢是「多一条假信息」。
        foreach (var note in blocks.Select(UnsupportedBlock.Skippable).OfType<string>())
            texts.Add(note);

        // ⚠️ 与 Chat 同一条判据：一个只调工具、不说话的 assistant 消息 content 是空的，
        // 而那是常态 ⇒ 判据要连调用一起看。
        var calls = blocks.OfType<Block.ToolUseBlock>().ToList();

        // 过滤掉推理块后可能一条不剩（推理模型只思考不落笔），必须早失败。
        // ⚠️ 2026-10-17：推理块在 Responses 里**不丢**——它们是独立的 `type:"reasoning"` 项
        // （见下面 `ReasoningItem`）。⚠️ 取自**原始** `message.Blocks`，不是过滤后的 `blocks`：
        // `FilterFor` 的职责正是把推理块挡在 content 之外，从它身上取会被自己挡掉。
        var reasoning = message.Blocks.OfType<Block.ReasoningBlock>().ToList();
        if (texts.Count == 0 && calls.Count == 0 && reasoning.Count == 0)
            throw new ArgumentException(
                $"时间线里有空消息（speaker={message.From}），无法构造请求", nameof(message));

        var items = new List<JsonObject>();

        // ⚠️ **推理项在整条消息的项之前**（与响应里的次序一致：先思考，再说话 / 调工具）。
        items.AddRange(ReasoningItems(reasoning));

        // ⚠️ **没有正文时不发那条 message 项**（`content: []` 是另一回事，而且多半非法）。
        // 一条「只调工具」的 assistant 消息在 Responses 下**就是**若干 function_call 项，没有消息项。
        if (texts.Count > 0)
        {
            // ⚠️ **带图时 content 是片数组**（2026-10-29）：用户用 `input_text`、助手用
            // `output_text`，而图片一律 `input_image`（`image_url` 是**字符串**，与 Chat
            // 的 `{url:…}` 对象不同——两条 OpenAI 协议的形状**不可互换**）。
            // ⚠️ 纯文本那条仍然发「全 `input_text`」的数组，**逐字节不变**。
            var withImages = blocks.Any(b => b is Block.ImageBlock image && ImagePayload.CanSend(image));
            var blockType = message.From == Speaker.User ? "input_text" : "output_text";

            var parts = new JsonArray();
            if (!withImages)
            {
                foreach (var t in texts)
                    parts.Add(new JsonObject { ["type"] = blockType, ["text"] = t });
            }
            else
            {
                foreach (var b in blocks)
                {
                    if (b is Block.TextBlock tb)
                        parts.Add(new JsonObject { ["type"] = blockType, ["text"] = tb.Text });
                    else if (b is Block.ImageBlock image && ImagePayload.CanSend(image))
                        parts.Add(new JsonObject
                        {
                            ["type"] = "input_image",
                            ["image_url"] = ImagePayload.DataUrl(image),
                        });
                    else if (b is not (Block.ToolUseBlock or Block.ReasoningBlock)
                             && UnsupportedBlock.Skippable(b) is { } note)
                        parts.Add(new JsonObject { ["type"] = blockType, ["text"] = note });
                }
            }

            items.Add(new JsonObject
            {
                ["type"] = "message",   // 必须——少了它上游报 upstream_error
                ["role"] = role,
                ["content"] = parts,
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

    /// <summary>
    /// 推理块 → `{"type":"reasoning", …}` 项。**一块一项**。
    ///
    /// <para>⚠️ <b>2026-10-28：这个方法补的是一个「只改了一半」的缺陷</b>。请求侧从 2026-10-17
    /// 起就在发 `type:"reasoning"` 项（`OpenAiResponsesRequest.ReasoningItem`），而响应侧
    /// 一直把推理项整个跳过 ⇒ 记录里<b>从来不会有</b>推理块 ⇒ 请求侧那段
    /// `OfType&lt;Block.ReasoningBlock&gt;()` <b>永远枚举不到东西</b>。测试全绿，因为它们是
    /// 手工 `new ReasoningBlock(...)` 造的输入——生产路径造不出来。</para>
    ///
    /// <para>⚠️ <b>`Kind = Text` → `summary`；`Kind = Redacted` → `encrypted_content`</b>。
    /// 两者是同一份协议项里的两格（可读摘要 / 不透明推理状态），而
    /// <see cref="Block.ReasoningBlock"/> 只有 Text 一格装不下两样 ⇒ 响应侧记成两块，
    /// 发回去时是<b>两条</b>项。</para>
    ///
    /// <para>⚠️ <b>为什么不把两半合回一条</b>：合并需要一个「这两块来自同一项」的凭据，而
    /// 唯一可用的候选是协议给的 `id`（`rs_…`）——它只能存在
    /// <see cref="Block.ReasoningBlock.Signature"/> 里，而那个格子是<b>Claude 的凭据</b>：
    /// 往里写 OpenAI 的 id 会让「签名（Claude 的凭据）不属于 Responses 的 reasoning 项——不发」
    /// 那条不变量静默失效（`OpenAiResponsesRequestTests.Build_NeverFabricatesReasoningIntoTheBody`
    /// 钉的正是它）。发两条项的代价是零：真 OpenAI 对不相关的推理项
    /// **接受但丢弃**（cookbook：「the API will simply discard any reasoning items that aren't
    /// relevant for the current turn」），而 `encrypted_content` 本身就是自足的
    /// （Azure 文档：无状态模式下「keep every output item, … and replay the complete history」，
    /// 不要求带 id）。⇒ <b>宁可两条项，不伪造一个跨协议的字段</b>。</para>
    ///
    /// <para>⚠️ <b>`Signature` 一律不发</b>：那是 Claude 的凭据，两条 OpenAI 协议都不认识它。</para>
    /// </summary>
    private static List<JsonObject> ReasoningItems(List<Block.ReasoningBlock> blocks)
    {
        var items = new List<JsonObject>();

        foreach (var block in blocks)
        {
            var item = new JsonObject { ["type"] = "reasoning" };

            if (block.Kind == ReasoningKind.Redacted)
            {
                // 密文：原样放进 `encrypted_content`，一个字节都不动。
                if (block.Text.Length > 0) item["encrypted_content"] = block.Text;
                // ⚠️ `summary` 也要给（空数组）：它是这个项形状的一部分，省略会让某些网关报错。
                // 但**绝不**把密文填进 summary——那是伪造（模型会读到一坨 base64）。
                item["summary"] = new JsonArray();
            }
            else
            {
                // 可读摘要照旧走 `summary`（DeepSeek 形状的用例）。
                item["summary"] = new JsonArray(new JsonObject
                {
                    ["type"] = "summary_text",
                    ["text"] = block.Text,
                });
            }

            items.Add(item);
        }

        return items;
    }
}
