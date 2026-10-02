using Lilara.Core.Adapters;
using Lilara.Core.Canonical;
using Lilara.Core.Control;
using Lilara.Core.Hosting;
using Lilara.Core.Providers;
using Lilara.Core.Session;

namespace Lilara.Core.Tests;

/// <summary>
/// 五轴（`01` §3.5）的命令路径。
/// ⚠️ 这三条一起钉住「轴是**状态**、不是事件」这个区别：设值**发事件**，
/// 而**重复设同一个值不发**——事件是**迁移**的产物（`01` §5）。
///
/// <para>⚠️ **放在独立文件 `AxisCommandTests.cs`**（不是追加进 `CommandDispatcherTests.cs`）：
/// 它有**自己的 harness**（双临时目录 + provider + FakeAdapter），
/// 塞进既有测试类要么复制它的字段、要么撞它的文件名前缀。
/// 独立文件是这类「同一套约定、另一组夹具」的常规做法（`ControlServerTests` 也是这么来的）。</para>
/// </summary>
public class AxisCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lilara-axis-cmd-" + Guid.NewGuid().ToString("N"));
    private readonly string _work = Path.Combine(Path.GetTempPath(), "lilara-axis-cmdwork-" + Guid.NewGuid().ToString("N"));

    public AxisCommandTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
    }

    private (CoreHost Host, CommandDispatcher Dispatcher, EventBus Bus, SessionHandle Session) Harness()
    {
        var host = new CoreHost(_root, _work);
        host.Providers.Save(new ProviderDefinition
        {
            Id = "p", Protocol = Protocols.ClaudeMessages, ApiKey = "k",
            DefaultModel = "m", Modality = "vision",
        });
        var bus = new EventBus();
        var adapter = new Fakes.FakeAdapter();
        var d = new CommandDispatcher(host, bus, h => new Stepper(host.Store, adapter));
        var created = d.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None)
            .GetAwaiter().GetResult();
        var sid = ((Ack.Accepted)created.Ack).SessionId!;
        host.TryGetActivated(sid, out var h);
        return (host, d, bus, h);
    }

    private static string NewSession(CommandDispatcher d)
    {
        var r = d.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None)
            .GetAwaiter().GetResult();
        return ((Ack.Accepted)r.Ack).SessionId!;
    }

    [Fact]
    public async Task SetAxis_ChangesTheValue_AndPublishesAxisChanged()
    {
        var (_, d, bus, session) = Harness();
        using var sub = bus.Subscribe(EventBus.SessionTopic(session.SessionId));

        var r = await d.DispatchAsync(
            new CoreCommand.SessionSetAxis(session.SessionId, "safety", "prod"), "a1", false, CancellationToken.None);

        Assert.IsType<Ack.Accepted>(r.Ack);
        Assert.Equal("prod", session.State.Axes!["safety"]);
        Assert.True(sub.Reader.TryRead(out var p), "改轴必须发 axis.changed——头靠它更新显示");
        var evt = Assert.IsType<WireEvent.AxisChanged>(p.Payload);
        Assert.Equal("safety", evt.Axis);
        Assert.Equal("prod", evt.Value);
    }

    /// <summary>
    /// ⚠️ **重复设同一个值不发事件**（`01` §5：事件是**迁移**的产物）。
    /// 发了的话，头每收到一条都要重渲染一次，而用户的鼠标正悬在上面。
    /// </summary>
    [Fact]
    public async Task SetAxis_ToTheSameValue_IsANoOpAndPublishesNothing()
    {
        var (_, d, bus, session) = Harness();
        await d.DispatchAsync(new CoreCommand.SessionSetAxis(session.SessionId, "safety", "prod"), "a1", false, CancellationToken.None);

        using var sub = bus.Subscribe(EventBus.SessionTopic(session.SessionId));
        var r = await d.DispatchAsync(new CoreCommand.SessionSetAxis(session.SessionId, "safety", "prod"), "a2", false, CancellationToken.None);

        Assert.IsType<Ack.Accepted>(r.Ack);
        Assert.False(sub.Reader.TryRead(out _), "同一个值不是迁移，不该发事件");
    }

    /// <summary>
    /// ⚠️ **非法取值 ⇒ `rejected`**（G6 / `03` §3：命令本身不合法）。
    /// 而理由必须**列出合法值**——用户唯一的线索就是它。
    /// </summary>
    [Fact]
    public async Task SetAxis_WithAnUnknownValue_IsRejected_AndListsTheLegalOnes()
    {
        var (_, d, _, session) = Harness();

        var r = await d.DispatchAsync(
            new CoreCommand.SessionSetAxis(session.SessionId, "safety", "production"), "a1", false, CancellationToken.None);

        var rejected = Assert.IsType<Ack.Rejected>(r.Ack);
        foreach (var v in Axes.ValuesOf("safety")!) Assert.Contains(v, rejected.Reason);
        Assert.Equal("dev", session.State.Axes!["safety"]);   // ⚠️ 值没被改
    }

    /// <summary>
    /// ⚠️ **拒绝顺序是会话 → 轴 → 值**。这里刻意用一个**不存在的会话 + 不认识的轴**，
    /// 断言报的是**轴不认识**（会话那关先过）。反过来的话，一个真实的用户
    /// （会话 id 敲错了、轴也敲错了）会被告知「轴不认识」，于是去改轴——而真正错的是会话 id。
    /// </summary>
    [Fact]
    public async Task SetAxis_OnAnUnknownAxis_IsRejected_AndListsTheAxes()
    {
        var (_, d, _, session) = Harness();

        var r = await d.DispatchAsync(
            new CoreCommand.SessionSetAxis(session.SessionId, "mood", "happy"), "a1", false, CancellationToken.None);

        var reason = Assert.IsType<Ack.Rejected>(r.Ack).Reason;
        foreach (var a in Axes.Names) Assert.Contains(a, reason);
    }

    /// <summary>⚠️ 会话不存在时**先报会话**——别让用户去改一个没错的轴名。</summary>
    [Fact]
    public async Task SetAxis_OnAnUnknownSession_IsRejected_AndNamesTheSession()
    {
        var (_, d, _, _) = Harness();

        var r = await d.DispatchAsync(
            new CoreCommand.SessionSetAxis("nope", "mood", "happy"), "a1", false, CancellationToken.None);

        Assert.Contains("nope", Assert.IsType<Ack.Rejected>(r.Ack).Reason);
    }

    /// <summary>
    /// ⚠️ **「会话在盘上、不在内存里」也必须能改轴**——核心重启之后的常态。
    ///
    /// <para>夹具用的是**第二个 `CoreHost` 指向同一个数据根**：它等价于「未激活」，
    /// 因为激活表是 per-`CoreHost` 的私有 `ConcurrentDictionary`（`CoreHost._activated`），
    /// 新实例的表是空的，而盘上的 `spec.json` / `state.json` 是**同一份**
    /// （`CoreHost` 与 `SessionStore` 都不对数据根加锁，`DataRootLock` 只在 `Program.cs` 用）。
    /// 这正是重启后的真实形状。⚠️ `CoreHost` **没有**能把会话从激活表里拿掉的公开手段
    /// （`Activate` / `Register` / `TryGetActivated` / `Activated` / `InvalidateSteppers`…），
    /// 所以不硬造那种夹具。</para>
    ///
    /// <para>⚠️ 改回「只查激活表」（`_host.TryGetActivated`）这条就**红**：
    /// 那条路会回一句**假话**——「不存在」，而它明明在盘上。</para>
    /// </summary>
    [Fact]
    public async Task SetAxis_OnASessionThatIsNotActivated_FallsBackToDisk()
    {
        var (host, _, _, session) = Harness();

        var fresh = new CoreHost(_root, _work);
        var d2 = new CommandDispatcher(fresh, new EventBus(), h => new Stepper(fresh.Store, new Fakes.FakeAdapter()));
        Assert.False(fresh.TryGetActivated(session.SessionId, out _), "前提：新宿主的激活表里没有它");

        var r = await d2.DispatchAsync(
            new CoreCommand.SessionSetAxis(session.SessionId, "safety", "prod"), "a1", false, CancellationToken.None);

        Assert.IsType<Ack.Accepted>(r.Ack);

        // 回落分支必须把它**载进激活表**（TryFind 的 Activate），改的是表里那一份
        Assert.True(fresh.TryGetActivated(session.SessionId, out var activated));
        Assert.Equal("prod", activated.State.Axes!["safety"]);

        // ⚠️ 而且**真的落了盘**：另起一个 handle 从磁盘读，读的不是内存那一份
        var reloaded = SessionHandle.Load(host.Store, session.ProjectKey, session.SessionId);
        Assert.Equal("prod", reloaded.State.Axes!["safety"]);
    }

    /// <summary>
    /// ⚠️ **`axis: null` 必须回 `rejected`，不能抛异常。**
    ///
    /// <para>线上那条路是可达的：`WireJson.Options` **没有**开
    /// `RespectNullableAnnotations`（`WireJson.cs:25-32`），所以
    /// <c>{"kind":"session.setAxis","axis":null}</c> 能一路落到
    /// `Axes.IsKnown(null)` ⇒ `Dictionary.ContainsKey(null)` ⇒ **`ArgumentNullException`**
    /// （⚠️ 修前实测的栈：`ContainsKey` → `FindValue` → 「Parameter 'key'」）。
    /// 而控制面的接收循环是**兜底 catch**（宽泛 `catch (Exception)`，`ControlServer.cs:323`），
    /// 所以连接不会死——但用户看到的是「帧形状不对」，而真正的问题是**轴名没给**。</para>
    ///
    /// <para>⇒ 修在 `Axes` 的三个入口（它们是这个契约的拥有者），**不是**在这里加空判断：
    /// `ValuesOf` 声明的是「未知 → null」，而 **null 就是未知**。</para>
    /// </summary>
    [Fact]
    public async Task SetAxis_WithANullAxis_IsRejected_NotACrash()
    {
        var (_, d, _, session) = Harness();

        var r = await d.DispatchAsync(
            new CoreCommand.SessionSetAxis(session.SessionId, null!, "dev"), "a1", false, CancellationToken.None);

        Assert.Contains("没有", Assert.IsType<Ack.Rejected>(r.Ack).Reason);
    }

    /// <summary>⚠️ 轴是状态，**必须落盘**——否则重启即丢，而用户以为设过了。</summary>
    [Fact]
    public async Task SetAxis_PersistsToDisk()
    {
        var (host, d, _, session) = Harness();
        await d.DispatchAsync(new CoreCommand.SessionSetAxis(session.SessionId, "verify", "strict"), "a1", false, CancellationToken.None);

        var reloaded = SessionHandle.Load(host.Store, session.ProjectKey, session.SessionId);
        Assert.Equal("strict", reloaded.State.Axes!["verify"]);
    }

    /// <summary>
    /// ⚠️ **值大小写不敏感**（`Axes.TryParse` 刻意如此），但**存的是表里的规范形**。
    /// 存原样的话，`Prod` 与 `prod` 会同时存在于不同会话，而头的显示按表里的规范拼法来
    /// ⇒ 头认不出那个取值（它不在表里），用户看到「设了但没生效」。
    /// </summary>
    [Fact]
    public async Task SetAxis_NormalizesTheCase()
    {
        var (_, d, _, session) = Harness();

        await d.DispatchAsync(new CoreCommand.SessionSetAxis(session.SessionId, "safety", "Prod"), "a1", false, CancellationToken.None);

        Assert.Equal("prod", session.State.Axes!["safety"]);
    }

    /// <summary>
    /// ⚠️ **这条才是「五轴真的接上了」的证据**——上面那些只证明「值存对了」。
    ///
    /// <para>背景：`Stepper` 原先把 effort 读自 `Spec.Model.Effort`（**建会话时冻结的快照**），
    /// 而轴是**活**的。两者分叉时，用户在头上拨到 `max`、头显示 `max`、
    /// 而**请求里发的还是建会话时的值**——「设了没用」，且无从发现。
    /// 这条测试走完整条路：**改轴 → 发消息 → 检查适配器收到的请求**。</para>
    ///
    /// <para>⚠️ 断言的是**适配器实际收到的 `ModelRequest.Effort`**，不是 `handle.State.Axes`——
    /// 后者在修复前后**都是 `max`**，对这条缺陷没有区分力。</para>
    /// </summary>
    [Fact]
    public async Task SetAxis_Effort_ActuallyReachesTheModelRequest()
    {
        var host = new CoreHost(_root, _work);
        host.Providers.Save(new ProviderDefinition
        {
            Id = "p", Protocol = Protocols.ClaudeMessages, ApiKey = "k",
            DefaultModel = "m", Modality = "vision",
        });
        var adapter = new Fakes.FakeAdapter().Reply("ok");
        var d = new CommandDispatcher(host, new EventBus(), h => new Stepper(host.Store, adapter));

        var created = await d.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
        var sid = ((Ack.Accepted)created.Ack).SessionId!;

        await d.DispatchAsync(new CoreCommand.SessionSetAxis(sid, "effort", "max"), "a1", false, CancellationToken.None);
        await d.DispatchAsync(
            new CoreCommand.SessionSend(sid, [new Block.TextBlock("hi")]), "a2", true, CancellationToken.None);

        Assert.NotEmpty(adapter.Calls);
        Assert.Equal("max", adapter.Calls[^1].Effort);
    }

    /// <summary>
    /// ⚠️ **步进行中改的轴，不能被这一步的失败回滚吞掉。**
    ///
    /// <para>窗口是**整个模型调用**（不是几条指令宽）：`session.setAxis` 不拿会话闸，
    /// 而 `Stepper` 在步开始时存下 `persisted = handle.State`，失败时
    /// `handle.State = persisted; throw;`——原样放回的是**整份** `State`，含 `Axes`。
    /// 这条走完整条命令路：**步进行中改轴 → 这一步失败 → 查轴**。</para>
    ///
    /// <para>症状：头把 effort 拨到 `max`（正好在模型调用那段时间里），这一步随后失败。
    /// 头收到过 `axis.changed`、显示 `max`，磁盘也写着 `max`；而**内存回到 `high`**，
    /// 下一次请求带 `high`；再下一次成功的步把 `high` 写回磁盘 ⇒ 用户看到的 `max`
    /// **就此消失，且没有任何报错**。</para>
    ///
    /// <para>⚠️ **内存那条才是本缺陷的判据**——磁盘上它一直是 `max`（改轴那一次
    /// `SaveAxes` 已经写过盘了），磁盘断言留着是为了同时钉住「改轴确实落盘」。</para>
    /// </summary>
    [Fact]
    public async Task StepFailure_DoesNotSwallowAnAxisChangedMidStep()
    {
        var host = new CoreHost(_root, _work);
        host.Providers.Save(new ProviderDefinition
        {
            Id = "p", Protocol = Protocols.ClaudeMessages, ApiKey = "k",
            DefaultModel = "m", Modality = "vision",
        });
        var adapter = new DuringCallAdapter { Fail = true };
        var d = new CommandDispatcher(host, new EventBus(), h => new Stepper(host.Store, adapter));
        var created = await d.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
        var sid = ((Ack.Accepted)created.Ack).SessionId!;
        host.TryGetActivated(sid, out var session);

        // 钩子 = 「步正卡在模型调用里」的那一刻
        adapter.DuringCall = () => d.DispatchAsync(
            new CoreCommand.SessionSetAxis(sid, "effort", "max"), "a1", false, CancellationToken.None);

        await Assert.ThrowsAsync<DispatchException>(() => d.DispatchAsync(
            new CoreCommand.SessionSend(sid, [new Block.TextBlock("hi")]), "a2", true, CancellationToken.None));

        // 内存：这一步的失败**不许**把用户改的轴吞掉
        Assert.Equal("max", session.State.Axes![Axes.Effort]);
        // 磁盘：轴落了盘，且没被回滚波及
        var reloaded = SessionHandle.Load(host.Store, session.ProjectKey, sid);
        Assert.Equal("max", reloaded.State.Axes![Axes.Effort]);
    }

    /// <summary>
    /// ⚠️ **步进行中改轴，不许把内存专用的 `Running` 带上磁盘。**
    ///
    /// <para>`SaveAxes` 写的是**整份** `State`，而步进行中内存里 `Status` 是 `"Running"`
    /// （`Stepper` 只写内存，刻意不落盘——「磁盘从不存 Running」是被四处断言的不变量）。
    /// 不修的话：改轴后进程被 kill / 崩溃 ⇒ 重启 `Load` 读到 `Status="Running"` ⇒
    /// 快照如实回 `Running` ⇒ **会话永远显示在跑，而没有任何步在跑**，也永远等不到 `step.end`。</para>
    ///
    /// <para>⚠️ 断言的是**磁盘文本**（落盘的原始字节），不是 `handle.State.Status`——
    /// 后者在修复前后**都是 `Running`**（那正是设计），对这条缺陷没有区分力。
    /// 也**必须在步进行中读**：步正常收尾时 `Stepper` 会把 `Idle` 写回去，
    /// 事后再读那个文本在两个版本里都对，同样没有区分力。</para>
    /// </summary>
    [Fact]
    public async Task SetAxisDuringAStep_DoesNotWriteRunningToDisk()
    {
        var host = new CoreHost(_root, _work);
        host.Providers.Save(new ProviderDefinition
        {
            Id = "p", Protocol = Protocols.ClaudeMessages, ApiKey = "k",
            DefaultModel = "m", Modality = "vision",
        });
        var adapter = new DuringCallAdapter();
        var d = new CommandDispatcher(host, new EventBus(), h => new Stepper(host.Store, adapter));
        var created = await d.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
        var sid = ((Ack.Accepted)created.Ack).SessionId!;
        host.TryGetActivated(sid, out var session);

        string? statusDuringCall = null;
        string? onDiskDuringCall = null;
        adapter.DuringCall = () =>
        {
            statusDuringCall = session.State.Status;
            d.DispatchAsync(new CoreCommand.SessionSetAxis(sid, "safety", "prod"), "a1", false, CancellationToken.None)
                .GetAwaiter().GetResult();
            onDiskDuringCall = File.ReadAllText(session.StatePath);
            return Task.CompletedTask;
        };

        await d.DispatchAsync(
            new CoreCommand.SessionSend(sid, [new Block.TextBlock("hi")]), "a2", true, CancellationToken.None);

        // 前提：窗口确实是「步进行中」，否则这条测试根本没测到它
        Assert.Equal("Running", statusDuringCall);
        Assert.NotNull(onDiskDuringCall);
        Assert.DoesNotContain("Running", onDiskDuringCall);
    }

    /// <summary>
    /// ⚠️ **回执的 `CommandId` 必须原样带回命令 id，不是会话 id。**
    ///
    /// <para>契约：命令 id 由头生成，核心**原样带回**——头靠它把「我发过的那条」对上。
    /// 而 `TryFind` 原先在 miss 时直接 `Reject(sessionId, …)`，把**会话 id** 当成了
    /// `CommandId` 回出去。今天无害（线上的回执用帧自己的 id），但契约是错的，
    /// 且这是本轮修复给 `setAxis` 带来的一处相对回退（改前它是 `Reject(id, …)`）。</para>
    /// </summary>
    [Fact]
    public async Task SetAxis_OnAnUnknownSession_EchoesTheCommandIdNotTheSessionId()
    {
        var (_, d, _, _) = Harness();

        var r = await d.DispatchAsync(
            new CoreCommand.SessionSetAxis("nope", "safety", "prod"), "cmd-7", false, CancellationToken.None);

        Assert.IsType<Ack.Rejected>(r.Ack);
        Assert.Equal("cmd-7", r.Ack.CommandId);
    }

    /// <summary>
    /// 在**模型调用的整个窗口**里执行一个钩子，然后按需失败或正常返回。
    /// ⚠️ 钩子是**同步阻塞**执行的：`session.setAxis` 不拿会话闸，所以它能在整个模型调用里跑
    /// ——这正是上面两条测试要复现的窗口。
    /// </summary>
    private sealed class DuringCallAdapter : IModelAdapter
    {
        /// <summary>模型调用的窗口里要做的事（测试在这里发 `session.setAxis`）。</summary>
        public Func<Task>? DuringCall { get; set; }

        /// <summary>调用末尾是否抛失败（`StepFailure_*` 那条用）。</summary>
        public bool Fail { get; set; }

        public ResolvedEndpoint? Endpoint => null;
        public string Protocol => Protocols.ClaudeMessages;

        public Task<ModelReply> CallAsync(ModelRequest request, CancellationToken ct)
        {
            DuringCall?.Invoke().GetAwaiter().GetResult();
            if (Fail) throw new ModelCallFailedException("上游 503");
            return Task.FromResult(new ModelReply
            {
                Blocks = [new Block.TextBlock("ok")],
                StopReason = "end_turn",
                Refused = false,
            });
        }
    }
}
