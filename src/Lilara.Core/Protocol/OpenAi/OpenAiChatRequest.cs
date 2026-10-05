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

        return body.ToJsonString(Json.Options);
    }

    // 一条 canonical 消息 → 协议消息。Chat 不暴露推理（返回体无推理字段）→ 丢且不伪造；
    // protocol 参数在这里对输出无可观测影响（过滤结果只被 OfType<TextBlock>() 消费）。
    private static JsonObject ToMessage(TimelineEvent evt, string protocol)
    {
        var message = (TimelineEvent.MessageEvent)evt;
        var role = message.From == Speaker.User ? "user" : "assistant";

        // 先按协议能力过滤推理块——Chat 不承载，丢且不伪造。
        var blocks = ReasoningPolicy.FilterFor(message.Blocks, protocol);
        var texts = blocks.OfType<Block.TextBlock>().Select(b => b.Text).ToList();

        // `content` 为空会被协议拒绝：过滤掉推理块后可能一条不剩（推理模型只思考不落笔），必须早失败。
        if (texts.Count == 0)
            throw new ArgumentException(
                $"时间线里有空消息（speaker={message.From}），无法构造请求", nameof(evt));

        // 单文本走字符串形式（协议原生形状，最省字节）；多文本走带类型的 part 数组。
        // 多块时绝不能用裸字符串数组：["一","二"] → invalid_request_error，带 type 的 part 才收。
        return texts.Count == 1
            ? new JsonObject { ["role"] = role, ["content"] = texts[0] }
            : new JsonObject
            {
                ["role"] = role,
                ["content"] = new JsonArray(
                    texts.Select(t => (JsonNode)new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = t,
                    }).ToArray()),
            };
    }
}
