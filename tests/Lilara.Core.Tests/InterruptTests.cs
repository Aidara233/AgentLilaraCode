using Lilara.Core.Adapters;
using Lilara.Core.Canonical;
using Lilara.Core.Control;
using Lilara.Core.Hosting;
using Lilara.Core.Persistence;
using Lilara.Core.Providers;
using Lilara.Core.Session;

namespace Lilara.Core.Tests;

/// <summary>
/// 中断（`03` §3 `session.interrupt`）。
///
/// <para>⚠️ 这些测试之所以存在，是因为**中断在核心侧曾经完全没实现**——
/// `CancellationToken` 管道通到底，但调用方**永远传 `None`**。
/// 而「实现错了」的代价特别大：一个长命的 CTS 会把**下一次**的步也变成已取消，
/// 于是裸的 `OperationCanceledException` 冒到前端（REPL 只接 `ModelCallFailedException`）
/// **打死常驻进程**。</para>
/// </summary>
public class InterruptTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-int-" + Guid.NewGuid().ToString("N"));
    private readonly string _work =
        Path.Combine(Path.GetTempPath(), "lilara-intwork-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
    }

    private (CoreHost Host, EventBus Bus, string SessionId) NewHarness(
        Func<Task<string>> gate, out CommandDispatcher dispatcher)
    {
        var host = new CoreHost(_root, _work);
        host.Providers.Save(new ProviderDefinition
        {
            Id = "p", Protocol = Protocols.ClaudeMessages, ApiKey = "k",
            DefaultModel = "m", Modality = "vision",
        });
        var bus = new EventBus();
        var adapter = new GatedAdapter(gate);
        var d = new CommandDispatcher(host, bus, _ => new Stepper(host.Store, adapter));
        dispatcher = d;

        var created = d.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None)
            .GetAwaiter().GetResult();
        return (host, bus, ((Ack.Accepted)created.Ack).SessionId!);
    }

    private static Task<DispatchResult> SendAsync(CommandDispatcher d, string sessionId, string text, bool awaitCompletion) =>
        d.DispatchAsync(new CoreCommand.SessionSend(sessionId, [new Block.TextBlock(text)]),
            Guid.NewGuid().ToString("N")[..8], awaitCompletion, CancellationToken.None);

    /// <summary>
    /// ⚠️ **控制面那条路的要害**：`awaitCompletion: false` 时步**在后台跑**，
    /// 命令**立刻**回来。没有它，`session.interrupt` 无事可做——
    /// 步要是在命令里同步跑完，interrupt 根本轮不到执行。
    /// </summary>
    [Fact]
    public async Task AsyncMode_ReturnsImmediatelyWhileTheStepRunsInTheBackground()
    {
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var (host, _, sessionId) = NewHarness(async () => { entered.SetResult(); await release.Task; return "ok"; },
            out var dispatcher);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await SendAsync(dispatcher, sessionId, "一", awaitCompletion: false);
        sw.Stop();

        // ⚠️ 断言的是「**远早于**模型返回就回来了」，不是精确毫秒。
        Assert.True(sw.ElapsedMilliseconds < 1000, "awaitCompletion:false 必须立刻回执");
        await entered.Task;   // 但步确实开始了

        release.SetResult();
        await Task.Delay(200);   // 让它收尾
    }

    /// <summary>
    /// ⚠️ 中断**不是失败**：不许报 `error`，且**不许把异常漏出去**。
    /// 漏出去的那个异常在后台路上是**未观测异常**——.NET 上会**直接终结进程**。
    /// </summary>
    [Fact]
    public async Task Interrupt_DoesNotEmitAnErrorEvent()
    {
        var entered = new TaskCompletionSource();
        var (host, bus, sessionId) = NewHarness(
            async () => { entered.SetResult(); await Task.Delay(30_000); return "never"; },
            out var dispatcher);

        using var sub = bus.Subscribe(EventBus.SessionTopic(sessionId));

        await SendAsync(dispatcher, sessionId, "一", awaitCompletion: false);
        await entered.Task;

        var r = await dispatcher.DispatchAsync(
            new CoreCommand.SessionInterrupt(sessionId), "i", true, CancellationToken.None);
        Assert.IsType<Ack.Accepted>(r.Ack);

        // 等中断传导到步里
        await Task.Delay(300);

        var payloads = new List<WireEvent>();
        while (sub.Reader.TryRead(out var p)) payloads.Add(p.Payload);

        // ⚠️ **一次正常中断不该产生 `error`**——否则头会开始忽略 error，
        // 而那正是它最该看的东西。
        Assert.Empty(payloads.OfType<WireEvent.Error>());

        // 而是回到 Idle
        var last = payloads.Last(e => e is WireEvent.StateChanged);
        Assert.Equal("Idle", ((WireEvent.StateChanged)last).Status);

        // 且 **Turn 不增**——这是一次**没完成的步**，不是一次失败的步
        Assert.Equal(0, host.Activated.First().State.Turn);
    }

    /// <summary>
    /// ⚠️ **空闲时 interrupt 必须是 no-op、且不许污染下一次**。
    ///
    /// <para>这是「长命 CTS」那个灾难的回归测试：若 CTS 是每会话一个长命的，
    /// 空闲期的 interrupt 会把它 Cancel，而**下一次**步的 token 出生即已取消 ⇒
    /// 适配器判「调用方取消」⇒ 裸异常冒到前端 ⇒ 打死进程。
    /// ⇒ 断言「先 interrupt、再正常发一条，那条**必须成功**」。</para>
    /// </summary>
    [Fact]
    public async Task InterruptWhileIdle_DoesNotPoisonTheNextStep()
    {
        var (host, bus, sessionId) = NewHarness(async () => "ok", out var dispatcher);

        // 空闲时中断（此刻没有步在跑）
        var r = await dispatcher.DispatchAsync(
            new CoreCommand.SessionInterrupt(sessionId), "i1", true, CancellationToken.None);
        Assert.IsType<Ack.Accepted>(r.Ack);

        using var sub = bus.Subscribe(EventBus.SessionTopic(sessionId));

        // ⚠️ 紧接着正常发一条——它必须**完整跑完**
        var result = await SendAsync(dispatcher, sessionId, "二", awaitCompletion: true);

        Assert.NotNull(result.Reply);
        Assert.Equal("ok", ((Block.TextBlock)result.Reply!.Blocks[0]).Text);

        var payloads = new List<WireEvent>();
        while (sub.Reader.TryRead(out var p)) payloads.Add(p.Payload);
        Assert.Empty(payloads.OfType<WireEvent.Error>());
        Assert.Equal(1, host.Activated.First().State.Turn);   // 这一步真的完成了
    }

    /// <summary>中断一个不存在的会话 ⇒ `rejected`（命令本身不合法）。</summary>
    [Fact]
    public async Task InterruptAnUnknownSession_IsRejected()
    {
        var (_, _, _) = NewHarness(async () => "ok", out var dispatcher);

        var r = await dispatcher.DispatchAsync(
            new CoreCommand.SessionInterrupt("nope"), "i", true, CancellationToken.None);

        Assert.IsType<Ack.Rejected>(r.Ack);
    }

    /// <summary>
    /// ⚠️ **「长命 CTS」那个灾难的**真正**回归测试**（上一条只覆盖了空闲中断那条路，
    /// 而那条路上 CTS 根本没被创建过——所以它区分不出 CTS 的生命周期）。
    ///
    /// <para>灾难场景要**先真的取消过一次**：步在跑 → 中断 → 那个 CTS **已是已取消状态**
    /// → 若它被**复用**（长命），下一次步的 token **出生即已取消** ⇒
    /// 适配器判「调用方取消」⇒ 不包装 ⇒ 裸 `OperationCanceledException`
    /// 冒到前端（REPL 只接 `ModelCallFailedException`）⇒ **打死常驻进程**。</para>
    ///
    /// <para>⇒ 断言「中断过一轮之后，**再发的那条必须完整跑完**」。</para>
    /// </summary>
    [Fact]
    public async Task AfterAnInterruptedStep_TheNextStepStillCompletes()
    {
        var call = 0;
        var entered = new TaskCompletionSource();
        var (host, _, sessionId) = NewHarness(async () =>
        {
            if (Interlocked.Increment(ref call) == 1) { entered.SetResult(); await Task.Delay(30_000); }
            return "第二次";
        }, out var dispatcher);

        await SendAsync(dispatcher, sessionId, "一", awaitCompletion: false);
        await entered.Task;

        await dispatcher.DispatchAsync(new CoreCommand.SessionInterrupt(sessionId), "i", true, CancellationToken.None);
        await Task.Delay(300);   // 让取消传导完、步收尾

        // ⚠️ **关键**：这一条必须完整跑完。若 CTS 被复用，它的 token 出生即已取消。
        var result = await SendAsync(dispatcher, sessionId, "二", awaitCompletion: true);

        Assert.NotNull(result.Reply);
        Assert.Equal("第二次", ((Block.TextBlock)result.Reply!.Blocks[0]).Text);
    }

    /// <summary>
    /// ⚠️ **同步路（控制台）的失败要抛给调用方**——控制台印一行人话、REPL 继续活着。
    /// 后台路则**必须吞掉**（未观测异常会终结进程）。两条路的差别就在这里，
    /// 而它由 `throwOnFailure` 一个开关控制——**别把两条路合并成一条**。
    /// </summary>
    [Fact]
    public async Task SyncMode_PropagatesFailureForTheConsoleToPrint()
    {
        var (_, _, sessionId) = NewHarness(
            () => Task.FromException<string>(new ModelCallFailedException("上游 503")), out var dispatcher);

        var ex = await Assert.ThrowsAsync<DispatchException>(
            () => SendAsync(dispatcher, sessionId, "一", awaitCompletion: true));

        Assert.Contains("上游 503", ex.Message);
    }

    private sealed class GatedAdapter(Func<Task<string>> gate) : IModelAdapter
    {
        public ResolvedEndpoint? Endpoint => null;
        public string Protocol => Protocols.ClaudeMessages;

        public async Task<ModelReply> CallAsync(ModelRequest request, CancellationToken ct)
        {
            var text = await gate().WaitAsync(ct);
            return new ModelReply
            {
                Blocks = [new Block.TextBlock(text)],
                StopReason = "end_turn",
                Refused = false,
            };
        }
    }
}
