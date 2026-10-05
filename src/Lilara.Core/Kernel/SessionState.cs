namespace Lilara.Core.Kernel;

// 每步原子重写。
public sealed record SessionState
{
    public required string SessionId { get; init; }

    // Idle / Running / WaitingForInput / WaitingExternal / Blocked。
    public required string Status { get; init; }

    public required int Turn { get; init; }

    // 待办集合 = 消息队列，进 state.json，崩溃重启后不丢。
    // 最小闭环是交互式的（入队即启动），队列表恒为空。
    public required IReadOnlyList<string> Pending { get; init; }

    // 行为控制轴的当前值。可空是刻意的：老 state.json 没这个键，读出来是 null，
    // 而 WhenWritingNull 让「没设轴」的序列化逐字节不变。只有 SessionHandle.Load/Create
    // 出来的那份保证非空，别在别处假设它非空。轴进的是状态，不是上下文。
    public IReadOnlyDictionary<string, string>? Axes { get; init; }
}
