using System.Threading.Channels;
using Lilara.Core.Control;

namespace Lilara.Core.Hosting;

/// <summary>一条已分配 <c>seq</c> 的事件。⚠️ <c>Seq</c> 是**该 topic 内**单调增的（`03` §4）。</summary>
public sealed record Published(string Topic, long Seq, WireEvent Payload);

/// <summary>
/// 一个订阅。⚠️ 消费方要**同时**看 <see cref="Reader"/> 与 <see cref="NeedsResync"/>：
/// 通道读完之后若 <c>NeedsResync</c> 为真，说明跟不上、**必须拉快照重建**（`03` §7）。
/// </summary>
public sealed class EventSubscription(Action unsubscribe, int capacity) : IDisposable
{
    private readonly Action _unsubscribe = unsubscribe;

    internal Channel<Published> Channel { get; } =
        System.Threading.Channels.Channel.CreateBounded<Published>(
            new BoundedChannelOptions(capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    /// <summary>
    /// ⚠️ **唯一**的降级信号（`03` §7）。为真时别再读通道了——拉快照重建。
    /// 它由溢出或「要补发的起点已不在缓冲里」置位。
    /// </summary>
    public bool NeedsResync { get; internal set; }

    public ChannelReader<Published> Reader => Channel.Reader;

    public void Dispose() => _unsubscribe();
}

/// <summary>
/// 事件广播（`03` §4/§5/§7）。
///
/// <para>⚠️ **它的全部存在理由是「投递有序」。** 一个看起来很自然但**错的**写法是：
/// 各生产者先 `Interlocked.Increment` 拿序号，再各自往 socket 写。那样**编号唯一但不保证有序**——
/// 甲拿到 5、乙拿到 6，乙先把帧写出去 ⇒ 头收到 6 再收到 5 ⇒
/// `lastSeq` 前进到 6，**5 被永久跳过且无从发现**（头永远不会知道自己缺了一条）。
/// 这违反 `03` §4 的硬约束，且**症状是「偶尔少一条」**——最难查的那一类。</para>
///
/// <para>⚠️ **本实现的临界区是那把锁，不是「单消费者泵」。**
/// 约束的原话是「编号必须和入队在**同一个临界区**里发生」——锁直接满足它，
/// 且比「通道 + 后台泵」少一层机械：没有后台任务、测试**完全确定性**、也不必等泵排空。
/// 两者保证等价（都是把 seq 分配与投递串行化），选少机械的那个。</para>
///
/// <para>⚠️ **缓冲是 per-topic 的，不是全局一个环。** 全局环会被一个话痨的 topic 挤爆：
/// 会话 A 流式上千条/秒 ⇒ 会话 B 的事件被淘汰 ⇒ B 的游标判「太旧」⇒
/// 拉全量快照 ⇒ 又被挤掉 ⇒ **循环抖动**，每次抖都付一次全量拉取。</para>
///
/// <para>⚠️ <b>已知的覆盖边界（实测，别当成已被守住）</b>：
/// <list type="bullet">
///   <item><b>守得住</b>：把 fan-out **挪出锁**（「先把锁放了再写」那种自然写法）——
///     `ConcurrentPublishers_DeliverStrictlyIncreasingSeq` **3/3 红**。</item>
///   <item><b>守不住</b>：只把 **seq 分配挪出锁**（改 `Interlocked`、投递仍在锁内）。
///     变异的窗口只有几条指令宽（分配 → 拿锁之间），实测 **32 线程 × 200 事件 × 200 轮压测
///     也从未复现**。⇒ 那条测试对**这个**变异没有区分力，**别把「全绿」读成「这样写也安全」**。</item>
/// </list>
/// 所以这里的选择（seq 与投递同在锁内）是**按推理**做的，不是靠测试逼出来的——
/// 而注释里那句「编号必须和入队在同一个临界区里」正是这个推理的陈述，不是实验结论。</para>
/// </summary>
public sealed class EventBus(
    int topicCapacity = EventBus.DefaultTopicCapacity,
    int subscriptionCapacity = EventBus.DefaultSubscriptionCapacity)
{
    public const int DefaultTopicCapacity = 512;

    /// <summary>每个订阅自己的队列上界。⚠️ 慢订阅者**不许**拖住发布方（发布方持着锁）。</summary>
    public const int DefaultSubscriptionCapacity = 256;

    /// <summary>
    /// ⚠️ **seq 分配、topic 缓冲、以及向订阅者投递，全在这一把锁里。**
    /// 任何一步跑到锁外，都会重新引入「编号有序、投递无序」那个洞。
    /// 锁内只做非阻塞的 `TryWrite`，所以临界区很短。
    /// </summary>
    private readonly Lock _gate = new();

    private readonly Dictionary<string, TopicState> _topics = new(StringComparer.Ordinal);

    private sealed class TopicState
    {
        /// <summary>该 topic 的**高水位**——下一个事件拿到的是 <c>Seq + 1</c>。</summary>
        public long Seq;

        /// <summary>最近若干条，供补发。⚠️ 只由泵（锁内）写。</summary>
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

            // ⚠️ fan-out 在**同一把锁里**——这是「投递有序」的全部依据，别把它挪出去。
            foreach (var sub in state.Subs)
            {
                if (sub.NeedsResync) continue;   // 已经跟不上了，别再灌

                // ⚠️ 队列满 ⇒ **丢掉后续、标记待重建**，而不是阻塞。
                // 阻塞会让一个慢订阅者把**所有**发布方（以及整把锁）拖住。
                // 丢了的那些靠 resync 补——那正是 `03` §7 唯一的降级路径。
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
    /// <para><paramref name="since"/>：要**从哪一条之后**开始（`03` §7 的 <c>hello(lastSeq)</c>）。
    /// 传 <c>null</c> = 只要从现在开始的（首次订阅的常见形态）。</para>
    /// <para>⚠️ 若 <paramref name="since"/> 太旧（要补发的起点已不在缓冲里），
    /// 订阅**立即**以 <see cref="EventSubscription.NeedsResync"/> 结束——消费方据此拉快照。
    /// **这是唯一该有的判断，别发明「部分合并」。**</para>
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

                    // ⚠️ 判据一句话：**「要补发的起点已不在缓冲里」**。
                    // 缓冲里最早那条的 seq 若大于 from+1，中间那段已经丢了。
                    var oldest = state.Buffer.Count > 0 ? state.Buffer.First!.Value.Seq : state.Seq + 1;
                    if (from + 1 < oldest)
                    {
                        sub.NeedsResync = true;
                        sub.Channel.Writer.TryComplete();
                        return sub;
                    }

                    foreach (var p in wanted) sub.Channel.Writer.TryWrite(p);
                }
            }

            state.Subs.Add(sub);
            return sub;
        }
    }

    /// <summary>
    /// 该 topic 的 **seq 高水位**。
    /// <para>⚠️ 快照端点必须**带上它**（`03` §7 的原子性）：头拉完快照再 <c>subscribe(since:)</c>，
    /// 两步之间落的事件要么漏、要么重。带上高水位、订阅时传它，那个窗口就关上了。
    /// 它在锁内读，所以与 seq 分配是同一个临界区。</para>
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

    /// <summary>topic 命名（`03` §5）。⚠️ 集中在这里，别让调用方各拼各的。</summary>
    public static string SessionTopic(string sessionId) => $"session:{sessionId}";

    public static string ProjectTopic(string projectKey) => $"project:{projectKey}";

    public const string CoreTopic = "core";
}
