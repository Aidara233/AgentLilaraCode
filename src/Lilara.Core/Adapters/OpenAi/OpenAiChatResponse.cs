using System.Text.Json;
using Lilara.Core.Kernel;

namespace Lilara.Core.Adapters.OpenAi;

// OpenAI Chat 响应 → canonical。纯函数：同一段 JSON 进，同一个 ModelReply 出。
// 本机网关实测过的形状：正常 finish_reason:"stop"；截断给 content:""（空串，不是 null）+ "length"；
// 带 tools 是 "tool_calls"（正常完成，不是拒答）；错误体没有 choices。
// 容忍缺失字段：JsonElement.TryGetProperty 在非对象根上抛 InvalidOperationException（[] / null / 5 / "hi" 全抛），
// 所以「容忍」必须从「根是对象」这一步就开始。
public static class OpenAiChatResponse
{
    /// <summary>认不出结束原因时的占位——绝不伪造成 "stop"。</summary>
    private const string UnknownFinish = "unknown";

    // OpenAI 文档定义的拒答信号。本机网关从未观测到它（只见 stop / length / tool_calls），
    // 也没有可观测的替代信号：只认协议定义值，绝不把 length 当成拒答。
    private const string ContentFilterFinish = "content_filter";

    public static ModelReply Map(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string? text = null;
        var finish = UnknownFinish;

        // 三层 ValueKind 判据缺一不可：根必须是对象、choices 必须是数组、choices[0] 必须是对象——
        // 非对象上的 TryGetProperty 会直接抛。
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0
            && choices[0].ValueKind == JsonValueKind.Object)
        {
            var first = choices[0];

            if (first.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                finish = fr.GetString() ?? UnknownFinish;

            // message 可能是 null / 非对象；content 可能是 null（推理模型烧完 token）、
            // 空串、或数组（typed parts）。只有字符串才取，其余一律「没有正文」，不解释、不伪造。
            if (first.TryGetProperty("message", out var msg)
                && msg.ValueKind == JsonValueKind.Object
                && msg.TryGetProperty("content", out var c)
                && c.ValueKind == JsonValueKind.String)
                text = c.GetString();
        }

        // 判据是 Length > 0，不是 is not null：截断响应给的是空串（max_tokens=4 → content:""），
        // is not null 会造出一个空的 TextBlock 并写进时间线。
        var blocks = text is { Length: > 0 }
            ? new List<Block> { new Block.TextBlock(text) }
            : [];

        return new ModelReply
        {
            Blocks = blocks,
            StopReason = finish,
            Refused = finish == ContentFilterFinish,
        };
    }
}
