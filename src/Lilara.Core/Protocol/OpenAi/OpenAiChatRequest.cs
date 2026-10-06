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
        if (request.Timeline.Count == 0)
            throw new ArgumentException("时间线为空，无法构造请求", nameof(request));

        var messages = new JsonArray();

        // 系统提示放第一条消息（Chat 协议没有独立 system 字段）。
        // 判空必须落在拼接后的文本上，不能判 SystemBlocks.Count：非空的输入若全是非文本块，
        // 拼接结果是空串，发出去就是一个空 system 消息（空串 vs 不存在）。
        var systemText = string.Concat(
            request.SystemBlocks.OfType<Block.TextBlock>().Select(b => b.Text));
        if (systemText.Length > 0)
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = systemText });

        foreach (var evt in request.Timeline)
            messages.Add(ToMessage(evt, request.Protocol));

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
    /// 一个 canonical 事件 → 协议消息。⚠️ **两种事件在这里分派，形状完全不同**：
    /// 消息是 role/content，工具结果是 role/**tool** + `tool_call_id`（Chat 把结果
    /// 做成一个**独立消息**，而不是 Claude 那种塞进 user 消息的块）。
    /// </summary>
    private static JsonObject ToMessage(TimelineEvent evt, string protocol) => evt switch
    {
        TimelineEvent.MessageEvent m => MessageToJson(m, protocol),
        TimelineEvent.ToolResultEvent r => ResultToJson(r, protocol),
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
    private static JsonObject ResultToJson(TimelineEvent.ToolResultEvent r, string protocol)
    {
        if (r.Ref is { } reference)
            throw new ArgumentException($"工具结果引用了记录 {reference}，但 `fetch` 还没实现", nameof(r));

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

    // 一条 canonical 消息 → 协议消息。Chat 不暴露推理（返回体无推理字段）→ 丢且不伪造；
    // protocol 参数在这里对输出无可观测影响（过滤结果只被 OfType<TextBlock>() 消费）。
    private static JsonObject MessageToJson(TimelineEvent.MessageEvent message, string protocol)
    {
        var role = message.From == Speaker.User ? "user" : "assistant";

        // 先按协议能力过滤推理块——Chat 不承载，丢且不伪造。
        var blocks = ReasoningPolicy.FilterFor(message.Blocks, protocol);
        var texts = blocks.OfType<Block.TextBlock>().Select(b => b.Text).ToList();

        // 模型的工具调用走 `tool_calls`（Chat 把它挂在 assistant 消息上）。
        var calls = blocks.OfType<Block.ToolUseBlock>().ToList();

        // ⚠️ 判据要**连调用一起看**：一个只调工具、不说话的 assistant 消息 content 是空的，
        // 而那是**常态**（模型先调再说）。只看 texts 会把它当成「空消息」拒掉。
        if (texts.Count == 0 && calls.Count == 0)
            throw new ArgumentException(
                $"时间线里有空消息（speaker={message.From}），无法构造请求", nameof(message));

        var obj = new JsonObject { ["role"] = role };

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
