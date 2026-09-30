using Lilara.Core.Control;
using Lilara.Core.Hosting;

namespace Lilara.Core.Tests;

public class EventBusTests
{
    private static WireEvent Evt(string s = "x") => new WireEvent.Error(s, "m");

    /// <summary>基本投递：订阅者按序拿到，seq 从 1 起单调增。</summary>
    [Fact]
    public async Task Subscribe_ReceivesEventsInOrderWithMonotonicSeq()
    {
        var bus = new EventBus();
        using var sub = bus.Subscribe(EventBus.SessionTopic("s1"));

        bus.Publish(EventBus.SessionTopic("s1"), Evt("a"));
        bus.Publish(EventBus.SessionTopic("s1"), Evt("b"));
        bus.Publish(EventBus.SessionTopic("s1"), Evt("c"));

        var got = new List<Published>();
        await foreach (var p in sub.Reader.ReadAllAsync()) { got.Add(p); if (got.Count == 3) break; }

        Assert.Equal([1L, 2L, 3L], got.Select(p => p.Seq));
    }

    /// <summary>
    /// ⚠️ **这条是整个事件层存在的理由**（`03` §4 的硬约束）。
    ///
    /// <para>要防的那个错写法：各生产者先 `Interlocked.Increment` 拿序号，再各自写 socket。
    /// 那样**编号唯一但不保证有序**——甲拿 5、乙拿 6，乙先写出去 ⇒ 头收 6 再收 5 ⇒
    /// `lastSeq` 前进到 6，**5 被永久跳过且无从发现**。</para>
    ///
    /// <para>⇒ 判据是「订阅者收到的 seq **严格递增**」（而不只是「不重复」：
    /// 不重复却乱序，同样是那个洞）。多生产者并发压，才试得出来。</para>
    ///
    /// <para>⚠️ **这条测试的区分力实测过，边界如下，别高估它**：
    /// <list type="bullet">
    ///   <item>**抓得住**：把 fan-out 挪出锁（锁只罩 seq 分配）⇒ **3/3 红**。</item>
    ///   <item>**抓不住**：只把 seq 分配挪出锁而投递仍在锁内。那个窗口只有几条指令宽，
    ///     控制器用 **32 线程 × 200 事件 × 200 轮**压测也**从未复现**。
    ///     ⇒ 全绿**不等于**那样写安全；那条约束是按推理守的，见 `EventBus` 的类注释。</item>
    /// </list></para>
    /// </summary>
    [Fact]
    public async Task ConcurrentPublishers_DeliverStrictlyIncreasingSeq()
    {
        // ⚠️ 订阅容量要够大，否则压测期间就溢出了——那测的是溢出行为，不是投递有序。
        var bus = new EventBus(subscriptionCapacity: 4096);
        const int perProducer = 400;
        const int producers = 4;
        using var sub = bus.Subscribe(EventBus.SessionTopic("s1"), since: null);

        // 先把订阅挂上，再开并发发布
        var publish = Task.Run(() =>
        {
            Parallel.For(0, producers, i =>
            {
                for (var n = 0; n < perProducer; n++)
                    bus.Publish(EventBus.SessionTopic("s1"), Evt($"p{i}-{n}"));
            });
        });

        var got = new List<long>();
        var expected = perProducer * producers;
        await foreach (var p in sub.Reader.ReadAllAsync())
        {
            got.Add(p.Seq);
            if (got.Count == expected) break;
        }
        await publish;

        Assert.Equal(expected, got.Count);

        // ⚠️ **严格递增**（不是「不重复」）——乱序正是要防的洞。
        for (var i = 1; i < got.Count; i++)
            Assert.True(got[i] > got[i - 1], $"第 {i} 条 seq={got[i]} 不大于前一条 {got[i - 1]}：投递乱序了");

        // 且编号无洞（1..N 恰好一次）
        Assert.Equal(Enumerable.Range(1, expected).Select(i => (long)i), got);
    }

    /// <summary>
    /// ⚠️ **per-topic 缓冲，不是全局一个环**：一个话痨 topic 不许挤掉另一个的补发能力。
    /// 全局环的后果是**循环抖动**——A 流式把环填满 ⇒ B 的游标判「太旧」⇒ 拉全量 ⇒
    /// 又被挤掉 ⇒ 再拉，每次都付一次全量快照。
    /// </summary>
    [Fact]
    public async Task ANoisyTopicDoesNotEvictAnotherTopicsBuffer()
    {
        var bus = new EventBus(topicCapacity: 8);

        bus.Publish(EventBus.SessionTopic("quiet"), Evt("重要"));

        // 话痨 topic 灌爆它自己的缓冲
        for (var i = 0; i < 50; i++) bus.Publish(EventBus.SessionTopic("noisy"), Evt($"{i}"));

        // ⚠️ quiet 的补发仍然够得着（since=0 → 要补 seq 1）
        using var sub = bus.Subscribe(EventBus.SessionTopic("quiet"), since: 0);

        Assert.False(sub.NeedsResync);
        Assert.True(sub.Reader.TryRead(out var p));
        Assert.Equal(1, p.Seq);
    }

