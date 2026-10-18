namespace Lilara.Core.Kernel;

/// <summary>
/// 三条协议归一化后的 token 用量。
/// 明细是可空的：null 表示协议未报告，不等于 0；明细若已包含在总量中不得再次相加。
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
