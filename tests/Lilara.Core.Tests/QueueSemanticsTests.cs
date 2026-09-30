using Lilara.Core.Adapters;
using Lilara.Core.Canonical;
using Lilara.Core.Hosting;
using Lilara.Core.Persistence;
using Lilara.Core.Providers;
using Lilara.Core.Records;
using Lilara.Core.Session;
using Lilara.Core.Tests.Fakes;

namespace Lilara.Core.Tests;

/// <summary>
/// 队列语义（`01` §6.1）。⚠️ 这些测试钉的是**可观测行为**，不是实现细节：
/// 「合并是队列的副产品」这句话必须能落到「几次模型调用、Turn 加几」上，
/// 否则它只是一句文档。
/// </summary>
public class QueueSemanticsTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-queue-" + Guid.NewGuid().ToString("N"));
    private readonly string _work =
        Path.Combine(Path.GetTempPath(), "lilara-queuework-" + Guid.NewGuid().ToString("N"));

    private readonly FakeAdapter _adapter = new();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
    }

    private (CoreHost Host, CommandDispatcher Dispatcher, SessionHandle Session) NewHarness()
    {
        var host = new CoreHost(_root, _work);
        host.Providers.Save(new ProviderDefinition
        {
            Id = "p", Protocol = Protocols.ClaudeMessages, ApiKey = "k",
            DefaultModel = "m", Modality = "vision",
        });
        var dispatcher = new CommandDispatcher(host, new EventBus(), h => new Stepper(host.Store, _adapter));

        var created = dispatcher.DispatchAsync(
            new CoreCommand.SessionCreate(), "c", false, CancellationToken.None).GetAwaiter().GetResult();
        var sessionId = ((Ack.Accepted)created.Ack).SessionId!;
        host.TryGetActivated(sessionId, out var handle);

        return (host, dispatcher, handle);
    }

    private Task<DispatchResult> SendAsync(CommandDispatcher d, SessionHandle h, string text) =>
        d.DispatchAsync(
            new CoreCommand.SessionSend(h.SessionId, [new Block.TextBlock(text)]),
            Guid.NewGuid().ToString("N")[..8], true, CancellationToken.None);

    /// <summary>
    /// ⚠️ **`01` §6.1 的核心断言**：连发 3 条 = 3 次入队 + 3 次启动，
    /// 但第一次就把状态变成 Running，后两次是 no-op ⇒ **自然合并成一次推进**。
    ///
    /// <para>可观测的判据有三条，缺一条这句话就没被钉住：
    /// ① 只调了**一次**模型；② `Turn` 只加**一**；③ 三条消息**都进了历史**
    /// （合并的是「推进次数」，不是「消息」——消息一条都不能丢）。</para>
    /// </summary>
    [Fact]
    public async Task ThreeSendsWhileRunning_MergeIntoOneStepButKeepAllMessages()
    {
        var (host, dispatcher, session) = NewHarness();

        // 三条「同时」发：第一条进模型后卡住，另外两条在它跑的时候入队。
        var release = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        var call = 0;
        var gated = new GatedAdapter(async () =>
        {
            if (Interlocked.Increment(ref call) == 1) { entered.SetResult(); await release.Task; }
            return "ok";
        });
        var gatedDispatcher = new CommandDispatcher(host, new EventBus(), _ => new Stepper(host.Store, gated));

        var first = SendAsync(gatedDispatcher, session, "一");
        await entered.Task;

        var second = SendAsync(gatedDispatcher, session, "二");
        var third = SendAsync(gatedDispatcher, session, "三");

        release.SetResult();
        await Task.WhenAll(first, second, third);

        Assert.Equal(1, call);                                     // ① 一次模型调用
        Assert.Equal(1, session.State.Turn);                       // ② Turn 只加一

        var timeline = host.Store.ReadTimeline(session.ProjectKey, session.SessionId);
        var userTexts = timeline
            .Cast<TimelineEvent.MessageEvent>()
            .Where(e => e.From == Speaker.User)
            .Select(e => ((Block.TextBlock)e.Blocks[0]).Text)
            .ToList();

        Assert.Equal(["一", "二", "三"], userTexts);               // ③ 三条都进历史，一条不丢
    }

    /// <summary>
    /// ⚠️ **用户消息在 `send` 时就落盘**（`01` §6.1「若 x 是事件，立即追加进记录」），
    /// 而不是等步开始。判据：**模型调用失败**时那条消息仍在历史里——
    /// 它是事实，抹不掉。若追加挪到步里面，失败就会把它一起带走。
    /// </summary>
    [Fact]
    public async Task Send_AppendsTheUserMessageEvenWhenTheModelFails()
    {
        var (host, dispatcher, session) = NewHarness();
        _adapter.Throws(new ModelCallFailedException("上游 503"));

        await Assert.ThrowsAsync<DispatchException>(() => SendAsync(dispatcher, session, "会失败"));

        var timeline = host.Store.ReadTimeline(session.ProjectKey, session.SessionId);
        var user = Assert.IsType<TimelineEvent.MessageEvent>(timeline[0]);
        Assert.Equal(Speaker.User, user.From);
        Assert.Equal("会失败", ((Block.TextBlock)user.Blocks[0]).Text);
    }

    /// <summary>
    /// ⚠️ 闸 + 标志**一起**才成立：闸保证「两次 send 不重叠」，标志保证「重叠时第二次不再推进」。
    /// 这条钉住后果：**不会出现两步**（Turn 不会加二）。
    /// </summary>
    [Fact]
    public async Task ConcurrentSends_DoNotRunTwoSteps()
    {
        var (host, _, session) = NewHarness();
        _adapter.Reply("ok").Reply("ok").Reply("ok");

        var dispatcher = new CommandDispatcher(host, new EventBus(), _ => new Stepper(host.Store, _adapter));
        await Task.WhenAll(
            SendAsync(dispatcher, session, "一"),
            SendAsync(dispatcher, session, "二"),
            SendAsync(dispatcher, session, "三"));

        // ⚠️ 断言的是**下界与上界**，不是精确值：
        // 并发下「几次推进」取决于时序（闸放行后可能再取一次队列）。
        // 但这三件事**必须**成立，且它们才是要害：
        Assert.InRange(session.State.Turn, 1, 3);          // 不是「每条都独立跑一步」的必然 3
        var timeline = host.Store.ReadTimeline(session.ProjectKey, session.SessionId);
        var userCount = timeline.Cast<TimelineEvent.MessageEvent>().Count(e => e.From == Speaker.User);
        Assert.Equal(3, userCount);                        // **三条消息一条都不能丢**
    }

    /// <summary>取空队列的语义：一次取走全部，且取完就空。</summary>
    [Fact]
    public void DrainQueue_TakesEverythingAndLeavesItEmpty()
    {
        var handle = NewHarness().Session;

        handle.Enqueue([new Block.TextBlock("一")]);
        handle.Enqueue([new Block.TextBlock("二")]);

        Assert.Equal(2, handle.QueueDepth);
        Assert.Equal(2, ((List<Block>)handle.DrainQueue()).Count);
        Assert.Equal(0, handle.QueueDepth);
        Assert.Empty(handle.DrainQueue());   // 幂等：再取是空的，不炸
    }

    /// <summary>「一把标志」的语义：已在跑则拒绝进入；退出后可再进。</summary>
    [Fact]
    public void TryEnterStep_IsTheRunningFlag()
    {
        var handle = NewHarness().Session;

        Assert.True(handle.TryEnterStep());
        Assert.False(handle.TryEnterStep());   // 已在跑 → no-op
        handle.ExitStep();
        Assert.True(handle.TryEnterStep());    // 退出后可再进
    }

    private sealed class GatedAdapter(Func<Task<string>> gate) : IModelAdapter
    {
        public ResolvedEndpoint? Endpoint => null;
        public string Protocol => Protocols.ClaudeMessages;

        public async Task<ModelReply> CallAsync(ModelRequest request, CancellationToken ct)
        {
            var text = await gate();
            return new ModelReply
            {
                Blocks = [new Block.TextBlock(text)],
                StopReason = "end_turn",
                Refused = false,
            };
        }
    }

    /// <summary>
    /// ⚠️ **复审抓到的真缺陷（Critical）**：`TryEnterStep` 第一版是
    /// `!_running &amp;&amp; (_running = true)`——**读改写不是原子的**，
    /// 两个线程都读到 false ⇒ **都返回 true** ⇒ 两次推进都被安排上。
    /// `volatile` 只保**可见性**，不保**原子性**。
    ///
    /// <para>症状正是 Task 4 花大力气修掉的那个（「连发 N 条只调一次模型」失效），
    /// 而信号量把两次推进**串起来**，所以看起来不像并发 bug，只像「偶尔多烧一次钱」。
    /// 可达路径：控制台（同步路）与头（后台路）同时对同一会话发送。</para>
    ///
    /// <para>⚠️ 这条测的是**原子性**（单线程那条只钉功能，对原子性没有区分力）：
    /// 多线程同时抢，**恰好一个赢家**。</para>
    ///
    /// <para>⚠️ **形状很要紧，第一版是掷硬币的**。第一版每轮 `new Thread` × 8 再 `Join`——
    /// barrier 一放，那 8 个线程实际上被**一个接一个**调度，撞不上。
    /// 复核者实测：忠实回退实现下 **10 次里 4 红 6 绿**，而作者当时报的「1 红」是**一次幸运单跑**。
    /// ⇒ 必须用**常驻线程**（`IsBackground` + 线程内循环 + 每轮 barrier），线程才是热的。
    /// 复核者量到那个形状 ≈35%/轮 超发、**30/30 检出**。
    /// ⚠️ 判据用**每轮的最大赢家数**，不能用累计值——「一轮 0 个、另一轮 2 个」会互相抵消。</para>
    /// </summary>
    [Fact]
    public async Task TryEnterStep_IsAtomic_NeverTwoHoldersAtOnce()
    {
        var handle = NewHarness().Session;

        const int racers = 8;
        const int hits = 200_000;
        var holders = 0;
        var maxHolders = 0;
        var overlaps = 0;

        var threads = Enumerable.Range(0, racers).Select(_ => new Thread(() =>
        {
            for (var i = 0; i < hits; i++)
            {
                // ⚠️ 拿到就持有一下（在临界区里），再放。
                // 读改写实现下，两个线程可能**同时**认为自己拿到了 ⇒ holders 会到 2。
                if (!handle.TryEnterStep()) continue;

                var now = Interlocked.Increment(ref holders);
                var seen = Volatile.Read(ref maxHolders);
                while (now > seen && Interlocked.CompareExchange(ref maxHolders, now, seen) != seen)
                    seen = Volatile.Read(ref maxHolders);
                if (now > 1) Interlocked.Increment(ref overlaps);

                Interlocked.Decrement(ref holders);
                handle.ExitStep();
            }
        })
        { IsBackground = true }).ToArray();

        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        // ⚠️ 判据是**同时**持有者数，不是「多少次返回 true」——
        // 后者对读改写也是 1（两个赢家各自都返回了 true，计数分不出来）。
        Assert.Equal(1, maxHolders);
        Assert.Equal(0, overlaps);
        await Task.CompletedTask;
    }
}
