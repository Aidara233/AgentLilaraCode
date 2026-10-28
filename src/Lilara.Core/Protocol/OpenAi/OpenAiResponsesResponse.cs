using System.Text.Json;
using Lilara.Core.Kernel;

namespace Lilara.Core.Protocol.OpenAi;

// OpenAI Responses 响应 → canonical。纯函数：output 是异构数组，取 message / function_call / reasoning 三类项，
// 其余（内置工具调用项等）跳过。
public static class OpenAiResponsesResponse
{
    public static ModelReply Map(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // 根守卫必须最先：TryGetProperty 在非对象根上直接抛，少了它 [] / null / 5 / "hi" 全是崩溃。
        if (root.ValueKind != JsonValueKind.Object)
            return new ModelReply { Blocks = [], StopReason = "unknown", Refused = false };

        // OpenAI 的 input_tokens 含缓存命中 ⇒ 让 UsageJson 归一成「未命中输入」（见那边的注释）。
        var usage = UsageJson.Parse(root, inputIncludesCache: true);
        var reportedModel = root.TryGetProperty("model", out var model)
            && model.ValueKind == JsonValueKind.String ? model.GetString() : null;
        var responseId = root.TryGetProperty("id", out var id)
            && id.ValueKind == JsonValueKind.String ? id.GetString() : null;

        var status = root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString() ?? "unknown"
            : "unknown";

        // ⚠️ **按遇到次序收集，文本与调用混在同一个列表里**：`output` 是一个有序数组，
        // 而「先说了句话、再调工具」与「先调、再解释」在历史上是不同的东西。
        // 分成两个列表再拼接会让两种次序变得一样（那是**改写了模型说过的话的顺序**）。
        var blocks = new List<Block>();
        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String) continue;

