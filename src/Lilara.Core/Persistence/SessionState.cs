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

    /// <summary>
    /// 行为控制轴的当前值（`01` §3.5）。
    ///
    /// <para>⚠️ **可空是刻意的，而且它是磁盘迁移的安全阀**：老 `state.json` 里没有这个键，
    /// 读出来是 <c>null</c>；`Json.Options` 的 <c>WhenWritingNull</c> 让「没设轴」的序列化
    /// **逐字节不变**（有金样钉着，实测：60 字节 / SHA256 EEF69F93…0C0C 前后同一个值）。</para>
    ///
    /// <para>⚠️ **何时非空，说清楚**：只有
    /// <see cref="Lilara.Core.Session.SessionHandle.Load"/>（把磁盘上的 null 补成默认值）
    /// 与 <see cref="Lilara.Core.Session.SessionHandle.Create"/>（建会话时播种）出来的那份
    /// 保证非空。**别在别处假设它非空**——直接 <c>new SessionState</c> 出来的、或者
    /// 直接 <c>new SessionHandle</c> 塞进去的，这个键就是 null。</para>
    ///
    /// <para>⚠️ **轴进的是状态，不是上下文**——`01` §3.5：`effort` 完全不进浮动。
    /// 这里存的是「用户设了什么」，怎么用是各轴自己的事。</para>
    /// </summary>
    public IReadOnlyDictionary<string, string>? Axes { get; init; }
}
