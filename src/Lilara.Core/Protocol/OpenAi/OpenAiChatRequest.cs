using System.Text.Json;
using System.Text.Json.Nodes;
using Lilara.Core.Kernel;

namespace Lilara.Core.Protocol.OpenAi;

// canonical → OpenAI Chat 请求体。纯函数：同一 ModelRequest 必须构造出逐字节相同的 JSON；
// 用 JsonObject 显式按序插入，绝不依赖字典枚举序。
public static class OpenAiChatRequest
{
    public const string Path = "/v1/chat/completions";

    public static string Build(
        ModelRequest request, bool explicitCache = false, bool sendEffort = false, bool stream = false)
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

        // ⚠️ `body` 先声明、`messages` 是按引用装进去的数组 ⇒ 后面往 `messages` 里追加
        // （尾槽位）仍然会反映到 `body` 上。声明放在这里只是为了让断点那一段能写 `body[...]`。
        var body = new JsonObject
        {
            ["model"] = request.ModelId,
            ["max_tokens"] = request.MaxTokens,
            ["messages"] = messages,
        };

        // ---- 显式缓存断点（2026-10-28，只在这个 provider 声明 `cacheMode: explicit` 时）----
        //
        // ⚠️ **必须打在这里：尾槽位追加之前**。断点的判据是「落在每步都变的东西之前」，
        // 而尾块（此刻的时间 / 步数 / 用量）<b>就是</b>每步都变的那一段 ⇒ 它一进 `messages`，
        // 「最后一条纯文本 user 消息」正好选中它，断点就划在变动内容上 ⇒ 每步 miss
        // （GPT-5.6+ 还要按 1.25× 付 `cache_write_tokens`，比不开缓存更贵）。
        //
        // ⚠️ 旧注释在这里断言「浮动读数那对没有可挂的文本块，被自动跳过 ⇒ 断点天然在它们之前」
        // ——那句话**只在有 `floating_context` 工具时成立**：那种会话的尾槽位被清空
        // （`Stepper`：`Slots = HasFloatingContextTool ? slots with { Tail = [] } : slots`），
        // 末尾是 `role:"tool"` 的合成结果。而没有那个工具的会话（纯 chat 预设、
        // 测试替身）尾块会**原样拼成一条 user 消息追加在最后**，旧判据当场选错。
        // ⇒ 位置判据不能靠「末尾那条是什么形状」猜，只能靠**代码次序**保证。
        if (explicitCache && MarkCacheBreakpoints(messages))
            body["prompt_cache_options"] = new JsonObject { ["mode"] = "explicit" };

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

        // ⚠️ **断点已在上面（尾槽位追加之前）打过了**——这里不再重复。
        // 那一处有完整理由；删掉这里的第二份是为了让「位置判据只有一处」兑现。

        // ---- 流式（由 provider 明确开启；默认请求字节不变）----
        if (stream)
        {
            body["stream"] = true;
            // 不带它的话流里**没有 usage** ⇒ 账本静默归零。
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

        // ---- 思考强度（2026-10-28）----
        //
        // ⚠️ **这是一条「此前根本没接线」的轴**：`Axes.Effort` 是 `InContext = false` 的纯参数轴
        // ⇒ 它只有进请求参数这一条路，而三条链路里此前只有 Claude 读过 `request.Effort`。
        // 两条 OpenAI 链路上改 effort，线上字节**逐字节相同**（没有错误、没有日志）。
        //
        // ⚠️ **默认不发**（`sendEffort` 由 provider 的 `effortParam: reasoning` 打开）：
        // `reasoning_effort` 不是所有端点都认（GLM / doubao / 网关代理对未知参数的处理不一致，
        // 有的 400 有的静默忽略）。判据与理由见 `EffortParam`。
        //
        // ⚠️ 档名**原样发**：本仓 5 档是真 OpenAI 与 DeepSeek 合法值的子集，中间不插映射表
        // （再写一张就是 G10 那种第二份副本）。
        if (sendEffort)
            body["reasoning_effort"] = request.Effort;

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
        // ⚠️ **带图的结果走片数组那条**（2026-10-29）：Chat 的 tool 消息 `content`
        // 也可以是片数组。⚠️ 只在真的有图时换形状 —— 纯文本结果逐字节不变。
        if (r.Content.Any(b => b is Block.ImageBlock image && ImagePayload.CanSend(image)))
        {
            var parts = new JsonArray();
            foreach (var b in r.Content)
            {
                if (b is Block.TextBlock tb)
                    parts.Add(new JsonObject { ["type"] = "text", ["text"] = tb.Text });
                else if (b is Block.ImageBlock image && ImagePayload.CanSend(image))
                    parts.Add(new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject { ["url"] = ImagePayload.DataUrl(image) },
                    });
                else
                    parts.Add(new JsonObject { ["type"] = "text", ["text"] = UnsupportedBlock.Note(b) });
            }

            return new JsonObject
            {
                ["role"] = "tool",
                ["tool_call_id"] = r.CallId,
                ["content"] = parts,
            };
        }

