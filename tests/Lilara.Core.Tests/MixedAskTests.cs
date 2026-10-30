using Lilara.Core.Hosting;
using Lilara.Core.Kernel;
using Lilara.Core.Protocol;
using Lilara.Core.Session;
using Lilara.Core.Session.Tools;
using Lilara.Core.Storage;
using Lilara.Core.Tests.Fakes;
using Block = Lilara.Core.Kernel.Block;

namespace Lilara.Core.Tests;

/// <summary>
/// **一批里同时有好几种「要问」的调用**（`01` §3.8）。
///
/// <para>⚠️ <b>这组用例是实测事故的化石</b>（2026-10-29，用户报「模型同时发起 `plan` 改动与
/// `ask` 时卡片反复要求询问，最后只能选作废」）。当时两处判据各自成立、合起来不收敛：</para>
/// <list type="number">
///   <item><b>「在等哪一条」取的是批里的第一条</b>（`toolCalls[0]`），而不是这一问替谁问的
///   ⇒ `ask` 不是第一条时，答复被配到**别的**调用上，真正被问的那条永远等不到答复。</item>
///   <item><b>答复那一步里「有问 ⇒ 一条都不跑」</b> ⇒ 已经答到的那几条跑不掉、结果落不下来
///   ⇒ 下一轮它们**又**没被回答 ⇒ 再问一次，永不收敛。</item>
/// </list>
///
/// <para>⚠️ 所以这里的判据都不是「有没有一张卡」，而是三件更硬的事：
/// ① 答复之后会话**不再等**（收敛）；② `tool_use` 与 `tool_result` **一一对上**（记录不悬空）；
/// ③ 用户答的那句话**真的进了模型的下一次请求**（答案没有在重挂的那一轮里被丢掉）。</para>
/// </summary>
public class MixedAskTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "lilara-mix-" + Guid.NewGuid().ToString("N"));
    private readonly string _work;
    private readonly SessionStore _store;
    private readonly FakeAdapter _adapter = new();

    public MixedAskTests()
    {
        _work = Path.Combine(_tmp, "work");
        Directory.CreateDirectory(_work);
        _store = new SessionStore(Path.Combine(_tmp, "data"));
    }

    public void Dispose()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true);
                return;
            }
            catch (IOException) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) { Thread.Sleep(50); }
        }
    }

    /// <summary>两条 `ask` 在**同一批**里 ⇒ 一张卡（两个问题都在上面）、一次答复就完。</summary>
    [Fact]
    public async Task TwoAskCallsInOneBatch_OneCardOneAnswer()
    {
        var handle = NewSession();
        var ask = new AskTool();
        _adapter.ReplyWith(_ => Calls(
            ("a1", "ask", """{"questions":[{"id":"q1","type":"text","question":"第一个问题？"}]}"""),
            ("a2", "ask", """{"questions":[{"id":"q2","type":"text","question":"第二个问题？"}]}""")));
        Replies(10);

        await new Stepper(_store, _adapter, [ask]).StepAsync(handle, "go", CancellationToken.None);
        Assert.NotNull(handle.State.Ask);

        var rounds = await AnswerRounds(handle, [ask], maxRounds: 4);

        AssertConverged(handle, rounds);
        Assert.Single(rounds);
        Assert.Equal("q1:Text | q2:Text", rounds[0].Items);      // 一张卡，两条问题
        AssertAllPaired(handle);
        AssertAnswerReached(handle, "答-q1");
        AssertAnswerReached(handle, "答-q2");
    }

    /// <summary>`ask`（问意见）+ 一个写类调用（闸门要问）：**一张卡问一种**，两张都答完就收敛。</summary>
    [Fact]
    public async Task AskPlusGatedWrite_TwoCardsThenConverges()
    {
        var handle = NewSession();
        var ask = new AskTool();
        var writer = new CountingWriter();
        _adapter.ReplyWith(_ => Calls(
            ("a1", "ask", """{"questions":[{"id":"q1","type":"text","question":"用什么格式？"}]}"""),
            ("c1", "write_thing", """{"a":1}""")));
        Replies(10);

        await new Stepper(_store, _adapter, [ask, writer]).StepAsync(handle, "go", CancellationToken.None);
        Assert.NotNull(handle.State.Ask);

        var rounds = await AnswerRounds(handle, [ask, writer], maxRounds: 4);

        AssertConverged(handle, rounds);
        // ⚠️ 两种 Kind 不并成一张卡（用户分不清自己答的是「准不准跑」还是「你怎么想」）。
        Assert.Equal(2, rounds.Count);
        Assert.Equal("ask", rounds[0].ToolName);                 // 卡片上写的是哪个工具
        Assert.Equal("write_thing", rounds[1].ToolName);
        AssertAllPaired(handle);
        Assert.Equal(1, writer.Runs);                            // 批准之后**真的跑了**，且只跑一次
        AssertAnswerReached(handle, "答-q1");                    // 第一张卡的答案没有被第二轮吞掉
    }

    /// <summary>`plan`（只读、不问）+ `ask`——用户报的那一组。</summary>
    [Fact]
    public async Task PlanPlusAsk_OneAnswerAppliesBoth()
    {
        var handle = NewSession();
        var ask = new AskTool();
        _adapter.ReplyWith(_ => Calls(
            ("p1", "plan", """{"operation":"overwrite","nodes":[{"title":"第一步","body":"先看目录结构"}]}"""),
            ("a1", "ask", """{"questions":[{"id":"q1","type":"text","question":"按这个计划做？"}]}""")));
        Replies(10);

        await new Stepper(_store, _adapter, [ask, new PlanTool()])
            .StepAsync(handle, "go", CancellationToken.None);
        Assert.NotNull(handle.State.Ask);
        Assert.Null(handle.State.Plan);                          // 前提：挂起时一条都没跑

        var rounds = await AnswerRounds(handle, [ask, new PlanTool()], maxRounds: 4);

        AssertConverged(handle, rounds);
        Assert.Single(rounds);
        Assert.Equal("ask", rounds[0].ToolName);                 // ⚠️ 不是 "plan"（事故里就是它）
        AssertAllPaired(handle);
        Assert.True(1 == handle.State.Plan?.Nodes.Count,
            "计划没落地。记录里的结果：" + TimelineDump(handle));   // 计划真的落地了
        AssertAnswerReached(handle, "答-q1");
    }

    /// <summary>`switch_plan_mode(act)`（退出规划模式要问）+ `ask`。</summary>
    [Fact]
    public async Task SwitchModePlusAsk_Converges()
    {
        var handle = NewSession();
        // 先处在规划模式：退出才要那张授权卡。
        handle.UpdateStateAndPersist(_store, s => s with
        {
            Axes = new Dictionary<string, string>(s.Axes!, StringComparer.Ordinal) { [Axes.Mode] = "plan" },
        }, diskStatus: SessionStatus.Idle);

        var ask = new AskTool();
        var mode = new PlanModeTool();
        _adapter.ReplyWith(_ => Calls(
            ("m1", "switch_plan_mode", """{"mode":"act","reason":"计划好了"}"""),
            ("a1", "ask", """{"questions":[{"id":"q1","type":"text","question":"现在就动手？"}]}""")));
        Replies(10);

        await new Stepper(_store, _adapter, [ask, mode]).StepAsync(handle, "go", CancellationToken.None);
        Assert.NotNull(handle.State.Ask);

        var rounds = await AnswerRounds(handle, [ask, mode], maxRounds: 4);

        AssertConverged(handle, rounds);
        AssertAllPaired(handle);
        Assert.Equal("act", handle.State.Axes![Axes.Mode]);      // 批准之后真的退出了规划模式
        AssertAnswerReached(handle, "答-q1");
    }

    // ---- 脚手架 ----

    private sealed record Round(string Kind, string ToolName, string AskId, string Items, bool RePaused);

    /// <summary>
    /// 一张卡一张卡地答，直到会话不再等（或者用满 <paramref name="maxRounds"/>）。
    /// ⚠️ 「又挂起」的判据是**确定**的两条出口：新的一张卡，或者模型被接着调了一次。
    /// </summary>
    private async Task<List<Round>> AnswerRounds(SessionHandle handle, ITool[] tools, int maxRounds)
    {
        var rounds = new List<Round>();

        for (var i = 0; i < maxRounds; i++)
        {
            var paused = handle.State.Ask;
            if (paused is null) break;

            var awaiting = handle.AwaitingInput;
            var items = awaiting?.Ask.Kind == paused.Kind
                ? awaiting.Ask.Items
                : AskTool.ItemsOf(_store, handle.ProjectKey, handle.SessionId, paused.CallId);

            var answers = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in items) answers[item.Id] = "答-" + item.Id;
            if (paused.Kind == AskKind.Permission) answers["decision"] = "allow";

            var before = paused.AskId;
            var callsBefore = _adapter.Calls.Count;
            var (dispatcher, _) = DispatcherFor(handle, tools);
            await dispatcher.DispatchAsync(
                new CoreCommand.ApprovalRespond(handle.SessionId, before, "submit", answers),
                "cmd-" + i, awaitCompletion: false, CancellationToken.None);

            await WaitUntil(() =>
                handle.State.Status == SessionStatus.WaitingForInput
                || _adapter.Calls.Count > callsBefore);

            var again = handle.State.Ask is { } now
                && handle.State.Status == SessionStatus.WaitingForInput
                && now.AskId != before;

            rounds.Add(new Round(paused.Kind.ToString(), paused.ToolName, before,
                string.Join(" | ", items.Select(x => x.Id + ":" + x.Shape)), again));

            if (!again) break;
        }

        return rounds;
    }

    private static void AssertConverged(SessionHandle handle, IEnumerable<Round> rounds) =>
        Assert.True(handle.State.Status != SessionStatus.WaitingForInput,
            "答完之后仍然在等回答 ⇒ 又弹了一张卡。经历过："
            + string.Join(" → ", rounds.Select(r =>
                $"[{r.Kind} {r.ToolName} items=({r.Items}){(r.RePaused ? " → 又挂起" : "")}]")));

    /// <summary>**每条 `tool_use` 都配上了 `tool_result`**（`01` §3.6 ①）——悬空会让下一条请求 400。</summary>
    private void AssertAllPaired(SessionHandle handle)
    {
        var timeline = _store.ReadTimeline(handle.ProjectKey, handle.SessionId);

        var uses = timeline.OfType<TimelineEvent.MessageEvent>()
            .SelectMany(m => m.Blocks.OfType<Block.ToolUseBlock>())
            .Select(b => b.CallId).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var results = timeline.OfType<TimelineEvent.ToolResultEvent>()
            .Select(r => r.CallId).OrderBy(x => x, StringComparer.Ordinal).ToList();

        Assert.Equal(uses, results);
    }

    /// <summary>**用户答的那句话进了模型的下一次请求**（答案没有在重挂那一轮里被丢掉）。</summary>
    private void AssertAnswerReached(SessionHandle handle, string answer)
    {
        Assert.True(_adapter.Calls.Count > 0, "模型一次都没被调过。");

        var text = string.Concat(_adapter.Calls[^1].Timeline
            .OfType<TimelineEvent.ToolResultEvent>()
            .SelectMany(r => r.Content.OfType<Block.TextBlock>()).Select(b => b.Text));

        Assert.Contains(answer, text, StringComparison.Ordinal);
    }

    private (CommandDispatcher Dispatcher, EventBus Bus) DispatcherFor(SessionHandle handle, params ITool[] tools)
    {
        var bus = new EventBus();
        var host = new CoreHost(_store.DataRoot, _work);
        host.Register(handle);

        return (new CommandDispatcher(host, bus, h => new Stepper(_store, _adapter, tools)), bus);
    }

    /// <summary>把结果正文摊平——失败时能看出那条工具**到底回了什么**。</summary>
    private string TimelineDump(SessionHandle handle) =>
        string.Join(" | ", _store.ReadTimeline(handle.ProjectKey, handle.SessionId)
            .OfType<TimelineEvent.ToolResultEvent>()
            .Select(r => r.CallId + "=" + string.Concat(
                r.Content.OfType<Block.TextBlock>().Select(b => b.Text))));

    private static async Task WaitUntil(Func<bool> done, int timeoutMs = 5000)    {
        for (var waited = 0; waited < timeoutMs; waited += 20)
        {
            if (done()) return;
            await Task.Delay(20);
        }

        Assert.Fail($"等了 {timeoutMs}ms，那个条件始终没成立（多半是那条后台推进根本没跑）。");
    }

    /// <summary>后面那些步都用空回复收尾（脚本耗尽会抛，从而掩盖真正的形态）。</summary>
    private void Replies(int n)
    {
        for (var i = 0; i < n; i++) _adapter.Reply("好的。", "end_turn");
    }

    private static ModelReply Calls(params (string CallId, string Name, string Args)[] calls) => new()
    {
        Blocks = calls.Select(c => (Block)new Block.ToolUseBlock(c.CallId, c.Name, c.Args)).ToList(),
        StopReason = "tool_use",
        Refused = false,
    };

    private static ModelSelection Model() => new()
    {
        ProviderId = "p", ModelId = "m", Protocol = Protocols.ClaudeMessages, Effort = "high", MaxTokens = 1000,
    };

    private SessionHandle NewSession() => SessionHandle.Create(
        _store, _work, Model(), sessionId: "s-mix-1").DynamicCache();

    private sealed class CountingWriter : ITool
    {
        public int Runs;
        public string Name => "write_thing";
        public PermissionClass PermissionClass => PermissionClass.Writing;
        public string Description => "测试替身：一个写入类工具。";
        public string ParametersSchemaJson => """{"type":"object","properties":{}}""";

        public Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct)
        {
            Runs++;
            return Task.FromResult(new ToolOutcome { Content = [new Block.TextBlock("写了")] });
        }
    }
}
