using Lilara.Core.Protocol;
using Lilara.Core.Control;
using Lilara.Core.Kernel;
using Lilara.Core.Hosting;
using Lilara.Core.Storage;
using Lilara.Core.Providers;
using Lilara.Core.Session;
using Lilara.Core.Tests.Fakes;

namespace Lilara.Core.Tests;

// CommandDispatcher 是将来头直接依赖的表面；ReplTests 走整条链证明没改坏，但证明不了 dispatcher 自己是对的。
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
        return (host, new CommandDispatcher(
            host, new EventBus(), _ => new Stepper(host.Store, _adapter), _ => _adapter));
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

    // dispatcher 必须按该会话自己的 provider 解析 stepper；共用一把会把 B 的全量历史发到 A 的端点上。
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

    // 同一会话的两步不许重叠，但入队不许被挡住——第二条卡在闸上就没机会入队，「合并是队列的副产品」不成立。
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

        // 第二个必须立刻回来（只入队，不推进）
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

    // rejected 只用于「命令本身不合法」；会话不存在 ⇒ rejected（用户把 id 写错了）。
    [Fact]
    public async Task UnknownSession_IsRejected()
    {
        var (host, dispatcher) = NewHarness();

        var r = await dispatcher.DispatchAsync(
            new CoreCommand.SessionSend("nope", [new Block.TextBlock("x")]), "c", true, CancellationToken.None);

        Assert.IsType<Ack.Rejected>(r.Ack);
        Assert.Contains("nope", ((Ack.Rejected)r.Ack).Reason);
    }

    // 模型失败不是 rejected——它是 DispatchException（→ 走 error 事件）；
    // 混了头会把网络抖动当成「命令写错了」，让用户去改一个没写错的命令。
    [Fact]
    public async Task ModelFailure_ThrowsDispatchExceptionNotRejected()
    {
        var (host, _) = NewHarness();
        SaveProvider(host);
        _adapter.Throws(new Lilara.Core.Protocol.ModelCallFailedException("上游 503"));

        var dispatcher = new CommandDispatcher(host, new EventBus(), h => new Stepper(host.Store, _adapter));
        var sessionId = await CreateSessionAsync(dispatcher);

        var ex = await Assert.ThrowsAsync<DispatchException>(() => dispatcher.DispatchAsync(
            new CoreCommand.SessionSend(sessionId, [new Block.TextBlock("x")]), "c", true, CancellationToken.None));

        Assert.Contains("上游 503", ex.Message);
    }

    // session.create 是分配型命令——必须回传新身份，否则后续命令无从指涉。
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

    // 未知协议必须在 dispatcher 里被拒（不是只在控制台），否则线上 provider.upsert 能写进控制台写不进的坏值。
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
        Assert.Null(host.Providers.Load("bad"));   // 且不落盘
    }

    // 未知 auth 同理，而且它是静默改行为：AuthSchemes.Bearer 的字面量是小写 bearer，
    // 手写 Bearer（大写）序号不等，会被悄悄当成 x-api-key 发出去。
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

    // stepper 缓存的失效点必须在 dispatcher 里——挂在控制台的字符串判据上，
    // 线上 provider.upsert 走不到它，缓存静默失效，用户改了 provider 却还在用旧的。
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

    // 省略 key = 保留旧 key；头改协议时必须能不带 key 走通，不然永远改不了已有 provider。
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
        Assert.Equal("k", after.ApiKey);                          // 盘上仍是旧 key
        Assert.Equal(Protocols.OpenAiChat, after.Protocol);       // 而这一项真的改了
    }

    // 新 provider 必须给 key：否则会存下一个发不出去的配置，比拒绝更糟（用户以为配好了，第一次发消息才炸）。
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
        Assert.Contains("brand-new", rejected.Reason);            // 理由要点名那个 id——
                                                                  // 用户手打了三个 provider 时，光说「必须给 key」不够
        Assert.Null(host.Providers.Load("brand-new"));            // 且没有半成品落盘
    }

    // InvalidateSteppers() 挂在 HandleProviderUpsert 的末尾，「省略 key」这条路径最自然的写法是提前 return
    // ⇒ 改了协议但缓存没失效 = 用旧适配器发新协议。
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

        Assert.NotSame(before, host.StepperFor(handle));          // 缓存必须换掉
    }

    // 空串与「缺失」等价（都是「没给」）；判据必须与 /providers 的 hasApiKey 共用同一个词（IsNullOrEmpty）——
    // 否则新 provider 带一个空串就绕过守卫、被 Accepted、存下一个空 key。
    [Fact]
    public async Task ProviderUpsert_WithAnEmptyApiKey_ForANewProvider_IsRejected()
    {
        var (host, _) = NewHarness();
        SaveProvider(host);
        var dispatcher = new CommandDispatcher(host, new EventBus(), host.StepperFor);

        var r = await dispatcher.DispatchAsync(
            new CoreCommand.ProviderUpsert("brand-new", "", null, null, null, null),
            "c", true, CancellationToken.None);

        Assert.IsType<Ack.Rejected>(r.Ack);                       // 不是 Accepted（空 key 发不出去）
        Assert.Null(host.Providers.Load("brand-new"));            // 不留半成品
    }

    // 已有 provider 带空串 ⇒ 保留旧 key，不是抹掉：旧判据 p.ApiKey ?? existing!.ApiKey 判 "" 非 null
    // ⇒ 把存着的 key 覆盖成空。
    [Fact]
    public async Task ProviderUpsert_WithAnEmptyApiKey_KeepsTheStoredOne()
    {
        var (host, _) = NewHarness();
        SaveProvider(host);                                       // 既有 provider（key="k"）
        var dispatcher = new CommandDispatcher(host, new EventBus(), host.StepperFor);

        await dispatcher.DispatchAsync(
            new CoreCommand.ProviderUpsert("p", "", null, Protocols.OpenAiChat, null, null),
            "c", true, CancellationToken.None);

        var after = host.Providers.Load("p")!;
        Assert.Equal("k", after.ApiKey);                          // 空输入框不该抹掉 key
        Assert.Equal(Protocols.OpenAiChat, after.Protocol);       // 而这一项真的改了
    }

    // 删掉之后盘上真的没有了；只断言 ack 是半条判据——用户以为删了、下次发消息还在用那个凭据。
    [Fact]
    public async Task ProviderDelete_RemovesTheStoredDefinition()
    {
        var (host, _) = NewHarness();
        SaveProvider(host);
        var dispatcher = new CommandDispatcher(host, new EventBus(), host.StepperFor);

        var r = await dispatcher.DispatchAsync(
            new CoreCommand.ProviderDelete("p"), "c", true, CancellationToken.None);

        Assert.IsType<Ack.Accepted>(r.Ack);
        Assert.Null(host.Providers.Load("p"));                       // 读不到了
        Assert.Empty(host.Providers.List());                        // 且列表里也没了
    }

    // 损坏的 provider 文件必须删得掉（判据是文件层，不是 Load is null）；
    // 其余三条删除测试用的是能正常解析的文件，抓不到这个改动。
    [Fact]
    public async Task ProviderDelete_OfACorruptProviderFile_IsAccepted_NotRejected()
    {
        var (host, _) = NewHarness();
        // 不能走 Providers.Save（那写的必定是合法 JSON）——直接往盘上写一个坏文件。
        var dir = Path.Combine(_root, "providers");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "broken.json");
        File.WriteAllText(file, "{ 这不是合法 JSON");

        // 先确认前提成立：Load 对它是 null（所以「改成 Load」会把它当不存在）。
        Assert.Null(host.Providers.Load("broken"));

        var dispatcher = new CommandDispatcher(host, new EventBus(), host.StepperFor);
        var r = await dispatcher.DispatchAsync(
            new CoreCommand.ProviderDelete("broken"), "c", true, CancellationToken.None);

        Assert.IsType<Ack.Accepted>(r.Ack);        // 改了判据这里会变 Rejected
        Assert.False(File.Exists(file));           // 且盘上真的没了
    }

    // 删一个不存在的 id ⇒ Rejected 且理由点名那个 id，不是幂等成功；
    // provider delet local（拼错）若回 ✓，用户以为删掉了、其实没删，下次发消息用的还是那个凭据。
    // 拒绝时不许顺手清缓存、也不许动别的文件——一次没做成的命令不该有任何副作用。
    [Fact]
    public async Task ProviderDelete_OfAProviderThatDoesNotExist_IsRejected_NamingTheId()
    {
        var (host, _) = NewHarness();
        SaveProvider(host);                                          // 盘上有 "p"
        var dispatcher = new CommandDispatcher(host, new EventBus(), host.StepperFor);

        var r = await dispatcher.DispatchAsync(
            new CoreCommand.ProviderDelete("local"), "c", true, CancellationToken.None);

        var rejected = Assert.IsType<Ack.Rejected>(r.Ack);
        Assert.Contains("local", rejected.Reason);                   // 理由点名那个 id
        Assert.NotNull(host.Providers.Load("p"));                    // 别的 provider 一根汗毛没动
    }

    // 删一个正被会话引用的 provider 不阻止（想改啥都行；用户可能就是要把一个坏配置删掉），
    // 但后果必须可见：删完 InvalidateSteppers()，被引用的会话下次发消息走 StepperResolver 的
    // 「绑定的提供商已不存在」那条路。必须先 StepperFor 一次把缓存捂热，
    // 否则对「InvalidateSteppers 被删掉」没有区分力。
    [Fact]
    public async Task ProviderDelete_OfAProviderBoundByASession_IsAccepted_NotBlocked()
    {
        var (host, _) = NewHarness();
        SaveProvider(host);
        var dispatcher = new CommandDispatcher(host, new EventBus(), host.StepperFor);
        var sessionId = await CreateSessionAsync(dispatcher);        // 会话绑到 "p"
        host.TryGetActivated(sessionId, out var handle);

        var before = host.StepperFor(handle);                        // 把缓存捂热（见上）

        var r = await dispatcher.DispatchAsync(
            new CoreCommand.ProviderDelete("p"), "c", true, CancellationToken.None);

        Assert.IsType<Ack.Accepted>(r.Ack);                          // 不阻止（没有「谁在用」的闸）
        Assert.Null(host.Providers.Load("p"));                       // 而且真删了

        var after = host.StepperFor(handle);                         // 下一次解析走的是……
        Assert.NotSame(before, after);                               // 缓存真的失效了
        Assert.IsType<UnconfiguredAdapter>(after.Adapter);           // 「绑定的提供商已不存在」那条路
    }

    // 失效点必须在 dispatcher 里（与 upsert 同源）：挂在控制台的字符串判据上，
    // 线上的 provider.delete 走不到它 ⇒ 缓存静默失效，用户删了 provider 而会话还在用那个配置跑。
    [Fact]
    public async Task ProviderDelete_InvalidatesTheStepperCache()
    {
        var (host, _) = NewHarness();
        SaveProvider(host);
        var dispatcher = new CommandDispatcher(host, new EventBus(), host.StepperFor);
        var sessionId = await CreateSessionAsync(dispatcher);
        host.TryGetActivated(sessionId, out var handle);

        var before = host.StepperFor(handle);
        await dispatcher.DispatchAsync(
            new CoreCommand.ProviderDelete("p"), "c", true, CancellationToken.None);

        Assert.NotSame(before, host.StepperFor(handle));
    }

    // 删一个正被别的句柄读着的 provider 必须回 Rejected（带理由），不是把异常抛出 DispatchAsync——
    // 抛出去会冒到 Program.cs 的主循环（那里没有 catch）把常驻进程打死。
    // 句柄带 FileShare.Read（不含 FILE_SHARE_DELETE）⇒ Windows 上 File.Delete 抛 IOException。
    [Fact]
    public async Task ProviderDelete_WhileAReadHandleIsOpen_IsRejectedWithAReason_NotAnException()
    {
        var (host, _) = NewHarness();
        SaveProvider(host);
        var dispatcher = new CommandDispatcher(host, new EventBus(), host.StepperFor);

        var file = Path.Combine(_root, "providers", "p.json");
        using var hold = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read);

        var r = await dispatcher.DispatchAsync(
            new CoreCommand.ProviderDelete("p"), "c", true, CancellationToken.None);

        var rejected = Assert.IsType<Ack.Rejected>(r.Ack);
        Assert.Contains("p", rejected.Reason);
        // 层 2 的定制消息——既不是原始异常文本，更不是那句假话「没有提供商 'p'」（它在，只是忙）。
        Assert.Contains("正被占用", rejected.Reason);
    }

    // 任何 handler 抛出的意外异常都必须变成 rejected，不许冒出去打死常驻进程；
    // DispatchException 另走一路，既有测试钉着它必须原样抛出。
    [Fact]
    public async Task UnexpectedExceptionFromAHandler_BecomesARejection_NotAnEscapingException()
    {
        var (host, _) = NewHarness();
        SaveProvider(host);
        var dispatcher = new CommandDispatcher(host, new EventBus(), host.StepperFor);

        // 让落盘必然失败：`providers/` 从目录换成一个同名文件 ⇒ `CreateDirectory` 抛 `IOException`。
        var providersPath = Path.Combine(_root, "providers");
        Directory.Delete(providersPath, recursive: true);
        File.WriteAllText(providersPath, "not a directory");

        var r = await dispatcher.DispatchAsync(
            new CoreCommand.ProviderUpsert("q", "k", null, null, null, null), "c", true, CancellationToken.None);

        var rejected = Assert.IsType<Ack.Rejected>(r.Ack);
        Assert.Contains("命令执行时出错", rejected.Reason);
    }

    // 挂起的适配器：让测试能精确控制「模型什么时候返回」。
    private sealed class GatedAdapter(Func<Task<string>> gate) : Lilara.Core.Protocol.IModelAdapter    {
        public Lilara.Core.Protocol.ResolvedEndpoint? Endpoint => null;
        public string Protocol => Protocols.ClaudeMessages;

        public async Task<Lilara.Core.Protocol.ModelReply> CallAsync(
            Lilara.Core.Protocol.ModelRequest request, CancellationToken ct)
        {
            var text = await gate();
            return new Lilara.Core.Protocol.ModelReply
            {
                Blocks = [new Block.TextBlock(text)],
                StopReason = "end_turn",
                Refused = false,
            };
        }
    }

    // 事件必须从 dispatcher 发，控制台那条路也要发；否则用户在控制台敲的那句话在头上是看不见的，
    // 头的视图会静默少一步，且它没办法发现自己少了。
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

        // 顺序固定：用户消息（send 时就发）→ 步开始 → 模型消息 → 步结束 → 状态。
        Assert.Equal(
            ["MessageAppended", "StepBegin", "MessageAppended", "StepEnd", "StateChanged"],
            kinds);

        // 且 seq 严格递增（同一把锁分配的）
        Assert.False(sub.NeedsResync);
    }

    // 拒答也必须走到 step.end（Stepper 的拒答分支不写模型消息，但 Turn 照样递增）；
    // 挂在「模型消息之后」会漏掉拒答，头永远等不到 step.end。
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
        // 拒答不产生模型消息——只有那条用户消息
        Assert.Single(payloads.OfType<WireEvent.MessageAppended>());
    }

    // ---- provider.test：自检的失败是**业务结果**，不是命令不合法 ----

    [Fact]
    public async Task ProviderTest_WhenTheModelAnswers_IsAcceptedWithAReachableNote()
    {
        var (host, d) = NewHarness();
        SaveProvider(host);
        _adapter.Reply("pong");

        var r = await d.DispatchAsync(
            new CoreCommand.ProviderTest("p"), "t", true, CancellationToken.None);

        var ack = Assert.IsType<Ack.Accepted>(r.Ack);
        Assert.Contains("通", ack.Note);
        // 自检不能跳过请求：真的发了一次（否则「通」是假的）。
        Assert.Single(_adapter.Calls);
    }

    // ⚠️ 这条钉住 03 §3 的分界：连不上是**业务结果**，走 Accepted{Note}，
    // **绝不能是 Rejected**——否则一次网络抖动会被说成「你的命令写错了」。
    [Fact]
    public async Task ProviderTest_WhenTheCallFails_IsStillAccepted_NotRejected()
    {
        var (host, d) = NewHarness();
        SaveProvider(host);
        _adapter.Throws(new ModelCallFailedException("连不上上游"));

        var r = await d.DispatchAsync(
            new CoreCommand.ProviderTest("p"), "t", true, CancellationToken.None);

        var ack = Assert.IsType<Ack.Accepted>(r.Ack);   // ← 不是 Rejected
        Assert.Contains("不通", ack.Note);
        Assert.Contains("连不上上游", ack.Note);
    }

    [Fact]
    public async Task ProviderTest_ForAMissingProvider_IsRejected()
    {
        var (_, d) = NewHarness();

        var r = await d.DispatchAsync(
            new CoreCommand.ProviderTest("p"), "t", true, CancellationToken.None);

        Assert.IsType<Ack.Rejected>(r.Ack);
        Assert.Contains("没有提供商", ((Ack.Rejected)r.Ack).Reason);
    }

    // 同一个闸：坏 id 在任何网络动作之前就被挡下（它本可以是 providers/ 之外的路径）。
    [Theory]
    [InlineData("../escaped")]
    [InlineData("NUL")]
    [InlineData("a/b")]
    public async Task ProviderTest_WithAnUnsafeId_IsRejectedWithoutCallingTheModel(string id)
    {
        var (host, d) = NewHarness();
        SaveProvider(host);

        var r = await d.DispatchAsync(
            new CoreCommand.ProviderTest(id), "t", true, CancellationToken.None);

        Assert.IsType<Ack.Rejected>(r.Ack);
        Assert.Empty(_adapter.Calls);   // 闸在任何网络动作之前
    }
}
