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

    /// <summary>
    /// ⚠️ **空串与「缺失」等价**（都是「没给」）——判据必须与 `/providers` 的 `hasApiKey`
    /// **共用同一个词**（`IsNullOrEmpty`）。两处漂过一次：守卫用 `is null`，于是
    /// **新** provider 带一个空串就**绕过守卫**、被 Accepted、存下一个**空 key**——
    /// 正是上面那条注释要防的「存一个发不出去的配置，比拒绝更糟」。
    /// </summary>
    [Fact]
    public async Task ProviderUpsert_WithAnEmptyApiKey_ForANewProvider_IsRejected()
    {
        var (host, _) = NewHarness();
        SaveProvider(host);
        var dispatcher = new CommandDispatcher(host, new EventBus(), host.StepperFor);

        var r = await dispatcher.DispatchAsync(
            new CoreCommand.ProviderUpsert("brand-new", "", null, null, null, null),
            "c", true, CancellationToken.None);

        Assert.IsType<Ack.Rejected>(r.Ack);                       // ⚠️ 不是 Accepted（空 key 发不出去）
        Assert.Null(host.Providers.Load("brand-new"));            // ⚠️ 不留半成品
    }

    /// <summary>
    /// ⚠️ **已有 provider 带空串 ⇒ 保留旧 key，不是抹掉**。
    /// 这正是 R-B 那条失败路径的**另一扇门**：头读不到 key ⇒ 一个天真的表单把空输入框原样提交
    /// ⇒ 旧判据（`p.ApiKey ?? existing!.ApiKey`）判 `""` 非 null ⇒ **把存着的 key 覆盖成空**。
    /// </summary>
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
        Assert.Equal("k", after.ApiKey);                          // ⚠️ 空输入框不该抹掉 key
        Assert.Equal(Protocols.OpenAiChat, after.Protocol);       // 而这一项**真的改了**
    }

    /// <summary>
    /// ⚠️ **删掉之后盘上真的没有了**——这一条钉的是「四条线」里 dispatcher 那条的**盘上后果**。
    ///
    /// <para>⚠️ **只断言 ack 是半条判据**：ack 说 accepted 而文件还在的那种错，
    /// 用户以为删了、下次发消息还在用那个凭据（`StepperResolver` 会照旧载入它）。
    /// ⇒ 判据必须落在盘上（`Load` 为 null / 文件不存在）。</para>
    /// </summary>
    [Fact]
    public async Task ProviderDelete_RemovesTheStoredDefinition()
    {
        var (host, _) = NewHarness();
        SaveProvider(host);
        var dispatcher = new CommandDispatcher(host, new EventBus(), host.StepperFor);

        var r = await dispatcher.DispatchAsync(
            new CoreCommand.ProviderDelete("p"), "c", true, CancellationToken.None);

        Assert.IsType<Ack.Accepted>(r.Ack);
        Assert.Null(host.Providers.Load("p"));                       // ⚠️ 读不到了
        Assert.Empty(host.Providers.List());                        // 且列表里也没了
    }

    /// <summary>
    /// ⚠️ **损坏的 provider 文件必须删得掉**——这一条钉的是「判据是**文件层**，不是 `Load is null`」。
    ///
    /// <para>`Load` 把**损坏**的文件也当作「不存在」⇒ 若有人把 dispatcher 的判据改成
    /// `Load(d.Id) is null`，一个坏配置会**永远删不掉**，而**「把一个坏配置删掉」正是用户最想做的事**。
    /// ⚠️ **其余三条删除测试全都抓不到**这个改动（`RemovesTheStoredDefinition` / `BoundBySession`
    /// / `InvalidatesTheCache`——**不都是在本条之上**）：它们用的是**能正常解析**的文件
    /// ⇒ `Load` 非 null、判据照样通过。⇒ 这一条是那块缺口唯一的钉子。</para>
    ///
    /// <para>⚠️ 断言要**落在盘上**（文件真的没了）——只断言 ack 是半条判据。</para>
    /// </summary>
    [Fact]
    public async Task ProviderDelete_OfACorruptProviderFile_IsAccepted_NotRejected()
    {
        var (host, _) = NewHarness();
        // ⚠️ **不能走 `Providers.Save`**（那写的必定是合法 JSON）——直接往盘上写一个坏文件。
        var dir = Path.Combine(_root, "providers");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "broken.json");
        File.WriteAllText(file, "{ 这不是合法 JSON");

        // ⚠️ 先确认前提成立：`Load` 对它是 null（所以「改成 Load」会把它当不存在）。
        Assert.Null(host.Providers.Load("broken"));

        var dispatcher = new CommandDispatcher(host, new EventBus(), host.StepperFor);
        var r = await dispatcher.DispatchAsync(
            new CoreCommand.ProviderDelete("broken"), "c", true, CancellationToken.None);

        Assert.IsType<Ack.Accepted>(r.Ack);        // ⚠️ 改了判据这里会变 Rejected
        Assert.False(File.Exists(file));           // ⚠️ 且盘上真的没了
    }

    /// <summary>
    /// ⚠️ **删一个不存在的 id ⇒ `Rejected`，且理由点名那个 id——不是幂等成功。**
    ///
    /// <para>理由：本项目一路在收「静默的成功」。`provider delet local`（拼错）若回 ✓，
    /// 用户以为删掉了、其实没删，而下一次发消息用的还是那个凭据。
    /// 「点名 id」是必需的：用户手打了三个 provider 时，只说「没有这个提供商」他不知道是哪一个。</para>
    ///
    /// <para>⚠️ **不变的那半也要断言**：拒绝时**不许**顺手清缓存、也不许动别的文件——
    /// 一次没做成的命令不该有任何副作用。</para>
    /// </summary>
    [Fact]
    public async Task ProviderDelete_OfAProviderThatDoesNotExist_IsRejected_NamingTheId()
    {
        var (host, _) = NewHarness();
        SaveProvider(host);                                          // 盘上有 "p"
        var dispatcher = new CommandDispatcher(host, new EventBus(), host.StepperFor);

        var r = await dispatcher.DispatchAsync(
            new CoreCommand.ProviderDelete("local"), "c", true, CancellationToken.None);

        var rejected = Assert.IsType<Ack.Rejected>(r.Ack);
        Assert.Contains("local", rejected.Reason);                   // ⚠️ 理由点名那个 id
        Assert.NotNull(host.Providers.Load("p"));                    // ⚠️ 别的 provider 一根汗毛没动
    }

    /// <summary>
    /// ⚠️ **删一个正被会话引用的 provider 不阻止**（R-A：想改啥都行；用户可能就是要把一个坏配置删掉）
    /// ——`SessionSpec` 的存在**不构成**「不能删」的理由。
    ///
    /// <para>⚠️ 但**后果必须可见**：删完 `InvalidateSteppers()`，被引用的会话下次发消息
    /// 会走 `StepperResolver` 的「绑定的提供商已不存在」那条路（已实现）。
    /// 断言那条路真的被走到（`UnconfiguredAdapter`），而不只是「缓存对象换了」——
    /// 后者在「删了但换了个同样坏的」时也会成立。</para>
    ///
    /// <para>⚠️ **必须先 `StepperFor` 一次把缓存捂热**（`before`）：不预热的话缓存里本来就
    /// 没有该会话那一条 ⇒ 删完再解析**必然**是新对象 ⇒ 这条测试在「`InvalidateSteppers()`
    /// 被删掉」时**照样绿**（实测：变异 3 下只有隔壁那条红）。预热之后，
    /// 少了失效的那一句就会把**旧的那个**还回来 ⇒ 这条也红。</para>
    /// </summary>
    [Fact]
    public async Task ProviderDelete_OfAProviderBoundByASession_IsAccepted_NotBlocked()
    {
        var (host, _) = NewHarness();
        SaveProvider(host);
        var dispatcher = new CommandDispatcher(host, new EventBus(), host.StepperFor);
        var sessionId = await CreateSessionAsync(dispatcher);        // 会话绑到 "p"
        host.TryGetActivated(sessionId, out var handle);

        var before = host.StepperFor(handle);                        // ⚠️ 把缓存捂热（见上）

        var r = await dispatcher.DispatchAsync(
            new CoreCommand.ProviderDelete("p"), "c", true, CancellationToken.None);

        Assert.IsType<Ack.Accepted>(r.Ack);                          // ⚠️ 不阻止（没有「谁在用」的闸）
        Assert.Null(host.Providers.Load("p"));                       // 而且真删了

        var after = host.StepperFor(handle);                         // 下一次解析走的是……
        Assert.NotSame(before, after);                               // ⚠️ 缓存真的失效了
        Assert.IsType<UnconfiguredAdapter>(after.Adapter);           // ⚠️ 「绑定的提供商已不存在」那条路
    }

    /// <summary>
    /// ⚠️ **失效点必须在 dispatcher 里**（与 upsert 同源）：挂在控制台的字符串判据上，
    /// 线上的 `provider.delete` 走不到它 ⇒ 缓存**静默失效**，
    /// 用户删了 provider 而会话还在用**那个已经被删掉的**配置跑。
    /// ⚠️ 这条**不建会话**、直接看缓存对象换没换——与上面那条「被引用的会话」分工不同。
    /// </summary>
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

    /// <summary>挂起的适配器：让测试能精确控制「模型什么时候返回」。</summary>
    private sealed class GatedAdapter(Func<Task<string>> gate) : Lilara.Core.Adapters.IModelAdapter    {
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
