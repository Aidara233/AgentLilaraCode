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
        // ⚠️ **删临时目录是「尽力而为」，不许把已经通过的测试判红**（2026-10-11 修）。
        //
        // <para>⚠️ <b>实测的失败长这样</b>（约 1/10 次全量跑会撞上，而单独跑这个类 10 次全绿）：
        // <c>System.IO.IOException : The process cannot access the file 'history.jsonl'
        // because it is being used by another process.</c>，栈顶是**本方法的 `Directory.Delete`**。
        // 断言早已跑完并且通过了——红的是**拆卸**。</para>
        //
        // <para>⚠️ <b>根因是这一批测的那条路本身</b>：`approval.respond` 之后那一步走的是
        // **fire-and-forget 的 `Task.Run`**（`01` §3.8：答复那条路不阻塞调用方），
        // 而测试**没法 await 它**（`DispatcherFor` 造的是新 `Stepper`，没有完成信号）。
        // ⇒ 测试结束时那个后台步可能还攥着 `history.jsonl`。
        // <b>这不是可以「修好」的东西</b>——要把它变成可等待的，就得给生产代码加一个
        // 只为测试存在的完成信号。</para>
        //
        // ⚠️ 重试几次再放弃：多数情况下那个步几十毫秒内就写完了。
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

    /// <summary>
    /// **重启之后批准，参数照样是对的**（`01` §3.8）。
    ///
    /// <para>⚠️ <b>这条是 E2E 抓出来的</b>（2026-10-11，真模型 + 真重启）：那条 `update`
    /// **真的执行了**（直接执行那条路是通的），却报「缺少 `path` 参数」——
    /// 因为内存那半（`AwaitingInput`）在重启后是 null，而当时退回了「凭空造一条调用」，
    /// 编出来的参数是空的。⇒ 与 `AskTool.ItemsOf` **同一个形状**：
    /// **内存是缓存，盘上那条 `tool_use` 才是真相**。同一个形状在同一天栽了两次。</para>
    /// </summary>
    [Fact]
    public async Task Approved_AfterRestart_UsesTheArgumentsFromDisk()
    {
        var handle = NewSession();
        var writer = new CountingWriter();
        var args = """{"a":1}""";
        _adapter.ReplyWith(_ => Calls(("c1", "write_thing", args)));

        await StepperWith(_store, _adapter, [writer]).StepAsync(handle, "go", CancellationToken.None);
        Assert.NotNull(handle.State.Ask);

        // ⚠️ **模拟「重启」**：摘掉内存那半，只留盘上那份挂起账。
        handle.AwaitingInput = null;
        var paused = _store.ReadState(handle.ProjectKey, handle.SessionId).Ask!;

        var (dispatcher, _) = DispatcherFor(handle, writer);
        await dispatcher.DispatchAsync(
            new CoreCommand.ApprovalRespond(handle.SessionId, paused.AskId, "submit",
                new Dictionary<string, string> { ["decision"] = "allow" }),
            "cmd-1", awaitCompletion: false, CancellationToken.None);

        await WaitUntil(() => writer.Seen is not null);

        // ⚠️ **判据是工具收到的那份原文**——编出来的那条会是 `{}`。
        Assert.Equal(args, writer.Seen);
    }

    /// <summary>
    /// **批准会记一条「这一次」**（`01` §3.8）——于是同一个调用**本合同参数**再来时不再问。
    ///
    /// <para>⚠️ <b>它的角色在 2026-10-11 变了</b>：从前它是「批准生效」的**唯一机制**
    /// （重发要靠它放行）⇒ 没写就活锁；现在批准**直接执行**，它退化成**一个优化**。
    /// ⚠️ 但**它仍然要写**：不写的话
    /// ① 头的「记住」复选框是个哑的；② 模型每次重发同一条都要重新问一遍。</para>
    /// </summary>
    [Fact]
    public async Task Approved_RecordsTheApprovalSoTheSameCallDoesNotAskAgain()
    {
        var handle = NewSession();
        var writer = new CountingWriter();
        _adapter.ReplyWith(_ => Calls(("c1", "write_thing", """{"a":1}""")));

        await StepperWith(_store, _adapter, [writer]).StepAsync(handle, "go", CancellationToken.None);

        var (dispatcher, _) = DispatcherFor(handle, writer);
        await dispatcher.DispatchAsync(
            new CoreCommand.ApprovalRespond(handle.SessionId, handle.State.Ask!.AskId, "submit",
                new Dictionary<string, string> { ["decision"] = "allow" }),
            "cmd-1", awaitCompletion: false, CancellationToken.None);

        await WaitUntil(() => writer.Runs > 0);

        // 同一条调用**再进一次闸门** ⇒ 不再问（记忆命中了）。
        var batch = await new ToolExecutor(
                [writer], new BlobStore(_store.DataRoot), "standard",
                new ApprovalStore(_store.DataRoot, handle.ProjectKey, handle.SessionId))
            .ExecuteAsync([new Block.ToolUseBlock("c9", "write_thing", """{"a":1}""")],
                new SessionToolContext(_work, new BlobStore(_store.DataRoot)), CancellationToken.None);

        Assert.Null(batch.Pending);
    }

    /// <summary>一个**会记数**的写类工具。⚠️ 计数器就是「它到底跑没跑」的判据。</summary>
    private sealed class CountingWriter : ITool
    {
        public int Runs;

        /// <summary>工具**收到的那份参数原文**——用来证明用户改的那份真的被执行了。</summary>
        public string? Seen;

        public string Name => "write_thing";
        public PermissionClass PermissionClass => PermissionClass.Writing;
        public string Description => "测试替身：一个写入类工具。";
        public string ParametersSchemaJson => """{"type":"object","properties":{}}""";

        public Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct)
        {
            Runs++;
            Seen = call.ArgumentsJson;
            return Task.FromResult(new ToolOutcome { Content = [new Block.TextBlock("写了")] });
        }
    }

    /// <summary>
    /// 等一个条件成立，**有界**。
    ///
    /// <para>⚠️ <b>它不是「睡一会儿」</b>：答复之后那一跑走的是 fire-and-forget 的
    /// `Task.Run`（`01` §3.8：答复那条路不阻塞调用方），测试**没法 await 它**。
    /// ⇒ 唯一能做的就是等那个可观测的后果出现，而**超时要失败**——
    /// 无条件 `Task.Delay` 会让「根本没跑」也变成绿。</para>
    /// </summary>
    private static async Task WaitUntil(Func<bool> done, int timeoutMs = 5000)
    {
        for (var waited = 0; waited < timeoutMs; waited += 20)
        {
            if (done()) return;
            await Task.Delay(20);
        }

        Assert.Fail($"等了 {timeoutMs}ms，那个条件始终没成立（多半是那条后台推进根本没跑）。");
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
    /// **`spec.AskTimeout` 真的算出截止时刻**（`01` §3.8 / D4）。
    ///
    /// <para>⚠️ <b>这一格原先「全都建好了」而没有任何入口</b>：字段、`DeadlineOf`、
    /// 盘面自证那条路都在，而 `session.create` / `session.configure` 都不收它
    /// ⇒ 这条路径**走不通**，而它正是「无人看管的会话撞上要授权的东西」的答案。</para>
    ///
    /// <para>⚠️ 判据是**挂起之后盘上那个 `DeadlineAt`**，不是「spec 里存了个值」——
    /// 存了而没用上，与没存对用户是同一件事。</para>
    /// </summary>
    [Fact]
    public async Task AskTimeout_ProducesADeadlineOnThePause()
    {
        var handle = SessionHandle.Create(
            _store, _work, Model(), sessionId: "s-ask-to", askTimeout: TimeSpan.FromMinutes(5));

        _adapter.ReplyWith(_ => Calls(("c1", "write_thing", "{}")));
        var before = CoreClock.Now();

        await StepperWith(_store, _adapter, [new CountingWriter()])
            .StepAsync(handle, "go", CancellationToken.None);

        var deadline = _store.ReadState(handle.ProjectKey, handle.SessionId).Ask!.DeadlineAt;
        Assert.NotNull(deadline);
        // ⚠️ 5 分钟**之后**——不早不晚（给个区间是因为这一步本身要花点时间）。
        Assert.InRange(deadline!.Value, before.AddMinutes(5), CoreClock.Now().AddMinutes(5));
    }

    /// <summary>不给 ⇒ **不超时**（`null`）。⚠️ 它是**默认**，不是「漏了」。</summary>
    [Fact]
    public async Task NoAskTimeout_LeavesTheDeadlineNull()
    {
        var handle = NewSession();      // `NewSession` 不传 askTimeout

        _adapter.ReplyWith(_ => Calls(("c1", "write_thing", "{}")));

        await StepperWith(_store, _adapter, [new CountingWriter()])
            .StepAsync(handle, "go", CancellationToken.None);

        Assert.Null(_store.ReadState(handle.ProjectKey, handle.SessionId).Ask!.DeadlineAt);
    }

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

    /// <summary>
    /// **批准 ⇒ 直接执行**（`01` §3.8，2026-10-11 改）——这一批之前走的是「让模型重新发起」。
    ///
    /// <para>⚠️ <b>旧行为与它的代价</b>：批准只落一句「请重新发起它，这次会放行」，
    /// 而重发要模型**再走一次往返** ⇒ 多一次模型调用、多一个失败面（模型可能不重发）、
    /// 而且**用户改过的参数要绕一圈经过模型才被执行**（`ArgumentsAmended` 那一格
    /// 本来是为此存在的，而它在 `src/` 里零读取点——设计意图与实现对不上）。
    /// ⇒ 现在批准就执行：跑的还是 `ToolExecutor`，执行点仍然只有一个。</para>
    ///
    /// <para>⚠️ <b>判据是「工具真的跑了」</b>（`writer.Runs`）——不是「回话里有『批准』」。
    /// 断言措辞会让这条测试在「批准了但没跑」时照样绿，而那正是要防的。</para>
    /// </summary>
    [Fact]
    public async Task Approved_RunsTheToolInsteadOfAskingTheModelToRetry()
    {
        var handle = NewSession();
        var writer = new CountingWriter();
        _adapter.ReplyWith(_ => Calls(("c1", "write_thing", """{"a":1}""")));

        await StepperWith(_store, _adapter, [writer])
            .StepAsync(handle, "go", CancellationToken.None);
        Assert.NotNull(handle.State.Ask);
        Assert.Equal(0, writer.Runs);        // 前提：挂起时一次都没跑

        var (dispatcher, _) = DispatcherFor(handle, writer);
        await dispatcher.DispatchAsync(
            new CoreCommand.ApprovalRespond(handle.SessionId, handle.State.Ask!.AskId, "submit",
                new Dictionary<string, string> { ["decision"] = "allow" }),
            "cmd-1", awaitCompletion: false, CancellationToken.None);

        // ⚠️ **后台那条路是 `Task.Run`**，测试没法 await ⇒ 等它跑完（有界）。
        await WaitUntil(() => writer.Runs > 0);

        Assert.Equal(1, writer.Runs);        // ⚠️ 真的跑了，而且只跑一次

        // 那条 `tool_use` 配上了——而这就是「不悬空」的全部内容。
        var results = _store.ReadTimeline(handle.ProjectKey, handle.SessionId)
            .OfType<TimelineEvent.ToolResultEvent>().ToList();
        Assert.Contains(results, r => r.CallId == "c1");

        // 挂起态清干净了（内存 + 盘）。
        Assert.Null(handle.AwaitingInput);
        Assert.Null(_store.ReadState(handle.ProjectKey, handle.SessionId).Ask);
    }

    /// <summary>
    /// **用户改过的参数就是执行用的参数**（`01` §3.8）。
    ///
    /// <para>⚠️ 这条钉的是那条路上最容易丢的东西：改写跟着 `tool_result` 的
    /// `ArgumentsAmended` 走，而**那个格子在 `src/` 里零读取点**——
    /// 重发那条路根本不传参数（只发「你重新发起」那句话）。
    /// ⇒ 现在由 <see cref="Stepper.Amended"/> 把它**换进调用本身**。</para>
    /// </summary>
    [Fact]
    public async Task Approved_WithEditedArguments_ExecutesTheEditedOnes()
    {
        var handle = NewSession();
        var writer = new CountingWriter();
        _adapter.ReplyWith(_ => Calls(("c1", "write_thing", """{"a":1}""")));

        await StepperWith(_store, _adapter, [writer])
            .StepAsync(handle, "go", CancellationToken.None);

        var (dispatcher, _) = DispatcherFor(handle, writer);
        await dispatcher.DispatchAsync(
            new CoreCommand.ApprovalRespond(handle.SessionId, handle.State.Ask!.AskId, "submit",
                new Dictionary<string, string>
                {
                    ["decision"] = "allow",
                    ["arguments"] = """{"a":999}""",     // ⚠️ 用户改成了 999
                }),
            "cmd-1", awaitCompletion: false, CancellationToken.None);

        await WaitUntil(() => writer.Seen is not null);

        // ⚠️ **判据是工具收到的那份原文**——不是「记录里有个 amended 键」。
        Assert.Equal("""{"a":999}""", writer.Seen);
    }

    /// <summary>
    /// **一张卡盖住一批时，每一条都执行、每一条都配上对**（`01` §14 #31 ① 的那个缺口）。
    ///
    /// <para>⚠️ 这条在旧行为下**根本写不出来**：那时批准只让模型重发第一条，
    /// 其余几条的 `tool_use` 一直悬着。</para>
    /// </summary>
    [Fact]
    public async Task Approved_CoversEveryCallThatWasWaiting()
    {
        var handle = NewSession();
        var writer = new CountingWriter();
        _adapter.ReplyWith(_ => Calls(
            ("c1", "write_thing", """{"a":1}"""), ("c2", "write_thing", """{"b":2}""")));

        await StepperWith(_store, _adapter, [writer])
            .StepAsync(handle, "go", CancellationToken.None);
        Assert.Equal(2, handle.AwaitingInput!.Calls.Count);

        var (dispatcher, _) = DispatcherFor(handle, writer);
        await dispatcher.DispatchAsync(
            new CoreCommand.ApprovalRespond(handle.SessionId, handle.State.Ask!.AskId, "submit",
                new Dictionary<string, string> { ["decision"] = "allow", ["arguments"] = """{"a":1}""" }),
            "cmd-1", awaitCompletion: false, CancellationToken.None);

        await WaitUntil(() => writer.Runs >= 2);

        // ⚠️ **两条都跑了**（旧行为下第二条从来不会跑）。
        Assert.Equal(2, writer.Runs);

        var uses = _store.ReadTimeline(handle.ProjectKey, handle.SessionId)
            .OfType<TimelineEvent.MessageEvent>()
            .SelectMany(m => m.Blocks.OfType<Block.ToolUseBlock>())
            .Select(b => b.CallId).OrderBy(x => x, StringComparer.Ordinal).ToList();

        var results = _store.ReadTimeline(handle.ProjectKey, handle.SessionId)
            .OfType<TimelineEvent.ToolResultEvent>()
            .Select(r => r.CallId).OrderBy(x => x, StringComparer.Ordinal).ToList();

        Assert.Equal(uses, results);       // ⚠️ 一一对上，一条不剩
        Assert.Equal(2, results.Count);
    }

    /// <summary>
    /// **`ask`（问意见）批准之后不跑任何工具**——答案是内容，工具一个字都不碰。
    /// ⚠️ 它与上面那几条是**两个 Kind 的两条路**，别混：授权那条跑，
    /// 提问那条落用户给的答案（`01` §3.8 的判据是「答完谁动手」）。
    /// </summary>
    [Fact]
    public async Task AnsweringAQuestion_RecordsTheAnswerWithoutRunningAnything()
    {
        var handle = NewSession();
        var writer = new CountingWriter();
        // ⚠️ **工具表里必须有 `ask`**，否则那条调用会落成「没有这个工具」的拒绝结果
        // ——而那**不会挂起**（挂起的判据是「有问」）。
        var ask = new AskTool();
        _adapter.ReplyWith(_ => new ModelReply
        {
            Blocks =
            [
                (Block)new Block.ToolUseBlock("c1", "ask",
                    """{"questions":[{"id":"fmt","type":"text","question":"什么格式？"}]}"""),
            ],
            StopReason = "tool_use",
            Refused = false,
        });

        await StepperWith(_store, _adapter, [ask, writer])
            .StepAsync(handle, "go", CancellationToken.None);
        Assert.NotNull(handle.State.Ask);

        var (dispatcher, _) = DispatcherFor(handle, ask, writer);
        await dispatcher.DispatchAsync(
            new CoreCommand.ApprovalRespond(handle.SessionId, handle.State.Ask!.AskId, "submit",
                new Dictionary<string, string> { ["fmt"] = "Markdown" }),
            "cmd-1", awaitCompletion: false, CancellationToken.None);

        // ⚠️ 判据要**容忍「还没有结果」**——`LastResultText` 在空时间线上会抛
        //（`Last()` 空序列）。等一个条件时那个条件必须能安全地问「到了吗」。
        await WaitUntil(() => _store.ReadTimeline(handle.ProjectKey, handle.SessionId)
            .OfType<TimelineEvent.ToolResultEvent>()
            .Any(r => string.Concat(r.Content.OfType<Block.TextBlock>().Select(b => b.Text)).Contains("fmt")));

        // ⚠️ **核心自己把答案拼进去，不经过模型**（这正是这一批的重点之一）。
        Assert.Contains("fmt: Markdown", LastResultText(handle));
        Assert.Equal(0, writer.Runs);

        // ⚠️ **而且没有多调一次模型**——那条路**只有一次**模型调用（最开始那一步）。
        // 这一格是这条测试真正的判据：答复之后核心直接往下走，
        // 旧行为（落一句正文 + 安排下一步）会**再调一次模型**去把答案转述出来。
        Assert.Single(_adapter.Calls);
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
    /// **`approval.respond` 的 `remember` 真的落成「工具级」记忆**——本批（2026-10-18）的判据，
    /// 修之前它红。⚠️ 走的必须是**命令那条路**（`DispatchAsync`），不是直接写 `ApprovalStore`：
    /// 缺陷正出在「线上拼法 ↔ dispatcher 判据」之间，而 `RememberTool_CoversAnyArgumentsOfThatTool`
    /// 那种直接写库的测试**碰不到那一层**（它测的是 store，不是命令）。
    ///
    /// <para>⚠️ <b>缺陷形状</b>：`Remember` 曾经是 <c>string?</c>，dispatcher 比
    /// <c>nameof(AskMemory.Tool)</c>（<c>"Tool"</c>），而线上所有生产方（`03` §3、头、控制台）
    /// 给的都是 <c>"tool"</c> ⇒ 勾「记住这个工具」只写了一条 args 粒度的记忆，**且无任何报错**。
    /// 头尤其自相矛盾：它把 `/state` 的 <c>ask.remember</c>（小写）原样发回。</para>
    ///
    /// <para>⚠️ <b>判据是「换个参数也放行」</b>——只断言「批过一条记忆」是分不出两档的
    /// （两档都会写一条）。</para>
    /// </summary>
    [Fact]
    public async Task RememberTool_FromTheCommand_CoversAnyArgumentsOfThatTool()
    {
        var handle = NewSession();
        var writer = new CountingWriter();
        _adapter.ReplyWith(_ => Calls(("c1", "write_thing", """{"a":1}""")));

        await StepperWith(_store, _adapter, [writer]).StepAsync(handle, "go", CancellationToken.None);
        Assert.NotNull(handle.State.Ask);

        var (dispatcher, _) = DispatcherFor(handle, writer);
        await dispatcher.DispatchAsync(
            new CoreCommand.ApprovalRespond(handle.SessionId, handle.State.Ask!.AskId, "submit",
                new Dictionary<string, string> { ["decision"] = "allow" }, AskMemory.Tool),
            "cmd-1", awaitCompletion: false, CancellationToken.None);

        // ⚠️ 记忆在 `DispatchAsync` 里**同步**写（在排那一步之前）⇒ 这里不用等后台步。
        var store = new ApprovalStore(_store.DataRoot, handle.ProjectKey, handle.SessionId);
        Assert.True(store.Covers("write_thing", ApprovalStore.HashArgs("""{"totally":"different"}""")));
    }

    /// <summary>
    /// **键序不同 = 同一个请求**（`01` §3.8）。⚠️ <b>真模型实测逼出来的</b>（2026-10-11）：
    /// 同一个 `update` 调用，模型第一次发 <c>{"path":…,"old":…,"new":…}</c>、
    /// 重发时发 <c>{"new":…,"old":…,"path":…}</c> ⇒ 按原文哈希是两个指纹
    /// ⇒ 用户批的那一次**不生效** ⇒ 又挂起 ⇒ 活锁（被 `MaxConsecutiveToolSteps` 兜住，
    /// 而用户看到的是「批了又弹、批了又弹」）。
    ///
    /// <para>⚠️ <b>这条测试占的正是原先那条 <c>HashArgs_IsByteExact_NotStructural</c> 的位置</b>
    /// ——它断言的是**相反的行为**（`NotEqual`），而它错了：JSON 对象无序，
    /// 倒键序就是同一个请求。整条换掉，理由见 `ApprovalStore.HashArgs`。</para>
    /// </summary>
    [Fact]
    public void HashArgs_IgnoresKeyOrder()
    {
        Assert.Equal(
            ApprovalStore.HashArgs("""{"a":1,"b":2}"""),
            ApprovalStore.HashArgs("""{"b":2,"a":1}"""));
    }

    /// <summary>空白不是语义 ⇒ 同一份参数写成两行也算同一次。</summary>
    [Fact]
    public void HashArgs_IgnoresWhitespace()
    {
        Assert.Equal(
            ApprovalStore.HashArgs("""{"a":1,"b":2}"""),
            ApprovalStore.HashArgs("{ \"a\" : 1,\n  \"b\" : 2 }"));
    }

    /// <summary>
    /// ⚠️ <b>值真的不同 ⇒ 指纹必须不同</b>——这条钉的是「规范化没有规范过头」。
    /// 用户批 <c>{"a":1,"b":2}</c> **不该**替 <c>{"a":1,"b":3}</c> 放行。
    /// </summary>
    [Fact]
    public void HashArgs_StillSeparatesDifferentValues()
    {
        Assert.NotEqual(
            ApprovalStore.HashArgs("""{"a":1,"b":2}"""),
            ApprovalStore.HashArgs("""{"a":1,"b":3}"""));

        // ⚠️ **嵌套**也要排到：键序那个洞在顶层看不出来。
        Assert.Equal(
            ApprovalStore.HashArgs("""{"o":{"a":1,"b":2}}"""),
            ApprovalStore.HashArgs("""{"o":{"b":2,"a":1}}"""));
        Assert.NotEqual(
            ApprovalStore.HashArgs("""{"o":{"a":1}}"""),
            ApprovalStore.HashArgs("""{"o":{"a":2}}"""));
    }

    /// <summary>
    /// ⚠️ **数组保序**：`["a","b"]` 与 `["b","a"]` 是两个不同的参数——
    /// 排序数组会是一次**真的放错了行**（「先删 A 再删 B」变成反过来）。
    /// </summary>
    [Fact]
    public void HashArgs_KeepsArrayOrder()
    {
        Assert.NotEqual(
            ApprovalStore.HashArgs("""{"paths":["a","b"]}"""),
            ApprovalStore.HashArgs("""{"paths":["b","a"]}"""));
    }

    /// <summary>
    /// ⚠️ **坏 JSON 退回哈希原文**，而且**两串不同的坏 JSON 不许撞**——
    /// 压成一个会让一次写坏的参数**冒充**另一个。
    /// </summary>
    [Fact]
    public void HashArgs_OnInvalidJson_FallsBackToRawAndStaysDistinct()
    {
        Assert.NotEqual(
            ApprovalStore.HashArgs("not json"),
            ApprovalStore.HashArgs("also not json"));

        // 同一串坏 JSON 照样稳定（幂等）——两次算出来必须一样，否则记忆根本存不住。
        Assert.Equal(ApprovalStore.HashArgs("not json"), ApprovalStore.HashArgs("not json"));
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

    // ---- 五之补：三个接缝缺陷的回归（2026-10-11）----
    //
    // ⚠️ **这三条都是「`dotnet test` 全绿时躺着」的**：链路的机制全对，
    // 错的是**它们之间的接缝**——正是 `01` §3.8 末尾那句「接缝只有 E2E 碰得到」。
    // 它们现在各有一条测试钉着。

    /// <summary>
    /// **闸门那条链终于在生产里有生产者了**（`01` §3.5 那两格「问」）。
    ///
    /// <para>⚠️ <b>这条用的是真 <see cref="UpdateTool"/>，不是替身</b>——本文件其余地方
    /// 用的 `CountingWriter` 是**探针**，它证明得了「闸门会问」，证明不了
    /// 「那个被问的东西是核心真有的那个工具」。而这一批要的正是后者：
    /// 在 `update` 之前，四个内建全只读 ⇒ `Gate.Decide` **从没返回过 `Ask`**。</para>
    ///
    /// <para>⚠️ 三档一起钉（`standard` 问 / `authorized` 放行 / `readonly` 拒）：
    /// 只钉一档的话，「`update` 的类别被改错了」这件事在另外两档上照样绿。</para>
    /// </summary>
    [Fact]
    public async Task TheGateChain_IsReachableWithTheRealUpdateTool()
    {
        var work = Path.Combine(_tmp, "gate-work");
        Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(work, "a.txt"), "hello\n");

        var call = """{"path":"a.txt","old":"hello","new":"hi"}""";
        var blobs = new BlobStore(_store.DataRoot);
        var ctx = new SessionToolContext(work, blobs);

        // ① standard ⇒ **问**（挂起，一条都不跑）。
        var asked = await new ToolExecutor([new UpdateTool()], blobs, "standard")
            .ExecuteAsync([new Block.ToolUseBlock("c1", "update", call)], ctx, CancellationToken.None);
        Assert.NotNull(asked.Pending);
        Assert.Equal(AskKind.Permission, asked.Pending!.Kind);
        Assert.Empty(asked.Results);

        // ② authorized ⇒ **放行，而且真的改了盘**。
        var ran = await new ToolExecutor([new UpdateTool()], blobs, "authorized")
            .ExecuteAsync([new Block.ToolUseBlock("c2", "update", call)], ctx, CancellationToken.None);
        Assert.Null(ran.Pending);
        Assert.Single(ran.Results);
        Assert.Equal("hi\n", File.ReadAllText(Path.Combine(work, "a.txt")));

        // ③ readonly ⇒ **拒**（落一条说得清的结果，且盘上没变）。
        File.WriteAllText(Path.Combine(work, "a.txt"), "hello\n");
        var denied = await new ToolExecutor([new UpdateTool()], blobs, "readonly")
            .ExecuteAsync([new Block.ToolUseBlock("c3", "update", call)], ctx, CancellationToken.None);
        Assert.Null(denied.Pending);                 // ⚠️ 拒**不挂起**（挂起 = 要问）
        Assert.Single(denied.Results);
        Assert.Equal("hello\n", File.ReadAllText(Path.Combine(work, "a.txt")));
    }

    // ⚠️ **下面这三条都是「`dotnet test` 全绿时躺着」的**：链路的机制全对，
    // 错的是**它们之间的接缝**——正是 `01` §3.8 末尾那句「接缝只有 E2E 碰得到」。

    /// <summary>
    /// **D3：头与控制台必须发同一种东西**（`01` §3.8「两层判据共用一个谓词」）。
    ///
    /// <para>⚠️ <b>这条是反向的：它先钉住今天的错</b>。缺陷是——核心判「批没批」
    /// 只看 `answers.decision`，而头的「批准」按钮**一颗都不发它**
    /// （控制台一直发 `decision:"allow"`）。⇒ 在头上点「批准」，落成的是
    /// **「用户拒绝了这次调用」**。</para>
    ///
    /// <para>⚠️ 判据用**头真发的那种载荷**（`{outcome:"submit"}` + 没有 `decision`），
    /// 断言它**不许**被当成批准——真批准由控制台那种形状（带 `decision`）证明（见上一条测试）。</para>
    /// </summary>
    [Fact]
    public async Task Decide_TheHeadShapeAloneIsNotAnApproval()
    {
        var handle = NewSession();
        var writer = new CountingWriter();
        _adapter.ReplyWith(_ => Calls(("c1", "write_thing", "{}")));

        await StepperWith(_store, _adapter, [writer]).StepAsync(handle, "go", CancellationToken.None);

        var (dispatcher, _) = DispatcherFor(handle, writer);
        await dispatcher.DispatchAsync(
            // ⚠️ 这就是头**修好之前**发的形状：`outcome` 对、而 `decision` 整个没有。
            new CoreCommand.ApprovalRespond(handle.SessionId, handle.State.Ask!.AskId, "submit",
                new Dictionary<string, string> { ["arguments"] = "{}" }),
            "cmd-1", awaitCompletion: false, CancellationToken.None);

        // 「没点头」的后果是**拒绝**——方向是安全的那一侧（宁可漏放，不可错放）。
        Assert.Contains("拒绝", LastResultText(handle));
    }

    /// <summary>
    /// **D2：批准之后，同一个调用必须真的放行**（否则「批准」是句空话）。
    ///
    /// <para>⚠️ <b>缺陷的形状是活锁</b>：批准的后果是「下一步带着批准去执行」，
    /// 而放行的唯一依据是 `ApprovalStore` —— 它原先**只在用户勾了「记住」时才写**。
    /// ⇒ 批了不勾记住 ⇒ 模型重发 ⇒ 闸门又 `Ask` ⇒ 又挂起，转到熔断为止。</para>
    ///
    /// <para>⚠️ <b>判据是「同一个调用第二次进闸门时放行」</b>，而这条只有**跑真闸门**
    /// 才证得了：断言「store 里有条目」证明的是写进去了，不是**它管事**。</para>
    ///
    /// <para>⚠️ <b>2026-10-11 之后这条测的是「记忆」本身</b>：批准那条路现在**直接执行**
    /// （不再靠记忆让重发放行，见 <c>Approved_RunsTheToolInsteadOfAskingTheModelToRetry</c>）
    /// ⇒ 这里问的变成「**同一个参数下次还要不要问**」，而那正是记忆那一格的含义。
    /// ⚠️ 它仍然要钉：`standards` 档 + 写类 ⇒ 闸门本来会 `Ask`，是**记忆**把它降成 `Allow` 的。</para>
    /// </summary>
    [Fact]
    public async Task RememberingTheApproval_LetsTheSameCallThroughNextTime()
    {
        var handle = NewSession();
        var writer = new CountingWriter();
        _adapter.ReplyWith(_ => Calls(("c1", "write_thing", """{"a":1}""")));

        await StepperWith(_store, _adapter, [writer]).StepAsync(handle, "go", CancellationToken.None);
        Assert.NotNull(handle.State.Ask);

        // 直接写一条记忆（等价于用户勾了「这一次」）——⚠️ 这一步**不走批准**：
        // 批准现在直接执行，跑不到「下一次进闸门」那一格。
        new ApprovalStore(_store.DataRoot, handle.ProjectKey, handle.SessionId)
            .Add(new Approval("write_thing", ApprovalStore.HashArgs("""{"a":1}"""),
                ToolWide: false, CoreClock.Now()));

        // 同一个调用**再进一次闸门** ⇒ 这一次必须直接跑掉。
        var batch = await new ToolExecutor(
                [writer], new BlobStore(_store.DataRoot), "standard",
                new ApprovalStore(_store.DataRoot, handle.ProjectKey, handle.SessionId))
            .ExecuteAsync([new Block.ToolUseBlock("c1", "write_thing", """{"a":1}""")],
                new SessionToolContext(_work, new BlobStore(_store.DataRoot)), CancellationToken.None);

        Assert.Null(batch.Pending);          // ⚠️ 不再问
        Assert.Single(batch.Results);        // 而是真的跑了
        Assert.Equal(1, writer.Runs);
    }

    /// <summary>
    /// **D1：一批里被一起挡下的调用，每一条都要配到结果**（`01` §3.6 ② 配对是结构性的）。
    ///
    /// <para>⚠️ <b>缺陷</b>：`PausedAsk` 只装得下一个 `CallId`，而 `ToolExecutor.Merge`
    /// 把一批里多条被挡下的调用合成**一个**信封 ⇒ 其余几条的 `tool_use` **一直悬着**，
    /// 而悬空的 `tool_use` 会让**下一条请求 400**（上游原话见 `Stepper.Supersede` 的注释）。</para>
    ///
    /// <para>⚠️ <b>判据是「这一批的 `tool_use` 与 `tool_result` 数得一样多」</b>——
    /// 断言「有一条结果」是不够的（那正是修之前的样子）。</para>
    /// </summary>
    [Fact]
    public async Task Reject_PairsEveryCallThatWasBlockedWithIt()
    {
        var handle = NewSession();
        var writer = new CountingWriter();
        // ⚠️ **一批里两条都要问**——这是 `Merge` 的触发条件。
        _adapter.ReplyWith(_ => Calls(("c1", "write_thing", """{"a":1}"""), ("c2", "write_thing", """{"b":2}""")));

        await StepperWith(_store, _adapter, [writer]).StepAsync(handle, "go", CancellationToken.None);
        Assert.NotNull(handle.State.Ask);
        Assert.Equal(2, handle.AwaitingInput!.Calls.Count);      // 前提：真的挂了两条

        var (dispatcher, _) = DispatcherFor(handle, writer);
        await dispatcher.DispatchAsync(
            new CoreCommand.ApprovalRespond(handle.SessionId, handle.State.Ask!.AskId, "submit",
                new Dictionary<string, string> { ["decision"] = "deny" }),
            "cmd-1", awaitCompletion: false, CancellationToken.None);

        var uses = _store.ReadTimeline(handle.ProjectKey, handle.SessionId)
            .OfType<TimelineEvent.MessageEvent>()
            .SelectMany(m => m.Blocks.OfType<Block.ToolUseBlock>())
            .Select(b => b.CallId).OrderBy(x => x, StringComparer.Ordinal).ToList();

        var results = _store.ReadTimeline(handle.ProjectKey, handle.SessionId)
            .OfType<TimelineEvent.ToolResultEvent>()
            .Select(r => r.CallId).OrderBy(x => x, StringComparer.Ordinal).ToList();

        // ⚠️ **这条是全部**：两条 `tool_use`，两条 `tool_result`，配对键一一对上。
        Assert.Equal(uses, results);
        Assert.Equal(2, results.Count);
    }

    /// <summary>
    /// **闸门那条询问的问题，重启之后照样解得出来**（`01` §3.8）。
    ///
    /// <para>⚠️ <b>它是跑真模型 + 真重启实测抓出来的</b>（2026-10-11），而单元测试
    /// 一直碰不到它——因为那条路要「挂起之后重开进程」。而**在此之前它根本不可达**：
    /// 闸门那两格没有生产者（四个内建全只读），所以 `AskTool.ItemsOf` 里那句
    /// 「名字不是 `ask` ⇒ 返回空」从来没有害过谁。`update` 一落地它当场露出来。</para>
    ///
    /// <para>⚠️ <b>症状</b>：重启之后 `/state` 交出一张**没有问题的卡** ⇒ 头显示
    /// 「这次询问的问题取不回来了」，而那张**改参数再批准**的卡整个消失——
    /// 用户只剩「批准 / 拒绝」（`01` §3.8 明确要求那张卡能改参数）。</para>
    ///
    /// <para>⚠️ 判据是「**参数原文逐字回得来**」：那张卡的用处就是让用户改参数。</para>
    /// </summary>
    [Fact]
    public void GateAsk_SurvivesARestart_SoTheCardIsNotBlank()
    {
        var handle = NewSession();
        var args = """{"path":"target.txt","old":"world","new":"there"}""";

        // 只落那条模型消息（含 tool_use）——挂起态的**盘面那一半**。
        _store.AppendEvent(handle.ProjectKey, handle.SessionId, new TimelineEvent.MessageEvent
        {
            Id = Ids.New(),
            At = CoreClock.Now(),
            From = Speaker.Model,
            Blocks = [new Block.ToolUseBlock("c-gate-1", "update", args)],
        });

        // ⚠️ 这条**只走盘**（没有内存那半）——正是「重启之后」的形状。
        var items = AskTool.ItemsOf(_store, handle.ProjectKey, handle.SessionId, "c-gate-1");

        Assert.Single(items);
        Assert.Equal(AskShape.Verbatim, items[0].Shape);
        // ⚠️ **逐字**：用户要改的就是这段原文，规范化一个空格都会让改写后的参数
        // 与「原来的」哈希对不上（`ApprovalStore.HashArgs` 那条逐字判据）。
        Assert.Equal(args, items[0].Fields.Single(f => f.Name == "arguments").Value);
        Assert.Contains("update", items[0].Question);
    }

    /// <summary>
    /// **`ask` 那条路不受影响**：它的问题来自工具参数，仍然由 `Parse` 解。
    /// ⚠️ 两条路在同一个方法里分叉，所以修闸门那条时**必须同时钉住这一条**
    /// ——否则「把分叉写反」会让 `ask` 也走闸门那条重建（问句变样、选项全丢）。
    /// </summary>
    [Fact]
    public void AskToolQuestions_StillComeFromTheToolArguments()
    {
        var handle = NewSession();
        var args = """{"questions":[{"id":"q1","question":"用哪个？","type":"choice","options":["A","B"]}]}""";

        _store.AppendEvent(handle.ProjectKey, handle.SessionId, new TimelineEvent.MessageEvent
        {
            Id = Ids.New(), At = CoreClock.Now(), From = Speaker.Model,
            Blocks = [new Block.ToolUseBlock("c-ask-1", "ask", args)],
        });

        var items = AskTool.ItemsOf(_store, handle.ProjectKey, handle.SessionId, "c-ask-1");

        Assert.Single(items);
        Assert.Equal(AskShape.Choice, items[0].Shape);       // ⚠️ 不是 Verbatim
        Assert.Equal("用哪个？", items[0].Question);
        Assert.Equal(2, items[0].Options.Count);             // ⚠️ 选项在
    }

    // ---- 脚手架 ----

    /// <summary>
    /// 造一个只服务本会话的 `CommandDispatcher`。
    /// ⚠️ `resolveStepper` 给的是一个**新** `Stepper`——那没关系：答复那条路
    /// **不碰模型**（`ApprovalRespond` 只合成结果并安排下一步），而 `FakeAdapter`
    /// 没有脚本时会返回空回复，正好让续跑那一步安静地结束。
    /// </summary>
    private (CommandDispatcher Dispatcher, EventBus Bus) DispatcherFor(
        SessionHandle handle, params ITool[] tools)    {
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
