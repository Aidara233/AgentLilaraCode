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
/// **挂起与恢复**（`01` §3.8）——从一条 <c>tool_use</c> 走到「会话停在等回答」，
/// 再走回答那条路回到可推进。
///
/// <para>⚠️ <b>这组测试里那个「写类替身」是探针，不是被测物</b>：闸门那两格
/// <see cref="Verdict.Ask"/> 只对 <c>Writing</c> / <c>Destructive</c> 生效，
/// 而三个内建（`ls` / `read` / `fetch`）**全是只读** ⇒ 不造一个写类替身，
/// 这张测试根本碰不到闸门那个分支（同 `GateTests` 那条「探针 ≠ 被测物」的分寸）。</para>
///
/// <para>⚠️ <b>它同时钉住一条不变量：有问 ⇒ 一条工具都没跑</b>。
/// 替身里那个计数器是**唯一**能证明这件事的东西——看结果列表是空的，
/// 那只说明「没落结果」，不说明「没执行」。</para>
/// </summary>
public class AskTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "lilara-ask-" + Guid.NewGuid().ToString("N"));
    private readonly string _work;
    private readonly SessionStore _store;
    private readonly FakeAdapter _adapter = new();

    public AskTests()
    {
        _work = Path.Combine(_tmp, "work");
        Directory.CreateDirectory(_work);
        _store = new SessionStore(Path.Combine(_tmp, "data"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true);
    }

    /// <summary>一个**会记数**的写类工具。⚠️ 计数器就是「它到底跑没跑」的判据。</summary>
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

    private static ModelSelection Model() => new()
    {
        ProviderId = "p", ModelId = "m", Protocol = Protocols.ClaudeMessages, Effort = "high", MaxTokens = 1000,
    };

    /// <summary>
    /// 默认档位（`standard`）——它的写入格在 `01` §3.5 那张表里正是「问」。
    ///
    /// <para>⚠️ <b>id 走注入</b>：`askId` 那条线要一条命令才答得上，而 id 随机时
    /// 断言写不出来（见 `Create` 的 `sessionId` 参数注释）。</para>
    /// </summary>
    private SessionHandle NewSession() => SessionHandle.Create(
        _store, _work, Model(), sessionId: "s-ask-1");

    private static ModelReply Calls(params (string CallId, string Name, string Args)[] calls) => new()
    {
        Blocks = calls.Select(c => (Block)new Block.ToolUseBlock(c.CallId, c.Name, c.Args)).ToList(),
        StopReason = "tool_use",
        Refused = false,
    };

    private static Stepper StepperWith(SessionStore store, FakeAdapter adapter, params ITool[] tools) =>
        new(store, adapter, tools);

    // ---- 一、挂起 ----

    /// <summary>
    /// ⚠️ <b>这条是本批最要紧的不变量</b>：一批里出现询问 ⇒ **一条工具都不许跑**。
    /// 否则「call 1 已经写了、call 2 要问」⇒ 中止 ⇒ 那次写入**没有记录**，而记录是事实。
    /// </summary>
    [Fact]
    public async Task AwaitingInput_ExecutesNothingNotEvenTheAllowedOne()
    {
        var handle = NewSession();
        var writer = new CountingWriter();
        _adapter.ReplyWith(_ => Calls(
            ("c1", "read", """{"path":"a.txt"}"""),      // 这一条本来会跑（只读，放行）
            ("c2", "write_thing", "{}")));               // 这一条要问

        var step = await StepperWith(_store, _adapter, [new ReadTool(), writer])
            .StepAsync(handle, "go", CancellationToken.None);

        Assert.NotNull(step.Refused);
        Assert.Equal(0, writer.Runs);                    // ⚠️ 关键：写了的那条**也没跑**

        // 一条结果都没落——那些 tool_use 悬着，等用户答完再配对。
        var timeline = _store.ReadTimeline(handle.ProjectKey, handle.SessionId);
        Assert.DoesNotContain(timeline, e => e is TimelineEvent.ToolResultEvent);
    }

    /// <summary>
    /// ⚠️ <b>模型消息<b>必须</b>落盘</b>——这一格我第一版写反了。
    /// 不落它，答完之后补的那条 `tool_result` 配对的 `tool_use` 就从没落过盘 ⇒
    /// 那条结果**悬空** ⇒ 下一步的请求非法（`01` §3.6 ②）。
    /// </summary>
    [Fact]
    public async Task AwaitingInput_StillPersistsTheModelMessageWithItsToolUse()
    {
        var handle = NewSession();
        _adapter.ReplyWith(_ => Calls(("c1", "write_thing", "{}")));

        await StepperWith(_store, _adapter, [new CountingWriter()])
            .StepAsync(handle, "go", CancellationToken.None);

        var toolUses = _store.ReadTimeline(handle.ProjectKey, handle.SessionId)
            .OfType<TimelineEvent.MessageEvent>()
            .SelectMany(m => m.Blocks.OfType<Block.ToolUseBlock>())
            .ToList();

        Assert.Single(toolUses);
        Assert.Equal("c1", toolUses[0].CallId);
    }

    /// <summary>状态与挂起账都要落盘——⚠️ `WaitingForInput` **是磁盘合法取值**（见 `SessionStatus`）。</summary>
    [Fact]
    public async Task AwaitingInput_PersistsWaitingForInputAndThePause()
    {
        var handle = NewSession();
        _adapter.ReplyWith(_ => Calls(("c1", "write_thing", "{}")));

        await StepperWith(_store, _adapter, [new CountingWriter()])
            .StepAsync(handle, "go", CancellationToken.None);

        var onDisk = _store.ReadState(handle.ProjectKey, handle.SessionId);
        Assert.Equal(SessionStatus.WaitingForInput, onDisk.Status);
        Assert.NotNull(onDisk.Ask);
        Assert.Equal("c1", onDisk.Ask!.CallId);
        Assert.Equal("write_thing", onDisk.Ask.ToolName);

        // 内存那一半也要在（答完要靠它把答案归位）。
        Assert.NotNull(handle.AwaitingInput);
        Assert.Equal(AskKind.Permission, handle.AwaitingInput!.Ask.Kind);
    }

    /// <summary>
    /// ⚠️ **挂起时那条模型消息是「含 tool_use、无配对结果」**——它**不许漏给模型**。
    /// 判据：那一步**没有**构造第二次请求（只调了一次模型）。
    /// </summary>
    [Fact]
    public async Task AwaitingInput_DoesNotCallTheModelAgain()
    {
        var handle = NewSession();
        _adapter.ReplyWith(_ => Calls(("c1", "write_thing", "{}")));

        await StepperWith(_store, _adapter, [new CountingWriter()])
            .StepAsync(handle, "go", CancellationToken.None);

        Assert.Single(_adapter.Calls);
    }

    // ---- 二、超时 ----

    /// <summary>
    /// **超时是盘面自证，不是定时器**（`01` §3.8）：过了 `deadlineAt` 之后再答复，
    /// 答案**不被采用**，按超时合成。⚠️ 授权超时 = **拒绝**（用户裁定）。
    /// </summary>
    [Fact]
    public async Task Expired_DoesNotAdoptTheLateAnswer()
    {
        var handle = NewSession();

        // 手工造一份「已经过期」的挂起态——现造一个 `DeadlineAt` 在过去的 PausedAsk。
        handle.UpdateStateAndPersist(_store, s => s with
        {
            Status = SessionStatus.WaitingForInput,
            Turn = 1,
            Ask = new PausedAsk("ask-1", AskKind.Permission, "c1", "write_thing", "h",
                DeadlineAt: CoreClock.Now() - TimeSpan.FromMinutes(1)),
        }, diskStatus: SessionStatus.WaitingForInput);

        Assert.True(handle.State.Ask!.DeadlineAt < CoreClock.Now());   // 前提：真的过期了

        // 直接走 dispatcher 那条路（超时判定住在那里）。
        var (dispatcher, bus) = DispatcherFor(handle);
        var result = await dispatcher.DispatchAsync(
            new CoreCommand.ApprovalRespond(handle.SessionId, "ask-1", "submit",
                new Dictionary<string, string> { ["decision"] = "allow" }),
            "cmd-1", awaitCompletion: false, CancellationToken.None);

        Assert.False(result.IsRejected);

        var text = LastResultText(handle);
        // ⚠️ 判据是「报的是**没人回答**」而不是「用户拒绝了」——迟到的那个「批准」
        // **没有被采用**。措辞本身（「等你回答了 X 分钟，没有人回答」）是这句的实现，
        // 而这条断言认的是**语义**：它说的是「没等到人」，不是「被否了」。
        Assert.Contains("没有人回答", text);
        Assert.DoesNotContain("批准", text);
    }

    // ---- 三、答复 ----

    /// <summary>批准 ⇒ 落一条说得清的结果，且**不在这里执行那个工具**（执行只归 `ToolExecutor`）。</summary>
    [Fact]
    public async Task Approved_LeavesAResultSayingSo_AndDoesNotRunTheTool()
    {
        var handle = NewSession();
        var writer = new CountingWriter();
        _adapter.ReplyWith(_ => Calls(("c1", "write_thing", "{}")));

        await StepperWith(_store, _adapter, [writer])
            .StepAsync(handle, "go", CancellationToken.None);
        Assert.NotNull(handle.State.Ask);

        var (dispatcher, _) = DispatcherFor(handle, writer);
        await dispatcher.DispatchAsync(
            new CoreCommand.ApprovalRespond(handle.SessionId, handle.State.Ask!.AskId, "submit",
                new Dictionary<string, string> { ["decision"] = "allow" }),
            "cmd-1", awaitCompletion: false, CancellationToken.None);

        // ⚠️ **那个工具仍然一次没跑**：批准只是「下一步会放行」，
        // 而「跑」是模型重新发起之后 `ToolExecutor` 的事。把执行塞进这里
        // 会让「谁执行工具」有两个答案。
        Assert.Equal(0, writer.Runs);
        Assert.Contains("批准", LastResultText(handle));

        // 挂起态清干净了（内存 + 盘）。
        Assert.Null(handle.AwaitingInput);
        Assert.Null(_store.ReadState(handle.ProjectKey, handle.SessionId).Ask);
    }

    /// <summary>
    /// **记忆**（`01` §3.8）：选了 `--remember` 之后，同一个工具**同样的参数**
    /// 不再触发询问。⚠️ 反过来（换个参数**照样要问**）才是这一格的要害。
    /// </summary>
    [Fact]
    public async Task RememberArgs_CoversTheSameArgumentsButNotOthers()
    {
        var store = new ApprovalStore(_store.DataRoot, "pk", "sid");
        store.Add(new Approval("write_thing", ApprovalStore.HashArgs("""{"a":1}"""), ToolWide: false, CoreClock.Now()));

        Assert.True(store.Covers("write_thing", ApprovalStore.HashArgs("""{"a":1}""")));
        // ⚠️ 这一条是整套东西的判据：**用户批的不是工具，是这一次调用**。
        Assert.False(store.Covers("write_thing", ApprovalStore.HashArgs("""{"a":2}""")));
        Assert.False(store.Covers("other", ApprovalStore.HashArgs("""{"a":1}""")));
    }

    /// <summary>粗的那一档（`Tool`）放行同一个工具的任何参数——⚠️ 它粗，所以卡片上要写明。</summary>
    [Fact]
    public void RememberTool_CoversAnyArgumentsOfThatTool()
    {
        var store = new ApprovalStore(_store.DataRoot, "pk", "sid");
        store.Add(new Approval("write_thing", ApprovalStore.HashArgs("""{"a":1}"""), ToolWide: true, CoreClock.Now()));

        Assert.True(store.Covers("write_thing", ApprovalStore.HashArgs("""{"totally":"different"}""")));
        Assert.False(store.Covers("other", ApprovalStore.HashArgs("""{"a":1}""")));
    }

    /// <summary>
    /// ⚠️ **参数哈希只哈希原文**：先解析再序列化会让键序不同的两段撞成同一个哈希，
    /// 于是用户批的这一条**替另一条放行**。
    /// </summary>
    [Fact]
    public void HashArgs_IsByteExact_NotStructural()
    {
        Assert.NotEqual(
            ApprovalStore.HashArgs("""{"a":1,"b":2}"""),
            ApprovalStore.HashArgs("""{"b":2,"a":1}"""));
    }

    /// <summary>
    /// ⚠️ <b>问题原文从**落盘**的那条 `tool_use` 解出来</b>——不依赖内存那半。
    ///
    /// <para>缺陷形状（实测 2026-10-08）：内存那份（`AwaitingInput`）只在「本进程亲手挂起过」
    /// 时才在，**任何一次「从盘重新载入」都会丢**（不只是重启）⇒ 用户打开页面时
    /// 问题**是空的**。而它在盘上本来就有。</para>
    ///
    /// <para>⚠️ 这条同时钉住「**只有一份实现**」：`/state` 与答复那条路必须走同一个函数，
    /// 否则会出现「页面看得见问题、而回答时说你没答」——那个分叉**真的发生过**
    /// （我先只修了 `/state`）。</para>
    /// </summary>
    [Fact]
    public async Task Items_AreRecoveredFromDisk_NotFromMemory()
    {
        var handle = NewSession();
        _adapter.ReplyWith(_ => new ModelReply
        {
            Blocks =
            [
                new Block.ToolUseBlock("c1", "ask",
                    """{"questions":[{"id":"fmt","type":"choice","question":"什么格式？","options":["A","B"]}]}"""),
            ],
            StopReason = "tool_use",
            Refused = false,
        });

        await StepperWith(_store, _adapter, [new AskTool()]).StepAsync(handle, "go", CancellationToken.None);

        // ⚠️ **模拟「从盘重新载入」**：内存那半丢掉（重启、或只是重新 Activate 一次）。
        handle.AwaitingInput = null;

        var items = AskTool.ItemsOf(_store, handle.ProjectKey, handle.SessionId, "c1");

        var one = Assert.Single(items);
        Assert.Equal("fmt", one.Id);
        Assert.Contains("什么格式", one.Question);

        // 对不上号的 callId ⇒ 空（**不能拿另一条历史询问的问题**顶替）。
        Assert.Empty(AskTool.ItemsOf(_store, handle.ProjectKey, handle.SessionId, "nope"));
    }

    // ---- 四、ask 工具 ----

    /// <summary>
    /// ⚠️ <b>挂起之后 agent 循环<b>必须</b>停</b>——而这一格是**实测抓出来的**
    /// （2026-10-08，真模型 + 真网关）。
    ///
    /// <para>缺陷形状：模型调 `ask` ⇒ `Stepper` 挂起、一条结果都没落 ⇒ 状态 `WaitingForInput`，
    /// 可 `ContinueIfToolCalledAsync` **照样**看见回复里那个 `tool_use` ⇒ 接着推下一步
    /// ⇒ 下一次请求里那条 `tool_use` 后面没有 `tool_result` ⇒ 上游原话
    /// 「`tool_use` ids were found without `tool_result` blocks immediately after」。
    /// ⇒ **那个会话在用户答完之前一点都推不动**，而它当时看起来完全正常
    /// （状态对、事件对、卡片也对）。</para>
    ///
    /// <para>判据：**只调了一次模型**。</para>
    /// </summary>
    [Fact]
    public async Task AwaitingInput_StopsTheToolLoop()
    {
        var handle = NewSession();
        _adapter.ReplyWith(_ => Calls(("c1", "write_thing", "{}")));

        var host = new CoreHost(_store.DataRoot, _work);
        host.Register(handle);
        var dispatcher = new CommandDispatcher(host, new EventBus(), h => new Stepper(_store, _adapter, [new CountingWriter()]));

        var result = await dispatcher.DispatchAsync(
            new CoreCommand.SessionSend(handle.SessionId, [new Block.TextBlock("go")]),
            "c1", awaitCompletion: true, CancellationToken.None);

        Assert.False(result.IsRejected);
        Assert.Equal(SessionStatus.WaitingForInput, handle.State.Status);

        // ⚠️ **一次，不是两次**：第二次就是那个会 400 的续跑。
        Assert.Single(_adapter.Calls);
    }

    /// <summary>
    /// ⚠️ <b>用户直接打字说别的 ⇒ 顶替那次询问</b>（`01` §3.8 的 `Superseded`）。
    ///
    /// <para>也是实测抓出来的：用户对着一个询问不去点卡片、直接打字 ⇒ 那条悬空的
    /// `tool_use` 让**下一条请求当场 400**。⇒ 发送之前必须先补一条结果把配对补上，
    /// 而且是**说得清的那种**（「用户没有回答，而是说了别的」）——**不能假装他答了**。</para>
    /// </summary>
    [Fact]
    public async Task SendWhileAwaiting_SupersedesTheAskInsteadOfLeavingItDangling()
    {
        var handle = NewSession();
        var writer = new CountingWriter();
        _adapter.ReplyWith(_ => Calls(("c1", "write_thing", "{}")));

        await StepperWith(_store, _adapter, [writer]).StepAsync(handle, "go", CancellationToken.None);
        Assert.NotNull(handle.State.Ask);

        // 用户直接打字（不点卡片）。
        var host = new CoreHost(_store.DataRoot, _work);
        host.Register(handle);
        var dispatcher = new CommandDispatcher(host, new EventBus(), h => new Stepper(_store, _adapter, []));

        var result = await dispatcher.DispatchAsync(
            new CoreCommand.SessionSend(handle.SessionId, [new Block.TextBlock("算了，先做别的")]),
            "c2", awaitCompletion: false, CancellationToken.None);

        Assert.False(result.IsRejected);
        Assert.Null(handle.State.Ask);      // 顶替掉了

        // ⚠️ **那条 `tool_use` 现在有配对了**——而这就是「下一条请求不会 400」的全部内容。
        var results = _store.ReadTimeline(handle.ProjectKey, handle.SessionId)
            .OfType<TimelineEvent.ToolResultEvent>()
            .ToList();

        Assert.Contains(results, r => r.CallId == "c1"
            && string.Concat(r.Content.OfType<Block.TextBlock>().Select(b => b.Text)).Contains("没有回答"));
    }
    /// <summary>`ask` 工具声明 <see cref="ITool.AsksBeforeRunning"/> ⇒ 它走「先判全」那一趟。</summary>
    [Fact]
    public void AskTool_DeclaresThatItAsksBeforeRunning()
    {
        var tool = new AskTool();

        Assert.True(tool.AsksBeforeRunning);
        // ⚠️ **只读**：问本身什么都不改。挂 Writing 会让「standard 档问用户一个问题」
        // 也去过闸门 ⇒ 变成**问「能不能问」**。
        Assert.Equal(PermissionClass.ReadOnly, tool.PermissionClass);
    }

    /// <summary>多问：一次询问一个信封，里面装好几个问题。</summary>
    [Fact]
    public async Task AskTool_ProducesOneEnvelopeWithSeveralMixedQuestions()
    {
        var tool = new AskTool();
        var outcome = await tool.RunAsync(
            new ToolCall("c1", "ask", """
                {"questions":[
                  {"id":"q1","type":"choice","question":"用哪个布局？","options":["A","B"]},
                  {"id":"q2","type":"text","question":"接口叫什么？"},
                  {"id":"q3","type":"form","question":"细节？","fields":[{"name":"n","label":"名字"}]}
                ]}
                """),
            new SessionToolContext(_work, new BlobStore(_store.DataRoot)), CancellationToken.None);

        Assert.NotNull(outcome.Ask);
        Assert.Equal(AskKind.Question, outcome.Ask!.Kind);
        Assert.Equal(3, outcome.Ask.Items.Count);
        Assert.Equal(AskShape.Choice, outcome.Ask.Items[0].Shape);
        Assert.Equal(AskShape.Text, outcome.Ask.Items[1].Shape);
        Assert.Equal(AskShape.Form, outcome.Ask.Items[2].Shape);

        // ⚠️ **问意见没有「记住」这回事**——答案是内容，不是许可。
        Assert.Empty(outcome.Ask.Remember);
    }

    /// <summary>
    /// ⚠️ **参数写错时给一条正常结果，而不是一张空卡**：一张没有问题的卡会让用户
    /// 对着空白面板发呆，而模型**看不出自己写错了**。
    /// </summary>
    [Theory]
    [InlineData("not json")]
    [InlineData("""{"questions":[]}""")]
    [InlineData("""{"questions":[{"id":"q1","type":"nope","question":"x"}]}""")]
    [InlineData("""{"questions":[{"id":"q1","type":"text","question":"x"},{"id":"q1","type":"text","question":"y"}]}""")]
    public async Task AskTool_BadArguments_AreAResultNotAQuestion(string args)
    {
        var outcome = await new AskTool().RunAsync(
            new ToolCall("c1", "ask", args),
            new SessionToolContext(_work, new BlobStore(_store.DataRoot)), CancellationToken.None);

        Assert.Null(outcome.Ask);                                   // ⚠️ **没有挂起**
        Assert.NotEmpty(outcome.Content);                           // 而是一句人话
    }

    /// <summary>上限（`01` §3.8）：一张卡是用户要挨个填的。</summary>
    [Fact]
    public async Task AskTool_TooManyQuestions_IsRejectedNotTruncated()
    {
        var many = string.Join(",", Enumerable.Range(0, ToolLimits.MaxAskItems + 1)
            .Select(i => $$"""{"id":"q{{i}}","type":"text","question":"?"}"""));

        var outcome = await new AskTool().RunAsync(
            new ToolCall("c1", "ask", $$"""{"questions":[{{many}}]}"""),
            new SessionToolContext(_work, new BlobStore(_store.DataRoot)), CancellationToken.None);

        Assert.Null(outcome.Ask);
        Assert.Contains(ToolLimits.MaxAskItems.ToString(), ((Block.TextBlock)outcome.Content[0]).Text);
    }

    // ---- 脚手架 ----

    /// <summary>
    /// 造一个只服务本会话的 `CommandDispatcher`。
    /// ⚠️ `resolveStepper` 给的是一个**新** `Stepper`——那没关系：答复那条路
    /// **不碰模型**（`ApprovalRespond` 只合成结果并安排下一步），而 `FakeAdapter`
    /// 没有脚本时会返回空回复，正好让续跑那一步安静地结束。
    /// </summary>
    private (CommandDispatcher Dispatcher, EventBus Bus) DispatcherFor(
        SessionHandle handle, params ITool[] tools)
    {
        var bus = new EventBus();
        var host = new CoreHost(_store.DataRoot, _work);
        host.Register(handle);

        var dispatcher = new CommandDispatcher(host, bus, h => new Stepper(_store, _adapter, tools));
        return (dispatcher, bus);
    }

    private string LastResultText(SessionHandle handle)
    {
        var last = _store.ReadTimeline(handle.ProjectKey, handle.SessionId)
            .OfType<TimelineEvent.ToolResultEvent>().Last();

        return string.Concat(last.Content.OfType<Block.TextBlock>().Select(b => b.Text));
    }
}
