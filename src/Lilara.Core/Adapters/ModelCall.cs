using Lilara.Core.Canonical;
using Lilara.Core.Records;

namespace Lilara.Core.Adapters;

/// <summary>
/// 一次模型调用要的全部东西。核心发全量 canonical（00 §6），
/// 适配器负责翻译成具体协议。
/// </summary>
public sealed record ModelRequest
{
    public required string ModelId { get; init; }
    public required int MaxTokens { get; init; }

    /// <summary>思考强度轴，映射到协议参数——**绝不进上下文**（01 §3.5）。</summary>
    public required string Effort { get; init; }

    /// <summary>
    /// 系统块。⚠️ 最小闭环恒为空——核心不带提示词（04 §1.3）。
    /// 「块数恒定」说的是「最多各 1 块」，不是非有不可。
    /// </summary>
    public required IReadOnlyList<Block> SystemBlocks { get; init; }

    public required IReadOnlyList<TimelineEvent> Timeline { get; init; }
}

/// <summary>模型回话的产物。</summary>
public sealed record ModelReply
{
    public required IReadOnlyList<Block> Blocks { get; init; }

    /// <summary>协议原样透传的停止原因，用于诊断。</summary>
    public required string StopReason { get; init; }

    /// <summary>
    /// ⚠️ 安全分类器拒答（Review Focus #2）。拒答时 Blocks 通常为空，
    /// 核心必须当**一等结果**处理，不能当成「模型说了空话」。
    /// </summary>
    public required bool Refused { get; init; }
}

/// <summary>调用失败，带一句人话。</summary>
public sealed class ModelCallFailedException(string message, Exception? inner = null)
    : Exception(message, inner);
