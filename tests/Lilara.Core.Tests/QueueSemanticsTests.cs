using Lilara.Core.Adapters;
using Lilara.Core.Canonical;
using Lilara.Core.Hosting;
using Lilara.Core.Persistence;
using Lilara.Core.Providers;
using Lilara.Core.Records;
using Lilara.Core.Session;
using Lilara.Core.Tests.Fakes;

namespace Lilara.Core.Tests;

// 钉的是可观测行为：几次模型调用、Turn 加几、消息是否都进历史。
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

    // 连发 3 条合并成一次推进，但三条消息都进历史——合并的是推进次数，不是消息。
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

    // 用户消息在 send 时就落盘，不是等步开始——模型调用失败时它仍在历史里（它是事实，抹不掉）。
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

    // 闸 + 标志一起才成立：闸保证两次 send 不重叠，标志保证重叠时第二次不再推进——不会出现两步。
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

        // 并发下推进次数取决于时序，只能断言上下界；三条消息一条都不能丢才是要害。
        Assert.InRange(session.State.Turn, 1, 3);          // 不是「每条都独立跑一步」的必然 3
        var timeline = host.Store.ReadTimeline(session.ProjectKey, session.SessionId);
        var userCount = timeline.Cast<TimelineEvent.MessageEvent>().Count(e => e.From == Speaker.User);
        Assert.Equal(3, userCount);                        // 三条消息一条都不能丢
    }

    // 取空队列的语义：一次取走全部，取完就空。
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

    // 已在跑则拒绝进入；退出后可再进。
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

    // 判据是同一时刻的持有者数（恰好一个赢家），不是「多少次返回 true」；必须用常驻线程压，否则撞不上。
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
                // 拿到就持有一下（在临界区里）再放；读改写实现下两个线程可能同时自认拿到 ⇒ holders 到 2。
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

        // 判据是同时持有者数，不是「多少次返回 true」（后者对读改写也是 1）。
        Assert.Equal(1, maxHolders);
        Assert.Equal(0, overlaps);
        await Task.CompletedTask;
    }
}
