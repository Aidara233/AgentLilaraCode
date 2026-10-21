namespace Lilara.Core.Kernel;

/// <summary>
/// 三条协议归一化后的 token 用量。
/// 明细是可空的：null 表示协议未报告，不等于 0。
///
/// <para>⚠️ <b>口径（`08` §3.1）：<see cref="InputTokens"/> 是「未被缓存覆盖的输入」，
/// <see cref="CacheReadTokens"/>/<see cref="CacheWriteTokens"/> 是并列明细，不是它的子集。</b>
/// 两族协议原本差在这里（OpenAI 报全量、Anthropic 报未命中），归一在各自的映射层完成
/// （见 <c>Protocol/UsageJson</c>）。⇒ 全部输入 = <c>InputTokens + CacheReadTokens</c>
/// （忽略写入），命中率 = <c>CacheReadTokens / (InputTokens + CacheReadTokens)</c>。</para>
/// </summary>
public sealed record TokenUsage
{
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public long? TotalTokens { get; init; }
    public long? CacheReadTokens { get; init; }
    public long? CacheWriteTokens { get; init; }
    public long? ReasoningTokens { get; init; }

    /// <summary>受限的原始 usage 摘要；不保存完整响应。</summary>
    public string? RawJson { get; init; }
}
