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

    /// <summary>
    /// **等用户回答**（`01` §5 / §3.8）。
    ///
    /// <para>⚠️ <b>它是**磁盘合法**取值，而且这一点是刻意的</b>——类型注释里那条判据在这里
    /// 得到的答案是「算」：一个挂在等待里的会话，它的 `state.json` 里那行状态
    /// **本来就该是它**。反过来（写 `Idle`）会让进程在等待中被 kill 之后，
    /// 重启读到 `Idle` ⇒ 快照如实回一个「没有任何步在跑」⇒ 而它**真的还在等**
    /// （那次调用一条结果都没落，那个 `tool_use` 悬着）。</para>
    ///
    /// <para>⚠️ **注意它与 <see cref="Running"/> 的形状相反**：`Running` 只写内存、
    /// 从不落盘（因为「这一刻有一步在跑」是**进程事实**，重启后它必然为假）；
    /// 而「有一个问题等着被回答」是**盘面事实**，重启后照样成立。</para>
    /// </summary>
    public const string WaitingForInput = "WaitingForInput";
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

    /// <summary>
    /// **等待中的那次询问**（`01` §3.8）。⚠️ 非 null ⟺ <see cref="Status"/> 是
    /// <see cref="SessionStatus.WaitingForInput"/>——两份事实、一处判断（见 <see cref="PausedAsk"/> 注释）。
    ///
    /// <para>⚠️ <b>可空 = 「没在等」</b>，与 <see cref="Axes"/> 那条「可空是历史遗留」不同：
    /// 这里可空是**语义**。老文件缺这个键 ⇒ `WhenWritingNull` 让盘上键不出现 ⇒ **逐字节不变**。</para>
    /// </summary>
    public PausedAsk? Ask { get; init; }
}

/// <summary>
/// **一次挂起的询问**（`01` §3.8）。
///
/// <para>⚠️ <b>它<b>不</b>落盘「问题原文」</b>——刻意如此。提问是一次**渲染产物**
/// （同 `01` §3.7 的框架），而挂起状态只需要知道「在等哪一次调用、等多久」。
///
/// <para>⚠️ <b>但「问题没留住」这条代价<b>已经不成立</b>了</b>（2026-10-08 实测后改）：
/// 问题原文在盘上**本来就有**——它在被挂起的那条 `tool_use` 的**参数**里
/// （那条模型消息必须落盘，否则答完补的 `tool_result` 会悬空）。
/// ⇒ `AskTool.ItemsOf` 从落盘字节**重新解**一遍，于是**重启之后照样拿得到问题**。
/// 内存那半（`SessionHandle.AwaitingInput`）从此只是缓存，不是真相来源。</para>
///
/// <para>⚠️ <b>教训要留着</b>：我原先在这里写着「丢了就伪造一句」，
/// 而那把一个**可以避免**的损失说成了设计代价——直到真模型跑一遍才发现
/// 用户在页面上看到的是一张**空卡**。⇒ 「认下的代价」也要有人验一次。</para>
///
/// <para>⚠️ <b>另一条边界</b>：用户答完之后，我们会**重新问一次**工具要它那批问题
/// （见 `SessionHandle.AwaitingInput` 的注释）。那要求工具是**纯的**——`00` §5 铁律 1 早就
/// 要求了（「同一个 `ToolCall` 配上同一个上下文，跑几次都一样」）⇒ 对一个守规矩的工具，
/// 重问得到的是**同一批问题**。不守规矩的工具会得到一张**对不上号的卡**，
/// 而那不会静默：答案按 id 归位，对不上就是一句「这一项没有被回答」。</para>
/// </summary>
/// <param name="AskId">稳定 id。⚠️ 它与下面那个 <paramref name="CallId"/> **不是一回事**（见 `01` 的 id 分工）。</param>
/// <param name="Kind">授权 / 问意见。</param>
/// <param name="CallId">被挂起的那个调用的**协议 id**（`tool_use.callId`）。回答时要靠它配对。</param>
/// <param name="ToolName">哪个工具的调用。</param>
/// <param name="ArgsHash">
/// 参数原文的哈希（`ApprovalStore.HashArgs`）。⚠️ **两个用途**：① 记忆的键；
/// ② 用户**改写**过参数之后，用它和原来那个比，就知道要不要标 `ArgumentsAmended`。
/// </param>
/// <param name="DeadlineAt">
/// 超时时刻（绝对）。⚠️ <b><c>null</c> = 不超时</b>（`spec.askTimeout` 也是 null）。
/// 看它的人**只有一个**：任何一次试图恢复这个会话的动作（`approval.respond` / `session.send`）。
/// ⇒ <b>超时不是定时器，是盘面自证</b>——见 `01` §3.8。
/// </param>
public sealed record PausedAsk(
    string AskId,
    AskKind Kind,
    string CallId,
    string ToolName,
    string ArgsHash,
    DateTimeOffset? DeadlineAt);
