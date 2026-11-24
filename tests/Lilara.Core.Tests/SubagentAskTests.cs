using Lilara.Core.Hosting;
using Lilara.Core.Kernel;
using Lilara.Core.Providers;
using Lilara.Core.Protocol;
using Lilara.Core.Session;
using Lilara.Core.Session.Tools;
using Lilara.Core.Storage;
using Lilara.Core.Tests.Fakes;
using CanonicalBlock = Lilara.Core.Kernel.Block;

namespace Lilara.Core.Tests;

/// <summary>
/// **子会话挂起之后，父会话能做什么**（2026-11-19，用户裁定）。
///
/// <para>⚠️ <b>这一族钉的是四件在当时都做不到的事</strong>：</para>
/// <list type="number">
///   <item>子的**提问**升级给父时带得回 `askId` 与每个问题的 id（父要照着它回答）；</item>
///   <item>父的 `tell_child` 落到挂着提问的子上 ⇒ 这句话**就是那条 ask 的答复**
///   （落进它的工具结果里、署父会话的名字），而**不再另入队一遍**；</item>
///   <item>子的**授权**也升级给父（从前一条都不发），且正文说清「你自己的档位能不能批」；</item>
///   <item>父在自己档位之内时 `approve_child` **真的放行**（那条被挡下的调用执行），
///   超出档位时**如实拒绝**（只有人能批）。</item>
/// </list>
///
/// <para>⚠️ 判定方式与 `SubagentTests` 同一条：后台步是 `Task.Run` 起的，所以等的是
/// **效果**（盘上出现那条记录），不是「调一次就该有」。</para>
/// </summary>
public class SubagentAskTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-subask-" + Guid.NewGuid().ToString("N"));
    private readonly string _work =
        Path.Combine(Path.GetTempPath(), "lilara-subaskwork-" + Guid.NewGuid().ToString("N"));
    private CoreHost? _host;

    public void Dispose()
    {
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

    /// <summary>
    /// 脚本化、线程安全的适配器（同 `SubagentTests.LoopAdapter`：父子会**并发**调，
    /// 而共享一个 `Queue` 会让「谁拿到哪一段」由调度决定）。
    /// </summary>
    private sealed class ScriptedAdapter : IModelAdapter
    {
        private readonly Queue<ModelReply> _scripted = new();
        private readonly Lock _gate = new();

        public List<ModelRequest> Calls { get; } = [];

        public ResolvedEndpoint? Endpoint => null;

        public string Protocol { get; set; } = Protocols.ClaudeMessages;

        public int CallCount
        {
            get { lock (_gate) return Calls.Count; }
        }

        public ScriptedAdapter Script(params CanonicalBlock[] blocks) => ScriptWith(new ModelReply
        {
            Blocks = blocks,
            StopReason = "end_turn",
            Refused = false,
        });

        public ScriptedAdapter ScriptWith(ModelReply reply)
        {
            lock (_gate) _scripted.Enqueue(reply);
            return this;
        }

        public Task<ModelReply> CallAsync(ModelRequest request, CancellationToken ct)
        {
            lock (_gate)
            {
                Calls.Add(request);
                return Task.FromResult(_scripted.Count > 0
                    ? _scripted.Dequeue()
                    : new ModelReply
                    {
                        // ⚠️ 默认回复**不带工具调用**：那才叫「这一轮到此为止」（= 落定）。
                        Blocks = [new CanonicalBlock.TextBlock("（这一步什么都没做）")],
                        StopReason = "end_turn",
                        Refused = false,
                    });
            }
        }
    }

    private (CoreHost Host, CommandDispatcher Dispatcher) Harness(
        ScriptedAdapter parent, ScriptedAdapter child)
    {
        // ⚠️ **工作目录得先存在**：`write` 拒绝「父目录不存在」的路径（它不建目录），
        // 而这里的会话工作目录就是它。
        Directory.CreateDirectory(_work);

        var host = new CoreHost(_root, _work);
        _host = host;
        host.Providers.Save(new ProviderDefinition
        {
            Id = "p", Protocol = Protocols.ClaudeMessages, ApiKey = "k",
            DefaultModel = "m", Modality = "vision",
        });
        var bus = new EventBus();

        return (host, new CommandDispatcher(host, bus,
            h => new Stepper(host.Store, h.Spec.ParentSessionId is null ? parent : child,
                BuiltInTools.All),
            _ => parent));
    }

    private static async Task<string> CreateAsync(CommandDispatcher dispatcher)
    {
        var r = await dispatcher.DispatchAsync(
            new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
        return ((Ack.Accepted)r.Ack).SessionId!;
    }

    private static async Task SendAsync(CommandDispatcher dispatcher, string sessionId, string text) =>
        await dispatcher.DispatchAsync(
            new CoreCommand.SessionSend(sessionId, [new Block.TextBlock(text)]),
            "s", awaitCompletion: true, CancellationToken.None);

    private static Task SetPermissionAsync(CommandDispatcher dispatcher, string sessionId, string value) =>
        dispatcher.DispatchAsync(
            new CoreCommand.SessionSetAxis(sessionId, Axes.Permission, value),
            "a", false, CancellationToken.None);

    private static async Task WaitUntil(Func<bool> probe, string what)
    {
        for (var i = 0; i < 400; i++)
        {
            if (probe()) return;
            await Task.Delay(10);
        }

        Assert.Fail($"等不到：{what}");
    }

    /// <summary>父记录里全部 `subagent_report` 结果的正文（升级报告就在里面）。</summary>
    private string ParentReports(CoreHost host, string parentId) => string.Concat(
        host.Store.ReadTimeline(host.Store.FindSession(parentId)!.Value.ProjectKey, parentId)
            .OfType<TimelineEvent.ToolResultEvent>()
            .Where(r => r.Framing?.Tool == SubagentReportTool.NameOf)
            .SelectMany(r => r.Content).OfType<Block.TextBlock>().Select(b => b.Text));

    /// <summary>子记录里那条 `ask` 调用的结果正文（`null` = 还没有）。</summary>
    private string? ChildResultOf(CoreHost host, SessionSpec child, string callName) => host.Store
        .ReadTimeline(child.ProjectKey, child.SessionId)
        .OfType<TimelineEvent.MessageEvent>()
        .SelectMany(m => m.Blocks.OfType<Block.ToolUseBlock>())
        .Where(c => c.Name == callName)
        .SelectMany(c => host.Store.ReadTimeline(child.ProjectKey, child.SessionId)
            .OfType<TimelineEvent.ToolResultEvent>()
            .Where(r => r.CallId == c.CallId)
            .Select(r => string.Concat(r.Content.OfType<Block.TextBlock>().Select(b => b.Text))))
        .FirstOrDefault();

    /// <summary>父记录里**全部**工具结果的正文（诊断用）。</summary>
    private string ParentResults(CoreHost host, string parentId) => string.Concat(
        host.Store.ReadTimeline(host.Store.FindSession(parentId)!.Value.ProjectKey, parentId)
            .OfType<TimelineEvent.ToolResultEvent>()
            .SelectMany(r => r.Content).OfType<Block.TextBlock>().Select(b => b.Text));

    /// <summary>子记录里全部工具结果的正文（诊断用）。</summary>
    private static string ChildResults(CoreHost host, SessionSpec child) => string.Concat(
        host.Store.ReadTimeline(child.ProjectKey, child.SessionId)
            .OfType<TimelineEvent.ToolResultEvent>()
            .SelectMany(r => r.Content).OfType<Block.TextBlock>().Select(b => b.Text));

    /// <summary>
    /// 读一个会话的 `state.json`。⚠️ <b>要容忍「文件正被写」</b>：后台步随时在写它，
    /// 而 Windows 上那会给读方一个 `IOException`（共享占用）。重试而不是让测试变成投骰子
    /// （同 `Dispose` 里删目录那段的重试）。
    /// </summary>
    private static Lilara.Core.Kernel.SessionState ReadStateWithRetry(CoreHost host, SessionSpec child)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return host.Store.ReadState(child.ProjectKey, child.SessionId); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 20)
            {
                Thread.Sleep(10 * (attempt + 1));
            }
        }
    }

    private static string StatusOf(CoreHost host, SessionSpec child) =>
        ReadStateWithRetry(host, child).Status;

    /// <summary>
    /// **提问：升级带得回 id，父的 `tell_child` 就是那条 ask 的答复**（2026-11-19）。
    ///
    /// <para>⚠️ 三件事一起钉：① 报告里有 `askId` 与问题的 id（否则父无从回答）；
    /// ② 答案写进了那条 `ask` 的**工具结果**并署了父会话的名字；③ 它**没有**再作为一条
    /// 用户消息进孩子的记录（从前那条路会让孩子同一句话读到两遍）。</para>
    /// </summary>
    [Fact]
    public async Task ChildQuestion_IsEscalatedWithItsIds_AndTheParentsTellBecomesTheAnswer()
    {
        var parent = new ScriptedAdapter()
            .Script(new Block.ToolUseBlock("p1", SpawnTool.NameOf, """{"task":"先问一句"}"""));

        // ⚠️ 子自己的第一段脚本是那一条 `ask`（`questions` 必须带 id 与 options）。
        var child = new ScriptedAdapter().Script(new Block.ToolUseBlock(
            "c1", "ask",
            """{"questions":[{"id":"which","question":"走 A 还是 B？","options":["A","B"]}]}"""));

        var (host, dispatcher) = Harness(parent, child);

        var parentId = await CreateAsync(dispatcher);
        await SendAsync(dispatcher, parentId, "派一个");

        var childSpec = Assert.Single(host.Store.ChildrenOf(parentId)).Spec;

        // ① 孩子挂住了；父收到了带 id 的升级。
        await WaitUntil(() => StatusOf(host, childSpec) == SessionStatus.WaitingForInput,
            "子会话挂在一条提问上");
        await WaitUntil(() => ParentReports(host, parentId).Length > 0, "父收到那条升级报告");

        var report = ParentReports(host, parentId);
        var askId = ReadStateWithRetry(host, childSpec).Ask!.AskId;
        Assert.Contains(askId, report, StringComparison.Ordinal);
        Assert.Contains("which", report, StringComparison.Ordinal);
        Assert.Contains("A", report, StringComparison.Ordinal);
        Assert.Contains("tell_child", report, StringComparison.Ordinal);

        // ⚠️ 等父**真的看到**那条升级（它会被自动推起来一步）**并且那一步跑完**再脚本下一步，
        // 否则那一步会先吃掉我们要用的那段脚本。判据用 `Live.IsLive`：`CallCount` 只说明
        // 请求发出去了，而那一步还在飞的时候 `session.send` 只入队就回执。
        await WaitUntil(() => parent.CallCount >= 2 && !host.Live.IsLive(parentId),
            "父被那条升级推起来过一步，并且那一步已经结束");

        parent.Script(new Block.ToolUseBlock(
            "p2", TellChildTool.NameOf,
            $$"""{"child":"{{childSpec.SessionId}}","text":"走 A"}"""));
        await SendAsync(dispatcher, parentId, "回答它");

        // ② 那句话落进了那条 `ask` 的结果里，并署了父会话。
        await WaitUntil(() => ChildResultOf(host, childSpec, "ask") is { } said
                             && said.Contains("父会话", StringComparison.Ordinal)
                             && said.Contains("走 A", StringComparison.Ordinal),
            "父的回答落进那条 ask 的结果");

        Assert.Contains("不是用户", ChildResultOf(host, childSpec, "ask")!);

        // ③ 同一句话**没有**再作为一条用户消息入队。
        var userSaid = host.Store.ReadTimeline(childSpec.ProjectKey, childSpec.SessionId)
            .OfType<TimelineEvent.MessageEvent>()
            .Where(m => m.From == Speaker.User)
            .SelectMany(m => m.Blocks.OfType<Block.TextBlock>())
            .Select(t => t.Text)
            .ToList();

        Assert.Equal(["先问一句"], userSaid);

        // ④ 孩子带着答复继续跑了（它的第二次模型调用发生了）。
        await WaitUntil(() => child.CallCount >= 2, "子会话带着答复继续推进");
    }

    /// <summary>
    /// **授权：也升级给父，并说清「你自己的档位不足以批」**（2026-11-19）。
    ///
    /// <para>⚠️ 从前这一路一条都不发（`EscalateAsk` 里 `Permission` 直接 return），于是父在
    /// `wait` 上耗着却不知道在等什么。⚠️ 而「父能不能批」的答案不许它自己去试——
    /// 正文直接写出来（判据 `Gate.Decide`）。</para>
    /// </summary>
    [Fact]
    public async Task ChildPermissionAsk_IsEscalated_AndSaysTheParentCannotApproveIt()
    {
        var parent = new ScriptedAdapter()
            .Script(new Block.ToolUseBlock("p1", SpawnTool.NameOf, """{"task":"写个文件"}"""));

        // 子要求写入 ⇒ 它自己是 `standard`（继承父），而 `standard` 对写入是**询问线**。
        // ⚠️ 路径用**相对**的：工具那道越界闸只认工作目录之下（绝对路径会被它拒）。
        var child = new ScriptedAdapter().Script(new Block.ToolUseBlock(
            "c1", "write", """{"path":"note.txt","content":"hi"}"""));;

        var (host, dispatcher) = Harness(parent, child);

        var parentId = await CreateAsync(dispatcher);
        await SendAsync(dispatcher, parentId, "派一个");

        var childSpec = Assert.Single(host.Store.ChildrenOf(parentId)).Spec;

        await WaitUntil(() => StatusOf(host, childSpec) == SessionStatus.WaitingForInput,
            "子会话挂在一条授权上");
        await WaitUntil(() => ParentReports(host, parentId).Length > 0, "父收到那条授权升级");

        var report = ParentReports(host, parentId);
        Assert.Contains("write", report, StringComparison.Ordinal);

        // ⚠️ 父自己也是 `standard` ⇒ **批不了这一条**（写入在它那一档同样要问），
        // 正文直接说这条路走不通，并把能做的两件事说出来。
        Assert.Contains("超出你的档位", report, StringComparison.Ordinal);
        Assert.Contains("只有人能批", report, StringComparison.Ordinal);
        Assert.Contains("tell_child", report, StringComparison.Ordinal);

        // 孩子那边的提问原文里，类别说法仍然成立（写入 = 会改动东西）。
        Assert.Equal("standard", ReadStateWithRetry(host, childSpec)
            .Axes![Axes.Permission]);

        // 什么都没发生：那条写调用没跑（目录里没有那个文件）。
        Assert.False(File.Exists(Path.Combine(_work, "note.txt")));
    }

    /// <summary>
    /// **父在自己的档位之内可以放行**（2026-11-19，用户裁定）：父 `authorized`、
    /// 子被收窄到 `standard`，子要写入 ⇒ 挂起；父 `approve_child` ⇒ **那条调用真的执行**。
    ///
    /// <para>⚠️ 判据是「授权范围 ⊆ 父」：写入在 `authorized` 是**放行**的，所以父批它
    /// 并没有把委托范围挪动一格。⚠️ 断言落在「文件真的写出来了」——
    /// 那才说明闸门被跳过、调用真的跑了（而不是只落了条"已批准"的话）。</para>
    /// </summary>
    [Fact]
    public async Task ParentCanApprove_WhenTheCallIsWithinItsOwnLevel()
    {
        var parent = new ScriptedAdapter()
            .Script(new Block.ToolUseBlock(
                "p1", SpawnTool.NameOf, """{"task":"写个文件","permission":"standard"}"""));
        // ⚠️ 路径用**相对**的：工具那道越界闸只认工作目录之下（绝对路径会被它拒）。
        var child = new ScriptedAdapter().Script(new Block.ToolUseBlock(
            "c1", "write", """{"path":"note.txt","content":"写完啦"}"""));

        var (host, dispatcher) = Harness(parent, child);

        var parentId = await CreateAsync(dispatcher);
        await SetPermissionAsync(dispatcher, parentId, "authorized");
        await SendAsync(dispatcher, parentId, "派一个");

        var childSpec = Assert.Single(host.Store.ChildrenOf(parentId)).Spec;

        Assert.Equal("standard", ReadStateWithRetry(host, childSpec)
            .Axes![Axes.Permission]);

        await WaitUntil(() => StatusOf(host, childSpec) == SessionStatus.WaitingForInput,
            "子会话卡在写入的授权上");
        await WaitUntil(() => ParentReports(host, parentId).Length > 0, "父收到那条授权升级");

        // ⚠️ 这一次正文里说的应该是「你可以批」。
        var report = ParentReports(host, parentId);
        Assert.Contains("在你的档位", report, StringComparison.Ordinal);

        // ⚠️ 与上一条同一条纪律：等那一步**真的结束**再脚本下一步，否则 `session.send`
        // 只入队就回执，而我们要用的那段脚本会被别的步吃掉（或压根没人来拿）。
        await WaitUntil(() => parent.CallCount >= 2 && !host.Live.IsLive(parentId),
            "父被那条升级推起来过一步，并且那一步已经结束");

        parent.Script(new Block.ToolUseBlock(
            "p2", ApproveChildTool.NameOf, $$"""{"child":"{{childSpec.SessionId}}"}"""));
        await SendAsync(dispatcher, parentId, "批了");

        // 那条写调用真的执行了 ⇒ 文件在盘上。
        await WaitUntil(() => File.Exists(Path.Combine(_work, "note.txt")),
            "父放行之后那条写调用真的跑了。父那边看到的结果：" + ParentResults(host, parentId)
            + " ｜ 子那边：" + ChildResults(host, childSpec)
            + " ｜ 子状态=" + StatusOf(host, childSpec));

        Assert.Equal("写完啦", File.ReadAllText(Path.Combine(_work, "note.txt")));

        // 挂起状态已经清干净（父那边再点一次会得到「没在等」的实话）。
        await WaitUntil(() => StatusOf(host, childSpec) != SessionStatus.WaitingForInput,
            "子会话不再挂着那条授权");
        Assert.Null(ReadStateWithRetry(host, childSpec).Ask);
    }

    /// <summary>
    /// **超出父档位时就如实拒绝，什么都不做**（2026-11-19）。
    ///
    /// <para>⚠️ 这条钉的是「不是先试一下」：结果一落盘就改不了，所以「已批准」这句话
    /// 绝不能出现在一次**不会生效**的批准上。判据在工具那一侧（`Gate.Decide`）。</para>
    /// </summary>
    [Fact]
    public async Task ApproveChild_RefusesWhenTheCallIsOutsideTheParentsLevel()
    {
        var tool = new ApproveChildTool();
        var context = new SessionToolContext(
            _work, new BlobStore(_root),
            subagents: new SubagentScope("parent", null, 0, 1,
            [
                new SubagentChild("kid", SessionStatus.WaitingForInput)
                {
                    Waiting = new WaitingAsk("ask-1", AskKind.Permission, "shell"),
                },
            ]),
            permission: "standard",
            toolClass: name => name == "shell" ? PermissionClass.Destructive : null);

        var outcome = await tool.RunAsync(
            new ToolCall("t1", ApproveChildTool.NameOf, """{"child":"kid"}"""),
            context, CancellationToken.None);

        Assert.Null(outcome.Approve);
        var said = string.Concat(outcome.Content.OfType<Block.TextBlock>().Select(b => b.Text));
        Assert.Contains("没批", said, StringComparison.Ordinal);
        Assert.Contains("只能等人批", said, StringComparison.Ordinal);
    }

    /// <summary>**父不能把一条提问当授权"批"掉**：那等于把一次询问吞了（2026-11-19）。</summary>
    [Fact]
    public async Task ApproveChild_RefusesAQuestionAsk_AndPointsAtTellChild()
    {
        var tool = new ApproveChildTool();
        var context = new SessionToolContext(
            _work, new BlobStore(_root),
            subagents: new SubagentScope("parent", null, 0, 1,
            [
                new SubagentChild("kid", SessionStatus.WaitingForInput)
                {
                    Waiting = new WaitingAsk("ask-2", AskKind.Question, "ask"),
                },
            ]),
            permission: "trusted",
            toolClass: _ => PermissionClass.ReadOnly);

        var outcome = await tool.RunAsync(
            new ToolCall("t1", ApproveChildTool.NameOf, """{"child":"kid"}"""),
            context, CancellationToken.None);

        Assert.Null(outcome.Approve);
        var said = string.Concat(outcome.Content.OfType<Block.TextBlock>().Select(b => b.Text));
        Assert.Contains("不是授权", said, StringComparison.Ordinal);
        Assert.Contains("tell_child", said, StringComparison.Ordinal);
    }

    /// <summary>
    /// **尾块那一行按询问种类分档**（2026-11-19）：授权的孩子写「等你批准」，提问的写「等你回答」。
    /// ⚠️ 这条只钉渲染——两个词都是**状态迁移级**的读数，不破「变化才注入」那条判据。
    /// </summary>
    [Fact]
    public void TailLine_DistinguishesWaitingForApprovalFromWaitingForAnAnswer()
    {
        var scope = new SubagentScope("parent", null, 0, 1,
        [
            new SubagentChild("a", SessionStatus.WaitingForInput)
            {
                Waiting = new WaitingAsk("ask-p", AskKind.Permission, "write"),
            },
            new SubagentChild("b", SessionStatus.WaitingForInput)
            {
                Waiting = new WaitingAsk("ask-q", AskKind.Question, "ask"),
            },
        ]);

        var line = SubagentView.Line(scope);

        Assert.NotNull(line);
        Assert.Contains("a 等你批准", line, StringComparison.Ordinal);
        Assert.Contains("b 等你回答", line, StringComparison.Ordinal);
    }
}
