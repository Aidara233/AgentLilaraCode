using System.Text.Json.Serialization;
using Lilara.Core.Canonical;

namespace Lilara.Core.Control;

// 控制面三层里的「事件」层。刻意不继承 TimelineEvent / Block（而是包含块列表）：
// 多态判别式表取自静态声明类型，继承会让派生类的判别式静默丢失、产出错形状。
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

    // 发到项目流：谁在动哪些文件，跨会话才有意义。
    public sealed record ClaimAcquired(string SessionId, string Path) : WireEvent;

    public sealed record ClaimReleased(string SessionId, string Path) : WireEvent;

    // 业务失败走这里（模型 400 / 上游 503）；回执里的 rejected 只用于「命令本身不合法」。
    public sealed record Error(string SessionId, string Message) : WireEvent;

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