                switch (t.GetString())
                {
                    case "message":
                        blocks.AddRange(TextsOf(item));
                        break;

                    case "function_call":
                        if (FunctionCallOf(item) is { } call) blocks.Add(call);
                        break;

                    // ⚠️ **2026-10-28：推理项不再丢**。此前这里写的是「其余（reasoning 等）跳过」，
                    // 而请求侧早就在发 `type:"reasoning"` 项（2026-10-17 那批）——两边合起来的
                    // 效果是**请求侧那段代码永远枚举不到东西**（记录里从来没有推理块）。
                    // 那是一个「只改了一半」的缺陷：测试全绿，因为它们手工 `new ReasoningBlock`。
                    case "reasoning":
                        blocks.AddRange(ReasoningOf(item));
                        break;

                    // 其余（web_search_call 等内置工具项）跳过：它们不是对话内容。
                }
            }
        }

        // 拒答信号不在 status 里（incomplete 是「没生成完」不是拒答）：没有可观测信号，故恒 false。
        // 原版另有 incomplete_details.reason，实测正常与截断两种响应都不含它，截断仍是 "completed"。
        // status 原样透传。
        return new ModelReply
        {
            Blocks = blocks,
            StopReason = status,
            Refused = false,
            Usage = usage,
            ReportedModel = reportedModel,
            ResponseId = responseId,
        };
    }

    /// <summary>
    /// `type:"reasoning"` 项 → 一或两个 <see cref="Block.ReasoningBlock"/>。
    ///
    /// <para>⚠️ <b>2026-10-28：这个方法补的是一个「只改了一半」的缺陷</b>。请求侧从 2026-10-17
    /// 起就在发 `type:"reasoning"` 项（`OpenAiResponsesRequest.ReasoningItems`），而响应侧
    /// 一直把推理项整个跳过 ⇒ 记录里<b>从来不会有</b>推理块 ⇒ 请求侧那段
    /// `OfType&lt;Block.ReasoningBlock&gt;()` <b>永远枚举不到东西</b>。测试全绿，因为它们是
    /// 手工 `new ReasoningBlock(...)` 造的输入——生产路径造不出来。</para>
    ///
    /// <para>⚠️ <b>为什么是「一或两个块」</b>：一条协议项里有两样东西要各自保住——
    /// <c>summary</c>（可读摘要）与 <c>encrypted_content</c>（不透明推理状态）。
    /// <see cref="Block.ReasoningBlock"/> 只有 Text 一格，塞不下两样，硬塞就是伪造
    /// ⇒ 拆成两块。发回去时是<b>两条</b>项（见 `ReasoningItems` 为什么不合回一条）。</para>
    ///
    /// <para>⚠️ <b>为什么 <c>encrypted_content</c> 记成 <see cref="ReasoningKind.Redacted"/></b>：
    /// 那一档的契约就是「加密载荷：原样保留、不解释、不打印」。它是<b>字节</b>，不是给人和模型
    /// 读的正文 ⇒ 当成 Text 会让它出现在界面上，也会让 Chat 链路把它当 `reasoning_content` 发出去。
    /// （<see cref="ReasoningKind.Redacted"/> 在 Claude 那边对应 `redacted_thinking`，语义一致。）</para>
    ///
    /// <para>两样都没有 ⇒ 空列表（一个空的 reasoning 项没有可记录的内容，别造空块）。</para>
    /// </summary>
    private static List<Block> ReasoningOf(JsonElement item)
    {
        var blocks = new List<Block>();

        // ⚠️ **`item.id`（`rs_…`）刻意不记录**：唯一能存它的格子是
        // <see cref="Block.ReasoningBlock.Signature"/>，而那一格是 **Claude 的凭据格**
        // （`ClaudeRequestBuilder` 把它当 thinking 块的 `signature` 发出去）。把 OpenAI 的 id
        // 写进去，`ReasoningItems` 就得再加一条「发的时候别发它」的判据——而那不是安全网，
        // 是把两家字段混装之后的补丁。`encrypted_content` 本身**自足**（Azure 文档：无状态模式
        // 「keep every output item, … and replay the complete history」，不要求带 id），
        // 所以记住它、把内容原样重放就够了。

        // summary 是数组（可多段）。拼成一格——契约上它是「这一项的可读摘要」，
        // 而分段只是生成时的换行，不是结构（与 `TextsOf` 处理 message 的 content 同一条判据）。
        var summary = string.Concat(SummaryTextsOf(item));
        if (summary.Length > 0)
            blocks.Add(new Block.ReasoningBlock(null, summary, ReasoningKind.Text));

        // 加密载荷：原样保住，一个字节都不动（`00` §7 判据 5：能保住就保住）。
        if (item.TryGetProperty("encrypted_content", out var encrypted)
            && encrypted.ValueKind == JsonValueKind.String
            && encrypted.GetString() is { Length: > 0 } payload)
            blocks.Add(new Block.ReasoningBlock(null, payload, ReasoningKind.Redacted));

        return blocks;
    }

    /// <summary>`summary: [{type:"summary_text", text:…}]` 里的各段文本，按原序。</summary>
    private static List<string> SummaryTextsOf(JsonElement item)
    {
        var texts = new List<string>();
        if (!item.TryGetProperty("summary", out var summary)
            || summary.ValueKind != JsonValueKind.Array) return texts;

        foreach (var part in summary.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object) continue;
            if (part.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String
                && text.GetString() is { Length: > 0 } str)
                texts.Add(str);
        }

        return texts;
    }

    private static List<Block> TextsOf(JsonElement message)
    {
        var texts = new List<Block>();
        if (!message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array) return texts;

        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;
            if (block.TryGetProperty("type", out var bt) && bt.GetString() == "output_text"
                && block.TryGetProperty("text", out var txt)
                && txt.ValueKind == JsonValueKind.String
                && txt.GetString() is { Length: > 0 } str)
                texts.Add(new Block.TextBlock(str));
        }

        return texts;
    }

    /// <summary>
    /// `type:"function_call"` 项 → <see cref="Block.ToolUseBlock"/>。
    ///
    /// <para>⚠️ <b>`arguments` 是<b>字符串</b></b>，正好对上我们「参数是逐字 JSON 原文」
    /// 那条纪律 ⇒ **原样取，不解析**（`01` §9 铁律）。</para>
    ///
    /// <para>⚠️ <b>缺 `call_id` / `name` 的项返回 <c>null</c>（跳过）</b>：没有 id 就没法配对，
    /// 落进记录是一条悬空的 `tool_use` ⇒ 下一步上游 400（`01` §3.6）。
    /// 而 `arguments` 缺失给 <c>{}</c>——空对象是合法 JSON，<c>null</c> 会让落盘那道闸抛。</para>
    /// </summary>
    private static Block.ToolUseBlock? FunctionCallOf(JsonElement item)
    {
        if (!item.TryGetProperty("call_id", out var id) || id.ValueKind != JsonValueKind.String) return null;
        if (!item.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) return null;

        var args = item.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String
            ? a.GetString()!
            : "{}";

        return new Block.ToolUseBlock(id.GetString()!, name.GetString()!, args);
    }
}
