using System.Text.Json.Serialization;
using Lilara.Core.Kernel;

namespace Lilara.Core.Hosting;

// 控制面三层里的「事件」层。刻意不继承 TimelineEvent / Block（而是包含块列表）：
// 多态判别式表取自静态声明类型，继承会让派生类的判别式静默丢失、产出错形状。
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(SessionCreated), "session.created")]
[JsonDerivedType(typeof(StepBegin), "session.step.begin")]
[JsonDerivedType(typeof(StepEnd), "session.step.end")]
[JsonDerivedType(typeof(MessageAppended), "message.appended")]
[JsonDerivedType(typeof(StateChanged), "session.state")]
[JsonDerivedType(typeof(AxisChanged), "axis.changed")]
[JsonDerivedType(typeof(SpecChanged), "spec.changed")]
[JsonDerivedType(typeof(ClaimAcquired), "claim.acquired")]
[JsonDerivedType(typeof(ClaimReleased), "claim.released")]
[JsonDerivedType(typeof(SessionDeactivated), "session.deactivated")]
[JsonDerivedType(typeof(SessionArchived), "session.archived")]
[JsonDerivedType(typeof(SessionDeleted), "session.deleted")]
[JsonDerivedType(typeof(SessionReclaimed), "session.reclaimed")]
[JsonDerivedType(typeof(Error), "error")]
public abstract record WireEvent
{
    public sealed record SessionCreated(string SessionId, string ProjectKey) : WireEvent;

    // 「在跑」的信号源是它，不是 StateChanged——后者是磁盘的镜像，而 Running 只进内存、不落盘。
    public sealed record StepBegin(string SessionId, int Turn, DateTimeOffset At) : WireEvent;

    // 拒答路径不写模型消息，但 Turn 照样递增 ⇒ 它不能挂在「模型消息落盘之后」。
    public sealed record StepEnd(string SessionId, int Turn, bool Refused, string StopReason) : WireEvent;

    // 会重复：用户消息在 session.send 时发一次，步末若模型有输出再发一次 ⇒ 头必须幂等。
    // 这是当前实现的可观测行为，不是协议承诺（协议只承诺「有序」）。
    public sealed record MessageAppended(
        string SessionId, string From, IReadOnlyList<Block> Blocks, DateTimeOffset At) : WireEvent;

    public sealed record StateChanged(string SessionId, string Status, int Turn) : WireEvent;

    // 只在真的迁移时发：重复设同一个值是 no-op，不发事件。头据此更新显示。
    public sealed record AxisChanged(string SessionId, string Axis, string Value) : WireEvent;

    /// <summary>
    /// `session.configure` 改了会话配置（换绑供应商 / 换模型）。
    ///
    /// <para>⚠️ <b>载荷只带**会变的那两块**，不带整个 `SessionSpec`</b>——带整份会让头以为
    /// 「别的那几项也可能变」，去写它根本不该有的更新逻辑。
    /// `SessionId` / `ProjectKey` / `WorkingDirectory` / `CreatedAt` 出生即定，永远不变。</para>
    ///
    /// <para>⚠️ <b>这里原先只带 `Model`，注释写着「spec 里能变的只有这一块」</b>——
    /// 那句在 <see cref="SessionSpec.SystemPromptPreset"/> 加进来之后**不再成立**
    /// （2026-10-07）。⚠️ 但今天**没有任何命令能改预设**（它只在 `session.create` 那一刻定），
    /// 所以第二个字段此刻恒等于原值；带上它是为了让**形状与事实一致**，
    /// 而不是等那条命令出现时再来改这个事件（那时改就晚了——头的分派已经按旧形状写好了）。</para>
    ///
    /// <para>⚠️ 与 `axis.changed` 同一条约定：**只在真的变了才发**（同样值不发）。</para>
    /// </summary>
    public sealed record SpecChanged(
        string SessionId, ModelSelection Model, string? SystemPromptPreset) : WireEvent;

    // 发到项目流：谁在动哪些文件，跨会话才有意义。
    public sealed record ClaimAcquired(string SessionId, string Path) : WireEvent;

    public sealed record ClaimReleased(string SessionId, string Path) : WireEvent;

    // 业务失败走这里（模型 400 / 上游 503）；回执里的 rejected 只用于「命令本身不合法」。
    public sealed record Error(string SessionId, string Message) : WireEvent;

    // ---- 销毁面（三条都是「会话没了/变了」，不是「会话里发生了什么」）----
    //
    // ⚠️ 载荷都是 `(SessionId, ProjectKey)`，与上面那条 `SessionCreated` 对称——
    // **不是** `SessionSpec` 那种「别的东西也可能变」的形状（那是 `spec.changed` 的活）。
    //
    // ⚠️ 三条的**投递范围不一样**，理由见各自的注释。

