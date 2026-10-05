using Lilara.Core.Control;
using Lilara.Core.Hosting;

namespace Lilara.Core.Tests;

public class EventBusTests
{
    private static WireEvent Evt(string s = "x") => new WireEvent.Error(s, "m");

    // 订阅者按序拿到，seq 从 1 起单调增。
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

    // 判据是订阅者收到的 seq 严格递增（不只是不重复）；seq 分配与 fan-out 必须在同一临界区。
    [Fact]
    public async Task ConcurrentPublishers_DeliverStrictlyIncreasingSeq()
    {
        // 订阅容量要够大，否则测的是溢出行为，不是投递有序。
        var bus = new EventBus(subscriptionCapacity: 4096);
        const int perProducer = 400;
        const int producers = 4;
        using var sub = bus.Subscribe(EventBus.SessionTopic("s1"), since: null);

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

        // 严格递增，不是「不重复」——乱序正是要防的洞。
        for (var i = 1; i < got.Count; i++)
            Assert.True(got[i] > got[i - 1], $"第 {i} 条 seq={got[i]} 不大于前一条 {got[i - 1]}：投递乱序了");

        // 且编号无洞（1..N 恰好一次）。
        Assert.Equal(Enumerable.Range(1, expected).Select(i => (long)i), got);
    }

    // 缓冲是 per-topic 的：一个话痨 topic 不许挤掉另一个的补发能力。
    [Fact]
    public async Task ANoisyTopicDoesNotEvictAnotherTopicsBuffer()
    {
        var bus = new EventBus(topicCapacity: 8);

        bus.Publish(EventBus.SessionTopic("quiet"), Evt("重要"));

        for (var i = 0; i < 50; i++) bus.Publish(EventBus.SessionTopic("noisy"), Evt($"{i}"));

        // quiet 的补发仍够得着（since=0 要补 seq 1）。
        using var sub = bus.Subscribe(EventBus.SessionTopic("quiet"), since: 0);

        Assert.False(sub.NeedsResync);
        Assert.True(sub.Reader.TryRead(out var p));
        Assert.Equal(1, p.Seq);
    }

    // 要补发的起点已不在该 topic 的缓冲里 ⇒ 立刻 NeedsResync。
    [Fact]
    public async Task Subscribe_SinceTooOld_RequiresResync()
    {
        var bus = new EventBus(topicCapacity: 4);
        for (var i = 0; i < 20; i++) bus.Publish(EventBus.SessionTopic("s1"), Evt($"{i}"));

        using var sub = bus.Subscribe(EventBus.SessionTopic("s1"), since: 1);   // 要补 2..20，但只剩最近 4 条

        Assert.True(sub.NeedsResync);
        await Task.CompletedTask;
    }

    // 补发够得着时正常补，不误报 resync。
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

    // 慢订阅者不许拖住发布方：队列满就丢并标记待重建，否则一个卡住的头会拖死所有发布方。
    [Fact]
    public void SlowSubscriber_IsDroppedNotBlockingThePublisher()
    {
        var bus = new EventBus();
        using var sub = bus.Subscribe(EventBus.SessionTopic("s1"));

        // 发布远超订阅队列上界，且从不去读。
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < EventBus.DefaultSubscriptionCapacity * 3; i++)
            bus.Publish(EventBus.SessionTopic("s1"), Evt($"{i}"));
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 5000, "发布被慢订阅者拖住了");
        Assert.True(sub.NeedsResync, "队列溢出后必须标记待重建，否则头会静默缺事件");
    }

    // 退订之后不再收到。
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
        Assert.False(sub.Reader.TryRead(out _));
    }

    // 高水位必须与 seq 分配在同一临界区里读，否则「拉快照 → 订阅」之间的事件会漏或重。
    [Fact]
    public void HighWaterMark_MatchesTheLastPublishedSeq()
    {
        var bus = new EventBus();
        Assert.Equal(0, bus.HighWaterMark(EventBus.SessionTopic("s1")));

        bus.Publish(EventBus.SessionTopic("s1"), Evt("a"));
        bus.Publish(EventBus.SessionTopic("s1"), Evt("b"));

        Assert.Equal(2, bus.HighWaterMark(EventBus.SessionTopic("s1")));
        Assert.Equal(0, bus.HighWaterMark(EventBus.SessionTopic("other")));
    }

    // seq 是每 topic 的，不是全局的。
    [Fact]
    public void Seq_IsPerTopic()
    {
        var bus = new EventBus();
        bus.Publish(EventBus.SessionTopic("a"), Evt());
        bus.Publish(EventBus.SessionTopic("b"), Evt());

        Assert.Equal(1, bus.HighWaterMark(EventBus.SessionTopic("a")));
        Assert.Equal(1, bus.HighWaterMark(EventBus.SessionTopic("b")));
    }

    // 补发写失败被无视会静默少一段：缺口要么补上、要么明说（NeedsResync）。
    [Fact]
    public void Subscribe_ReplayBiggerThanTheQueue_ReportsResyncInsteadOfTruncatingSilently()
    {
        var bus = new EventBus(topicCapacity: 512, subscriptionCapacity: 256);
        for (var i = 0; i < 400; i++) bus.Publish(EventBus.SessionTopic("t"), Evt($"{i}"));

        // 要补 300 条，而订阅队列只有 256
        using var sub = bus.Subscribe(EventBus.SessionTopic("t"), since: 100);

        var got = 0;
        while (sub.Reader.TryRead(out _)) got++;

        // 要么补齐，要么标 resync；只拿到 256 且 NeedsResync=false 才是缺陷。
        Assert.True(got == 300 || sub.NeedsResync,
            $"补发被静默截断：拿到 {got} 条、NeedsResync={sub.NeedsResync}（两者必居其一）");
    }
}
