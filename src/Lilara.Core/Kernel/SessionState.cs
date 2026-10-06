namespace Lilara.Core.Kernel;

/// <summary>
/// 会话状态的取值。⚠️ **这是「磁盘格式」的一部分，不是「代码里的一个枚举」**——
/// 落盘与线上都用**字符串字面量**，两处都有逐字节的守卫钉着：
/// <c>DiskFormatGoldenTests</c> 钉 <c>"status":"Idle"</c> 那串字节，
/// <c>CoreHostTests</c> / <c>InterruptTests</c> 钉线上 <c>session.state</c> 的取值。
/// ⚠️ 所以**别把它换成 C# 枚举**：<c>JsonStringEnumConverter(camelCase)</c> 会写成 <c>"idle"</c>，
/// 同时打破磁盘与线上两边的契约。
///
/// <para>⚠️ <b>磁盘合法取值与内存合法取值不是同一个集合，而这个区别只在两处生效。</b>
/// 今天磁盘上出现过的只有 <see cref="Idle"/> + <see cref="Running"/>，
/// 其中 <b><see cref="Running"/> 只写内存、从不落盘</b>——
/// 「磁盘从不存 Running」这句话被 <c>WireEvent</c> / <c>CommandDispatcher</c> /
/// <c>CoreHost</c> / <c>SessionQueries</c> 四处断言，是**快照端点在步进行中必须回落内存**那条设计的理由。</para>
///
/// <para>⚠️ <b>将来接上 <c>WaitingForInput</c> / <c>WaitingExternal</c> / <c>Blocked</c> 时，
/// 判据是「它们算不算磁盘合法的取值」，不是「照抄今天的做法」。</b>
/// 写整份 <c>State</c> 的那条路（任何一次改轴、任何一次收尾）都要按那个判据决定钉哪个值——
/// 钉错了的表现是：进程在步中被 kill，重启后 <c>Load</c> 读到它，
/// 快照如实回一个「没有任何步在跑」的状态，而它永远等不到 <c>step.end</c>。</para>
/// </summary>
public static class SessionStatus
{
    /// <summary>空闲。**唯一**的磁盘合法取值。</summary>
    public const string Idle = "Idle";

    /// <summary>
    /// 正在跑。**只写内存、从不落盘**——见类型注释。
    /// </summary>
    public const string Running = "Running";
}

// 每步原子重写。
public sealed record SessionState
{
    /// <summary>
    /// 磁盘格式版本。语义同 <see cref="SessionSpec.Version"/>。
    /// ⚠️ **它不是「有没有东西变了」的判据**——`status` / `turn` / `axes` 各自表达那个。
    /// 它只表达「这份文件是按哪一版格式写的」。
    /// </summary>
    public int Version { get; init; } = Json.FormatVersion;

    public required string SessionId { get; init; }

    // 取值见 `SessionStatus`：Idle / Running / WaitingForInput / WaitingExternal / Blocked。
    public required string Status { get; init; }

    public required int Turn { get; init; }

    // 待办集合 = 消息队列，进 state.json，崩溃重启后不丢。
    // 最小闭环是交互式的（入队即启动），队列表恒为空。
    public required IReadOnlyList<string> Pending { get; init; }

    // 行为控制轴的当前值。
    //
    // ⚠️ **可空今天已经是「历史遗留」，不是设计。** 它当初可空的理由是老 `state.json` 没有这个键；
    // 有了格式版本之后，那条理由由**版本**承担了（缺字段 = 一份老文件，`CompleteAxes` 按
    // `Axes.Defaults()` 逐键补齐）。`SessionHandle.Load` / `Create` 出来的那份**保证非空**
    // ⇒ 读取点的那些 `Axes!` 是**契约**（能拿到 null 的只有手工 `new SessionState`，那不是运行时路径）。
    //
    // ⚠️ **收紧成非空要连线上一起改**：`GET /sessions/{id}/state` 把 `State` **原样序列化**交出去
    //（不是逐字段手选），而 `ControlServerTests` 有键集守卫钉着那个形状 ⇒ 类型一改，
    // 那条守卫会红（这正是它存在的意义）。那是一次**线上 schema 变更**，
    // 与磁盘那半是两件事 ⇒ 别在这里顺手做半截。
    public IReadOnlyDictionary<string, string>? Axes { get; init; }
}
