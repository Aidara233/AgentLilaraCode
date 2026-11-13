using Lilara.Core.Hosting;
using Lilara.Core.Kernel;
using Lilara.Core.Protocol;
using Lilara.Core.Session;

namespace Lilara.Core.Tests;

/// <summary>
/// **每 20 秒那一趟维护**（2026-11 性能批）：空闲卸载、询问到期的廉价前置判据、
/// 以及事件总线那条 topic 的释放。
///
/// <para>⚠️ <b>为什么这些要单独钉</b>：它们全是「后台自己动手」的活——没有请求打进来，
/// 所以坏掉的样子不是 500，而是**内存只涨不落 / 会话永远挂着**，而没有任何东西会报错。
/// 本仓对这类「静默维护」的既有教训是 `01` §12.8 那次（过了截止 75 秒纹丝不动）。</para>
/// </summary>
public class MaintenanceSweepTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-sweep-" + Guid.NewGuid().ToString("N"));
    private readonly string _work =
        Path.Combine(Path.GetTempPath(), "lilara-sweepwork-" + Guid.NewGuid().ToString("N"));

    private readonly CoreHost _host;
    private readonly EventBus _bus = new();
    private readonly CommandDispatcher _dispatcher;

    public MaintenanceSweepTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_work);
        _host = new CoreHost(_root, _work);
        _dispatcher = new CommandDispatcher(
            _host, _bus, h => new Stepper(_host.Store, new UnconfiguredAdapter(), plugins: _host.Plugins));
    }

    public void Dispose()
    {
        foreach (var handle in _host.Activated)
            handle.ExitStep();

        Remove(_root);
        Remove(_work);

        static void Remove(string path)
        {
            for (var attempt = 0; ; attempt++)
            {
                if (!Directory.Exists(path)) return;
                try { Directory.Delete(path, recursive: true); return; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 20)
                {
                    Thread.Sleep(10 * (attempt + 1));
                }
            }
        }
    }

    private SessionHandle NewSession(string title)
    {
        var handle = SessionHandle.Create(_host.Store, _work, new ModelSelection
        {
            ProviderId = "p", ModelId = "m", Protocol = Protocols.ClaudeMessages,
            Effort = "high", MaxTokens = 8192,
        }, title: title);

        _host.Register(handle);
        return handle;
    }

    private static PausedAsk Ask(DateTimeOffset? deadline) =>
        new("a1", AskKind.Permission, "c1", "shell", "h", deadline);

    /// <summary>
    /// **空闲的摘掉，而且把跟着它占内存的那两份也摘掉**（stepper 缓存 + 事件总线 topic）。
    ///
    /// <para>⚠️ 前半句（激活表空了）只证明「摘了会话」；后半句才是**内存真的回来了**
    /// ——本仓那次「摘了会话、512 条补发缓冲还钉着正文」正是只做前半句的样子。</para>
    /// </summary>
    [Fact]
    public void SweepIdleSessions_UnloadsIdleSessions_AndDropsTheirCachesAndTopics()
    {
        var first = NewSession("一");
        var second = NewSession("二");

        // 让两个会话的 stepper 缓存各有一份（生产里是「跑过一步」的自然结果）。
        var cachedFirst = _host.StepperFor(first);
        var cachedSecond = _host.StepperFor(second);

        // 让 session topic 上有一条「保留」的事件，再用无订阅者的状态释放它。
        _bus.Publish(EventBus.SessionTopic(first.SessionId),
            new WireEvent.StateChanged(first.SessionId, SessionStatus.Idle, 0));
        Assert.Equal(1, _bus.HighWaterMark(EventBus.SessionTopic(first.SessionId)));

        // 事件在**项目流**上收（卸载那条会双投；会话流那条马上要被释放）。
        using var project = _bus.Subscribe(EventBus.ProjectTopic(first.ProjectKey));

        _host.Live.BackdateForTest(first.SessionId, TimeSpan.FromHours(1));
        _host.Live.BackdateForTest(second.SessionId, TimeSpan.FromHours(1));

        var unloaded = _dispatcher.SweepIdleSessions(TimeSpan.FromMinutes(10));

        Assert.Equal(2, unloaded);
        Assert.Empty(_host.Activated);

        // ⚠️ 缓存丢了：再要一份是**另一份**（`GetOrAdd` 重新解析）。
        Assert.NotSame(cachedFirst, _host.StepperFor(first));
        Assert.NotSame(cachedSecond, _host.StepperFor(second));

        // ⚠️ topic 的序号从头开始 = 那份缓冲真的没了。
        Assert.Equal(0, _bus.HighWaterMark(EventBus.SessionTopic(first.SessionId)));

        var events = new List<WireEvent>();
        while (project.Reader.TryRead(out var published)) events.Add(published.Payload);

        var deactivated = events.OfType<WireEvent.SessionDeactivated>().ToList();
        Assert.Equal(2, deactivated.Count);
        Assert.Contains(deactivated, e => e.SessionId == first.SessionId);
        Assert.Contains(deactivated, e => e.SessionId == second.SessionId);
    }

    /// <summary>
    /// **三条不许摘的**：正在跑、挂着询问、刚活动过。
    ///
    /// <para>⚠️ 每一条都对应一种「摘了就说假话」：丢队列里没落盘的话、忘掉 `Remember` 档位、
    /// 以及把用户正要接着用的会话关掉（下一句要多等一次载入）。</para>
    /// </summary>
    [Fact]
    public void SweepIdleSessions_KeepsRunning_Asking_AndRecentlyActive()
    {
        var running = NewSession("在跑");
        var asking = NewSession("等回答");
        var fresh = NewSession("刚活动过");
        var stale = NewSession("没人理");

        // ① 在跑：那把标志被占着（与 `Start` 之后的形态相同）。
        running.TryEnterStep();

        // ② 挂着一次不会超时的询问（`State.Ask` 非 null ⇒ 内存里那份 `AwaitingInput` 还带着 Remember）。
        asking.UpdateState(s => s with { Status = SessionStatus.WaitingForInput, Ask = Ask(null) });

        // ③ 刚活动过：不拨表 ⇒ 距「现在」不到阈值。
        _host.Live.BackdateForTest(stale.SessionId, TimeSpan.FromHours(1));

        var unloaded = _dispatcher.SweepIdleSessions(TimeSpan.FromMinutes(10));

        Assert.Equal(1, unloaded);
        Assert.True(_host.TryGetActivated(running.SessionId, out _));
        Assert.True(_host.TryGetActivated(asking.SessionId, out _));
        Assert.True(_host.TryGetActivated(fresh.SessionId, out _));
        Assert.False(_host.TryGetActivated(stale.SessionId, out _));
    }

    /// <summary>
    /// **阈值 0 = 把所有「此刻没有东西在动」的都摘掉**——测试用的那一档，也是
    /// 「卸载真的会动手」的最小证明（不需要等十分钟）。
    /// </summary>
    [Fact]
    public void SweepIdleSessions_WithZeroIdle_UnloadsEverythingThatIsNotLive()
    {
        var idle = NewSession("一");
        var live = NewSession("二");
        live.TryEnterStep();

        Assert.Equal(1, _dispatcher.SweepIdleSessions(TimeSpan.Zero));
        Assert.False(_host.TryGetActivated(idle.SessionId, out _));
        Assert.True(_host.TryGetActivated(live.SessionId, out _));
    }

    /// <summary>
    /// **心跳那一趟的廉价前置判据**：只有「挂着等回答 + 有截止时刻 + 已经过了」才值得读那份记录。
    ///
    /// <para>⚠️ 这条钉的是**性能判据**，而它的失败方向是「多读一份历史」（不是错），
    /// 所以更要钉住**反方向**：不许把「该读的」判成「不用读」——那会让一个会话永远挂着。</para>
    /// </summary>
    [Fact]
    public void MayHaveExpiredAsk_OnlyForWaitingSessionsWithDueDeadline()
    {
        var handle = NewSession("问");
        var now = DateTimeOffset.UtcNow;

        // 空闲：没有可到期的东西。
        Assert.False(CommandDispatcher.MayHaveExpiredAsk(handle, now));

        // 挂着、但**没有**截止时刻（默认「不超时」）⇒ 心跳永远做不了事，不该读记录。
        handle.UpdateState(s => s with { Status = SessionStatus.WaitingForInput, Ask = Ask(null) });
        Assert.False(CommandDispatcher.MayHaveExpiredAsk(handle, now));

        // 挂着、截止时刻还没到 ⇒ 不读。
        handle.UpdateState(s => s with { Ask = Ask(now.AddMinutes(5)) });
        Assert.False(CommandDispatcher.MayHaveExpiredAsk(handle, now));

        // 挂着、已经过了 ⇒ 必须读（这一条是「不许判少」）。
        handle.UpdateState(s => s with { Ask = Ask(now.AddSeconds(-1)) });
        Assert.True(CommandDispatcher.MayHaveExpiredAsk(handle, now));
    }

    /// <summary>
    /// **`ReleaseTopic` 只在没有订阅者时动手**：有人看着的时候清掉缓冲，
    /// 等于让那个客户端的 `since` 游标静默失去补发来源。
    /// </summary>
    [Fact]
    public void ReleaseTopic_DropsTheBuffer_OnlyWhenNobodyIsSubscribed()
    {
        var topic = EventBus.SessionTopic("s-release");

        using (var subscription = _bus.Subscribe(topic))
        {
            _bus.Publish(topic, new WireEvent.StateChanged("s-release", SessionStatus.Idle, 0));

            _bus.ReleaseTopic(topic);
            // ⚠️ 有订阅者 ⇒ 高水位不动（缓冲还在，而且订阅者那条事件照样读得到）。
            Assert.Equal(1, _bus.HighWaterMark(topic));
            Assert.True(subscription.Reader.TryRead(out _));
        }

        _bus.ReleaseTopic(topic);
        Assert.Equal(0, _bus.HighWaterMark(topic));
    }

    // ---- 阈值的解析（`IdleUnload`）：默认、关闭、小数、坏值 ----

    [Fact]
    public void IdleUnload_DefaultsToTenMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), IdleUnload.Resolve(_ => null));
        Assert.Equal(TimeSpan.FromMinutes(10), IdleUnload.Resolve(_ => "   "));
    }

    [Fact]
    public void IdleUnload_ZeroMeansDisabled()
    {
        Assert.Equal(TimeSpan.Zero, IdleUnload.Resolve(_ => "0"));
    }

    [Fact]
    public void IdleUnload_AcceptsFractionalMinutes()
    {
        // 0.5 分钟 = 30 秒（调试时比等十分钟现实）。
        Assert.Equal(TimeSpan.FromSeconds(30), IdleUnload.Resolve(_ => "0.5"));
    }

    [Fact]
    public void IdleUnload_BadValuesFallBackToTheDefault_NotToDisabled()
    {
        // ⚠️ 坏值读成「永不卸载」会**静默关掉**整条维护；读成默认值最多是「按你以为的那样跑」。
        Assert.Equal(IdleUnload.DefaultIdle, IdleUnload.Resolve(_ => "abc"));
        Assert.Equal(IdleUnload.DefaultIdle, IdleUnload.Resolve(_ => "-5"));
        Assert.Equal(IdleUnload.DefaultIdle, IdleUnload.Resolve(_ => "NaN"));
        Assert.Equal(IdleUnload.DefaultIdle, IdleUnload.Resolve(_ => "Infinity"));
    }

    [Fact]
    public void IdleUnload_DescribesBothStates()
    {
        Assert.Contains("关闭", IdleUnload.Describe(TimeSpan.Zero));
        Assert.Contains("10", IdleUnload.Describe(TimeSpan.FromMinutes(10)));
    }
}
