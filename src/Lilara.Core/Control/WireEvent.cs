using System.Text.Json.Serialization;
using Lilara.Core.Canonical;

namespace Lilara.Core.Control;

/// <summary>
/// 控制面三层里的**事件**层（`03` §2/§4）。
///
/// <para>⚠️ **这些类型刻意不继承 `TimelineEvent` / `Block`**，而是**包含**块列表。理由（G4）：
/// 多态的判别式表取自**静态声明类型**——`Serialize&lt;TimelineEvent&gt;(wireEvt)` 只会拿到
/// <c>TimelineEvent</c> 自己的判别式表（只有 <c>"message"</c>），派生类上的
/// <c>[JsonDerivedType]</c> **根本不会被读**。继承会静默产出一个错形状。</para>
///
/// <para>⚠️ 判别式用 `"kind"`，与既有 wire 形状一致。它与**嵌套 Block 的** `kind`
/// 处在不同层级，不会撞（撞名只发生在**同一类型**的成员之间——
/// `ReasoningBlock` 那次就是这么炸的）。</para>
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(SessionCreated), "session.created")]
[JsonDerivedType(typeof(StepBegin), "session.step.begin")]
[JsonDerivedType(typeof(StepEnd), "session.step.end")]
[JsonDerivedType(typeof(MessageAppended), "message.appended")]
[JsonDerivedType(typeof(StateChanged), "session.state")]
[JsonDerivedType(typeof(AxisChanged), "axis.changed")]
[JsonDerivedType(typeof(ClaimAcquired), "claim.acquired")]
[JsonDerivedType(typeof(ClaimReleased), "claim.released")]
[JsonDerivedType(typeof(Error), "error")]
public abstract record WireEvent
{
    public sealed record SessionCreated(string SessionId, string ProjectKey) : WireEvent;

    /// <summary>
    /// ⚠️ **「在跑」的信号源是它，不是 <see cref="StateChanged"/>。**
    /// `Stepper` 在步开始时只把 `Running` 写进**内存**（刻意不落盘），到步末才写 `state.json`。
    /// 所以 `session.state` 是**磁盘的镜像，而磁盘从不存 Running** ⇒
    /// 头拿它判「在跑」永远判不出来（**连轮询也判不出来**）。
    /// </summary>
    public sealed record StepBegin(string SessionId, int Turn, DateTimeOffset At) : WireEvent;

    /// <summary>
    /// ⚠️ 它**不能**挂在「模型消息落盘之后」——**拒答路径不写模型消息，但 `Turn` 照样递增**。
    /// 挂错地方会让「拒答过一次」在头上看起来像「什么都没发生」。
    /// </summary>
    public sealed record StepEnd(string SessionId, int Turn, bool Refused, string StopReason) : WireEvent;

    /// <summary>
    /// ⚠️ 会**重复**：用户消息在 `session.send` 时就发一次（那时它刚落盘），
    /// 而步结束时若模型有输出会再发一次。头必须**按 <c>(sessionId, at, from, 内容)</c> 幂等**，
    /// 或者干脆只信「一次步内最多两条」这个形状。⚠️ 这条是**当前实现的可观测行为**，
    /// 不是协议承诺——协议只承诺「有序」。
    /// </summary>
    public sealed record MessageAppended(
        string SessionId, string From, IReadOnlyList<Block> Blocks, DateTimeOffset At) : WireEvent;

    public sealed record StateChanged(string SessionId, string Status, int Turn) : WireEvent;

    /// <summary>
    /// 一条轴变了（`03` §4 / `01` §3.5）。
    /// ⚠️ **只在真的迁移时发**——重复设同一个值是 no-op，不发事件。
    /// 头据此更新显示；若是 `safety` 或 `permission`，还要重算跨轴告警。
    /// </summary>
    public sealed record AxisChanged(string SessionId, string Axis, string Value) : WireEvent;

    /// <summary>⚠️ 发到**项目流**（`03` §5）——「谁在动哪些文件」跨会话才有意义。</summary>
    public sealed record ClaimAcquired(string SessionId, string Path) : WireEvent;

    public sealed record ClaimReleased(string SessionId, string Path) : WireEvent;

    /// <summary>
    /// ⚠️ **业务失败走这里，不走回执**（`03` §3）：模型 400 / 上游 503 / 提供商失联
    /// 都是「命令没错，但这次没成」。回执里的 `rejected` 只用于「**命令本身不合法**」。
    /// </summary>
    public sealed record Error(string SessionId, string Message) : WireEvent;

    /// <summary>
    /// 稳定序列化入口。**参数类型必须是 <see cref="WireEvent"/>（基类）。**
    /// ⚠️ 与 <see cref="Records.TimelineEvent.StableSerialize"/> 同一个陷阱：
    /// 泛型版会在静态类型推断成子类时**静默丢掉判别式**，而头直到解析才发现。
    /// </summary>
    public static string StableSerialize(WireEvent value) =>
        System.Text.Json.JsonSerializer.Serialize(value, WireJson.Options);
}

/// <summary>
/// 头上的信封（`03` §2 的三层）。⚠️ **它与 <see cref="WireEvent"/> 是两层不同的东西**：
/// 这是「帧」，那是「载荷」。分开是为了让 <c>seq</c> 与 <c>topic</c> 这些**流**的属性
/// 独立于**领域**的属性——加一个事件类型不该动信封的形状。
///
/// <para>⚠️ **它也必须有多态配置**：忘了会静默序列化成 `{}`（只有基类的属性，而基类没有属性）。
/// 这个洞是测试抓出来的——控制器第一版漏了这三个特性，三条测试同时红成 `"{}"`。</para>
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(Event), "event")]
[JsonDerivedType(typeof(Ack), "ack")]
[JsonDerivedType(typeof(ResyncRequired), "resync_required")]
public abstract record WireFrame
{
    /// <summary>核心 → 头：一条事件。`seq` **流内单调增**（`03` §4）。</summary>
    public sealed record Event(string Topic, long Seq, WireEvent Payload) : WireFrame;

    /// <summary>
    /// 核心 → 头：回执（`03` §3）。⚠️ **只有两类**：`ack` / `rejected`。
    /// 命令 id 由头生成，核心原样带回——头靠它把「我发过的那条」对上。
    /// </summary>
    public sealed record Ack(string Id, bool Accepted, string? SessionId, string? Reason, string? Note) : WireFrame;

    /// <summary>
    /// 核心 → 头：**该流的补发起点已经不在缓冲里了**，请拉快照重建。
    /// ⚠️ 这是**唯一**的降级路径（`03` §7）——别发明「部分合并」的聪明逻辑，
    /// 那是另一套状态机，且必然与快照对不齐。
    /// </summary>
    public sealed record ResyncRequired(string Topic) : WireFrame;
}
