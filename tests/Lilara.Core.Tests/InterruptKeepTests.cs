using Lilara.Core.Control;
using Lilara.Core.Hosting;
using Lilara.Core.Kernel;
using Lilara.Core.Protocol;
using Lilara.Core.Providers;
using Lilara.Core.Session;
using Lilara.Core.Session.Tools;
using Lilara.Core.Storage;
using Lilara.Core.Tests.Fakes;
using Block = Lilara.Core.Kernel.Block;

namespace Lilara.Core.Tests;

/// <summary>
/// **打断之后，记录里留下什么**（2026-11-07，用户裁定）。
///
/// <para>两条要求，正好对应两个相位：</para>
/// <list type="number">
///   <item><b>流式模式、生成到一半被打断</b> ⇒ 已经吐出来的正文要留下（一条 `Interrupted`
///   的模型消息），而不是随 `OperationCanceledException` 一起蒸发。</item>
///   <item><b>生成已结束、工具执行到一半被打断</b> ⇒ 模型那条消息（含 `tool_use`）必须留，
///   而且**每条调用都要有结果**（跑完的 / 半截的 / 还没开始的）——否则记录里留下悬空的
///   `tool_use`，下一次请求直接非法（`01` §3.6 ①）。</item>
/// </list>
///
/// <para>⚠️ <b>为什么这两条要单独一个文件</b>：`InterruptTests` 钉的是「中断不是失败」
/// （不发 `error`、`Turn` 不乱涨、CTS 不污染下一步）；这一族钉的是**留什么**。
/// 两批判据在同一处会互相盖住——第一版就是把「什么都不写」当成中断的定义，而那是错的
/// （用户看得见屏幕上那半页字，也知道那些工具真的跑过）。</para>
/// </summary>
public class InterruptKeepTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-keep-" + Guid.NewGuid().ToString("N"));
    private readonly string _work =
        Path.Combine(Path.GetTempPath(), "lilara-keepwork-" + Guid.NewGuid().ToString("N"));

    public InterruptKeepTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_work);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { Directory.Delete(_work, recursive: true); } catch { }
    }

    // ---- ① 流式：半截生成 ----

    [Fact]
    public async Task StreamingInterrupt_KeepsTheHalfMessage_AndSaysItWasInterrupted()
    {
        var emitted = new TaskCompletionSource();
        var (host, bus, sessionId, dispatcher) = Harness(
            new StreamingHangingAdapter(emitted), [new HangTool(new TaskCompletionSource())]);

        using var sub = bus.Subscribe(EventBus.SessionTopic(sessionId));

        await SendAsync(dispatcher, sessionId, "开始吧", awaitCompletion: false);
        await emitted.Task;   // 两段正文已经吐出来了（模型正在「说」）

        await dispatcher.DispatchAsync(
            new CoreCommand.SessionInterrupt(sessionId), "i", true, CancellationToken.None);

        await WaitUntil(() => host.Store.ReadTimeline(Pk(host, sessionId), sessionId)
            .OfType<TimelineEvent.MessageEvent>().Any(m => m.Interrupted == true), "半截消息落盘");

        var timeline = host.Store.ReadTimeline(Pk(host, sessionId), sessionId);
        // ⚠️ **认的是「标记了半截」那一条**，不是「有一条模型消息」：`cache=stable` 的会话
        // 每步开始前还会追加一条**读数**（它也是 `From=Model`，块里只有一个 `tool_use`）。
        var model = Assert.Single(timeline.OfType<TimelineEvent.MessageEvent>(),
            m => m.Interrupted == true);

        // ⚠️ **两段都在、而且按顺序**：吐出来多少就留多少。
        var text = string.Concat(model.Blocks.OfType<Block.TextBlock>().Select(b => b.Text));
        Assert.Contains("我先看一眼", text, StringComparison.Ordinal);
        Assert.Contains("还没说完", text, StringComparison.Ordinal);

        // ⚠️ **标记在记录里**：没有它，下一步的模型会把这半句话当成自己完整的发言。
        Assert.True(model.Interrupted);

        // 这一步真的落了记录 ⇒ `Turn` 与「记录里非结果事件数」仍然对得上
        //（`Recovery.BeforeStep` 的判据）。
        Assert.Equal(1, host.Activated.First().State.Turn);
        Assert.Equal(SessionStatus.Idle, host.Activated.First().State.Status);

        // ⚠️ **等到那一枪到**（发布点在落盘之后，见 `WaitForEvent` 的注释）。
        var payloads = await WaitForEvent(sub,
            e => e is WireEvent.StateChanged { Status: "Idle" }, "回到 Idle");
        Assert.Empty(payloads.OfType<WireEvent.Error>());

        // ⚠️ **头靠这两个东西把临时气泡换成「带标记的正式气泡」，再把状态收回 Idle。
        // ⚠️ 认「带标记」那一条**：读数那条也是 `from=model`（它是 `cache=stable` 的记账动作）。
        var appended = Assert.Single(payloads.OfType<WireEvent.MessageAppended>(),
            m => m.From == "model" && m.Interrupted == true);
        Assert.True(appended.Interrupted);
        Assert.NotNull(appended.HistoryId);
        Assert.Equal("Idle", payloads.OfType<WireEvent.StateChanged>().Last().Status);
    }

    // ---- ② 工具相位：模型消息 + 每条调用的结果 ----

    [Fact]
    public async Task ToolPhaseInterrupt_KeepsTheModelMessageAndEveryResult_SoTheNextRequestIsLegal()
    {
        var started = new TaskCompletionSource();
        var adapter = new FakeAdapter()
            .ReplyBlocks(
                new Block.ToolUseBlock("c1", "hang", """{"x":1}"""),
                new Block.ToolUseBlock("c2", "hang", """{"x":2}"""));

        var (host, bus, sessionId, dispatcher) = Harness(adapter, [new HangTool(started)]);

        using var sub = bus.Subscribe(EventBus.SessionTopic(sessionId));

        await SendAsync(dispatcher, sessionId, "两条一起跑", awaitCompletion: false);
        await started.Task;   // 第一批真的开始跑了

        await dispatcher.DispatchAsync(
            new CoreCommand.SessionInterrupt(sessionId), "i", true, CancellationToken.None);

        await WaitUntil(() => host.Store.ReadTimeline(Pk(host, sessionId), sessionId)
            .OfType<TimelineEvent.ToolResultEvent>().Count() >= 2, "两条结果都落盘");

        var timeline = host.Store.ReadTimeline(Pk(host, sessionId), sessionId);

        // ⚠️ **模型那条消息一字不缺**（生成早就结束了）：含两条 `tool_use`。
        // ⚠️ 认「带工具调用」那一条：读数那条也是 `From=Model`（`cache=stable` 的记账动作）。
        var model = Assert.Single(timeline.OfType<TimelineEvent.MessageEvent>(),
            m => m.Blocks.OfType<Block.ToolUseBlock>().Any(u => u.CallId == "c1"));
        Assert.Equal(["c1", "c2"], model.Blocks.OfType<Block.ToolUseBlock>().Select(u => u.CallId));
        // ⚠️ 它**不是半截**（正文说完了）——`Interrupted` 只标「正文没说完」那一种。
        Assert.Null(model.Interrupted);

        // ⚠️ **每一条调用都有结果**（跑完的 / 半截的 / 还没开始的），按调用序。
        // ⚠️ 排掉读数那条（`floating-…-readout-result`）：它属于 `cache=stable` 的记账，
        // 与本批调用无关。
        var results = timeline.OfType<TimelineEvent.ToolResultEvent>()
            .Where(r => r.CallId is "c1" or "c2").ToList();
        Assert.Equal(["c1", "c2"], results.Select(r => r.CallId));
        Assert.Contains("跑到一半", TextOf(results[0]), StringComparison.Ordinal);
        Assert.Contains("还没开始", TextOf(results[1]), StringComparison.Ordinal);
        Assert.Contains("partial", results[0].Framing!.Render(), StringComparison.Ordinal);
        Assert.Contains("not-started", results[1].Framing!.Render(), StringComparison.Ordinal);

        // ⚠️ **这一条是「下一次请求还合法」的机械判据**：记录里不再有悬空的 `tool_use`。
        Assert.False(Interruption.WasInterrupted(timeline, "c1", Interruption.NoteIdPrefix));
        Assert.False(Interruption.WasInterrupted(timeline, "c2", Interruption.NoteIdPrefix));

        Assert.Equal(1, host.Activated.First().State.Turn);
        Assert.Equal(SessionStatus.Idle, host.Activated.First().State.Status);

        var payloads = await WaitForEvent(sub,
            e => e is WireEvent.StateChanged { Status: "Idle" }, "回到 Idle");
        Assert.Empty(payloads.OfType<WireEvent.Error>());

        // ⚠️ 头那边两条「执行中」的行也要各自收到一条结果——否则它们会永远停在「执行中」。
        Assert.Equal(["c1", "c2"],
            payloads.OfType<WireEvent.ToolResultAppended>().Select(r => r.CallId));
        Assert.Equal("Idle", payloads.OfType<WireEvent.StateChanged>().Last().Status);
    }

    // ---- ③ 真 sh：半截输出 ----

    [Fact]
    public async Task Shell_Interrupt_KeepsTheOutputItAlreadyProduced()
    {
        if (!ShRunner.TryLocate(null, ShRunner.DefaultExists, out _, out _)) return;

        var jobs = new BackgroundJobHost(_root);
        var context = new SessionToolContext(
            _work, new BlobStore(Path.Combine(_root, ".blobs")),
            shell: new ShellAccess(new ShellConfig { TimeoutSeconds = 60 }, jobs, "s1"));

        using var cts = new CancellationTokenSource();

        var running = new ShellTool().RunAsync(
            new ToolCall("c", "shell", """{"command":"echo 已经跑了一半; sleep 20"}"""),
            context, cts.Token);

        // ⚠️ 给足时间让 `echo` 真的被读到：被测的是「打断时那一段还在不在」，
        // 不是「多快能打断」。
        cts.CancelAfter(TimeSpan.FromMilliseconds(1500));

        var ex = await Assert.ThrowsAsync<ToolInterruptedException>(() => running);

        Assert.Equal("c", ex.CallId);
        var text = TextOf(ex.Partial!);
        Assert.Contains("已经跑了一半", text, StringComparison.Ordinal);
        Assert.Contains("中断", text, StringComparison.Ordinal);

        // ⚠️ **没有转后台**：被打断就是被杀掉了（`Interrupted` 与 `Promoted` 是两条互斥的出口）。
        Assert.Empty(jobs.List("s1"));
    }

    // ---- 装配 ----

    private (CoreHost Host, EventBus Bus, string SessionId, CommandDispatcher Dispatcher) Harness(
        IModelAdapter adapter, IReadOnlyList<ITool> tools)
    {
        var host = new CoreHost(_root, _work);
        host.Providers.Save(new ProviderDefinition
        {
            Id = "p", Protocol = Protocols.ClaudeMessages, ApiKey = "k",
            DefaultModel = "m", Modality = "vision",
        });

        var bus = new EventBus();
        var dispatcher = new CommandDispatcher(host, bus,
            _ => new Stepper(host.Store, adapter, tools), _ => adapter);

        var created = dispatcher.DispatchAsync(
            new CoreCommand.SessionCreate(), "c", false, CancellationToken.None).GetAwaiter().GetResult();

        return (host, bus, ((Ack.Accepted)created.Ack).SessionId!, dispatcher);
    }

    private static string Pk(CoreHost host, string sessionId) =>
        host.Store.FindSession(sessionId)!.Value.ProjectKey;

    private static Task<DispatchResult> SendAsync(
        CommandDispatcher dispatcher, string sessionId, string text, bool awaitCompletion) =>
        dispatcher.DispatchAsync(new CoreCommand.SessionSend(sessionId, [new Block.TextBlock(text)]),
            Guid.NewGuid().ToString("N")[..8], awaitCompletion, CancellationToken.None);

    /// <summary>
    /// **边收边等某条事件**（收下的全留下）。
    ///
    /// <para>⚠️ 不能「等记录落盘之后读一次队列」：发布点在落盘**之后**（`Stepper` 不碰事件总线，
    /// 由分派器发）⇒ 满载时那一步还没走到发事件，队列就是空的。整套并行跑时这是稳定复现的。</para>
    /// </summary>
    private static async Task<List<WireEvent>> WaitForEvent(
        EventSubscription subscription, Func<WireEvent, bool> predicate, string what)
    {
        var all = new List<WireEvent>();
        for (var i = 0; i < 300; i++)
        {
            while (subscription.Reader.TryRead(out var published)) all.Add(published.Payload);
            if (all.Any(predicate)) return all;
            await Task.Delay(20);
        }

        Assert.Fail($"等不到事件：{what}");
        return all;
    }

    private static string TextOf(TimelineEvent.ToolResultEvent result) =>
        string.Join("\n", result.Content.OfType<Block.TextBlock>().Select(b => b.Text));

    private static string TextOf(ToolOutcome outcome) =>
        string.Join("\n", outcome.Content.OfType<Block.TextBlock>().Select(b => b.Text));

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        for (var i = 0; i < 300; i++)
        {
            if (condition()) return;
            await Task.Delay(20);
        }

        Assert.Fail($"等不到：{what}");
    }

    /// <summary>
    /// **流式、吐两段正文、然后永远不结束**：被打断那一刻手里正好有半截。
    /// </summary>
    private sealed class StreamingHangingAdapter(TaskCompletionSource emitted) : IModelAdapter, IStreamingModelAdapter
    {
        public ResolvedEndpoint? Endpoint => null;
        public string Protocol => Protocols.ClaudeMessages;
        public bool Streaming => true;

        public Task<ModelReply> CallAsync(ModelRequest request, CancellationToken ct) =>
            StreamAsync(request, null, ct);

        public async Task<ModelReply> StreamAsync(
            ModelRequest request, Action<ModelDelta>? onDelta, CancellationToken ct)
        {
            // ⚠️ 两块正文、同一块序号里两片——这正是真实上游的切片形状（一次回复可以有很多片）。
            onDelta?.Invoke(new ModelDelta(0, "text", "我先看一眼"));
            onDelta?.Invoke(new ModelDelta(0, "text", "……然后（还没说完"));
            emitted.TrySetResult();

            await Task.Delay(Timeout.Infinite, ct);
            return new ModelReply { Blocks = [], StopReason = "end_turn", Refused = false };
        }
    }

    /// <summary>**挂到被取消为止**的工具：只响应取消，永远不自己结束。</summary>
    private sealed class HangTool(TaskCompletionSource started) : ITool
    {
        public string Name => "hang";
        public PermissionClass PermissionClass => PermissionClass.ReadOnly;
        public string Description => "测试替身：挂到被取消为止。";
        public string ParametersSchemaJson => """{"type":"object","properties":{}}""";

        public async Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct)
        {
            started.TrySetResult();
            // ⚠️ 让 OCE 照常冒出去：被测的是「执行器把它翻成一条结果」，
            // 而不是「工具自己把取消吞掉」（后者会让打断变成一次正常完成）。
            await Task.Delay(Timeout.Infinite, ct);
            return new ToolOutcome { Content = [] };
        }
    }
}
