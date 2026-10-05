using Lilara.Core.Adapters;
using Lilara.Core.Control;
using Lilara.Core.Canonical;
using Lilara.Core.Hosting;
using Lilara.Core.Persistence;
using Lilara.Core.Providers;
using Lilara.Core.Session;
using Lilara.Core.Tests.Fakes;

namespace Lilara.Core.Tests;

/// <summary>
/// ⚠️ 这些测试存在的原因是**结构性的**：`CommandDispatcher` 是**头**将来直接依赖的表面。
/// 既有 `ReplTests` 走的是「文本 → Repl → dispatcher」整条链，它们能证明没改坏，
/// 但**证明不了 dispatcher 自己是对的**（比如头不带 `--flag` 语法，直接用类型化命令）。
/// </summary>
public class CommandDispatcherTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-disp-" + Guid.NewGuid().ToString("N"));
    private readonly string _work =
        Path.Combine(Path.GetTempPath(), "lilara-dispwork-" + Guid.NewGuid().ToString("N"));

    private readonly FakeAdapter _adapter = new();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
    }

    private (CoreHost Host, CommandDispatcher Dispatcher) NewHarness()
    {
        var host = new CoreHost(_root, _work);
        return (host, new CommandDispatcher(host, new EventBus(), _ => new Stepper(host.Store, _adapter)));
    }

    private static void SaveProvider(CoreHost host, string protocol = Protocols.ClaudeMessages) =>
        host.Providers.Save(new ProviderDefinition
        {
            Id = "p", Protocol = protocol, ApiKey = "k", DefaultModel = "m", Modality = "vision",
        });

    private static async Task<string> CreateSessionAsync(CommandDispatcher d)
    {
        var r = await d.DispatchAsync(new CoreCommand.SessionCreate(), "c1", false, CancellationToken.None);
        return ((Ack.Accepted)r.Ack).SessionId!;
    }

    /// <summary>
    /// ⚠️ **数据外泄那条的运行时证明**：dispatcher 必须按**该会话自己的** provider 解析 stepper。
    /// 共用一把的话，给 B 发消息会把 B 的全量历史发到 A 的端点上。
    /// （`CoreHostTests` 在宿主层钉过一次；这里钉的是 **dispatcher 真的用了它**。）
    /// </summary>
    [Fact]
    public async Task Send_UsesTheSessionsOwnStepper()
    {
        var (host, _) = NewHarness();
        SaveProvider(host);
        _adapter.Reply("ok");

        var seen = new List<string>();
        var dispatcher = new CommandDispatcher(host, new EventBus(), h =>
        {
            seen.Add(h.SessionId);
            return new Stepper(host.Store, _adapter);
        });

        var sessionId = await CreateSessionAsync(dispatcher);
        await dispatcher.DispatchAsync(
            new CoreCommand.SessionSend(sessionId, [new Block.TextBlock("你好")]), "c2", true, CancellationToken.None);

        Assert.Equal([sessionId], seen);
    }

    /// <summary>
    /// ⚠️ **同一会话的两步不许重叠**——但**入队不许被挡住**。
    ///
    /// <para>⚠️ 这条测试在 Task 4 被**改过断言**，理由正当，记在此处：
    /// Task 3 时 `send` = 直接跑一步，所以「第二个 send 必须**阻塞**到第一个放行」是对的。
    /// Task 4 按 `01` §6.1 改成「send = 入队 + 安排启动」后，第二个 send **必须立刻返回**
    /// （它只入队，不推进）——**否则「合并是队列的副产品」永远不成立**：
    /// 卡在闸上的第二条根本没机会入队，连发 3 条就真的调 3 次模型（第一次实测就是 `call=3`）。
    /// ⇒ 不变量从「第二个阻塞」变成「**两步不重叠** + 第二个立即返回」。
    /// 防重叠的职责也从闸（覆盖全部）收窄成 `TryEnterStep` 那把标志（只管推进）。</para>
    /// </summary>
    [Fact]
    public async Task Send_DoesNotOverlapTwoStepsButDoesNotBlockEnqueueing()
    {
        var (host, _) = NewHarness();
        SaveProvider(host);

        var firstEntered = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();
        var concurrent = 0;
        var maxConcurrent = 0;
        var call = 0;

        var gated = new GatedAdapter(async () =>
        {
            var n = Interlocked.Increment(ref call);
            var now = Interlocked.Increment(ref concurrent);
            maxConcurrent = Math.Max(maxConcurrent, now);
            if (n == 1) { firstEntered.SetResult(); await releaseFirst.Task; }
            Interlocked.Decrement(ref concurrent);
            return "ok";
        });

        var dispatcher = new CommandDispatcher(host, new EventBus(), _ => new Stepper(host.Store, gated));
        var sessionId = await CreateSessionAsync(dispatcher);

        var first = dispatcher.DispatchAsync(
            new CoreCommand.SessionSend(sessionId, [new Block.TextBlock("一")]), "a", true, CancellationToken.None);
        await firstEntered.Task;   // 第一个已经在模型里了

        // ⚠️ 第二个必须**立刻**回来（只入队，不推进）
        var second = await dispatcher.DispatchAsync(
            new CoreCommand.SessionSend(sessionId, [new Block.TextBlock("二")]), "b", true, CancellationToken.None);
        Assert.IsType<Ack.Accepted>(second.Ack);

        // 给它足够时间去**重叠**——如果防重叠失效，它现在就在模型里了
        await Task.Delay(150);
        Assert.Equal(1, Volatile.Read(ref maxConcurrent));

        releaseFirst.SetResult();
        await first;

        Assert.Equal(1, maxConcurrent);   // 全程没有两个步同时在飞
    }

    /// <summary>
    /// ⚠️ `rejected` **只用于「命令本身不合法」**（`03` §3）。
    /// 会话不存在 ⇒ rejected（用户把 id 写错了）。
    /// </summary>
    [Fact]
    public async Task UnknownSession_IsRejected()
    {
        var (host, dispatcher) = NewHarness();

        var r = await dispatcher.DispatchAsync(
            new CoreCommand.SessionSend("nope", [new Block.TextBlock("x")]), "c", true, CancellationToken.None);

        Assert.IsType<Ack.Rejected>(r.Ack);
        Assert.Contains("nope", ((Ack.Rejected)r.Ack).Reason);
    }

    /// <summary>
    /// ⚠️ 而**模型失败不是 rejected**——它是 `DispatchException`（→ 将来走 `error` 事件）。
    /// 混了的话，头会把一次网络抖动当成「你的命令写错了」，
    /// 然后让用户去改一个没写错的命令。
    /// </summary>
    [Fact]
    public async Task ModelFailure_ThrowsDispatchExceptionNotRejected()
    {
        var (host, _) = NewHarness();
        SaveProvider(host);
        _adapter.Throws(new Lilara.Core.Adapters.ModelCallFailedException("上游 503"));

        var dispatcher = new CommandDispatcher(host, new EventBus(), h => new Stepper(host.Store, _adapter));
        var sessionId = await CreateSessionAsync(dispatcher);

        var ex = await Assert.ThrowsAsync<DispatchException>(() => dispatcher.DispatchAsync(
            new CoreCommand.SessionSend(sessionId, [new Block.TextBlock("x")]), "c", true, CancellationToken.None));

        Assert.Contains("上游 503", ex.Message);
    }

    /// <summary>⚠️ `session.create` 是**分配型命令**——必须回传新身份，否则后续命令无从指涉。</summary>
    [Fact]
    public async Task SessionCreate_ReturnsTheNewSessionId()
    {
        var (host, dispatcher) = NewHarness();
        SaveProvider(host);

        var id = await CreateSessionAsync(dispatcher);

        Assert.False(string.IsNullOrEmpty(id));
        // 并且必须**登记进激活表**——否则快照端点会从磁盘读到过期的 Idle
        Assert.True(host.TryGetActivated(id, out _));
    }

    /// <summary>
    /// ⚠️ **未知协议必须在 dispatcher 里被拒**（不是只在控制台）。
    /// 否则线上的 `provider.upsert` 能写进一个控制台写不进的坏值——
    /// 而「两个前端行为不一致」正是 `04` §1.1 要消灭的东西。
    /// </summary>
    [Theory]
    [InlineData("gemini")]
    [InlineData("Claude-Messages")]   // 大小写：看着像，但不是
    public async Task ProviderUpsert_RejectsAnUnknownProtocol(string protocol)
    {
        var (host, dispatcher) = NewHarness();

        var r = await dispatcher.DispatchAsync(
            new CoreCommand.ProviderUpsert("bad", "k", null, protocol, null, null),
            "c", true, CancellationToken.None);

        Assert.IsType<Ack.Rejected>(r.Ack);
        Assert.Null(host.Providers.Load("bad"));   // 且**不落盘**
    }

    /// <summary>
    /// ⚠️ 未知 auth 同理，而且它是**静默改行为**：`AuthSchemes.Bearer` 的字面量是小写 `bearer`，
    /// 手写 `Bearer`（大写）序号不等，会被悄悄当成 x-api-key 发出去。
    /// </summary>
    [Fact]
    public async Task ProviderUpsert_RejectsAnUnknownAuth()
    {
        var (host, dispatcher) = NewHarness();

        var r = await dispatcher.DispatchAsync(
            new CoreCommand.ProviderUpsert("bad", "k", null, null, null, "Bearer"),
            "c", true, CancellationToken.None);

        Assert.IsType<Ack.Rejected>(r.Ack);
        Assert.Null(host.Providers.Load("bad"));
    }

    /// <summary>
    /// ⚠️ **stepper 缓存的失效点必须在 dispatcher 里**（不能挂在控制台的字符串判据上——
    /// 线上 `provider.upsert` 走不到它 ⇒ 缓存**静默失效**，用户改了 provider 却还在用旧的）。
    /// 这条直接钉住「upsert 之后缓存确实空了」。
    /// </summary>
    [Fact]
    public async Task ProviderUpsert_InvalidatesTheStepperCache()
    {
        var (host, _) = NewHarness();
        SaveProvider(host);

        var dispatcher = new CommandDispatcher(host, new EventBus(), host.StepperFor);
        var sessionId = await CreateSessionAsync(dispatcher);
        host.TryGetActivated(sessionId, out var handle);

        var before = host.StepperFor(handle);
        await dispatcher.DispatchAsync(
            new CoreCommand.ProviderUpsert("p", "k2", null, null, null, null),
            "c", true, CancellationToken.None);

        Assert.NotSame(before, host.StepperFor(handle));
    }

    /// <summary>
    /// ⚠️ **省略 key = 保留旧 key**（R-B：头读不到 key，不该逼它重发）。
    /// 头改协议时**必须**能不带 key 走通——不然它永远改不了已有 provider。
    /// </summary>
    [Fact]
    public async Task ProviderUpsert_WithoutApiKey_KeepsTheStoredOne()
    {
        var (host, _) = NewHarness();
        SaveProvider(host);                                       // 既有 provider（key="k"）
        var dispatcher = new CommandDispatcher(host, new EventBus(), host.StepperFor);

        await dispatcher.DispatchAsync(
            new CoreCommand.ProviderUpsert("p", null, null, Protocols.OpenAiChat, null, null),
            "c", true, CancellationToken.None);

        var after = host.Providers.Load("p")!;
        Assert.Equal("k", after.ApiKey);                          // ⚠️ 盘上**仍是旧 key**
        Assert.Equal(Protocols.OpenAiChat, after.Protocol);       // 而这一项**真的改了**
    }

    /// <summary>
    /// ⚠️ 新 provider **必须**给 key：否则会存下一个**发不出去的配置**，比拒绝更糟
    /// （用户以为配好了，第一次发消息才炸）。
    /// </summary>
    [Fact]
    public async Task ProviderUpsert_WithoutApiKey_ForANewProvider_IsRejected()
    {
        var (host, _) = NewHarness();
        SaveProvider(host);
        var dispatcher = new CommandDispatcher(host, new EventBus(), host.StepperFor);

        var r = await dispatcher.DispatchAsync(
            new CoreCommand.ProviderUpsert("brand-new", null, null, null, null, null),
            "c", true, CancellationToken.None);

        var rejected = Assert.IsType<Ack.Rejected>(r.Ack);
        Assert.Contains("brand-new", rejected.Reason);            // ⚠️ 理由要**点名那个 id**——
                                                                  // 用户手打了三个 provider 时，光说「必须给 key」不够
        Assert.Null(host.Providers.Load("brand-new"));            // ⚠️ 且**没有半成品落盘**
    }

    /// <summary>
    /// ⚠️ **第三条不是凑数**：`InvalidateSteppers()` 挂在 `HandleProviderUpsert` 的**末尾**，
    /// 而「省略 key」这条路径最自然的写法就是**提前 return** ⇒ **改了协议但缓存没失效**
    /// = 用旧适配器发新协议（正是本文件里 `ProviderUpsert_InvalidatesTheStepperCache` 防的那件事，
    /// 换了个入口回来）。
    /// ⇒ 范本就在上面那条，**唯一的区别是这次不带 key**。
    /// </summary>
    [Fact]
    public async Task ProviderUpsert_WithoutApiKey_StillInvalidatesSteppers()
    {
        var (host, _) = NewHarness();
        SaveProvider(host);
        var dispatcher = new CommandDispatcher(host, new EventBus(), host.StepperFor);
        var sessionId = await CreateSessionAsync(dispatcher);
        host.TryGetActivated(sessionId, out var handle);

        var before = host.StepperFor(handle);
        await dispatcher.DispatchAsync(
            new CoreCommand.ProviderUpsert("p", null, null, Protocols.OpenAiChat, null, null),
            "c", true, CancellationToken.None);

        Assert.NotSame(before, host.StepperFor(handle));          // ⚠️ 缓存必须换掉
    }

    /// <summary>挂起的适配器：让测试能精确控制「模型什么时候返回」。</summary>
    private sealed class GatedAdapter(Func<Task<string>> gate) : Lilara.Core.Adapters.IModelAdapter
    {
        public Lilara.Core.Adapters.ResolvedEndpoint? Endpoint => null;
        public string Protocol => Protocols.ClaudeMessages;

        public async Task<Lilara.Core.Adapters.ModelReply> CallAsync(
            Lilara.Core.Adapters.ModelRequest request, CancellationToken ct)
        {
            var text = await gate();
            return new Lilara.Core.Adapters.ModelReply
            {
                Blocks = [new Block.TextBlock(text)],
                StopReason = "end_turn",
                Refused = false,
            };
        }
    }

    /// <summary>
    /// ⚠️ **事件必须从 dispatcher 发，而且控制台那条路也要发**。
    ///
    /// <para>`03` §8.1 只说控制台**不订阅**任何流，**没说它的命令不产生事件**。
    /// 若事件由控制面自己发（或发在 `Stepper` 里），那么**用户在控制台敲的那句话
    /// 在头上是看不见的**——头的视图会**静默**少一步，且它没办法发现自己少了。
    /// 这条测试直接走 dispatcher（控制台走的就是它），断言事件确实出来了。</para>
    /// </summary>
    [Fact]
    public async Task Send_PublishesTheStepLifecycleEvents()
    {
        var host = new CoreHost(_root, _work);
        SaveProvider(host);
        _adapter.Reply("ok");

        var bus = new EventBus();
        var dispatcher = new CommandDispatcher(host, bus, _ => new Stepper(host.Store, _adapter));
        var sessionId = await CreateSessionAsync(dispatcher);

        using var sub = bus.Subscribe(EventBus.SessionTopic(sessionId));

        await dispatcher.DispatchAsync(
            new CoreCommand.SessionSend(sessionId, [new Block.TextBlock("你好")]), "c", true, CancellationToken.None);

        var kinds = new List<string>();
        while (sub.Reader.TryRead(out var p)) kinds.Add(p.Payload.GetType().Name);

        // ⚠️ 四条都要在，且顺序固定：
        //   用户消息（send 时就发）→ 步开始 → 模型消息 → 步结束 → 状态
        Assert.Equal(
            ["MessageAppended", "StepBegin", "MessageAppended", "StepEnd", "StateChanged"],
            kinds);

        // 且 seq 严格递增（同一把锁分配的）
        Assert.False(sub.NeedsResync);
    }

    /// <summary>
    /// ⚠️ **拒答也必须走到 `step.end`**。`Stepper` 的拒答分支**不写模型消息**，
    /// 但 `Turn` 照样递增。把 `step.end` 挂在「模型消息之后」会**漏掉拒答**——
    /// 于是头看着一次拒答像「什么都没发生」，并且**永远等不到 step.end**，会以为那一步卡住了。
    /// </summary>
    [Fact]
    public async Task Refusal_StillPublishesStepEnd()
    {
        var host = new CoreHost(_root, _work);
        SaveProvider(host);
        _adapter.Reply("", stopReason: "refusal");
        var bus = new EventBus();

        var dispatcher = new CommandDispatcher(host, bus, _ => new Stepper(host.Store, _adapter));
        var sessionId = await CreateSessionAsync(dispatcher);
        using var sub = bus.Subscribe(EventBus.SessionTopic(sessionId));

        await dispatcher.DispatchAsync(
            new CoreCommand.SessionSend(sessionId, [new Block.TextBlock("x")]), "c", true, CancellationToken.None);

        var payloads = new List<WireEvent>();
        while (sub.Reader.TryRead(out var p)) payloads.Add(p.Payload);

        var end = Assert.IsType<WireEvent.StepEnd>(payloads.Last(e => e is WireEvent.StepEnd));
        Assert.True(end.Refused);
        Assert.Equal(1, end.Turn);
        // ⚠️ 拒答**不产生**模型消息——只有那条用户消息
        Assert.Single(payloads.OfType<WireEvent.MessageAppended>());
    }
}
