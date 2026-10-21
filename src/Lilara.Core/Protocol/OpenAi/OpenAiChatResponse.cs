using System.Text.Json;
using Lilara.Core.Kernel;

namespace Lilara.Core.Protocol.OpenAi;

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

        // OpenAI 的 prompt_tokens 含缓存命中 ⇒ 让 UsageJson 归一成「未命中输入」（见那边的注释）。
        var usage = UsageJson.Parse(root, inputIncludesCache: true);
        var reportedModel = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("model", out var model)
            && model.ValueKind == JsonValueKind.String ? model.GetString() : null;
        var responseId = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("id", out var id)
            && id.ValueKind == JsonValueKind.String ? id.GetString() : null;

        string? text = null;
        string? reasoning = null;
        var finish = UnknownFinish;
        List<Block> calls = [];

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
            if (first.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object)
            {
                if (msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    text = c.GetString();

                calls = ToolCalls(msg);

                // ⚠️ **DeepSeek 思考模式的 `reasoning_content`**（2026-10-17）：
                // 不在 content 里，而是 message 上的一个平级字段。不读它 ⇒ 下一步根本没东西可回传
                // ⇒ 请求带工具时 400 `must be passed back`。**空串也留**——它的语义是
                // 「这一轮思考了但没有正文」，与「没有这个字段」在回传时不同（见 ReasoningPolicy）。
                // ⚠️ 只认明文推理（ReasoningKind.Text）：redacted 是加密载荷，不能当正文回传，
                // 与 Claude 的 `redacted_thinking` 同理。
                if (msg.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
                    reasoning = rc.GetString()!;
            }
        }

        // 判据是 Length > 0，不是 is not null：截断响应给的是空串（max_tokens=4 → content:""），
        // is not null 会造出一个空的 TextBlock 并写进时间线。
        var blocks = new List<Block>();
        // ⚠️ 推理块在前、正文在后（与 Claude 那条链路同序）：`reasoning_content` 是「我打算这么做」，
        // `content` 是说完的话。⚠️ 空串**不造块**——与下面正文同一条判据（Length > 0）：
        // 造一个空文本块会让记录里多一条不存在的思考。
        if (reasoning is { Length: > 0 }) blocks.Add(new Block.ReasoningBlock(null, reasoning));
        if (text is { Length: > 0 }) blocks.Add(new Block.TextBlock(text));
        // ⚠️ 文本在前、调用在后：与响应里两者同时出现时的**自然次序**一致
        //（`content` 是「我打算这么做」，`tool_calls` 是「于是这么调」）。
        blocks.AddRange(calls);

        return new ModelReply
        {
            Blocks = blocks,
            StopReason = finish,
            Refused = finish == ContentFilterFinish,
            Usage = usage,
            ReportedModel = reportedModel,
            ResponseId = responseId,
        };
    }

    /// <summary>
    /// `message.tool_calls[]` → canonical 的 <see cref="Block.ToolUseBlock"/>。
    ///
    /// <para>⚠️ <b>`arguments` 在协议里是<b>字符串</b></b>（不是对象）——正好对上我们
    /// 「参数是逐字 JSON 原文」那条纪律 ⇒ **原样取，不解析**（`01` §9 铁律）。
    /// 这是两条链路里唯一一处不需要过类型的参数。</para>
    ///
    /// <para>⚠️ <b>参数不是合法 JSON 时留原文、不抛</b>：抛会让整步失败，而
    /// 「模型给了坏参数」是该由**工具**回答的事（它会返回一条说人话的结果），
    /// 不是构造期该炸的事。⚠️ 但记录层会拒（`JsonRawConverter` 的写出会抛）——
    /// 那是**落盘**那道闸，与这里不同，别把两处混成一处。</para>
    ///
    /// <para>⚠️ 缺失 `id` / `function.name` 的条目**跳过**：没有 id 就没法配对，
    /// 落进记录只会变成一条悬空的 `tool_use` ⇒ 下一步上游 400。</para>
    /// </summary>
    private static List<Block> ToolCalls(JsonElement message)
    {
        if (!message.TryGetProperty("tool_calls", out var tc) || tc.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<Block>();
        foreach (var call in tc.EnumerateArray())
        {
            if (call.ValueKind != JsonValueKind.Object) continue;
            if (!call.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) continue;
            if (!call.TryGetProperty("function", out var fn) || fn.ValueKind != JsonValueKind.Object) continue;
            if (!fn.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) continue;

            // arguments 缺失给 "{}"：空对象是**合法**的 JSON，而 null 会让落盘那道闸抛。
            var args = fn.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String
                ? a.GetString()!
                : "{}";

            result.Add(new Block.ToolUseBlock(id.GetString()!, name.GetString()!, args));
        }

        return result;
    }
}
