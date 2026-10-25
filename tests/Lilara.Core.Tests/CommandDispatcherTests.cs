using Lilara.Core.Protocol;
using Lilara.Core.Control;
using Lilara.Core.Kernel;
using Lilara.Core.Hosting;
using Lilara.Core.Storage;
using Lilara.Core.Providers;
using Lilara.Core.Session;
using Lilara.Core.Session.Tools;
using Lilara.Core.Tests.Fakes;

namespace Lilara.Core.Tests;

// CommandDispatcher 是将来头直接依赖的表面；ReplTests 走整条链证明没改坏，但证明不了 dispatcher 自己是对的。
public class CommandDispatcherTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-disp-" + Guid.NewGuid().ToString("N"));
    private readonly string _work =
        Path.Combine(Path.GetTempPath(), "lilara-dispwork-" + Guid.NewGuid().ToString("N"));
    private CoreHost? _host;

    private readonly FakeAdapter _adapter = new();

    public void Dispose()
    {
        // ⚠️ **删临时根之前，先停掉所有仍在后台跑的会话**（2026-10-24 实测）：
        // `spawn` 之后子会话由 `Start` 走 `Task.Run` 在后台推进，测试方法返回时它可能
        // **还在写 `spec.json` / `history.jsonl` / `floating.jsonl`**。Windows 上一个
        // `FileShare.Read` 句柄就足以让整棵 `Directory.Delete` 抛 `IOException`，
        // 而那会把一条无关的回归测试打红（全量并发实测复现）。⇒ 先对所有激活会话
        // 做 `StopSubtree` 同形的取消 + epoch，再退避删（与 `SessionStore.DeleteSession`）。
        if (_host is { } host)
        {
            foreach (var handle in host.Activated)
            {
                handle.NoteInterrupt();
                try { host.CtsFor(handle.SessionId)?.Cancel(); }
                catch (ObjectDisposedException) { /* 步已结束；句柄已摘。 */ }
            }
        }

        Remove(_root);
        Remove(_work);

        static void Remove(string path)
        {
            for (var attempt = 0; ; attempt++)
            {
                if (!Directory.Exists(path)) return;

                try
                {
                    Directory.Delete(path, recursive: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                              && attempt < 20)
                {
                    Thread.Sleep(10 * (attempt + 1));
                }
            }
        }
    }

    private (CoreHost Host, CommandDispatcher Dispatcher) NewHarness()
    {
        var host = new CoreHost(_root, _work);
        _host = host;
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

    // 2026-10-22：压缩改成「下发指令 + 推一步」，不再是「核心自己跑一次模型调用」。
    // 这一条钉两件事：① `session.compact` 只落指令并推进（下一次调用里看得到它）；
    // ② 模型调 `compact` 之后检查点落地、指令被清。
    [Fact]
    public async Task Compact_RecordsARequest_AndTheNextStepHonoursIt()
    {
        var host = new CoreHost(_root, _work);
        SaveProvider(host);

        var adapter = new FakeAdapter()
            .Reply("seed")
            .ReplyBlocks(new Block.ToolUseBlock(
                "c1", "compact", """{"requested":true,"summary":"摘要"}"""))
            .Reply("done");
        // ⚠️ 工具表必须带上内建工具：压缩是模型调 `compact` 完成的，没有工具表那一步只会
        // 得到一条「不存在的工具」的结果——而**那不会报错**，只是检查点永远不出现。
        // 尾块那句话也要带上，否则「指令进没进请求」没法按请求本身判。
        var dispatcher = new CommandDispatcher(host, new EventBus(),
            _ => new Stepper(host.Store, adapter, BuiltInTools.All,
                pendingCompactText: CorePrompts.CompactDirective),
            _ => adapter);
        var sessionId = await CreateSessionAsync(dispatcher);

        await dispatcher.DispatchAsync(
            new CoreCommand.SessionSend(sessionId, [new Block.TextBlock("seed")]),
            "seed", true, CancellationToken.None);

        var compact = await dispatcher.DispatchAsync(
            new CoreCommand.SessionCompact(sessionId), "compact", false, CancellationToken.None);
        Assert.IsType<Ack.Accepted>(compact.Ack);

        var projectKey = host.Store.FindSession(sessionId)!.Value.ProjectKey;
        // 指令被消费（检查点在它之前落、状态在它之后清 —— 见 `Stepper.CommitCompaction` 的次序）。
        await WaitUntil(() => new CompactionStore(host.Store).ReadAll(projectKey, sessionId).Count == 1);
        await WaitUntil(() => host.Store.ReadState(projectKey, sessionId).PendingCompact == false);

        // ⚠️ 判据是**真正发出去的请求**（`FakeAdapter.Calls`）：第一次（`seed`）不带压缩指令，
        // 第二次（模型在里面调 `compact`）带。
        Assert.True(adapter.Calls.Count >= 2);
        Assert.DoesNotContain(CorePrompts.CompactDirective, DirectiveText(adapter.Calls[0]));
        Assert.Contains(CorePrompts.CompactDirective, DirectiveText(adapter.Calls[1]));
    }

    /// <summary>
    /// 请求里所有「核心注入的文字」。⚠️ **尾块通常不在 `Slots.Tail` 里**：
    /// 工具表有 `floating_context` 时它被挪进末尾那条合成调用/结果（`Stepper.FloatingContextPair`），
    /// 而两者对模型是同一件事——只看 `Slots.Tail` 会得到一个假阴性。
    /// </summary>
    private static string DirectiveText(ModelRequest request) =>
        string.Concat(request.Slots.Tail.OfType<Block.TextBlock>().Select(b => b.Text))
        + string.Concat(request.Timeline.OfType<TimelineEvent.ToolResultEvent>()
            .SelectMany(r => r.Content.OfType<Block.TextBlock>()).Select(b => b.Text));

    // 指令是**持久状态**：它写进 `state.json`，而不是只活在内存里。
    // ⚠️ 这条刻意用**拒绝一切**的适配器 + 没有工具表的推进器：那一步一定失败、一点不动盘，
    // 于是「指令还在」只可能来自 `session.compact` 那次写入本身
    // （否则「指令被消费」与「指令没写进去」在断言上长得一样）。
    [Fact]
    public async Task Compact_PersistsTheRequestToDisk()
    {
        var host = new CoreHost(_root, _work);
        SaveProvider(host);
        var failing = new ThrowingAdapter();
        var dispatcher = new CommandDispatcher(host, new EventBus(), _ => new Stepper(host.Store, failing),
            _ => failing);
        var sessionId = await CreateSessionAsync(dispatcher);

        // 先有一条用户消息（空会话会被这条命令拒绝）。
        AddUserMessage(host, sessionId, "seed");

        var projectKey = host.Store.FindSession(sessionId)!.Value.ProjectKey;
        var r = await dispatcher.DispatchAsync(
            new CoreCommand.SessionCompact(sessionId), "compact", false, CancellationToken.None);
        Assert.IsType<Ack.Accepted>(r.Ack);

        await WaitUntil(() => failing.Called > 0);   // 那一步真的试过（然后失败了）
        Assert.True(host.Store.ReadState(projectKey, sessionId).PendingCompact);
    }

    // 模型在拿到指令后的**第一回合没有调工具就结束了** ⇒ 续跑一步，而记录里**没有**假用户消息
    // （2026-10-22：`session.compact` 没有新输入，靠的就是这条续跑；塞一条假消息会污染记录）。
    [Fact]
    public async Task Compact_KeepsPushing_UntilTheModelCallsTheTool()
    {
        var host = new CoreHost(_root, _work);
        SaveProvider(host);

        var adapter = new FakeAdapter()
            .Reply("seed")
            .Reply("我需要先看一下再总结。")          // 停回合、没调工具
            .ReplyBlocks(new Block.ToolUseBlock(
                "c1", "compact", """{"requested":true,"summary":"摘要"}"""))
            .Reply("done");
        var dispatcher = new CommandDispatcher(host, new EventBus(),
            _ => new Stepper(host.Store, adapter, BuiltInTools.All,
                pendingCompactText: CorePrompts.CompactDirective),
            _ => adapter);
        var sessionId = await CreateSessionAsync(dispatcher);

        await dispatcher.DispatchAsync(
            new CoreCommand.SessionSend(sessionId, [new Block.TextBlock("seed")]),
            "seed", true, CancellationToken.None);
        await dispatcher.DispatchAsync(
            new CoreCommand.SessionCompact(sessionId), "compact", false, CancellationToken.None);

        var projectKey = host.Store.FindSession(sessionId)!.Value.ProjectKey;
        await WaitUntil(() => new CompactionStore(host.Store).ReadAll(projectKey, sessionId).Count == 1);

        // 用户消息只有 `seed` 那一条：续跑不能往记录里写东西。
        var userMessages = host.Store.ReadTimeline(projectKey, sessionId)
            .OfType<TimelineEvent.MessageEvent>().Count(m => m.From == Speaker.User);
        Assert.Equal(1, userMessages);
        Assert.True(adapter.Calls.Count >= 3);   // seed → 「先看一下」→ compact（+ 可能还有一步收尾）
    }

    private static void AddUserMessage(CoreHost host, string sessionId, string text)
    {
        var found = host.Store.FindSession(sessionId)!.Value;
        host.Store.AppendEvent(found.ProjectKey, sessionId, new TimelineEvent.MessageEvent
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            At = CoreClock.Now(),
            From = Speaker.User,
            Blocks = [new Block.TextBlock(text)],
        });
    }

    private sealed class ThrowingAdapter : Lilara.Core.Protocol.IModelAdapter
    {
        private int _called;
        public int Called => Volatile.Read(ref _called);
        public Lilara.Core.Protocol.ResolvedEndpoint? Endpoint => null;
        public string Protocol => Protocols.ClaudeMessages;

        public Task<Lilara.Core.Protocol.ModelReply> CallAsync(
            Lilara.Core.Protocol.ModelRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _called);
            throw new ModelCallFailedException("测试：故意失败") { Transient = false };
        }
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("条件没有在时限内成立");
            await Task.Delay(10);
        }
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
        _adapter.Throws(new ModelCallFailedException("连不上上游") { Transient = false });

        var r = await d.DispatchAsync(
            new CoreCommand.ProviderTest("p"), "t", true, CancellationToken.None);

        var ack = Assert.IsType<Ack.Accepted>(r.Ack);   // ← 不是 Rejected
        Assert.Contains("不通", ack.Note);
        Assert.Contains("连不上上游", ack.Note);
    }

    // ⚠️ 自检走与正式调用**同一条**重试路（RetryPolicy）：一次 429 / 5xx 的老实回答是「再试一次」，
    // 不是「不通」。⚠️ 这会真的等一次退避（约 0.5s）——那是这条路应有的代价，别为了快把它改成 0。
    [Fact]
    public async Task ProviderTest_TransientFailure_IsRetriedThenReportsReachable()
    {
        var (host, d) = NewHarness();
        SaveProvider(host);
        _adapter.Throws(new ModelCallFailedException("上游 503") { Transient = true }).Reply("pong");

        var r = await d.DispatchAsync(
            new CoreCommand.ProviderTest("p"), "t", true, CancellationToken.None);

        var ack = Assert.IsType<Ack.Accepted>(r.Ack);
        Assert.Contains("通", ack.Note);
        Assert.Equal(2, _adapter.Calls.Count);

        // 两次真实请求各记一行账：失败那行带原因与次数，成功那行带 2。
        var calls = host.Usage.ReadAll();
        Assert.Equal(2, calls.Count);
        Assert.Equal("failed", calls[0].Status);
        Assert.Contains("上游 503", calls[0].Error);
        Assert.Equal("completed", calls[1].Status);
        Assert.Equal(2, calls[1].Attempt);
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

    // ---- provider.upsert：一次模型调用的超时（按提供商） ----

    // 超时必须真的落盘：只改内存的话，重启后悄悄回到默认，而用户以为「我调长过了」。
    [Fact]
    public async Task ProviderUpsert_WithTimeout_PersistsIt()
    {
        var (host, d) = NewHarness();
        SaveProvider(host);

        var r = await d.DispatchAsync(
            new CoreCommand.ProviderUpsert("p", null, null, null, null, null) { TimeoutSeconds = 600 },
            "t", true, CancellationToken.None);

        Assert.IsType<Ack.Accepted>(r.Ack);
        Assert.Equal(600, host.Providers.Load("p")!.TimeoutSeconds);
    }

    // 省略 ⇒ 保留旧值（`null` 在这条命令里是「本次没提到」，不是「清回默认」）——
    // 与 key / endpoint / models 同一条语义。只改 key 一次就把超时抹掉会很意外。
    [Fact]
    public async Task ProviderUpsert_WithoutTimeout_KeepsTheExistingOne()
    {
        var (host, d) = NewHarness();
        SaveProvider(host);
        await d.DispatchAsync(
            new CoreCommand.ProviderUpsert("p", null, null, null, "http://x.invalid", null) { TimeoutSeconds = 300 },
            "t1", true, CancellationToken.None);

        var r = await d.DispatchAsync(
            new CoreCommand.ProviderUpsert("p", "k2", null, null, null, null), "t2", true, CancellationToken.None);

        Assert.IsType<Ack.Accepted>(r.Ack);
        var loaded = host.Providers.Load("p")!;
        Assert.Equal(300, loaded.TimeoutSeconds);
        Assert.Equal("k2", loaded.ApiKey);   // 同一次里改的那一格照常生效
    }

    // 越界 ⇒ 拒绝，**不夹紧**：夹紧会把「我设了 0 秒」静默变成「1 秒」，
    // 而配置写错了没有人会知道。拒绝之后盘上不许留下半成品。
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1801)]
    [InlineData(99999)]
    public async Task ProviderUpsert_WithATimeoutOutOfRange_IsRejected(int seconds)
    {
        var (host, d) = NewHarness();
        SaveProvider(host);

        var r = await d.DispatchAsync(
            new CoreCommand.ProviderUpsert("p", null, null, null, "http://x.invalid", null) { TimeoutSeconds = seconds },
            "t", true, CancellationToken.None);

        Assert.IsType<Ack.Rejected>(r.Ack);
        // 被拒 ⇒ 别的字段也不许落盘（拒绝要挡在 Save 之前）。
        Assert.Null(host.Providers.Load("p")!.Endpoint);
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

    // ---- session.create：选项目 + 起名 ----

    // 不给项目 ⇒ 回落核心的工作目录（控制台 `session create` 一直就是这个行为）。
    [Fact]
    public async Task SessionCreate_WithoutAProject_LandsInTheWorkingDirectory()
    {
        var (host, d) = NewHarness();
        SaveProvider(host);

        var r = await d.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);

        var sessionId = ((Ack.Accepted)r.Ack).SessionId!;
        var found = host.Store.FindSession(sessionId)!;
        Assert.Equal(ProjectKey.FromPath(_work), found.Value.ProjectKey);
        Assert.Equal(Path.GetFullPath(_work), found.Value.Spec.WorkingDirectory);
    }

    // 给目录 ⇒ 落到**那个**项目里。这是本批新增的自由度。
    [Fact]
    public async Task SessionCreate_WithACwd_LandsThere()
    {
        var (host, d) = NewHarness();
        SaveProvider(host);
        var other = Path.Combine(Path.GetTempPath(), "lilara-other-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(other);

        try
        {
            var r = await d.DispatchAsync(
                new CoreCommand.SessionCreate { Cwd = other }, "c", false, CancellationToken.None);

            var sessionId = ((Ack.Accepted)r.Ack).SessionId!;
            var pk = ProjectKey.FromPath(other);

            Assert.Equal(pk, host.Store.FindSession(sessionId)!.Value.ProjectKey);
            Assert.Equal([sessionId], host.Store.ListSessions(pk));
            Assert.Contains(pk, host.Store.ListProjects());
        }
        finally
        {
            if (Directory.Exists(other)) Directory.Delete(other, recursive: true);
        }
    }

    // ⚠️ `session.created` 要发在**新会话自己的项目流**上，不是核心工作目录的。
    // 头订的是前者；发错了头永远等不到这个事件，新会话不会出现（且不会自愈）。
    [Fact]
    public async Task SessionCreate_WithACwd_PublishesOnThatProjectsTopic()
    {
        var (host, _) = NewHarness();
        SaveProvider(host);
        var bus = new EventBus();
        var d = new CommandDispatcher(host, bus, h => new Stepper(host.Store, _adapter), _ => _adapter);
        var other = Path.Combine(Path.GetTempPath(), "lilara-other-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(other);

        try
        {
            using var sub = bus.Subscribe(EventBus.ProjectTopic(ProjectKey.FromPath(other)));

            await d.DispatchAsync(
                new CoreCommand.SessionCreate { Cwd = other }, "c", false, CancellationToken.None);

            Assert.True(sub.Reader.TryRead(out var p), "新会话的项目流上必须收到 session.created");
            Assert.Equal(ProjectKey.FromPath(other),
                Assert.IsType<WireEvent.SessionCreated>(p.Payload).ProjectKey);
        }
        finally
        {
            if (Directory.Exists(other)) Directory.Delete(other, recursive: true);
        }
    }

    // ⚠️ 目录不存在 ⇒ 拒绝，**不建**目录：建目录是策略，核心不做策略（00 §1.3）。
    [Fact]
    public async Task SessionCreate_WithAMissingDirectory_IsRejected_AndCreatesNothing()
    {
        var (host, d) = NewHarness();
        SaveProvider(host);
        var ghost = Path.Combine(Path.GetTempPath(), "lilara-ghost-" + Guid.NewGuid().ToString("N"));

        var r = await d.DispatchAsync(
            new CoreCommand.SessionCreate { Cwd = ghost }, "c", false, CancellationToken.None);

        Assert.IsType<Ack.Rejected>(r.Ack);
        Assert.False(Directory.Exists(ghost), "核心不许替用户建目录");
    }

    // 相对目录也要落成**绝对**路径——否则 `SessionDirectory` 会拼出一个相对目录、
    // 会话根本不在数据根里（今天撞不到是因为唯一调用点给的是 `GetCurrentDirectory()`）。
    [Fact]
    public async Task SessionCreate_WithARelativeCwd_StoresAnAbsolutePath()
    {
        var (host, d) = NewHarness();
        SaveProvider(host);
        var other = Path.Combine(Path.GetTempPath(), "lilara-rel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(other);
        var previous = Directory.GetCurrentDirectory();

        try
        {
            // 直接给一个相对路径（`..` 这种也一起证明了会规范化），而不是去改进程的当前目录。
            var relative = Path.GetRelativePath(previous, other);

            var r = await d.DispatchAsync(
                new CoreCommand.SessionCreate { Cwd = relative }, "c", false, CancellationToken.None);

            var sessionId = ((Ack.Accepted)r.Ack).SessionId!;
            var spec = host.Store.FindSession(sessionId)!.Value.Spec;

            Assert.True(Path.IsPathFullyQualified(spec.WorkingDirectory));
            Assert.Equal(ProjectKey.FromPath(other), ProjectKey.FromPath(spec.WorkingDirectory));
        }
        finally
        {
            if (Directory.Exists(other)) Directory.Delete(other, recursive: true);
        }
    }

    [Fact]
    public async Task SessionCreate_WithATitle_PersistsIt()
    {
        var (host, d) = NewHarness();
        SaveProvider(host);

        var r = await d.DispatchAsync(
            new CoreCommand.SessionCreate { Title = "重构会话" }, "c", false, CancellationToken.None);

        var sessionId = ((Ack.Accepted)r.Ack).SessionId!;
        Assert.Equal("重构会话", host.Store.ReadSpec(ProjectKey.FromPath(_work), sessionId).Title);
    }

    // ⚠️ 空白标题要落成 **null**，不是 `""`：`Title` 可空正是磁盘格式不破的前提
    // （null ⇒ `WhenWritingNull` 省掉整个键）；存一个 `""` 进去就不用想着那条了。
    [Fact]
    public async Task SessionCreate_WithABlankTitle_StoresNull()
    {
        var (host, d) = NewHarness();
        SaveProvider(host);

        var r = await d.DispatchAsync(
            new CoreCommand.SessionCreate { Title = "   " }, "c", false, CancellationToken.None);

        var sessionId = ((Ack.Accepted)r.Ack).SessionId!;
        Assert.Null(host.Store.ReadSpec(ProjectKey.FromPath(_work), sessionId).Title);
    }

    // ---- session.configure：改绑供应商 / 换模型 ----

    // 落盘必须真的发生：只改内存的话，重启后会话悄悄退回旧供应商，而用户以为改过了。
    [Fact]
    public async Task Configure_ChangesTheModel_AndPersistsItToDisk()
    {
        var (host, d) = NewHarness();
        SaveProvider(host);
        var sessionId = await CreateSessionAsync(d);

        var r = await d.DispatchAsync(
            new CoreCommand.SessionConfigure(sessionId, null, "other-model"), "x", false, CancellationToken.None);

        Assert.IsType<Ack.Accepted>(r.Ack);

        // 内存里换了，且**盘上也换了**——两个都断，只断一个漏得掉「只改内存」。
        var reloaded = SessionHandle.Load(host.Store, ProjectKey.FromPath(_work), sessionId);
        Assert.Equal("other-model", reloaded.Spec.Model.ModelId);
    }

    // 换供应商必须让 stepper 缓存失效——否则下一步仍走**旧供应商的适配器**（旧 key、旧端点）。
    //
    // ⚠️ 这条必须走**真的** `host.StepperFor`，不能注入一个自己的 lambda：
    // 第一版我注入了一个「记下 providerId 再返回 stepper」的 lambda，**它是绿的，而变异也是绿的**——
    // 因为那个 lambda 每次 send 都被调、绕过了 `CoreHost._steppers` 那层缓存，
    // 于是「缓存没失效」这个缺陷根本不在我的判据里（探针 ≠ 被测物）。
    // 现在的判据是**缓存里那把适配器的身份**：同一个 handle 取两次 stepper，
    // 中间改绑 ⇒ 必须是**两个不同实例**（或至少是换了供应商的实例）。
    [Fact]
    public async Task Configure_ToAnotherProvider_ReResolvesTheStepper()
    {
        var (host, _) = NewHarness();
        SaveProvider(host);   // id = "p"
        host.Providers.Save(new ProviderDefinition
        {
            Id = "p2", Protocol = Protocols.OpenAiChat, ApiKey = "k2", DefaultModel = "m2", Modality = "vision",
        });

        // 生产那条路：dispatcher 用 host.StepperFor（走缓存），适配器注入只为不联网。
        var d = new CommandDispatcher(host, new EventBus(), host.StepperFor, _ => _adapter);

        var r = await d.DispatchAsync(new CoreCommand.SessionCreate(), "c1", false, CancellationToken.None);
        var sessionId = ((Ack.Accepted)r.Ack).SessionId!;
        var handle = host.Activated.Single(h => h.SessionId == sessionId);

        // 第一次解析 → 进缓存。
        var first = host.StepperFor(handle);
        Assert.Equal("p", handle.Spec.Model.ProviderId);

        await d.DispatchAsync(
            new CoreCommand.SessionConfigure(sessionId, "p2", null), "x", false, CancellationToken.None);

        // ⚠️ 这里必须重新取——缓存没失效的话拿回来的是**同一把**旧 stepper（旧适配器）。
        var second = host.StepperFor(handle);

        Assert.NotSame(first, second);
        Assert.Equal("p2", handle.Spec.Model.ProviderId);
    }

    // 反向那半：**只换模型**（同一个供应商）**不该**清缓存——白清会把别的会话的缓存也连累掉。
    // ⚠️ 这条与上一条是一对：只断「换供应商要失效」的话，
    // 把 InvalidateSteppers() 写成无条件的也会绿。
    [Fact]
    public async Task Configure_ToTheSameProvider_KeepsTheCachedStepper()
    {
        var (host, _) = NewHarness();
        SaveProvider(host);
        var d = new CommandDispatcher(host, new EventBus(), host.StepperFor, _ => _adapter);

        var r = await d.DispatchAsync(new CoreCommand.SessionCreate(), "c1", false, CancellationToken.None);
        var sessionId = ((Ack.Accepted)r.Ack).SessionId!;
        var handle = host.Activated.Single(h => h.SessionId == sessionId);

        var first = host.StepperFor(handle);

        await d.DispatchAsync(
            new CoreCommand.SessionConfigure(sessionId, null, "some-other-model"), "x", false, CancellationToken.None);

        Assert.Same(first, host.StepperFor(handle));
        Assert.Equal("some-other-model", handle.Spec.Model.ModelId);
    }

    // 换供应商时 Protocol 快照要跟着重取——否则它和 ProviderId 不同代，快照端点交出去的就是假话。
    [Fact]
    public async Task Configure_ToAnotherProvider_ReSnapshotsTheProtocol()
    {
        var (host, d) = NewHarness();
        SaveProvider(host);
        host.Providers.Save(new ProviderDefinition
        {
            Id = "p2", Protocol = Protocols.OpenAiChat, ApiKey = "k2", DefaultModel = "m2", Modality = "vision",
        });
        var sessionId = await CreateSessionAsync(d);

        Assert.Equal(Protocols.ClaudeMessages, host.Store.ReadSpec(ProjectKey.FromPath(_work), sessionId).Model.Protocol);

        await d.DispatchAsync(
            new CoreCommand.SessionConfigure(sessionId, "p2", null), "x", false, CancellationToken.None);

        Assert.Equal(Protocols.OpenAiChat, host.Store.ReadSpec(ProjectKey.FromPath(_work), sessionId).Model.Protocol);
    }

    [Fact]
    public async Task Configure_PublishesSpecChanged()
    {
        var (host, _) = NewHarness();
        SaveProvider(host);
        var bus = new EventBus();
        var d = new CommandDispatcher(host, bus, h => new Stepper(host.Store, _adapter), _ => _adapter);

        var r = await d.DispatchAsync(new CoreCommand.SessionCreate(), "c1", false, CancellationToken.None);
        var sessionId = ((Ack.Accepted)r.Ack).SessionId!;
        using var sub = bus.Subscribe(EventBus.SessionTopic(sessionId));

        await d.DispatchAsync(
            new CoreCommand.SessionConfigure(sessionId, null, "m9"), "x", false, CancellationToken.None);

        Assert.True(sub.Reader.TryRead(out var p), "改绑必须发 spec.changed——头靠它更新右栏");
        var evt = Assert.IsType<WireEvent.SpecChanged>(p.Payload);
        Assert.Equal("m9", evt.Model.ModelId);
    }

    // 同值不是迁移 ⇒ 不发事件（与 setAxis 同一条约定）。
    [Fact]
    public async Task Configure_ToTheSameModel_PublishesNothing()
    {
        var (host, _) = NewHarness();
        SaveProvider(host);
        var bus = new EventBus();
        var d = new CommandDispatcher(host, bus, h => new Stepper(host.Store, _adapter), _ => _adapter);

        var r = await d.DispatchAsync(new CoreCommand.SessionCreate(), "c1", false, CancellationToken.None);
        var sessionId = ((Ack.Accepted)r.Ack).SessionId!;

        // 建会话用的就是 provider 的 DefaultModel（"m"）⇒ 再设一次 "m" 是同值。
        _ = await d.DispatchAsync(
            new CoreCommand.SessionConfigure(sessionId, null, "m"), "x1", false, CancellationToken.None);

        using var sub = bus.Subscribe(EventBus.SessionTopic(sessionId));
        await d.DispatchAsync(
            new CoreCommand.SessionConfigure(sessionId, null, "m"), "x2", false, CancellationToken.None);

        Assert.False(sub.Reader.TryRead(out _), "设成同一个值不该发事件");
    }

    // 两个都没给 ⇒ 拒绝，不是「成功的 no-op」（那是把没说清的命令说成做成了）。
    [Fact]
    public async Task Configure_WithNeitherFieldIsRejected()
    {
        var (host, d) = NewHarness();
        SaveProvider(host);
        var sessionId = await CreateSessionAsync(d);

        var r = await d.DispatchAsync(
            new CoreCommand.SessionConfigure(sessionId, null, null), "x", false, CancellationToken.None);

        Assert.IsType<Ack.Rejected>(r.Ack);
    }

    // 换绑到一个**不存在**的供应商必须拒绝：这条命令正是为了修「绑的供应商没了」那个坏状态，
    // 放行等于把坏状态又写一遍。
    [Fact]
    public async Task Configure_ToAMissingProvider_IsRejected_AndLeavesTheSessionAlone()
    {
        var (host, d) = NewHarness();
        SaveProvider(host);
        var sessionId = await CreateSessionAsync(d);

        var r = await d.DispatchAsync(
            new CoreCommand.SessionConfigure(sessionId, "ghost", null), "x", false, CancellationToken.None);

        Assert.IsType<Ack.Rejected>(r.Ack);
        Assert.Contains("ghost", ((Ack.Rejected)r.Ack).Reason);
        // 被拒就不能动盘：否则用户看到一个「没换成功」却已经换掉的会话。
        Assert.Equal("p", host.Store.ReadSpec(ProjectKey.FromPath(_work), sessionId).Model.ProviderId);
    }

    // 会话不存在 ⇒ rejected（命令本身不合法）。
    [Fact]
    public async Task Configure_WithAnUnknownSession_IsRejected()
    {
        var (host, d) = NewHarness();
        SaveProvider(host);

        var r = await d.DispatchAsync(
            new CoreCommand.SessionConfigure("nope", null, "m"), "x", false, CancellationToken.None);

        Assert.IsType<Ack.Rejected>(r.Ack);
    }

    // ---- 销毁面：deactivate / archive / delete ----

    // 把会话推进到「在跑」。⚠️ 走**内存**（`Running` 从不落盘）——这正是要测的那一档：
    // 磁盘上它还是 Idle，所以只有「看内存」的判据才发现得了它。
    private static void MakeRunning(CoreHost host, string sessionId) =>
        host.Activated.Single(h => h.SessionId == sessionId).UpdateState(s => s with { Status = "Running" });

    [Fact]
    public async Task Deactivate_DropsTheInMemoryCopy_ButLeavesTheDiskAlone()
    {
        var (host, d) = NewHarness();
        SaveProvider(host);
        var sessionId = await CreateSessionAsync(d);
        Assert.True(host.TryGetActivated(sessionId, out _));

        var r = await d.DispatchAsync(
            new CoreCommand.SessionDeactivate(sessionId), "x", false, CancellationToken.None);

        Assert.IsType<Ack.Accepted>(r.Ack);
        Assert.False(host.TryGetActivated(sessionId, out _));
        // 磁盘零改动：目录还在、还能再激活。
        Assert.NotNull(host.Store.FindSession(sessionId));
        Assert.Equal(sessionId, host.Activate(ProjectKey.FromPath(_work), sessionId).SessionId);
    }

    // ⚠️ 「不存在」与「本来就没激活」**必须分开**：用户要能分辨自己敲错了 id
    // 还是那一刀砍在空气上。
    [Fact]
    public async Task Deactivate_OnAMissingSession_SaysMissing()
    {
        var (host, d) = NewHarness();
        SaveProvider(host);

        var r = await d.DispatchAsync(
            new CoreCommand.SessionDeactivate("nope"), "x", false, CancellationToken.None);

        Assert.Contains("不存在", ((Ack.Rejected)r.Ack).Reason);
    }

    [Fact]
    public async Task Deactivate_OnAnAlreadyInactiveSession_SaysSo_NotMissing()
    {
        var (host, d) = NewHarness();
        SaveProvider(host);
        var sessionId = await CreateSessionAsync(d);
        host.Deactivate(sessionId);

        var r = await d.DispatchAsync(
            new CoreCommand.SessionDeactivate(sessionId), "x", false, CancellationToken.None);

        var reason = ((Ack.Rejected)r.Ack).Reason;
        Assert.Contains("本来就没激活", reason);
        Assert.DoesNotContain("不存在", reason);
    }

    [Fact]
    public async Task Deactivate_PublishesOnBothTopics()
    {
        var (host, _) = NewHarness();
        SaveProvider(host);
        var bus = new EventBus();
        var d = new CommandDispatcher(host, bus, h => new Stepper(host.Store, _adapter), _ => _adapter);
        var sessionId = await CreateSessionAsync(d);

        using var session = bus.Subscribe(EventBus.SessionTopic(sessionId));
        using var project = bus.Subscribe(EventBus.ProjectTopic(ProjectKey.FromPath(_work)));

        await d.DispatchAsync(new CoreCommand.SessionDeactivate(sessionId), "x", false, CancellationToken.None);

        Assert.True(session.Reader.TryRead(out var s), "会话还在，会话流上要发");
        Assert.IsType<WireEvent.SessionDeactivated>(s.Payload);
        Assert.True(project.Reader.TryRead(out _), "项目流上也要发");
    }

    // 归档：原地标记，`ListSessions` **照列**——这是本批最要紧的不变量
    //（滤掉它会让 `/claims` 跟着一起漏，用户看到两份自相矛盾的真相）。
    [Fact]
    public async Task Archive_MarksInPlace_AndListSessionsStillShowsIt()
    {
        var (host, d) = NewHarness();
        SaveProvider(host);
        var sessionId = await CreateSessionAsync(d);

        var r = await d.DispatchAsync(
            new CoreCommand.SessionArchive(sessionId), "x", false, CancellationToken.None);

        Assert.IsType<Ack.Accepted>(r.Ack);
        Assert.NotNull(host.Store.ReadSpec(ProjectKey.FromPath(_work), sessionId).ArchivedAt);
        Assert.Equal([sessionId], host.Store.ListSessions(ProjectKey.FromPath(_work)));
    }

    // ⚠️ 三态的关键：`Archived` 不给（null）是**归档**，不是「没给就当没提」。
    // 这条钉的是「`bool Archived = false` 那个默认值写法」——那样写这条会红。
    [Fact]
    public async Task Archive_WithNoArgument_MeansArchive()
    {
        var (host, d) = NewHarness();
        SaveProvider(host);
        var sessionId = await CreateSessionAsync(d);

        var r = await d.DispatchAsync(
            new CoreCommand.SessionArchive(sessionId), "x", false, CancellationToken.None);

        Assert.IsType<Ack.Accepted>(r.Ack);
        Assert.NotNull(host.Store.ReadSpec(ProjectKey.FromPath(_work), sessionId).ArchivedAt);
    }

    [Fact]
    public async Task Archive_ThenUnarchive_ClearsIt()
    {
        var (host, d) = NewHarness();
        SaveProvider(host);
        var sessionId = await CreateSessionAsync(d);

        await d.DispatchAsync(new CoreCommand.SessionArchive(sessionId), "x", false, CancellationToken.None);
        var r = await d.DispatchAsync(
            new CoreCommand.SessionArchive(sessionId, Archived: false), "x", false, CancellationToken.None);

        Assert.IsType<Ack.Accepted>(r.Ack);
        Assert.Null(host.Store.ReadSpec(ProjectKey.FromPath(_work), sessionId).ArchivedAt);
    }

    // 幂等**不许当成功**：写成 no-op 的话用户永远分不清「存进去了」与「什么都没发生」。
    [Fact]
    public async Task Archive_WhenAlreadyArchived_IsRejected()
    {
        var (host, d) = NewHarness();
        SaveProvider(host);
        var sessionId = await CreateSessionAsync(d);
        await d.DispatchAsync(new CoreCommand.SessionArchive(sessionId), "x", false, CancellationToken.None);

        var r = await d.DispatchAsync(
            new CoreCommand.SessionArchive(sessionId), "x", false, CancellationToken.None);

        Assert.Contains("已经归档", ((Ack.Rejected)r.Ack).Reason);
    }

    [Fact]
    public async Task Unarchive_WhenNeverArchived_IsRejected()
    {
        var (host, d) = NewHarness();
        SaveProvider(host);
        var sessionId = await CreateSessionAsync(d);

        var r = await d.DispatchAsync(
            new CoreCommand.SessionArchive(sessionId, Archived: false), "x", false, CancellationToken.None);

        Assert.Contains("本来就没归档", ((Ack.Rejected)r.Ack).Reason);
    }

    // 取消归档时事件里的 `ArchivedAt` 必须是 **null**——头靠它决定折叠还是展开。
    [Fact]
    public async Task Unarchive_PublishesANullArchivedAt()
    {
        var (host, _) = NewHarness();
        SaveProvider(host);
        var bus = new EventBus();
        var d = new CommandDispatcher(host, bus, h => new Stepper(host.Store, _adapter), _ => _adapter);
        var sessionId = await CreateSessionAsync(d);
        await d.DispatchAsync(new CoreCommand.SessionArchive(sessionId), "x", false, CancellationToken.None);

        using var project = bus.Subscribe(EventBus.ProjectTopic(ProjectKey.FromPath(_work)));
        await d.DispatchAsync(
            new CoreCommand.SessionArchive(sessionId, Archived: false), "x", false, CancellationToken.None);

        Assert.True(project.Reader.TryRead(out var p));
        Assert.Null(Assert.IsType<WireEvent.SessionArchived>(p.Payload).ArchivedAt);
    }

    // ⚠️ **没激活的会话也要能归档**——刻意不为了改一个字段去 `Activate`
    //（那会产生一份用户没要求的内存副本）。
    [Fact]
    public async Task Archive_WorksOnAnInactiveSession_WithoutActivatingIt()
    {
        var (host, d) = NewHarness();
        SaveProvider(host);
        var sessionId = await CreateSessionAsync(d);
        host.Deactivate(sessionId);

        var r = await d.DispatchAsync(
            new CoreCommand.SessionArchive(sessionId), "x", false, CancellationToken.None);

        Assert.IsType<Ack.Accepted>(r.Ack);
        Assert.NotNull(host.Store.ReadSpec(ProjectKey.FromPath(_work), sessionId).ArchivedAt);
        Assert.False(host.TryGetActivated(sessionId, out _));   // 没有被顺手激活
    }

    // 三条销毁命令共用「在跑 ⇒ 拒绝」：不是原地掐断——`Stepper` 的收尾会把 state.json
    // 写回去，留下一个有 state、没 spec 的目录，而 `ListSessions` 静默跳过它。
    [Theory]
    [InlineData("deactivate")]
    [InlineData("archive")]
    [InlineData("delete")]
    public async Task DestructiveCommands_RefuseARunningSession(string which)
    {
        var (host, d) = NewHarness();
        SaveProvider(host);
        var sessionId = await CreateSessionAsync(d);
        MakeRunning(host, sessionId);

        CoreCommand command = which switch
        {
            "deactivate" => new CoreCommand.SessionDeactivate(sessionId),
            "archive" => new CoreCommand.SessionArchive(sessionId),
            _ => new CoreCommand.SessionDelete(sessionId),
        };

        var r = await d.DispatchAsync(command, "x", false, CancellationToken.None);

        Assert.IsType<Ack.Rejected>(r.Ack);
        Assert.Contains("正在跑", ((Ack.Rejected)r.Ack).Reason);
        // 半删都没发生：目录、激活项都在。
        Assert.NotNull(host.Store.FindSession(sessionId));
        Assert.True(host.TryGetActivated(sessionId, out _));
    }

    [Fact]
    public async Task Delete_RemovesTheDirectory_AndTheSessionDisappears()
    {
        var (host, d) = NewHarness();
        SaveProvider(host);
        var sessionId = await CreateSessionAsync(d);

        var r = await d.DispatchAsync(
            new CoreCommand.SessionDelete(sessionId), "x", false, CancellationToken.None);

        Assert.IsType<Ack.Accepted>(r.Ack);
        Assert.False(host.Store.SessionExists(ProjectKey.FromPath(_work), sessionId));
        Assert.Empty(host.Store.ListSessions(ProjectKey.FromPath(_work)));
    }

    [Fact]
    public async Task Delete_OnAMissingSession_IsRejected()
    {
        var (host, d) = NewHarness();
        SaveProvider(host);

        var r = await d.DispatchAsync(
            new CoreCommand.SessionDelete("nope"), "x", false, CancellationToken.None);

        Assert.Contains("不存在", ((Ack.Rejected)r.Ack).Reason);
    }

    // ⚠️ 该会话占的声明要一并释放，并在回执里**点名**——用户可能正靠那几条锁挡着别人。
    [Fact]
    public async Task Delete_ReleasesItsClaims_AndNamesThem()
    {
        var (host, d) = NewHarness();
        SaveProvider(host);
        var sessionId = await CreateSessionAsync(d);

        var claims = new ClaimStore(_work);
        claims.Save([
            new Claim(sessionId, ProjectKey.FromPath(_work), Path.Combine(_work, "src", "a.cs"), DateTimeOffset.UtcNow),
            new Claim("someone-else", ProjectKey.FromPath(_work), Path.Combine(_work, "src", "b.cs"), DateTimeOffset.UtcNow),
        ]);

        var r = await d.DispatchAsync(
            new CoreCommand.SessionDelete(sessionId), "x", false, CancellationToken.None);

        var note = ((Ack.Accepted)r.Ack).Note!;
        Assert.Contains("a.cs", note);

        // 自己那条没了，**别人的那条还在**——只摘自己的。
        var left = new ClaimStore(_work).All();
        Assert.Equal(["someone-else"], left.Select(c => c.SessionId));
    }

    // 删除**只发项目流**：会话 topic 上的订阅者已经无处可投，钉在上面是泄漏。
    [Fact]
    public async Task Delete_PublishesOnTheProjectTopic_ButNotTheSessionTopic()
    {
        var (host, _) = NewHarness();
        SaveProvider(host);
        var bus = new EventBus();
        var d = new CommandDispatcher(host, bus, h => new Stepper(host.Store, _adapter), _ => _adapter);
        var sessionId = await CreateSessionAsync(d);

        using var session = bus.Subscribe(EventBus.SessionTopic(sessionId));
        using var project = bus.Subscribe(EventBus.ProjectTopic(ProjectKey.FromPath(_work)));

        await d.DispatchAsync(new CoreCommand.SessionDelete(sessionId), "x", false, CancellationToken.None);

        Assert.True(project.Reader.TryRead(out var p));
        Assert.Equal(sessionId, Assert.IsType<WireEvent.SessionDeleted>(p.Payload).SessionId);
        Assert.False(session.Reader.TryRead(out _), "会话已经没了，会话流上不该再发");
    }

    // 声明在自己工作目录的 `claims.json` 里 ⇒ 只需要一个 store；**别扫所有项目**
    // （那会在别人的目录上做读-改-写）。
    [Fact]
    public async Task Delete_LeavesOtherProjectsClaimsAlone()
    {
        var (host, d) = NewHarness();
        SaveProvider(host);
        var other = Path.Combine(Path.GetTempPath(), "lilara-other-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(other);

        try
        {
            var mine = await CreateSessionAsync(d);
            var r = await d.DispatchAsync(
                new CoreCommand.SessionCreate { Cwd = other }, "c2", false, CancellationToken.None);
            var theirs = ((Ack.Accepted)r.Ack).SessionId!;

            new ClaimStore(other).Save([
                new Claim(theirs, ProjectKey.FromPath(other), Path.Combine(other, "x.cs"), DateTimeOffset.UtcNow),
            ]);

            await d.DispatchAsync(new CoreCommand.SessionDelete(mine), "x", false, CancellationToken.None);

            Assert.Single(new ClaimStore(other).All());   // 别的项目原封不动
        }
        finally
        {
            if (Directory.Exists(other)) Directory.Delete(other, recursive: true);
        }
    }

    // ---- provider 的模型清单（头的模型下拉靠它） ----

    [Fact]
    public async Task ProviderUpsert_WithModels_PersistsThem()
    {
        var (host, d) = NewHarness();

        await d.DispatchAsync(
            new CoreCommand.ProviderUpsert("p", "k", null, null, null, null)
            {
                Models = ["a", "b"],
            }, "x", false, CancellationToken.None);

        Assert.Equal(["a", "b"], host.Providers.Load("p")!.Models);
    }

    // 「省略 = 保留」也要覆盖新字段——漏了它的话，改端点会把模型清单静默清空。
    [Fact]
    public async Task ProviderUpsert_WithoutModels_KeepsTheExistingList()
    {
        var (host, d) = NewHarness();

        await d.DispatchAsync(
            new CoreCommand.ProviderUpsert("p", "k", null, null, null, null) { Models = ["a", "b"] },
            "x1", false, CancellationToken.None);

        // 第二次只改端点，**不提** models。
        await d.DispatchAsync(
            new CoreCommand.ProviderUpsert("p", null, null, null, "http://x.invalid", null),
            "x2", false, CancellationToken.None);

        var saved = host.Providers.Load("p")!;
        Assert.Equal("http://x.invalid", saved.Endpoint);
        Assert.Equal(["a", "b"], saved.Models);   // 没被静默清掉
    }
}
