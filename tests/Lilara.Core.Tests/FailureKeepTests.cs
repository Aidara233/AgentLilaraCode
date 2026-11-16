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
/// **报错的时候，记录里留下什么，以及要不要接着跑**（2026-11-13，用户裁定：
/// 「检查一下项目，尽量就算发生报错的时候也保留模型生成结果，然后自动接续而不是直接打断」）。
///
/// <para>两条要求，各对应一个相位：</para>
/// <list type="number">
///   <item><b>生成到一半上游断了</b>（流式：已经吐过字）⇒ 那半截**要留住**
///   （一条 `Interrupted` 的模型消息，与「用户按停」同一个形状），然后**自动再推一步**
///   让模型把话说完——而不是回滚 + 抛出去 + 停在那里。</item>
///   <item><b>工具自己抛了异常</b> ⇒ 它是**一条结果**（「这条调用崩了」+ 异常原话），
///   不是「整步炸掉」：同批别的结果、模型那条含 `tool_use` 的消息都照常落盘，
///   而工具循环接着往下走。</item>
/// </list>
///
/// <para>⚠️ <b>为什么单独一个文件，而不是塞进 `InterruptKeepTests`</b>：那个文件钉的是
/// 「用户按停」（不接续、不发 `error`），这一族钉的是「上游断了」（**接续**、发 `error`）。
/// 两批判据在同一处会互相盖住——它们的记录形状**一模一样**，只有「要不要再推一步」相反。</para>
/// </summary>
public class FailureKeepTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-fail-" + Guid.NewGuid().ToString("N"));
    private readonly string _work =
        Path.Combine(Path.GetTempPath(), "lilara-failwork-" + Guid.NewGuid().ToString("N"));

    public FailureKeepTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_work);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { Directory.Delete(_work, recursive: true); } catch { }
    }

    // ---- ① 生成到一半断了：留半截 + 自动接续 ----

    [Fact]
    public async Task StreamingFailure_KeepsTheHalfMessage_AndContinuesAutomatically()
    {
        // 第一步：吐两段正文，然后断（`InvalidOperationException` = 非瞬态 ⇒ 一次尝试就抛）。
        // 第二步（自动接续）：正常说完。
        var adapter = new PartialThenBrokenAdapter(failEveryCall: false);

        var (host, bus, sessionId, dispatcher) = Harness(adapter, []);
        using var sub = bus.Subscribe(EventBus.SessionTopic(sessionId));

        await SendAsync(dispatcher, sessionId, "把这件事做完");

        var payloads = await WaitForEvent(sub,
            e => e is WireEvent.StepEnd && host.Activated.First().State.Turn >= 2, "两步都跑完");

        // ⚠️ **它真的被调了两次**：一次断掉的 + 一次自动接续的。没有这一条，
        // 「保留了半截」这件事可能只是「断了但没接」。
        Assert.Equal(2, adapter.Calls.Count);

        var timeline = host.Store.ReadTimeline(Pk(host, sessionId), sessionId);
        var interrupted = Assert.Single(timeline.OfType<TimelineEvent.MessageEvent>(),
            m => m.Interrupted == true);
        var text = string.Concat(interrupted.Blocks.OfType<Block.TextBlock>().Select(b => b.Text));
        Assert.Contains("我先看一眼", text, StringComparison.Ordinal);
        Assert.Contains("还没说完", text, StringComparison.Ordinal);

        // ⚠️ **接续那一步真的看到了它**：第二次请求的历史里必须有那条半截消息
        // （否则模型不知道自己说过什么，会从头再来一遍——那正是这一批要修的东西）。
        var second = adapter.Calls[1];
        Assert.Contains(second.Timeline.OfType<TimelineEvent.MessageEvent>(),
            m => m.Interrupted == true || m.Blocks.OfType<Block.TextBlock>()
                .Any(b => b.Text.Contains("我先看一眼", StringComparison.Ordinal)));

        // ⚠️ **接上之后模型的话也落了盘**，且这一步是正常结束（不是半截）。
        Assert.Contains(timeline.OfType<TimelineEvent.MessageEvent>(),
            m => m.Blocks.OfType<Block.TextBlock>().Any(b => b.Text.Contains("说完了", StringComparison.Ordinal)));

        // 记录真的多了一步 ⇒ Turn 与「落盘的步」对得上（`Recovery.BeforeStep` 的判据）。
        Assert.Equal(2, host.Activated.First().State.Turn);
        Assert.Equal(SessionStatus.Idle, host.Activated.First().State.Status);

        // ⚠️ **用户看得到那次失败**（但不是终点）：`error` 里带失败原话。
        var error = Assert.Single(payloads.OfType<WireEvent.Error>());
        Assert.Contains("上游断了", error.Message, StringComparison.Ordinal);

        // ⚠️ 半截那条照样以 `message.appended`（带 `interrupted`）到达头上。
        Assert.Contains(payloads.OfType<WireEvent.MessageAppended>(),
            m => m.From == "model" && m.Interrupted == true);
    }

    // ---- ② 预算：连不上就停，最多再推 3 步 ----

    [Fact]
    public async Task EveryCallFails_ContinuesThreeTimesThenStopsAndSaysSo()
    {
        var adapter = new PartialThenBrokenAdapter(failEveryCall: true);

        var (host, bus, sessionId, dispatcher) = Harness(adapter, []);
        using var sub = bus.Subscribe(EventBus.SessionTopic(sessionId));

        // ⚠️ 同步路（控制台）在**预算用完**那一刻拿到一句人能读的话——同「中断」那条形状
        // （`throwOnFailure` 就是这个意思：调用方在前面等着，它得知道这一步没跑完）。
        // ⚠️ 这 4 次调用是**串着**发生的（同步续跑），所以异常在整串跑完之后才冒出来。
        await Assert.ThrowsAsync<DispatchException>(() => SendAsync(dispatcher, sessionId, "开始"));

        // 1 次首发 + 3 次自动接续 = 4 次；第 4 次失败之后预算用完 ⇒ 停。
        await WaitUntil(() => adapter.Calls.Count >= 4 && !host.Activated.First().IsRunning, "接续停下");
        await Task.Delay(300);   // ⚠️ 再等一会儿，确认**没有**第五次

        Assert.Equal(4, adapter.Calls.Count);
        Assert.Equal(4, host.Activated.First().State.Turn);
        Assert.Equal(SessionStatus.Idle, host.Activated.First().State.Status);

        var payloads = await WaitForEvent(sub,
            e => e is WireEvent.StateChanged { Status: "Idle" }, "回到 Idle");

        // ⚠️ 每一次失败各说一句（4 条 `error`），最后一条说清「接续用完了、先停在这里」。
        var errors = payloads.OfType<WireEvent.Error>().ToList();
        Assert.Equal(4, errors.Count);
        Assert.Contains("已经用完", errors[^1].Message, StringComparison.Ordinal);

        // ⚠️ **四次都留了半截**：一条都没被回滚掉。
        Assert.Equal(4, host.Store.ReadTimeline(Pk(host, sessionId), sessionId)
            .OfType<TimelineEvent.MessageEvent>().Count(m => m.Interrupted == true));
    }

    // ---- ③ 没有半截的失败：照旧停（不盲目重发同一份请求）----

    [Fact]
    public async Task FailureWithoutAnyOutput_DoesNotContinue()
    {
        // 非流式、一上来就抛：这一趟一个字都没说 ⇒ 没有半截可留 ⇒ 不进接续循环。
        // （同一次请求已经由 `RetryPolicy` 重试过；再推一步发的是**同一份字节**。）
        var adapter = new FakeAdapter().Throws(new InvalidOperationException("boom"));

        var (host, bus, sessionId, dispatcher) = Harness(adapter, []);
        using var sub = bus.Subscribe(EventBus.SessionTopic(sessionId));

        await Assert.ThrowsAsync<DispatchException>(() => SendAsync(dispatcher, sessionId, "hi"));

        Assert.Single(adapter.Calls);
        Assert.Equal(0, host.Activated.First().State.Turn);
        Assert.DoesNotContain(host.Store.ReadTimeline(Pk(host, sessionId), sessionId)
            .OfType<TimelineEvent.MessageEvent>(), m => m.Interrupted == true);

        var payloads = await WaitForEvent(sub, e => e is WireEvent.Error, "失败那条事件");
        var error = Assert.Single(payloads.OfType<WireEvent.Error>());
        Assert.Contains("boom", error.Message, StringComparison.Ordinal);
    }

    // ---- ④ 工具抛异常：一条结果 + 接着往下跑 ----

    [Fact]
    public async Task ToolThatThrows_BecomesAResult_AndTheLoopKeepsGoing()
    {
        var adapter = new FakeAdapter()
            .ReplyBlocks(new Block.ToolUseBlock("c1", "boom", "{}"))
            .Reply("看到结果了，接着做。");

        var (host, bus, sessionId, dispatcher) = Harness(adapter, [new ThrowingTool()]);
        using var sub = bus.Subscribe(EventBus.SessionTopic(sessionId));

        await SendAsync(dispatcher, sessionId, "跑一下");

        var payloads = await WaitForEvent(sub,
            e => e is WireEvent.StepEnd && host.Activated.First().State.Turn >= 2, "两步都跑完");

        // ⚠️ **工具那次崩溃之后循环照常续跑**（模型又被打了一次）。
        Assert.Equal(2, adapter.Calls.Count);

        var timeline = host.Store.ReadTimeline(Pk(host, sessionId), sessionId);
        var result = Assert.Single(timeline.OfType<TimelineEvent.ToolResultEvent>(), r => r.CallId == "c1");
        var text = string.Concat(result.Content.OfType<Block.TextBlock>().Select(b => b.Text));
        Assert.Contains("抛了异常", text, StringComparison.Ordinal);
        Assert.Contains("磁盘满了", text, StringComparison.Ordinal);   // 异常原话要带上
        Assert.Contains("error", result.Framing!.Render(), StringComparison.Ordinal);

        // ⚠️ **配对完整**：没有悬空的 `tool_use`，下一次请求合法（`01` §3.6 ①）。
        Assert.False(Interruption.WasInterrupted(timeline, "c1", Interruption.NoteIdPrefix));

        // ⚠️ 这不是「这一步失败了」：不发 `error`（工具的失败是内容，`ToolOutcome` 那条纪律）。
        Assert.Empty(payloads.OfType<WireEvent.Error>());
    }

    // ---- ⑤ 硬失败但队列里有新话：接着处理，不把那条消息晾着 ----

    [Fact]
    public async Task HardFailure_WithAQueuedMessage_KeepsGoingAndAnswersIt()
    {
        // 第一次调用：先在 `entered` 上说「我已经开始了」，等测试把第二句话塞进队列，
        // 然后**抛出去**（这一趟什么都没产出 ⇒ 走 `catch (Exception)` 那条硬失败）。
        // 第二次调用（自动接续那一步）：正常回一句。
        var adapter = new BlockingThenBrokenAdapter();

        var (host, bus, sessionId, dispatcher) = Harness(adapter, []);
        using var sub = bus.Subscribe(EventBus.SessionTopic(sessionId));

        await SendAsync(dispatcher, sessionId, "第一件事", awaitCompletion: false);
        await adapter.Entered.Task;

        // 步在跑 ⇒ 这句话只入队（`EnqueuePending`），还没有进记录。
        await SendAsync(dispatcher, sessionId, "插一句", awaitCompletion: false);
        Assert.True(host.Activated.First().QueueDepth > 0);

        adapter.Break.SetResult();

        // ⚠️ **它必须自己接上**：硬失败之后队列里还有一句没答的话 ⇒ 再推一步。
        await WaitUntil(() => adapter.Calls.Count >= 2 && !host.Activated.First().IsRunning, "接续那一步跑完");
        await Task.Delay(200);   // 再等一会儿，确认没有第三、第四次

        Assert.Equal(2, adapter.Calls.Count);

        var timeline = host.Store.ReadTimeline(Pk(host, sessionId), sessionId);
        var users = timeline.OfType<TimelineEvent.MessageEvent>()
            .Where(m => m.From == Speaker.User)
            .Select(m => string.Concat(m.Blocks.OfType<Block.TextBlock>().Select(b => b.Text)))
            .ToList();
        Assert.Equal(["第一件事", "插一句"], users);

        // ⚠️ **接续那一步的请求里看得到那句话**：不然「接着处理」只是跑了两次同一份请求。
        Assert.Contains(adapter.Calls[1].Timeline.OfType<TimelineEvent.MessageEvent>(),
            m => m.From == Speaker.User && m.Blocks.OfType<Block.TextBlock>()
                .Any(b => b.Text.Contains("插一句", StringComparison.Ordinal)));

        // 失败那一步没有推进任何东西；接上的那一步才 +1。
        Assert.Equal(1, host.Activated.First().State.Turn);
        Assert.Equal(SessionStatus.Idle, host.Activated.First().State.Status);

        var payloads = await WaitForEvent(sub,
            e => e is WireEvent.StateChanged { Status: "Idle" }, "回到 Idle");
        var errors = payloads.OfType<WireEvent.Error>().Select(e => e.Message).ToList();

        // ⚠️ 两条红错：①失败本身 ②「队列里还有你刚说的话，接着处理」。
        // 头据此知道「出过错」以及「核心没有把那条消息扔掉」。
        Assert.Contains(errors, m => m.Contains("boom", StringComparison.Ordinal));
        Assert.Contains(errors, m => m.Contains("队列里还有", StringComparison.Ordinal));
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
        CommandDispatcher dispatcher, string sessionId, string text, bool awaitCompletion = true) =>
        dispatcher.DispatchAsync(new CoreCommand.SessionSend(sessionId, [new Block.TextBlock(text)]),
            Guid.NewGuid().ToString("N")[..8], awaitCompletion, CancellationToken.None);

    /// <summary>
    /// **边收边等某条事件**（收下的全留下）。⚠️ 与 `InterruptKeepTests` 同一份理由：
    /// 发布点在落盘**之后**，所以「等记录落盘再读一次队列」会漏。
    /// </summary>
    private static async Task<List<WireEvent>> WaitForEvent(
        EventSubscription subscription, Func<WireEvent, bool> predicate, string what)
    {
        var all = new List<WireEvent>();
        for (var i = 0; i < 400; i++)
        {
            while (subscription.Reader.TryRead(out var published)) all.Add(published.Payload);
            if (all.Any(predicate)) return all;
            await Task.Delay(20);
        }

        Assert.Fail($"等不到事件：{what}");
        return all;
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        for (var i = 0; i < 400; i++)
        {
            if (condition()) return;
            await Task.Delay(20);
        }

        Assert.Fail($"等不到：{what}");
    }

    /// <summary>
    /// **流式：每次先吐两段正文，然后断**。<paramref name="failEveryCall"/> = false 时
    /// 只有第一次断，第二次正常说完（用来钉「接上了」）。
    /// </summary>
    private sealed class PartialThenBrokenAdapter(bool failEveryCall) : IModelAdapter, IStreamingModelAdapter
    {
        public List<ModelRequest> Calls { get; } = [];
        public ResolvedEndpoint? Endpoint => null;
        public string Protocol => Protocols.ClaudeMessages;
        public bool Streaming => true;

        public Task<ModelReply> CallAsync(ModelRequest request, CancellationToken ct) =>
            StreamAsync(request, null, ct);

        public async Task<ModelReply> StreamAsync(
            ModelRequest request, Action<ModelDelta>? onDelta, CancellationToken ct)
        {
            Calls.Add(request);

            if (!failEveryCall && Calls.Count > 1)
                return new ModelReply
                {
                    Blocks = [new Block.TextBlock("我接着说完了。")],
                    StopReason = "end_turn",
                    Refused = false,
                };

            onDelta?.Invoke(new ModelDelta(0, "text", "我先看一眼"));
            onDelta?.Invoke(new ModelDelta(0, "text", "……然后（还没说完"));
            await Task.Yield();

            // ⚠️ **非瞬态**：`RetryPolicy` 一次就抛（瞬态那条会先退避重试十次，
            // 而它吐过增量之后本来也不会重试——这条用例不该被那十次等待拖住）。
            throw new InvalidOperationException("上游断了");
        }
    }

    /// <summary>**一开跑就抛**的工具：钉「工具抛异常 = 一条结果」。</summary>
    private sealed class ThrowingTool : ITool
    {
        public string Name => "boom";
        public PermissionClass PermissionClass => PermissionClass.ReadOnly;
        public string Description => "测试替身：一跑就抛。";
        public string ParametersSchemaJson => """{"type":"object","properties":{}}""";

        public Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct) =>
            throw new IOException("磁盘满了");
    }

    /// <summary>
    /// **第一次调用：等测试放行之后抛；之后的调用：正常回一句**（2026-11-13）。
    /// 用来钉「硬失败时队列里还有话 ⇒ 接着处理」，所以第一次必须在「已经开始」之后才断
    /// ——否则测试没有窗口把那句话塞进队列。
    /// </summary>
    private sealed class BlockingThenBrokenAdapter : IModelAdapter
    {
        public List<ModelRequest> Calls { get; } = [];
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Break { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ResolvedEndpoint? Endpoint => null;
        public string Protocol => Protocols.ClaudeMessages;

        public async Task<ModelReply> CallAsync(ModelRequest request, CancellationToken ct)
        {
            Calls.Add(request);

            if (Calls.Count == 1)
            {
                Entered.TrySetResult();
                await Break.Task;
                throw new InvalidOperationException("boom");
            }

            return new ModelReply
            {
                Blocks = [new Block.TextBlock("接上了。")],
                StopReason = "end_turn",
                Refused = false,
            };
        }
    }
}