        var text = string.Concat(r.Content.Select(b => b switch
        {
            Block.TextBlock t => t.Text,

            // ⚠️ **不再抛**（2026-10-29，见 `UnsupportedBlock`）：一条翻译不了的结果不该让
            // 这个会话此后每一次请求都炸（记录只追加）。占位是**模型看得见的**，不是静默丢。
            _ => UnsupportedBlock.Note(b),
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

        // ⚠️ **翻译不了的块要留一句话**（2026-10-29，见 `UnsupportedBlock`）：原先这里
        // `OfType<TextBlock>()` 把它们**静默丢了**——而「图不见了、模型也不知道少了什么」
        // 是比抛更坏的那种失败（多一条假信息：它以为自己看到了全部）。
        // ⚠️ 正文 / 工具调用 / 推理块都由各自的路径表达，`Skippable` 对它们返回 null。
        foreach (var note in blocks.Select(UnsupportedBlock.Skippable).OfType<string>())
            texts.Add(note);

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

        // ⚠️ **带图的消息走"片数组"那条**（2026-10-29）：Chat 的多模态 content 是
        // `[{type:"text"},{type:"image_url"}]`。⚠️ 只在**真的有图**时才换形状：
        // 纯文本历史仍旧走「字符串 / {type:text} 数组」，**逐字节不变**（前缀缓存按字节算）。
        if (blocks.Any(b => b is Block.ImageBlock image && ImagePayload.CanSend(image)))
        {
            var parts = new List<JsonNode>();
            foreach (var b in blocks)
            {
                switch (b)
                {
                    case Block.TextBlock text:
                        parts.Add(new JsonObject { ["type"] = "text", ["text"] = text.Text });
                        break;
                    case Block.ImageBlock image when ImagePayload.CanSend(image):
                        parts.Add(new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject { ["url"] = ImagePayload.DataUrl(image) },
                        });
                        break;
                    case Block.ToolUseBlock:
                    case Block.ReasoningBlock:
                        break;   // 各自走 `tool_calls` / `reasoning_content` 那条（见下）
                    default:
                        if (UnsupportedBlock.Skippable(b) is { } note)
                            parts.Add(new JsonObject { ["type"] = "text", ["text"] = note });
                        break;
                }
            }

            obj["content"] = new JsonArray(parts.ToArray());
            AppendToolCalls(obj, calls);
            return obj;
        }

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

        AppendToolCalls(obj, calls);
        return obj;
    }

    /// <summary>`tool_calls` 那一格（模型输出的一部分）。⚠️ 两条分支共用，别各写一遍。</summary>
    private static void AppendToolCalls(JsonObject obj, IReadOnlyList<Block.ToolUseBlock> calls)
    {
        if (calls.Count == 0) return;

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

    /// <summary>
    /// 给 system（header）与最后一条「content 是纯文本」的 user/assistant 消息各打一个断点；
    /// 返回**历史那一条**是否打上了（打不上就不该开 explicit，见 `Build` 里那段）。
    /// ⚠️ 浮动读数那对（assistant 的 tool_use + role=tool 的结果）没有可挂的纯文本块，被自动跳过
    /// ⇒ 断点天然落在它们之前。
    /// </summary>
    private static bool MarkCacheBreakpoints(JsonArray messages)
    {
        static JsonObject Part(string text) => new()
        {
            ["type"] = "text",
            ["text"] = text,
            ["prompt_cache_breakpoint"] = new JsonObject { ["mode"] = "explicit" },
        };

        static string? RoleOf(JsonObject m) =>
            m["role"] is JsonValue r && r.TryGetValue<string>(out var s) ? s : null;

        static string? TextOf(JsonObject m) =>
            m["content"] is JsonValue v && v.TryGetValue<string>(out var text) && text.Length > 0
                ? text
                : null;

        var historyMarked = false;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is not JsonObject m) continue;
            if (RoleOf(m) is not ("user" or "assistant")) continue;
            if (TextOf(m) is not { } text) continue;
            m["content"] = new JsonArray(Part(text));
            historyMarked = true;
            break;
        }

        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i] is not JsonObject m) continue;
            if (RoleOf(m) != "system") continue;
            if (TextOf(m) is not { } text) continue;
            m["content"] = new JsonArray(Part(text));
            break;
        }

        return historyMarked;
    }
}