    /// <summary>
    /// ⚠️ 补发起点太旧 ⇒ **立刻** `NeedsResync`（`03` §7 唯一的降级路径）。
    /// 判据一句话：**「要补发的起点已不在该 topic 的缓冲里」**。
    /// </summary>
    [Fact]
    public async Task Subscribe_SinceTooOld_RequiresResync()
    {
        var bus = new EventBus(topicCapacity: 4);
        for (var i = 0; i < 20; i++) bus.Publish(EventBus.SessionTopic("s1"), Evt($"{i}"));

        using var sub = bus.Subscribe(EventBus.SessionTopic("s1"), since: 1);   // 要补 2..20，但只剩最近 4 条

        Assert.True(sub.NeedsResync);
        await Task.CompletedTask;
    }

    /// <summary>补发够得着时**正常补**，不该误报 resync。</summary>
    [Fact]
    public async Task Subscribe_SinceWithinBuffer_ReplaysWithoutResync()
    {
        var bus = new EventBus(topicCapacity: 16);
        for (var i = 0; i < 5; i++) bus.Publish(EventBus.SessionTopic("s1"), Evt($"{i}"));

        using var sub = bus.Subscribe(EventBus.SessionTopic("s1"), since: 3);

        Assert.False(sub.NeedsResync);
        var got = new List<long>();
        while (sub.Reader.TryRead(out var p)) got.Add(p.Seq);
        Assert.Equal([4L, 5L], got);
    }

    /// <summary>
    /// ⚠️ 慢订阅者**不许**拖住发布方：队列满就丢、标记待重建。
    /// 若改成阻塞，一个卡住的头会把整把锁（以及所有发布方）拖死。
    /// </summary>
    [Fact]
    public void SlowSubscriber_IsDroppedNotBlockingThePublisher()
    {
        var bus = new EventBus();
        using var sub = bus.Subscribe(EventBus.SessionTopic("s1"));

        // 发布远超订阅队列上界，且**从不去读**
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < EventBus.DefaultSubscriptionCapacity * 3; i++)
            bus.Publish(EventBus.SessionTopic("s1"), Evt($"{i}"));
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 5000, "发布被慢订阅者拖住了");
        Assert.True(sub.NeedsResync, "队列溢出后必须标记待重建，否则头会静默缺事件");
    }

    /// <summary>退订之后不再收到——否则断开的头会永远占着内存。</summary>
    [Fact]
    public void Dispose_StopsDelivery()
    {
        var bus = new EventBus();
        var sub = bus.Subscribe(EventBus.SessionTopic("s1"));
        bus.Publish(EventBus.SessionTopic("s1"), Evt("1"));

        sub.Dispose();
        bus.Publish(EventBus.SessionTopic("s1"), Evt("2"));

        Assert.True(sub.Reader.TryRead(out var first));
        Assert.Equal(1, first.Seq);
        Assert.False(sub.Reader.TryRead(out _));   // 退订后的那条没进来
    }

    /// <summary>
    /// ⚠️ 快照端点要用的**高水位**：头拉完快照再 `subscribe(since: 高水位)`，
    /// 「拉快照 → 订阅」之间落的事件才不漏不重。它必须与 seq 分配在同一临界区里读。
    /// </summary>
    [Fact]
    public void HighWaterMark_MatchesTheLastPublishedSeq()
    {
        var bus = new EventBus();
        Assert.Equal(0, bus.HighWaterMark(EventBus.SessionTopic("s1")));

        bus.Publish(EventBus.SessionTopic("s1"), Evt("a"));
        bus.Publish(EventBus.SessionTopic("s1"), Evt("b"));

        Assert.Equal(2, bus.HighWaterMark(EventBus.SessionTopic("s1")));
        Assert.Equal(0, bus.HighWaterMark(EventBus.SessionTopic("other")));   // 互不影响
    }

    /// <summary>seq 是**每 topic** 的，不是全局的（`03` §4「流内单调增」）。</summary>
    [Fact]
    public void Seq_IsPerTopic()
    {
        var bus = new EventBus();
        bus.Publish(EventBus.SessionTopic("a"), Evt());
        bus.Publish(EventBus.SessionTopic("b"), Evt());

        Assert.Equal(1, bus.HighWaterMark(EventBus.SessionTopic("a")));
        Assert.Equal(1, bus.HighWaterMark(EventBus.SessionTopic("b")));
    }
}
