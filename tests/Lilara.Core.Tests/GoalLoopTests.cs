using System.Text.Json;
using Lilara.Core.Hosting;
using Lilara.Core.Kernel;
using Lilara.Core.Protocol;
using Lilara.Core.Providers;
using Lilara.Core.Session;
using Lilara.Core.Session.Tools;
using Lilara.Core.Storage;
using Lilara.Core.Tests.Fakes;
using CanonicalBlock = Lilara.Core.Kernel.Block;

namespace Lilara.Core.Tests;

/// <summary>
/// goal 模式：模型停回合而目标未收 ⇒ 自动再推一步；`finish_goal` 只是把主动性拨回 standard，
/// 拨回后**再多跑一步**让模型收尾。⚠️ 不设步数上限（用户 2026-10-22 的选择）——
/// 停止只有：模型调用失败、用户中断、拒答 / 空回复、或模型自己调 `finish_goal`。
/// </summary>
public class GoalLoopTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-goal-" + Guid.NewGuid().ToString("N"));
    private readonly string _work =
        Path.Combine(Path.GetTempPath(), "lilara-goalwork-" + Guid.NewGuid().ToString("N"));

    private readonly FakeAdapter _adapter = new();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
    }

    private (CoreHost Host, CommandDispatcher Dispatcher, SessionHandle Session) NewHarness(
        IModelAdapter? adapter = null)
    {
        var host = new CoreHost(_root, _work);
        host.Providers.Save(new ProviderDefinition
        {
            Id = "p", Protocol = Protocols.ClaudeMessages, ApiKey = "k",
            DefaultModel = "m", Modality = "vision",
        });

        var used = adapter ?? _adapter;
        var dispatcher = new CommandDispatcher(host, new EventBus(),
            h => new Stepper(host.Store, used, BuiltInTools.All));

        var created = dispatcher.DispatchAsync(
            new CoreCommand.SessionCreate(), "c", false, CancellationToken.None).GetAwaiter().GetResult();
        var sessionId = ((Ack.Accepted)created.Ack).SessionId!;
        host.TryGetActivated(sessionId, out var handle);

        return (host, dispatcher, handle);
    }

    private static Task<DispatchResult> SendAsync(CommandDispatcher d, SessionHandle h, string text) =>
        d.DispatchAsync(new CoreCommand.SessionSend(h.SessionId, [new CanonicalBlock.TextBlock(text)]),
            Guid.NewGuid().ToString("N")[..8], true, CancellationToken.None);

    private static async Task SetAxisAsync(CommandDispatcher d, SessionHandle h, string axis, string value)
    {
        var ack = await d.DispatchAsync(new CoreCommand.SessionSetAxis(h.SessionId, axis, value),
            Guid.NewGuid().ToString("N")[..8], false, CancellationToken.None);
        Assert.True(((Ack.Accepted)ack.Ack).CommandId.Length > 0);
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("等待条件超时");
            await Task.Delay(25);
        }
    }

    private static ModelReply Say(string text, string stopReason = "end_turn") => new()
    {
        Blocks = [new CanonicalBlock.TextBlock(text)],
        StopReason = stopReason,
        Refused = stopReason == "refusal",
    };

    private static ModelReply CallFinishGoal() => new()
    {
        Blocks = [new CanonicalBlock.ToolUseBlock("c-finish", "finish_goal", "{}")],
        StopReason = "tool_use",
        Refused = false,
    };

    // ---- 基本：standard 不续跑 ----

    [Fact]
    public async Task Standard_DoesNotAutoContinue()
    {
        var (_, dispatcher, session) = NewHarness();
        _adapter.Reply("done");

        await SendAsync(dispatcher, session, "hi");

        Assert.Single(_adapter.Calls);
        Assert.Equal(1, session.State.Turn);
        Assert.Equal("Idle", session.State.Status.ToString());
    }

    // ---- goal：一直续跑，直到 finish_goal ----

    [Fact]
    public async Task Goal_ContinuesUntilFinishGoal_AndThenAllowsOneMoreSummaryStep()
    {
        var (_, dispatcher, session) = NewHarness();
        await SetAxisAsync(dispatcher, session, Axes.Initiative, "goal");

        _adapter.Reply("working")
                .Reply("still working")
                .ReplyBlocks(new CanonicalBlock.ToolUseBlock("c-finish", "finish_goal", "{}"))
                .Reply("all done");

        await SendAsync(dispatcher, session, "start");

        // 2 次自主续跑 + finish_goal 那一步 + 收尾那一步。
        Assert.Equal(4, _adapter.Calls.Count);
        Assert.Equal(4, session.State.Turn);
        Assert.Equal("standard", session.State.Axes![Axes.Initiative]);
        Assert.Equal("Idle", session.State.Status.ToString());
    }

    [Fact]
    public async Task FinishGoal_OutsideGoalMode_ChangesNothing()
    {
        var (_, dispatcher, session) = NewHarness();
        _adapter.ReplyBlocks(new CanonicalBlock.ToolUseBlock("c-finish", "finish_goal", "{}"))
                .Reply("ok");

        await SendAsync(dispatcher, session, "hi");

        // 工具照常执行、普通工具循环照常再走一步——但轴没被改。
        Assert.Equal(2, _adapter.Calls.Count);
        Assert.Equal("step", session.State.Axes![Axes.Initiative]);
    }

    // ---- 模型拒答 / 空回复：不续跑（同一句拒绝会无限重放）----

    [Fact]
    public async Task Goal_Refusal_StopsInsteadOfReplaying()
    {
        var (_, dispatcher, session) = NewHarness();
        await SetAxisAsync(dispatcher, session, Axes.Initiative, "goal");
        _adapter.Reply("no", stopReason: "refusal");

        await SendAsync(dispatcher, session, "start");

        Assert.Single(_adapter.Calls);
        Assert.Equal("goal", session.State.Axes![Axes.Initiative]);
    }

    // ---- ask：挂起是刻意的停顿，不续跑 ----

    [Fact]
    public async Task Goal_Ask_StopsAtWaitingForInput()
    {
        var (_, dispatcher, session) = NewHarness();
        await SetAxisAsync(dispatcher, session, Axes.Initiative, "goal");

        _adapter.ReplyBlocks(new CanonicalBlock.ToolUseBlock("c-ask", "ask",
            """{"questions":[{"id":"q","question":"继续吗？","type":"text"}]}"""));

        await SendAsync(dispatcher, session, "start");

        Assert.Single(_adapter.Calls);
        Assert.Equal(SessionStatus.WaitingForInput, session.State.Status);
    }

    // ---- 模型调用失败：唯一的框架级停止 ----

    [Fact]
    public async Task Goal_ModelFailure_Stops()
    {
        var (_, dispatcher, session) = NewHarness();
        await SetAxisAsync(dispatcher, session, Axes.Initiative, "goal");

        _adapter.Reply("working").Throws(new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<DispatchException>(() => SendAsync(dispatcher, session, "start"));

        Assert.Equal(2, _adapter.Calls.Count);
        Assert.Equal("goal", session.State.Axes![Axes.Initiative]);
        Assert.NotEqual(SessionStatus.Running, session.State.Status);
    }

    // ---- 用户中断：真停，不自动恢复；目标仍然在 ----

    private sealed class BlockingAdapter(string firstText) : IModelAdapter
    {
        public List<ModelRequest> Calls { get; } = [];
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ResolvedEndpoint? Endpoint => null;
        public string Protocol => Protocols.ClaudeMessages;

        public async Task<ModelReply> CallAsync(ModelRequest request, CancellationToken ct)
        {
            Calls.Add(request);
            if (Calls.Count == 1) return Say(firstText);

            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    [Fact]
    public async Task Goal_UserInterrupt_StopsAndDoesNotResume()
    {
        var adapter = new BlockingAdapter("working");
        var (_, dispatcher, session) = NewHarness(adapter);

        await SetAxisAsync(dispatcher, session, Axes.Initiative, "goal");

        await dispatcher.DispatchAsync(
            new CoreCommand.SessionSend(session.SessionId, [new CanonicalBlock.TextBlock("start")]),
            "s", false, CancellationToken.None);

        // 自动续跑已经起来（第二次调用在等取消）。
        await WaitUntil(() => adapter.Calls.Count >= 2);

        await dispatcher.DispatchAsync(new CoreCommand.SessionInterrupt(session.SessionId),
            "i", false, CancellationToken.None);

        await Task.Delay(500);

        Assert.Equal(2, adapter.Calls.Count);   // 没有第三次
        Assert.Equal("goal", session.State.Axes![Axes.Initiative]);
        Assert.Equal(SessionStatus.Idle, session.State.Status);
    }

    // ---- 目标线进尾块，不进历史 ----

    [Fact]
    public async Task GoalDirective_GoesIntoTheFloatingTail_NotTheHistory()
    {
        var (host, dispatcher, session) = NewHarness();
        await SetAxisAsync(dispatcher, session, Axes.Initiative, "goal");

        _adapter.Reply("working")
                .ReplyBlocks(new CanonicalBlock.ToolUseBlock("c-finish", "finish_goal", "{}"))
                .Reply("done");

        await SendAsync(dispatcher, session, "start");

        // ⚠️ 尾块在「有 floating_context 工具」时被挪进末尾那对合成调用（`Slots.Tail` 被清空），
        // 所以目标线要从**请求时间线**里找，而不是从 Slots.Tail。
        var tail = FloatingTailText(_adapter.Calls[0].Timeline);
        Assert.Contains("目标模式", tail);

        var history = host.Store.ReadTimeline(session.ProjectKey, session.SessionId);
        var text = string.Concat(history.OfType<TimelineEvent.MessageEvent>()
            .SelectMany(m => m.Blocks.OfType<CanonicalBlock.TextBlock>())
            .Select(b => b.Text));
        Assert.DoesNotContain("目标模式", text);
    }

    [Fact]
    public async Task Standard_DoesNotCarryTheGoalDirective()
    {
        var (_, dispatcher, session) = NewHarness();
        _adapter.Reply("done");

        await SendAsync(dispatcher, session, "hi");

        var tail = FloatingTailText(_adapter.Calls[0].Timeline);
        Assert.DoesNotContain("目标模式", tail);
    }

    /// <summary>末尾那对合成 `floating_context` 调用的结果文本（尾块在请求里的实际住处）。</summary>
    private static string FloatingTailText(IReadOnlyList<TimelineEvent> timeline) =>
        string.Concat(timeline.OfType<TimelineEvent.ToolResultEvent>()
            .Where(e => e.Id.StartsWith("floating-", StringComparison.Ordinal))
            .SelectMany(e => e.Content.OfType<CanonicalBlock.TextBlock>())
            .Select(b => b.Text));

    // ---- 一个在最后一步收尾期间入队的消息，不能被落下（2026-10-22）----

    private sealed class GateAdapter : IModelAdapter
    {
        public List<ModelRequest> Calls { get; } = [];
        private readonly TaskCompletionSource<ModelReply> _gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ResolvedEndpoint? Endpoint => null;
        public string Protocol => Protocols.ClaudeMessages;

        public void Release(ModelReply first) => _gate.TrySetResult(first);

        public async Task<ModelReply> CallAsync(ModelRequest request, CancellationToken ct)
        {
            Calls.Add(request);
            if (Calls.Count == 1) return await _gate.Task.WaitAsync(ct);
            return Say("second");
        }
    }

    [Fact]
    public async Task MessageQueuedDuringTheFinalStep_IsStillConsumed()
    {
        var adapter = new GateAdapter();
        var (host, dispatcher, session) = NewHarness(adapter);

        await dispatcher.DispatchAsync(
            new CoreCommand.SessionSend(session.SessionId, [new CanonicalBlock.TextBlock("first")]),
            "s1", false, CancellationToken.None);
        await WaitUntil(() => adapter.Calls.Count == 1);

        // 这一步还在跑 ⇒ 第二条只能入队。
        await dispatcher.DispatchAsync(
            new CoreCommand.SessionSend(session.SessionId, [new CanonicalBlock.TextBlock("second")]),
            "s2", false, CancellationToken.None);

        adapter.Release(Say("first done"));    // 没有工具调用，按旧行为会就此停下
        await WaitUntil(() => adapter.Calls.Count == 2);

        var history = host.Store.ReadTimeline(session.ProjectKey, session.SessionId);
        var text = string.Concat(history.OfType<TimelineEvent.MessageEvent>()
            .SelectMany(m => m.Blocks.OfType<CanonicalBlock.TextBlock>()).Select(b => b.Text));
        Assert.Contains("second", text);
    }

    // ---- 前缀稳定：轴值 / 联网配置都不进工具表 ----

    [Fact]
    public async Task ToolTable_IsIdenticalAcrossAxisAndConfig()
    {
        var (_, dispatcher, session) = NewHarness();
        _adapter.Reply("done");
        await SendAsync(dispatcher, session, "first");
        var before = JsonSerializer.Serialize(_adapter.Calls[0].Tools, WireJson.Options);

        await SetAxisAsync(dispatcher, session, Axes.Network, "all");
        await SetAxisAsync(dispatcher, session, Axes.Initiative, "goal");
        _adapter.Reply("working")
                .ReplyBlocks(new CanonicalBlock.ToolUseBlock("c-finish", "finish_goal", "{}"))
                .Reply("done");
        await SendAsync(dispatcher, session, "second");

        var after = JsonSerializer.Serialize(_adapter.Calls[1].Tools, WireJson.Options);
        Assert.Equal(before, after);
        Assert.Contains(_adapter.Calls[1].Tools, t => t.Name == WebSearchTool.NameOf);
        Assert.Contains(_adapter.Calls[1].Tools, t => t.Name == WebFetchTool.NameOf);
        Assert.Contains(_adapter.Calls[1].Tools, t => t.Name == FinishGoalTool.NameOf);
    }
}
