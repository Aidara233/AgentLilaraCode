using Anthropic.Models.Messages;
using CanonicalBlock = Lilara.Core.Canonical.Block;

namespace Lilara.Core.Adapters.Claude;

// Claude Messages 响应 → canonical。纯函数：文本块与推理块都要取，保持原顺序（推理在前、文本在后）；
// 推理块逐字回传（含签名）——丢掉它多轮 tool use 就断了。
public static class ClaudeResponseMapper
{
    public static ModelReply Map(Message response)
    {
        // 必须用 .Raw()：StopReason 的 ToString() 返回带引号的值（`"refusal"` 而非 `refusal`），比较会永远为 false。
        var stopReason = response.StopReason?.Raw() ?? "unknown";
        var refused = stopReason == "refusal";

        // 拒答时 content 可能是空数组，Select 天然容忍，不要提前索引。
        // 只跳认不出的块，不跳不想渲染的块——推理块的 Text 只是摘要，但整块必须留下来。
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
