using System.Text.Json;
using Lilara.Core.Canonical;

namespace Lilara.Core.Adapters.OpenAi;

/// <summary>
/// OpenAI Chat 响应 → canonical。
/// ⚠️ 纯函数：同一段 JSON 进，同一个 <see cref="ModelReply"/> 出。无时钟、无环境、无顺序依赖。
///
/// <para>⚠️ <b>本机网关实测过的形状</b>（不是推测）：</para>
/// <list type="bullet">
///   <item>正常：<c>{"choices":[{"message":{"role":"assistant","content":"OK"},"finish_reason":"stop"}]}</c></item>
///   <item>截断：<c>content:""</c>（**空串，不是 null**）+ <c>finish_reason:"length"</c>；
///         <c>completion_tokens</c> 恰好等于请求的 <c>max_tokens</c>。</item>
///   <item>带 tools：<c>finish_reason:"tool_calls"</c>——正常完成，不是拒答。</item>
///   <item>错误体：<c>{"error":{...}}</c> 与 <c>{"error":"Authentication required"}</c>，
///         两者都**没有 choices**。</item>
/// </list>
///
/// <para>⚠️ <b>容忍缺失字段</b>：上游出什么都不能让核心崩。实测
/// <c>JsonElement.TryGetProperty</c> 在**非对象根**上抛
/// <c>InvalidOperationException</c>（`[]` / `null` / `5` / `"hi"` 全抛），
/// 所以「容忍」必须从「根是对象」这一步就开始，否则第一个非对象响应就是一次崩溃。</para>
/// </summary>
public static class OpenAiChatResponse
{
    /// <summary>认不出结束原因时的占位——绝不伪造成 "stop"。</summary>
    private const string UnknownFinish = "unknown";

    /// <summary>
    /// OpenAI 文档定义的拒答信号。
    /// ⚠️ **本机网关从未观测到它**（实测只见过 stop / length / tool_calls），
    /// 也**没有任何可观测的替代信号**——见报告。这里只认协议定义值，保持保守：
    /// 绝不把 <c>length</c>（推理模型烧完预算 / 真截断）当成拒答。
    /// </summary>
    private const string ContentFilterFinish = "content_filter";

    public static ModelReply Map(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string? text = null;
        var finish = UnknownFinish;

        // ⚠️ 三层 ValueKind 判据缺一不可：
        //   1. 根必须是对象——TryGetProperty 在非对象根上直接抛（实测，见类注释）。
        //   2. choices 必须是数组——它可能是 null / 对象。
        //   3. choices[0] 必须是对象——它可能是数字（`{"choices":[1]}`），
        //      在那上面 TryGetProperty 同样抛。
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0
            && choices[0].ValueKind == JsonValueKind.Object)
        {
            var first = choices[0];

            if (first.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                finish = fr.GetString() ?? UnknownFinish;

            // ⚠️ message 可能是 null / 非对象；content 可能是 null（推理模型烧完 token）、
            // 空串、或数组（typed parts）。只有字符串才取，其余一律「没有正文」——
            // 不解释、不伪造。
            if (first.TryGetProperty("message", out var msg)
                && msg.ValueKind == JsonValueKind.Object
                && msg.TryGetProperty("content", out var c)
                && c.ValueKind == JsonValueKind.String)
                text = c.GetString();
        }

        // ⚠️ 判据是 `Length > 0`，**不是** `is not null`。
        // 实测截断响应给的是**空串**（`max_tokens=4` → `content:""`）：
        // `is not null` 会在这里造出一个空的 TextBlock 并写进时间线，
        // 正是 04 §「空串 vs 不存在」要防的那种「空模型消息」。
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
