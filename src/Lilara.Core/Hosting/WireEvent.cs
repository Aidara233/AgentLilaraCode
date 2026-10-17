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
[JsonDerivedType(typeof(ReleaseRecorded), "release.recorded")]
[JsonDerivedType(typeof(ApprovalRequested), "approval.requested")]
[JsonDerivedType(typeof(ApprovalResolved), "approval.resolved")]
[JsonDerivedType(typeof(Error), "error")]
[JsonDerivedType(typeof(UsageUpdated), "usage.updated")]
[JsonDerivedType(typeof(CompactionCompleted), "session.compaction.completed")]
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
        string SessionId, string From, IReadOnlyList<Block> Blocks, DateTimeOffset At) : WireEvent
    {
        // 持久消息对应的 TimelineEvent.Id；临时消息可以为空。
        public string? HistoryId { get; init; }
    }

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

    /// <summary>
    /// **模型画了一条线**（`release`，`01` §12.4）——头据此重拉历史并画一条分隔线。
    ///
    /// <para>⚠️ <b>为什么不复用 <see cref="SessionReclaimed"/></b>：那条讲的是「回收算完了」
    /// （`session.gc` 的观察口），而线是**另一件事**——线落下时回收还没发生，
    /// 发生的是「接下来会收」。把两者并成一个事件，头就分不清「为什么重拉」，
    /// 而两者的载荷与措辞本来就不同（那条带一句回执）。</para>
    ///
    /// <para>⚠️ <b>线本身仍然不进模型上下文</b>：`TimelineRender.IsRenderable` 照样挡它。
    /// 这条事件只是给**人**的出口（`01` §14 #32）。</para>
    /// <para>⚠️ <b>载荷是线的作用域与可选摘要</b>，不是整条 `ReleaseEvent`——
    /// 头的渲染只需要这两样，交整条记录会把落盘格式泄漏到线上。</para>
    /// </summary>
    public sealed record ReleaseRecorded(
        string SessionId, string Id, IReadOnlyList<string> Scope, string? Summary, DateTimeOffset At) : WireEvent;

    /// <summary>
    /// **核心在问用户**（`01` §3.8）。⚠️ <b>它同时投两条流</b>：
    /// <c>approvals</c>（头最需要的那条，`03` §5）与**会话流**（那个会话自己的订阅者）。
    ///
    /// <para>⚠️ <b>为什么两条都要</b>：审批放在独立流上，是为了**不混在会话事件里淹掉**
    /// （`03` §5 定的）；而只投 `approvals` 会让一个**只订了自己会话**的客户端
    /// （比如将来的子会话 / 控制台）看不见那个问题。⇒ 两条是**两个视角**，不是两份收件人。</para>
    ///
    /// <para>⚠️ <b>`deadlineAt` 可空 = 不超时</b>（`spec.askTimeout` 是 null）。
    /// 头拿它画倒计时——⚠️ 但**核心不靠它超时**（那是盘面自证，见 `01` §3.8）：
    /// 头只是提前告诉用户「这个会过期」。</para>
    /// </summary>
    /// <param name="Ask">那批问题。⚠️ **它是内存里那份**——重启之后原问题丢了，
    /// 所以**重启后不会有这条事件被补发**（没有生产者：那次挂起发生在进程死之前）。
    /// 头要靠 `GET /sessions` 的 `ask` 那一格看到它。</param>
    /// <param name="AskKind">
    /// ⚠️ <b>字段名原名是 <c>Kind</c>，而那是一个**会掀翻整个进程的**名字</b>——
    /// 实测（2026-10-08）：`WireEvent` 的多态判别式就叫 <c>kind</c>（camelCase 之后），
    /// 于是 STJ 抛 <c>PropertyConflictsWithMetadataPropertyName</c>，
    /// 而那**不是「这条事件序列化失败」，是 `WireJson.Options` 整个类型解析失败** ⇒
    /// **全进程所有帧的序列化一起炸**，症状是「随便发什么帧都断连」。
    /// 名字躲开它是唯一解法（不像 `ApprovalResolved.Outcome` 那样只是「语义上该避开」）。</param>
    /// <param name="AskKindName">
    /// ⚠️ <b>参数名叫 `AskKindName`、类型是枚举——那个名字是历史</b>（2026-10-11 改）。
    /// 它**曾经是 `string`**，调用点手写 `asked.Kind.ToString()` ⇒ 线上是 <c>"Permission"</c>
    /// （PascalCase），而**同一份数据在 `/sessions/{id}/state` 里是 <c>"permission"</c>**
    /// （走 `WireJson.Options` 的 `JsonStringEnumConverter(camelCase)`）⇒ **同一条响应里两种拼法**。
    /// 头因此把 `"？授权"` 徽标画成了 `"？提问"`（`app.js` 拿 PascalCase 去比）。
    ///
    /// <para>⇒ 改成**让序列化器接管枚举**：线上的拼法此后只有一个来源，手写 `ToString()`
    /// 那条路（它绕开了 `WireJson.Options`）不再存在。⚠️ 判据是「线上一律 camelCase」——
    /// 同 `message.appended` 的 `"event"` / `"message"` / `"toolUse"` 等**每一个线上枚举值**。</para>
    /// </param>
    public sealed record ApprovalRequested(
        string SessionId, string AskId, AskKind AskKind, IReadOnlyList<AskItem> Items,
        IReadOnlyList<AskMemory> Remember, DateTimeOffset? DeadlineAt) : WireEvent;

    /// <summary>**那次询问结束了**（答了 / 作废了 / 超时了）。同投两条流。</summary>
    /// <param name="Outcome">`submit` / `cancel` / `expired`。</param>
    public sealed record ApprovalResolved(string SessionId, string AskId, string Outcome) : WireEvent;

    /// <summary>一次会话模型调用的 token 账本已经更新，头可据此刷新实时统计。</summary>
    public sealed record UsageUpdated(
        string SessionId, Storage.UsageCall Call, Storage.UsageSummary Summary) : WireEvent;

    public sealed record CompactionCompleted(
        string SessionId, string CheckpointId, string CoveredThroughEventId, long? SummaryTokens) : WireEvent;

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
