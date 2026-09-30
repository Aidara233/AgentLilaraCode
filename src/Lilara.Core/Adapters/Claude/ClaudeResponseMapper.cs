using Anthropic.Models.Messages;
using CanonicalBlock = Lilara.Core.Canonical.Block;

namespace Lilara.Core.Adapters.Claude;

/// <summary>
/// Claude Messages 响应 → canonical。
/// ⚠️ 纯函数。只取文本块：最小闭环没有工具，推理块**永不进上下文**
/// （01 §11.5：推理块逐字回传，永不可优化——那是多轮 tool use 的活儿，本计划不做）。
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

        // ⚠️ 拒答时 content 可能是空数组，OfType 天然容忍，不要提前索引
        var blocks = response.Content
            .Select(b => b.Value)
            .OfType<TextBlock>()
            .Select(t => (CanonicalBlock)new CanonicalBlock.TextBlock(t.Text))
            .ToList();

        return new ModelReply
        {
            Blocks = blocks,
            StopReason = stopReason,
            Refused = refused,
        };
    }
}
