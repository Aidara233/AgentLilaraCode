using Anthropic.Models.Messages;
using CanonicalBlock = Lilara.Core.Canonical.Block;

namespace Lilara.Core.Adapters.Claude;

/// <summary>
/// Claude Messages 响应 → canonical。
/// ⚠️ 纯函数。文本块与推理块**都要取**，且**保持原顺序**（推理在前、文本在后）。
/// 00 §5 铁律：推理块逐字回传（含签名），永不可优化——丢掉它，
/// 多轮 tool use 就断了，严格的上游还会因为「回合里的推理块被剥掉」直接拒绝这一轮。
/// </summary>
public static class ClaudeResponseMapper
{
    public static ModelReply Map(Message response)
    {
        // ⚠️ 必须用 .Raw()，不能用 .ToString()。
        // StopReason 是 ApiEnum<string, StopReason>，它的 ToString() 返回**带引号**的值
        // （实测 `.ToString()` → `"refusal"`，`.Raw()` → `refusal`）——
        // 用它比较会让拒答判断**永远为 false**，拒答被当成正常空回复处理。
        var stopReason = response.StopReason?.Raw() ?? "unknown";
        var refused = stopReason == "refusal";

        // ⚠️ 拒答时 content 可能是空数组，Select 天然容忍，不要提前索引。
        // ⚠️ 只跳「认不出的块」，不跳「不想渲染的块」——推理块的 Text 只是摘要，
        // 但整块必须留下来。
        var blocks = response.Content
            .Select(b => b.Value)
            .Select(b => b switch
            {
                ThinkingBlock t => (CanonicalBlock)new CanonicalBlock.ReasoningBlock(t.Signature, t.Thinking),
                TextBlock t => new CanonicalBlock.TextBlock(t.Text),
                _ => null,
            })
            .Where(b => b is not null)
            .Select(b => b!)
            .ToList();

        return new ModelReply
        {
            Blocks = blocks,
            StopReason = stopReason,
            Refused = refused,
        };
    }
}
