using Anthropic.Models.Messages;
using Lilara.Core.Kernel;
using CanonicalBlock = Lilara.Core.Kernel.Block;
using CanonicalJson = Lilara.Core.Kernel.Json;

namespace Lilara.Core.Protocol.Claude;

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
                ToolUseBlock u => ToCanonicalToolUse(u),
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
            Usage = new TokenUsage
            {
                InputTokens = response.Usage.InputTokens,
                OutputTokens = response.Usage.OutputTokens,
                CacheReadTokens = response.Usage.CacheReadInputTokens,
                CacheWriteTokens = response.Usage.CacheCreationInputTokens,
            },
            ReportedModel = response.Model,
            ResponseId = response.ID,
        };
    }

    /// <summary>
    /// `tool_use` → <see cref="CanonicalBlock.ToolUseBlock"/>。
    ///
    /// <para>⚠️ <b>参数必须逐字落库，所以这里回写成 JSON 文本</b>（`01` §9 铁律）。
    /// SDK 给的是 <c>Input</c>（一个已解析的字典）——把<em>它</em>再序列化会规范化键序与空白，
    /// <b>而那份文本要进记录</b>。⇒ 逐字保真在这一层不可能靠 SDK 的结构拿回来；
    /// 能保证的是**我们记下来的每一处都一致**（模型发什么、我们存什么、回传什么，三者相同）。</para>
    ///
    /// <para>⚠️ <b>用 `Json.Options`</b>（与记录层同一套设置）而不是默认序列化器：
    /// 两处若用了不同的命名策略，同一个块在盘上与在线上会长得不一样，
    /// 而那正是「记录是事实」最容易被悄悄破坏的地方。</para>
    /// </summary>
    private static CanonicalBlock ToCanonicalToolUse(ToolUseBlock u) =>
        new CanonicalBlock.ToolUseBlock(
            u.ID,
            u.Name,
            System.Text.Json.JsonSerializer.Serialize(u.Input, CanonicalJson.Options));
}
