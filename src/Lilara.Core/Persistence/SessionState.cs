namespace Lilara.Core.Persistence;

/// <summary>每步原子重写（01 §8）。</summary>
public sealed record SessionState
{
    public required string SessionId { get; init; }

    /// <summary>Idle / Running / WaitingForInput / WaitingExternal / Blocked（01 §5）。</summary>
    public required string Status { get; init; }

    /// <summary>已完成的步数。</summary>
    public required int Turn { get; init; }

    /// <summary>
    /// 待办集合 = 消息队列（01 §6.1）。进 state.json，崩溃重启后待办不丢。
    /// ⚠️ 最小闭环是交互式的（入队即启动），队列表恒为空。
    /// </summary>
    public required IReadOnlyList<string> Pending { get; init; }
}