    /// <summary>
    /// 会话被摘出内存（`session.deactivate`）。**磁盘上什么都没变**——这正是它与
    /// <see cref="SessionArchived"/> 的分界：那是持久事实，这只是这一份进程的内存账。
    /// </summary>
    /// <remarks>
    /// ⚠️ 发在**会话流 + 项目流**：会话还在，头可能正开着它。
    /// ⚠️ 但**头对它的处理是「什么都不做」**——头的界面完全不显示「激活」这件事
    /// （快照端点会自己回落磁盘）。发它是因为**另一个客户端**（控制台）也在盯这条流，
    /// 而「摘掉了」对它是可观测的。别因为头不用就把它删掉。
    /// </remarks>
    public sealed record SessionDeactivated(string SessionId, string ProjectKey) : WireEvent;

    /// <summary>
    /// 归档态变了（`session.archive`）。<c>ArchivedAt</c> 为 <c>null</c> = **取消归档**。
    /// 头据此折叠/展开左栏那一行。
    /// </summary>
    /// <remarks>
    /// ⚠️ 带 `ArchivedAt` 而不是一个 bool：**取消归档**与「从没归档过」在 bool 下是同一个值，
    /// 而它们是两回事（后者不会产生这条事件，前者会）。
    /// </remarks>
    public sealed record SessionArchived(
        string SessionId, string ProjectKey, DateTimeOffset? ArchivedAt) : WireEvent;

    /// <summary>
    /// 会话**被删掉了**（`session.delete`）。目录、记录、状态一起没了。
    /// 头据此把它从列表里拿掉；若它正开着，必须清空中栏。
    /// </summary>
    /// <remarks>
    /// ⚠️ **只发项目流，不发会话流**：会话 topic 上的订阅者**已经无处可投**
    /// ——那条流之后不会再有这个会话的任何事件，钉在上面是泄漏。
    /// （`session.created` 那两份投递里「会话流那份」是既有的死投递，本批**不动**它。）
    /// </remarks>
    public sealed record SessionDeleted(string SessionId, string ProjectKey) : WireEvent;

    /// <summary>
    /// **回收算完了**（`session.gc`）：<paramref name="Reason"/> 就是**那条命令的回执里那句话**
    /// （「回收了 N 条结果」/「没有可回收的结果…」）。
    ///
    /// <remarks>
    /// ⚠️ <b>它必须在场，因为回收<b>不改盘</b></b>（`01` §8.0.2 一）：头看的
    /// `/sessions/{id}/history` 是**核心渲染的形态**，而回收正好换的就是那个形态
    /// ⇒ 不发这条事件，头会一直显示回收之前的（而且完全看不出来）。
    /// ⇒ 头收到它就**重拉一次历史**。
    ///
    /// <para>⚠️ <b>为什么载荷是「那句话」而不是条数</b>（2026-10-06 改，原先是 <c>int Reclaimed</c>）：<b>条数
    /// 到不了用户眼前</b>。回执先到、事件后到，而头处理事件时要 <c>openSession</c> 重拉历史，
    /// 那一步会 <c>timeline.innerHTML = ""</c> —— 把回执刚画上的那行<b>一起抹掉</b>。
    /// 页面于是<b>一个字都不说</b>，与「坏了」分不开。（实测：三次免费帧，三次都是这个序。）</para>
    ///
    /// <para>⇒ 头把这句话交给重拉之后的渲染，<b>在同一处、同一时刻</b>画出来——同一件事只有一个出处，
    /// 不为它单开一条显示路径。⇒ 那句话的<b>唯一出处仍是核心</b>（<see cref="Ack.Accepted.Note"/> 的既定用途，
    /// `provider.test` 就是这么用的），头只是换了个落点。</para>
    ///
    /// <para>⚠️ <b>它不是「重算的请求」</b>：头收到的只是「该重拉了」这个信号，判定仍在核心
    /// （<c>Reclaim.Decide</c> 一处）。</para>
    ///
    /// <para>⚠️ <b>投递范围与会话内的事件一视同仁</b>（会话流）：它讲的是「这个会话的形态变了」。</para>
    /// </remarks>
    /// </summary>
    public sealed record SessionReclaimed(string SessionId, string Reason) : WireEvent;

    // 参数类型必须是 WireEvent（基类）：泛型版在静态类型推断成子类时会静默丢掉判别式。
    public static string StableSerialize(WireEvent value) =>
        System.Text.Json.JsonSerializer.Serialize(value, WireJson.Options);
}

// 头上的信封。它与 WireEvent 是两层不同的东西——这是「帧」，那是「载荷」，
// seq / topic 这类流的属性独立于领域属性。必须有 JsonPolymorphic，否则静默序列化成 {}。
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(Event), "event")]
[JsonDerivedType(typeof(Ack), "ack")]
[JsonDerivedType(typeof(ResyncRequired), "resync_required")]
public abstract record WireFrame
{
    // 核心 → 头：一条事件。seq 流内单调增。
    public sealed record Event(string Topic, long Seq, WireEvent Payload) : WireFrame;

    // 回执：只有 ack / rejected 两类。id 由头生成、核心原样带回，头靠它对上。
    public sealed record Ack(string Id, bool Accepted, string? SessionId, string? Reason, string? Note) : WireFrame;

    // 该流的补发起点已不在缓冲里 ⇒ 请拉快照重建。这是唯一的降级路径，别发明「部分合并」。
    public sealed record ResyncRequired(string Topic) : WireFrame;
}
