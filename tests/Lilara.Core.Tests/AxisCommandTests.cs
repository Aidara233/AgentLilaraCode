using Lilara.Core.Protocol;
using Lilara.Core.Kernel;
using Lilara.Core.Control;
using Lilara.Core.Hosting;
using Lilara.Core.Providers;
using Lilara.Core.Session;

namespace Lilara.Core.Tests;

// 轴是状态、不是事件：设值发事件，而重复设同一个值不发——事件是迁移的产物。
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

    // 重复设同一个值不发事件：发了头每收到一条都要重渲染，而用户的鼠标正悬在上面。
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

    // 非法取值 ⇒ rejected；理由必须列出合法值——用户唯一的线索就是它。
    [Fact]
    public async Task SetAxis_WithAnUnknownValue_IsRejected_AndListsTheLegalOnes()
    {
        var (_, d, _, session) = Harness();

        var r = await d.DispatchAsync(
            new CoreCommand.SessionSetAxis(session.SessionId, "safety", "production"), "a1", false, CancellationToken.None);

        var rejected = Assert.IsType<Ack.Rejected>(r.Ack);
        foreach (var v in Axes.ValuesOf("safety")!) Assert.Contains(v, rejected.Reason);
        Assert.Equal("dev", session.State.Axes!["safety"]);   // 值没被改
    }

    // 拒绝顺序是会话 → 轴 → 值——否则一个把会话 id 和轴名都敲错的用户会被告知「轴不认识」，去改没错的轴。
    [Fact]
    public async Task SetAxis_OnAnUnknownAxis_IsRejected_AndListsTheAxes()
    {
        var (_, d, _, session) = Harness();

        var r = await d.DispatchAsync(
            new CoreCommand.SessionSetAxis(session.SessionId, "mood", "happy"), "a1", false, CancellationToken.None);

        var reason = Assert.IsType<Ack.Rejected>(r.Ack).Reason;
        foreach (var a in Axes.Names) Assert.Contains(a, reason);
    }

    // 会话不存在时先报会话——别让用户去改一个没错的轴名。
    [Fact]
    public async Task SetAxis_OnAnUnknownSession_IsRejected_AndNamesTheSession()
    {
        var (_, d, _, _) = Harness();

        var r = await d.DispatchAsync(
            new CoreCommand.SessionSetAxis("nope", "mood", "happy"), "a1", false, CancellationToken.None);

        Assert.Contains("nope", Assert.IsType<Ack.Rejected>(r.Ack).Reason);
    }

    // 「会话在盘上、不在内存里」也必须能改轴——核心重启后的常态；改回「只查激活表」要回一句假话「不存在」。
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

        // 回落分支必须把它载进激活表（TryFind 的 Activate），改的是表里那一份
        Assert.True(fresh.TryGetActivated(session.SessionId, out var activated));
        Assert.Equal("prod", activated.State.Axes!["safety"]);

        // 而且真的落了盘：另起一个 handle 从磁盘读，读的不是内存那一份
        var reloaded = SessionHandle.Load(host.Store, session.ProjectKey, session.SessionId);
        Assert.Equal("prod", reloaded.State.Axes!["safety"]);
    }

    // axis: null 必须回 rejected，不能抛：线上 {"kind":"session.setAxis","axis":null} 可达，
    // 落进 Axes.IsKnown(null) ⇒ Dictionary.ContainsKey(null) ⇒ ArgumentNullException。
    [Fact]
    public async Task SetAxis_WithANullAxis_IsRejected_NotACrash()
    {
        var (_, d, _, session) = Harness();

        var r = await d.DispatchAsync(
            new CoreCommand.SessionSetAxis(session.SessionId, null!, "dev"), "a1", false, CancellationToken.None);

        Assert.Contains("没有", Assert.IsType<Ack.Rejected>(r.Ack).Reason);
    }

    // 轴是状态，必须落盘——否则重启即丢，而用户以为设过了。
    [Fact]
    public async Task SetAxis_PersistsToDisk()
    {
        var (host, d, _, session) = Harness();
        await d.DispatchAsync(new CoreCommand.SessionSetAxis(session.SessionId, "verify", "strict"), "a1", false, CancellationToken.None);

        var reloaded = SessionHandle.Load(host.Store, session.ProjectKey, session.SessionId);
        Assert.Equal("strict", reloaded.State.Axes!["verify"]);
    }

    // 值大小写不敏感（Axes.TryParse 刻意如此），但存的是表里的规范形——存原样的话头认不出那个取值。
    [Fact]
    public async Task SetAxis_NormalizesTheCase()
    {
        var (_, d, _, session) = Harness();

        await d.DispatchAsync(new CoreCommand.SessionSetAxis(session.SessionId, "safety", "Prod"), "a1", false, CancellationToken.None);

        Assert.Equal("prod", session.State.Axes!["safety"]);
    }

    // 这是「五轴真的接上了」的证据：断言适配器实际收到的 ModelRequest.Effort，
    // 不是 handle.State.Axes（后者修复前后都是 max，对缺陷没有区分力）。
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

    // 步进行中改的轴不能被这一步的失败回滚吞掉：Stepper 失败时 handle.State = persisted，放回的是整份 State（含 Axes）。
    // 症状：头显示 max、磁盘也写着 max，而内存回到 high，下一次成功的步把 high 写回磁盘 ⇒ 用户看到的 max 消失且无报错。
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

        // 内存：这一步的失败不许把用户改的轴吞掉
        Assert.Equal("max", session.State.Axes![Axes.Effort]);
        // 磁盘：轴落了盘，且没被回滚波及
        var reloaded = SessionHandle.Load(host.Store, session.ProjectKey, sid);
        Assert.Equal("max", reloaded.State.Axes![Axes.Effort]);
    }

    // 步进行中改轴不许把内存专用的 Running 带上磁盘：SaveAxes 写整份 State，崩溃重启会读到 Running。
    // 断言用磁盘文本（不是 handle.State.Status），且必须在步进行中读——步正常收尾时 Stepper 会写回 Idle。
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

    // 回执的 CommandId 必须原样带回命令 id，不是会话 id——头靠它把「我发过的那条」对上。
    [Fact]
    public async Task SetAxis_OnAnUnknownSession_EchoesTheCommandIdNotTheSessionId()
    {
        var (_, d, _, _) = Harness();

        var r = await d.DispatchAsync(
            new CoreCommand.SessionSetAxis("nope", "safety", "prod"), "cmd-7", false, CancellationToken.None);

        Assert.IsType<Ack.Rejected>(r.Ack);
        Assert.Equal("cmd-7", r.Ack.CommandId);
    }

    // 在模型调用的整个窗口里执行一个钩子（同步阻塞）：session.setAxis 不拿会话闸，故它能在整个模型调用里跑。
    private sealed class DuringCallAdapter : IModelAdapter
    {
        // 模型调用的窗口里要做的事（测试在这里发 session.setAxis）。
        public Func<Task>? DuringCall { get; set; }

        // 调用末尾是否抛失败（StepFailure_* 那条用）。
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
