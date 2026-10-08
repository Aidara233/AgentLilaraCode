using System.Threading.Channels;

namespace Lilara.Core.Hosting;

/// <summary>一条已分配 <c>seq</c> 的事件。<c>Seq</c> 是**该 topic 内**单调增的。</summary>
public sealed record Published(string Topic, long Seq, WireEvent Payload);

/// <summary>
/// 一个订阅。消费方要**同时**看 <see cref="Reader"/> 与 <see cref="NeedsResync"/>：
/// 通道读完之后若 <c>NeedsResync</c> 为真，说明跟不上、**必须拉快照重建**。
/// </summary>
public sealed class EventSubscription(Action unsubscribe, int capacity) : IDisposable
{
    private readonly Action _unsubscribe = unsubscribe;

    internal Channel<Published> Channel { get; } =
        System.Threading.Channels.Channel.CreateBounded<Published>(
            new BoundedChannelOptions(capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    /// <summary>
    /// **唯一**的降级信号。为真时别再读通道了——拉快照重建。
    /// 它由溢出或「要补发的起点已不在缓冲里」置位。
    /// </summary>
    public bool NeedsResync { get; internal set; }

    public ChannelReader<Published> Reader => Channel.Reader;

    /// <summary>
    /// **除了摘掉自己，还要 `TryComplete()` 那条 channel。**
    /// 只摘不完成的话，**健康的**旧订阅被换掉时，它的泵任务会**永远阻塞**在
    /// `ReadAllAsync` 上——没人会再往那条 channel 写，也没人会完成它（每连接泄漏一个 Task）。
    /// </summary>
    public void Dispose()
    {
        _unsubscribe();
        Channel.Writer.TryComplete();
    }
}

/// <summary>
/// 事件广播。
///
/// **它的全部存在理由是「投递有序」。** 一个看起来很自然但**错的**写法是：
/// 各生产者先 `Interlocked.Increment` 拿序号，再各自往 socket 写。那样**编号唯一但不保证有序**——
/// 甲拿到 5、乙拿到 6，乙先把帧写出去 ⇒ 头收到 6 再收到 5 ⇒
/// `lastSeq` 前进到 6，**5 被永久跳过且无从发现**。症状是「偶尔少一条」——最难查的那一类。
///
/// **本实现的临界区是那把锁，不是「单消费者泵」。** 约束的原话是「编号必须和入队在**同一个临界区**里
/// 发生」——锁直接满足它，且没有后台任务、测试**完全确定性**。
///
/// **缓冲是 per-topic 的，不是全局一个环。** 全局环会被一个话痨的 topic 挤爆：
/// 会话 A 流式上千条/秒 ⇒ 会话 B 的事件被淘汰 ⇒ B 的游标判「太旧」⇒
/// 拉全量快照 ⇒ 又被挤掉 ⇒ **循环抖动**，每次抖都付一次全量拉取。
/// </summary>
public sealed class EventBus(
    int topicCapacity = EventBus.DefaultTopicCapacity,
    int subscriptionCapacity = EventBus.DefaultSubscriptionCapacity)
{
    public const int DefaultTopicCapacity = 512;

    /// <summary>每个订阅自己的队列上界。慢订阅者**不许**拖住发布方（发布方持着锁）。</summary>
    public const int DefaultSubscriptionCapacity = 256;

    /// <summary>
    /// **seq 分配、topic 缓冲、以及向订阅者投递，全在这一把锁里。**
    /// 任何一步跑到锁外，都会重新引入「编号有序、投递无序」那个洞。
    /// 锁内只做非阻塞的 `TryWrite`，所以临界区很短。
    /// </summary>
    private readonly Lock _gate = new();

    private readonly Dictionary<string, TopicState> _topics = new(StringComparer.Ordinal);

    private sealed class TopicState
    {
        /// <summary>该 topic 的**高水位**——下一个事件拿到的是 <c>Seq + 1</c>。</summary>
        public long Seq;

        /// <summary>最近若干条，供补发。只由泵（锁内）写。</summary>
        public readonly LinkedList<Published> Buffer = new();

        public readonly List<EventSubscription> Subs = [];
    }

    /// <summary>发布一条事件。任何线程都能调，**立即返回**（不阻塞在订阅者身上）。</summary>
    public void Publish(string topic, WireEvent payload)
    {
        lock (_gate)
        {
            var state = StateOf(topic);
            var seq = ++state.Seq;

            var published = new Published(topic, seq, payload);
            state.Buffer.AddLast(published);
            while (state.Buffer.Count > topicCapacity) state.Buffer.RemoveFirst();

            // fan-out 在**同一把锁里**——这是「投递有序」的全部依据，别把它挪出去。
            foreach (var sub in state.Subs)
            {
                if (sub.NeedsResync) continue;   // 已经跟不上了，别再灌

                // 队列满 ⇒ **丢掉后续、标记待重建**，而不是阻塞——阻塞会让一个慢订阅者
                // 把**所有**发布方（以及整把锁）拖住；丢了的那些靠 resync 补。
                if (!sub.Channel.Writer.TryWrite(published))
                {
                    sub.NeedsResync = true;
                    sub.Channel.Writer.TryComplete();
                }
            }
        }
    }

    /// <summary>
    /// 订阅一个 topic。
    /// <paramref name="since"/>：要**从哪一条之后**开始（<c>hello(lastSeq)</c>）。
    /// 传 <c>null</c> = 只要从现在开始的（首次订阅的常见形态）。
    /// 若 <paramref name="since"/> 太旧（要补发的起点已不在缓冲里），
    /// 订阅**立即**以 <see cref="EventSubscription.NeedsResync"/> 结束——消费方据此拉快照。
    /// **这是唯一该有的判断，别发明「部分合并」。**
    /// </summary>
    public EventSubscription Subscribe(string topic, long? since = null)
    {
        lock (_gate)
        {
            var state = StateOf(topic);
            EventSubscription? sub = null;

            sub = new EventSubscription(() =>
            {
                lock (_gate) state.Subs.Remove(sub!);
            }, subscriptionCapacity);

            if (since is { } from)
            {
                if (from > state.Seq)
                {
                    // 头声称见过的比我们发过的还新——它连的是**另一个核心的数据**，
                    // 或者它自己记错了。两种都不能假装没事。
                    sub.NeedsResync = true;
                    sub.Channel.Writer.TryComplete();
                }
                else if (from < state.Seq)
                {
                    var wanted = state.Buffer.Where(p => p.Seq > from).ToList();

                    // 判据一句话：**「要补发的起点已不在缓冲里」**——
                    // 缓冲里最早那条的 seq 若大于 from+1，中间那段已经丢了。
                    var oldest = state.Buffer.Count > 0 ? state.Buffer.First!.Value.Seq : state.Seq + 1;
                    if (from + 1 < oldest)
                    {
                        sub.NeedsResync = true;
                        sub.Channel.Writer.TryComplete();
                        return sub;
                    }

                    // **补发也必须检查 `TryWrite` 的返回值**——要让补的条数落在
                    // (subscriptionCapacity, topicCapacity] 之间时，前 subscriptionCapacity 条
                    // 写进队列、**其余的写失败且被无视**，`NeedsResync` 还是 false
                    // ⇒ 头**静默**少一段。缺口的宽度由两个容量之差决定，所以**两者必须一起看**。
                    foreach (var p in wanted)
                    {
                        if (sub.Channel.Writer.TryWrite(p)) continue;

                        sub.NeedsResync = true;
                        sub.Channel.Writer.TryComplete();
                        break;
                    }

                    // 补发途中就溢出了 ⇒ 已经标了待重建，别把它当正常订阅加进去
                    if (sub.NeedsResync) return sub;
                }
            }

            state.Subs.Add(sub);
            return sub;
        }
    }

    /// <summary>
    /// 该 topic 的 **seq 高水位**。
    /// 快照端点必须**带上它**：头拉完快照再 <c>subscribe(since:)</c>，
    /// 两步之间落的事件要么漏、要么重。带上高水位、订阅时传它，那个窗口就关上了。
    /// 它在锁内读，所以与 seq 分配是同一个临界区。
    /// </summary>
    public long HighWaterMark(string topic)
    {
        lock (_gate) return StateOf(topic).Seq;
    }

    private TopicState StateOf(string topic)
    {
        if (!_topics.TryGetValue(topic, out var state))
        {
            state = new TopicState();
            _topics[topic] = state;
        }
        return state;
    }

    /// <summary>topic 命名。**集中在这里，别让调用方各拼各的**。</summary>
    public static string SessionTopic(string sessionId) => $"session:{sessionId}";

    public static string ProjectTopic(string projectKey) => $"project:{projectKey}";

    public const string CoreTopic = "core";

    /// <summary>
    /// **所有待审批**（`01` §3.8 · `03` §5）。⚠️ <b>它是那条「头最需要」的流</b>——
    /// 审批单独一条流，是为了不混在会话事件里淹掉。
    ///
    /// <para>⚠️ <b>它长期零生产者</b>（没有审批子系统），本批起**有了**：
    /// `Stepper` 挂起时发 `approval.requested`，`CommandDispatcher` 答复时发
    /// `approval.resolved`。⚠️ 两条都是**双投**（这条流 + 会话流）——理由见
    /// `WireEvent.ApprovalRequested` 的注释。</para>
    /// </summary>
    public const string ApprovalsTopic = "approvals";
}
