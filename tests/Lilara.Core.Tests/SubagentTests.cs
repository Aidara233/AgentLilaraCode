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
/// **子 agent**（`01` §10，2026-10-24）。
///
/// <para>⚠️ <b>这一族钉的是四件语义，而不是「工具返回了什么」</b>：① 派生关系真的落进了
/// `spec.json`（深度 / 上限 / 父）；② 权限**只能更窄**；③ 回流是**一对合成工具对**
/// （不是用户消息）；④ 停止**沿树传播**。</para>
///
/// <para>⚠️ <b>它为什么自带一个适配器而不是用 `FakeAdapter`</b>：`spawn` 之后
/// **子会话在后台真的跑起来了**（`Start` → `Task.Run`），于是两个会话会**并发**调同一个
/// 适配器——而 `FakeAdapter` 的脚本是一个普通 `Queue`：并发下既会乱序、又会「没有预置回复了」。
/// ⇒ 这里用一个**线程安全、脚本用完就回固定话**的替身。<b>这不是测试的妥协，是那个并发是真实的</b>
/// （`02` §3.1：多个会话的步是真正同时在飞的）。</para>
/// </summary>
public class SubagentTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-sub-" + Guid.NewGuid().ToString("N"));
    private readonly string _work =
        Path.Combine(Path.GetTempPath(), "lilara-subwork-" + Guid.NewGuid().ToString("N"));
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
    /// 脚本化、线程安全、**用完之后一直回一句固定的话**。
    /// ⚠️ 「用完之后」那一格是刻意的：子会话要跑几步在这条测试里不是被测对象，
    /// 而「脚本耗尽就抛」会让那些与主题无关的步把测试打红。
    /// </summary>
    private sealed class LoopAdapter : IModelAdapter
    {
        private readonly Queue<ModelReply> _scripted = new();
        private readonly Lock _gate = new();

        public List<ModelRequest> Calls { get; } = [];

        public ResolvedEndpoint? Endpoint => null;

        public string Protocol { get; set; } = Protocols.ClaudeMessages;

        public LoopAdapter Script(params CanonicalBlock[] blocks) => ScriptWith(new ModelReply
        {
            Blocks = blocks,
            StopReason = "end_turn",
            Refused = false,
        });

        public LoopAdapter ScriptWith(ModelReply reply)
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
                        Blocks = [new CanonicalBlock.TextBlock("（子会话做完了）")],
                        StopReason = "end_turn",
                        Refused = false,
                    });
            }
        }
    }

    private (CoreHost Host, EventBus Bus, CommandDispatcher Dispatcher) Harness(
        LoopAdapter parent, LoopAdapter? child = null)
    {
        var host = new CoreHost(_root, _work);
        _host = host;
        host.Providers.Save(new ProviderDefinition
        {
            Id = "p", Protocol = Protocols.ClaudeMessages, ApiKey = "k",
            DefaultModel = "m", Modality = "vision",
        });
        var bus = new EventBus();

        // ⚠️ 工具表必须带 `BuiltInTools.All`：`spawn` / `tell_child` 是**内建机制**
        // （不是插件贡献），没有它们那一步只会得到一条「没有这个工具」的结果
        // ——而**那不会报错**，只是子会话永远不出现。
        //
        // ⚠️⚠️ **父与子必须用两个适配器**（2026-10-24 实测）：`spawn` 之后子会话在后台
        // 真的跑起来了，而**同一个脚本队列被两个会话争抢**会让「哪一步拿到哪一段」
        // 由调度决定 ⇒ 测试变成投骰子（实测：本该被深度限制挡住的那条，因为父抢到了
        // 子那一段脚本，多建了一个孩子）。⇒ 按 `ParentSessionId` 分流。
        return (host, bus, new CommandDispatcher(host, bus,
            h => new Stepper(host.Store, h.Spec.ParentSessionId is null ? parent : child ?? parent,
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

    /// <summary>等到 <paramref name="probe"/> 为真（后台步是 `Task.Run` 起的，判定要给它时间）。</summary>
    private static async Task WaitUntil(Func<bool> probe, string what)
    {
        for (var i = 0; i < 200; i++)
        {
            if (probe()) return;
            await Task.Delay(10);
        }

        Assert.Fail($"等不到：{what}");
    }

    /// <summary>
    /// **派生真的落地**：子会话在盘上、深度 +1、上限沿树继承、**权限只能更窄**。
    ///
    /// <para>⚠️ 这一条同时钉住「提权请求无害」：模型在 `spawn` 里写了 `trusted`，
    /// 而父是 `readonly` ⇒ 子仍然是 `readonly`。反过来（照单全收）就是
    /// `01` §10.3 说的**沙箱逃逸**。</para>
    /// </summary>
    [Fact]
    public async Task Spawn_CreatesAChildWithInheritedDepth_AndNeverWidensPermission()
    {
        var parent = new LoopAdapter().Script(new Block.ToolUseBlock(
            "c1", SpawnTool.NameOf,
            """{"task":"去看一眼","title":"小工","permission":"trusted"}"""));
        // ⚠️ **子必须有自己的适配器**：`spawn` 之后它在后台真的跑起来了，而共享一个脚本队列
        // 会让「谁拿到哪一段」由调度决定（实测过一次：子抢走了父下一步的脚本）。
        var (host, _, dispatcher) = Harness(parent, new LoopAdapter());

        var parentId = await CreateAsync(dispatcher);

        // ⚠️ 父保持默认的 `standard`，而 **`spawn` 自己再请求一个更宽的档位**：
        // 提权请求不生效 ⇒ 子落成父那一档。
        // ⚠️ **不能把父降到 `readonly` 来测**：`spawn` 是 `Consuming` 类
        // （它再开一个模型循环 = 花钱花时间），而 `readonly` 只放行 `ReadOnly`
        // ⇒ 那样**工具层就把它拦掉了**，测的就不再是「收窄」而是「闸门」。
        await SendAsync(dispatcher, parentId, "派个人去看看");

        var children = host.Store.ChildrenOf(parentId);
        var childSpec = Assert.Single(children).Spec;

        Assert.Equal(parentId, childSpec.ParentSessionId);
        Assert.Equal(1, childSpec.Depth);
        // ⚠️ 上限**沿树继承**（改常量不影响已经在跑的树）：默认 1。
        Assert.Equal(Subagent.DefaultMaxDepth, childSpec.MaxDepth);

        // ⚠️ 判据是**子会话自己的轴**（`state.json`），不是 spec：权限轴住在 state 里。
        // 提权请求（`trusted`）不生效 ⇒ 子是父那一档。
        var childState = host.Store.ReadState(childSpec.ProjectKey, childSpec.SessionId);
        Assert.Equal("standard", childState.Axes![Axes.Permission]);

        // 首条任务作为**一条真的用户消息**落盘（子看不到父的历史，所以那是它唯一的输入）。
        var timeline = host.Store.ReadTimeline(childSpec.ProjectKey, childSpec.SessionId);
        var first = Assert.IsType<TimelineEvent.MessageEvent>(timeline[0]);
        Assert.Equal(Speaker.User, first.From);
        Assert.Equal("去看一眼", Assert.IsType<Block.TextBlock>(Assert.Single(first.Blocks)).Text);
    }

    /// <summary>
    /// **深度到顶 ⇒ 一句人话，不建会话**（`01` §10.3「深度限量而非禁止」）。
    ///
    /// <para>⚠️ 判据在**工具**这一侧（`SubagentScope.CanSpawn` → `Subagent.CanSpawnFrom`），
    /// 所以结果是**模型读得到的一句话**，而不是一次静默失败——它据此才知道该自己干。</para>
    /// </summary>
    [Fact]
    public async Task Spawn_AtTheDepthLimit_AnswersInWords_AndCreatesNothing()
    {
        var parent = new LoopAdapter()
            .Script(new Block.ToolUseBlock("c1", SpawnTool.NameOf, """{"task":"第一层"}"""));
        // ⚠️ 子会话的第二步会拿到这一段 ⇒ 它试图再派一层（而它已经到顶）。
        var child = new LoopAdapter()
            .Script(new Block.ToolUseBlock("c2", SpawnTool.NameOf, """{"task":"第二层"}"""));
        var (host, _, dispatcher) = Harness(parent, child);

        var parentId = await CreateAsync(dispatcher);
        await SendAsync(dispatcher, parentId, "派一个");

        var childSpec = Assert.Single(host.Store.ChildrenOf(parentId)).Spec;

        // 子会话的第二步会拿到 `c2`（第二段脚本），于是它试图再派一层。
        await WaitUntil(
            () => host.Store.ReadTimeline(childSpec.ProjectKey, childSpec.SessionId)
                .OfType<TimelineEvent.ToolResultEvent>().Any(),
            "子会话那一步跑完");

        Assert.Empty(host.Store.ChildrenOf(childSpec.SessionId));

        // ⚠️ **结果是「说清楚」而不是「什么都没返回」**：模型据此才知道该自己干。
        var said = string.Concat(host.Store.ReadTimeline(childSpec.ProjectKey, childSpec.SessionId)
            .OfType<TimelineEvent.ToolResultEvent>()
            .SelectMany(r => r.Content).OfType<Block.TextBlock>().Select(t => t.Text));
        Assert.Contains("派生深度上限", said, StringComparison.Ordinal);
    }

    /// <summary>
    /// **父 → 子是「一条用户消息」**（`01` §10.1 的纯注入），而且它**真的会推动子会话**。
    ///
    /// <para>⚠️ 为什么它能够写成用户消息：子那边「用户」就是驱动它的人，而父正是那个人
    /// ⇒ 不需要新 `Speaker` 取值，也不需要改三个适配器。</para>
    /// </summary>
    [Fact]
    public async Task TellChild_AppendsAUserMessageToTheChild()
    {
        var parent = new LoopAdapter()
            .Script(new Block.ToolUseBlock("c1", SpawnTool.NameOf, """{"task":"第一件事"}"""));
        var (host, _, dispatcher) = Harness(parent, new LoopAdapter());

        var parentId = await CreateAsync(dispatcher);
        await SendAsync(dispatcher, parentId, "派一个");

        var child = Assert.Single(host.Store.ChildrenOf(parentId)).Spec;

        // 父补一句（这段脚本落在父的下一步上——子有自己的适配器，抢不走它）。
        parent.Script(new Block.ToolUseBlock(
            "c2", TellChildTool.NameOf, $$"""{"child":"{{child.SessionId}}","text":"补充一句"}"""));
        await SendAsync(dispatcher, parentId, "跟它说一句");

        await WaitUntil(
            () => host.Store.ReadTimeline(child.ProjectKey, child.SessionId)
                .OfType<TimelineEvent.MessageEvent>()
                .Where(m => m.From == Speaker.User)
                .SelectMany(m => m.Blocks.OfType<Block.TextBlock>())
                .Any(t => t.Text == "补充一句"),
            "那句补充话落进子的记录");

        var said = host.Store.ReadTimeline(child.ProjectKey, child.SessionId)
            .OfType<TimelineEvent.MessageEvent>()
            .Where(m => m.From == Speaker.User)
            .SelectMany(m => m.Blocks.OfType<Block.TextBlock>())
            .Select(t => t.Text)
            .ToList();

        // 第一条是派它的任务，第二条是这次补的话。
        Assert.Equal(["第一件事", "补充一句"], said);
    }

    /// <summary>
    /// **子 → 父是「一对合成的工具对」**（2026-10-24 用户裁定：回流复用
    /// 「投递截断 + `fetch(offset)`」那套 ⇒ 它必须是 `ToolResultEvent`）。
    ///
    /// <para>⚠️ 这一条同时钉住那次形状选择的两个后果：① id 带 `subagent-` 前缀
    /// （`Interruption` 靠它把合成对排除在配对判定之外）；② 它**不是用户消息**
    /// ——父的记录不许谎报「用户说过话」。</para>
    /// </summary>
    [Fact]
    public async Task ChildSettling_ReportsBackAsASynthesizedToolPair_NotAsAUserMessage()
    {
        // 第一段给父（`spawn`），子用**它自己的**适配器（它自己的第一句话）。
        var parent = new LoopAdapter()
            .Script(new Block.ToolUseBlock("c1", SpawnTool.NameOf, """{"task":"干点活"}"""));
        var child = new LoopAdapter().ScriptWith(new ModelReply
        {
            Blocks = [new CanonicalBlock.TextBlock("我干完了")],
            StopReason = "end_turn",
            Refused = false,
        });
        var (host, _, dispatcher) = Harness(parent, child);

        var parentId = await CreateAsync(dispatcher);
        var parentProject = host.Store.FindSession(parentId)!.Value.ProjectKey;
        await SendAsync(dispatcher, parentId, "派一个");

        var childSpec = Assert.Single(host.Store.ChildrenOf(parentId)).Spec;

        await WaitUntil(
            () => host.Store.ReadTimeline(parentProject, parentId).Any(Subagent.IsInjected),
            "子会话落定之后的回流");

        var parentTimeline = host.Store.ReadTimeline(parentProject, parentId);

        // 一对：一条带 tool_use 的模型消息 + 它那条结果。
        var call = parentTimeline.OfType<TimelineEvent.MessageEvent>()
            .SelectMany(m => m.Blocks.OfType<Block.ToolUseBlock>())
            .Single(t => t.Name == SubagentReportTool.NameOf);
        var result = parentTimeline.OfType<TimelineEvent.ToolResultEvent>()
            .Single(r => r.CallId == call.CallId);
        Assert.Contains("我干完了", string.Concat(result.Content.OfType<Block.TextBlock>().Select(t => t.Text)));

        // ⚠️ 配对完整（上游硬要求：悬空的任一半都是 400）。
        Assert.StartsWith(Subagent.InjectedIdPrefix, result.Id, StringComparison.Ordinal);

        // ⚠️ **回流不是用户消息**：父的记录里那两条用户消息只有父自己说过的那两句。
        var userSaid = parentTimeline.OfType<TimelineEvent.MessageEvent>()
            .Where(m => m.From == Speaker.User)
            .SelectMany(m => m.Blocks.OfType<Block.TextBlock>())
            .Select(t => t.Text)
            .ToList();
        Assert.DoesNotContain("我干完了", userSaid);
    }

    /// <summary>
    /// **合成对不许把父那一步判成「断裂」**（`Interruption.IgnoresPairing`）。
    ///
    /// <para>⚠️ 这条守的是一个**静默且恶毒**的形状：父正挂着一次询问（那条 `tool_use` 悬着），
    /// 此时子的回报夹进来 —— 若判定把它当成「另一个回合」，`Recovery.BeforeStep` 会补一条
    /// 「上一次这一步被打断了」，而**用户只是在想怎么回答**。这条直接在纯函数上钉，
    /// 不绕一整圈 E2E（那圈里看不出「为什么」）。</para>
    /// </summary>
    [Fact]
    public void AnInjectedPair_DoesNotCountAsAnotherTurn()
    {
        var at = DateTimeOffset.UnixEpoch;

        TimelineEvent[] timeline =
        [
            new TimelineEvent.MessageEvent
            {
                Id = "m1", At = at, From = Speaker.Model,
                Blocks = [new Block.ToolUseBlock("call-1", new AskTool().Name, "{}")],
            },
            // ⚠️ 子的回报正好夹在「父的调用」与「它的答复」之间。
            new TimelineEvent.MessageEvent
            {
                Id = "subagent-x-call", At = at, From = Speaker.Model,
                Blocks = [new Block.ToolUseBlock("subagent-x", SubagentReportTool.NameOf, "{}")],
            },
            new TimelineEvent.ToolResultEvent
            {
                Id = "subagent-x-result", At = at, CallId = "subagent-x",
                Content = [new Block.TextBlock("子说了一句话")],
            },
            new TimelineEvent.ToolResultEvent
            {
                Id = "r1", At = at, CallId = "call-1",
                Content = [new Block.TextBlock("用户答完了")],
            },
        ];

        // 那条调用**有配对**（在合成对之后），所以它不悬着。
        Assert.False(Interruption.WasInterrupted(timeline, "call-1", Interruption.NoteIdPrefix));

        // ⚠️ **反方向也要钉**：真的悬空时它仍然答「断裂」——
        // 否则上面那条可以靠「永远返回假」通过。
        var dangling = timeline.Take(3).ToList();
        Assert.True(Interruption.WasInterrupted(dangling, "call-1", Interruption.NoteIdPrefix));
    }

    /// <summary>
    /// **级联停止**（2026-10-24 用户裁定）：`session.interrupt` 打父，子也停。
    ///
    /// <para>⚠️ 判据是子的 `InterruptEpoch` **变了**——那正是 `RunStepAsync` 尾部
    /// 用来判断「要不要 goal 自动续跑」的那一格。只 `Cancel` 不涨序号的话，
    /// 一个 goal 模式的子会在这一步之后**自己继续跑**，而界面上看不出区别。</para>
    /// </summary>
    [Fact]
    public async Task Interrupt_CascadesToChildren()
    {
        var adapter = new LoopAdapter()
            .Script(new Block.ToolUseBlock("c1", SpawnTool.NameOf, """{"task":"慢慢做"}"""));
        var (host, _, dispatcher) = Harness(adapter);

        var parentId = await CreateAsync(dispatcher);
        await SendAsync(dispatcher, parentId, "派一个");

        var childSpec = Assert.Single(host.Store.ChildrenOf(parentId)).Spec;
        var child = host.Activate(childSpec.ProjectKey, childSpec.SessionId);

        var before = child.InterruptEpoch;

        await dispatcher.DispatchAsync(
            new CoreCommand.SessionInterrupt(parentId), "i", false, CancellationToken.None);

        Assert.True(child.InterruptEpoch > before,
            "父被叫停之后，子的停止计数必须也变——否则 goal 循环会接着跑。");
    }

    /// <summary>
    /// **归档 = 从视野里消失**（2026-10-26）。四件事一起钉：
    /// ① 标记真的落进 `spec.json`（可逆：非空 = 已归档）；
    /// ② `ChildrenOf` 不再列它（「看不见」的唯一判据）；
    /// ③ 下一次请求的尾块里**没有那个 id**（那一行整行不出现）；
    /// ④ `tell_child` 不再认它——**说的是实话**，不是静默失败。
    ///
    /// <para>⚠️ <b>它同时钉住「不删任何东西」</b>：那条记录与那个会话目录都还在
    /// （`TryReadSpec` 读得出来），归档只是一个字段。</para>
    /// </summary>
    [Fact]
    public async Task ArchiveChild_RemovesTheChildFromTheTailLineAndFromTellChild()
    {
        var parent = new LoopAdapter()
            .Script(new Block.ToolUseBlock("c1", SpawnTool.NameOf, """{"task":"一件事"}"""));
        var (host, _, dispatcher) = Harness(parent, new LoopAdapter());

        var parentId = await CreateAsync(dispatcher);
        await SendAsync(dispatcher, parentId, "派一个");

        var child = Assert.Single(host.Store.ChildrenOf(parentId)).Spec;

        // 父把它收起（这段脚本落在父的下一步上——子有自己的适配器，抢不走它）。
        parent.Script(new Block.ToolUseBlock(
            "c2", ArchiveChildTool.NameOf, $$"""{"child":"{{child.SessionId}}"}"""));
        await SendAsync(dispatcher, parentId, "把它收起");

        // ⚠️ **等它落盘，不假设 `awaitCompletion` 就等到了这一步**：`session.send` 在
        // 「已有一步在飞」时只**入队**就回执（`TryEnterStep` 那条启动去重），而子会话落定
        // 的回报完全可能刚好把父推起来 ⇒ 直接断言会变成投骰子（实测过一次）。
        await WaitUntil(
            () => host.Store.TryReadSpec(child.ProjectKey, child.SessionId)?.ArchivedAt is not null,
            "归档标记落盘");

        // ① 标记落盘，会话一条不少。
        var marked = host.Store.TryReadSpec(child.ProjectKey, child.SessionId);
        Assert.NotNull(marked);
        Assert.True(marked!.ArchivedAt is not null,
            $"归档标记没落盘。父这次的结果：{ParentResults(host, parentId)}");

        // ② 视野里没了。
        Assert.Empty(host.Store.ChildrenOf(parentId));

        // ③ 再走一步：尾块里不该再有它的 id。
        parent.Script(new Block.ToolUseBlock(
            "c3", TellChildTool.NameOf, $$"""{"child":"{{child.SessionId}}","text":"喂"}"""));
        await SendAsync(dispatcher, parentId, "再跟它说话");
        await WaitUntil(
            () => ParentResults(host, parentId).Contains("不是你的子会话", StringComparison.Ordinal),
            "`tell_child` 那句拒绝的结果");

        var tail = string.Concat(parent.Calls[^1].Slots.Tail
            .OfType<Block.TextBlock>().Select(b => b.Text));
        Assert.DoesNotContain(child.SessionId, tail, StringComparison.Ordinal);

        // ④ `tell_child` 说的是一句实话（而不是「已转达」）。
        // ⚠️ 判据取**整份结果正文**再看两句在不在：每轮末尾都会由核心合成一对
        // `floating_context` 占位结果（2026-10-26 的裁定），所以「最后一条结果」
        // 不是模型那句话——按位置取会在那一对上打空。
        var said = ParentResults(host, parentId);
        Assert.Contains("不是你的子会话", said, StringComparison.Ordinal);
        Assert.DoesNotContain("已转达", said, StringComparison.Ordinal);
    }

    /// <summary>
    /// **`archive_child` 只认自己的孩子**：一个别人的（或已经不存在的）id ⇒ 一句人话 + 什么都不做。
    ///
    /// <para>⚠️ 这条判据必须在**工具**这一侧（结果一落盘就改不了），而它的失败模式很难看出来：
    /// 若只判「id 非空」，模型就能把**任何**会话从它父亲的视野里抹掉，
    /// 而重复归档也会落成一句「已收起」的假话。</para>
    /// </summary>
    [Fact]
    public async Task ArchiveChild_RefusesAnIdThatIsNotYourChild()
    {
        var tool = new ArchiveChildTool();
        var context = new SessionToolContext(_work, new BlobStore(_root),
            subagents: new SubagentScope("p", null, 0, 1, [new SubagentChild("mine", SessionStatus.Idle)]));

        var notMine = await tool.RunAsync(
            new ToolCall("t1", ArchiveChildTool.NameOf, """{"child":"someone-else"}"""),
            context, CancellationToken.None);

        Assert.Null(notMine.Archive);
        var said = string.Concat(notMine.Content.OfType<Block.TextBlock>().Select(b => b.Text));
        Assert.Contains("不是你的子会话", said, StringComparison.Ordinal);
        // ⚠️ 名单要如实交出来——模型据此才知道该用哪个 id。
        Assert.Contains("mine", said, StringComparison.Ordinal);

        // 自己的孩子 ⇒ 真的产出请求（而**工具自己不写任何东西**）。
        var mine = await tool.RunAsync(
            new ToolCall("t2", ArchiveChildTool.NameOf, """{"child":"mine"}"""), context, CancellationToken.None);
        Assert.Equal("mine", mine.Archive?.ChildSessionId);
    }

    /// <summary>
    /// **`ChildrenOf` 不列归档的孩子**（2026-10-26）——它是「看不见」的唯一判据，
    /// 所以这一条盯的是**判别式本身**：两个活孩子 + 一个归档孩子 ⇒ 只列两个，
    /// 而 `ListSessions` 仍然三个都在（账照记，视野里消失）。
    /// </summary>
    [Fact]
    public void ChildrenOf_HidesArchivedChildren_ButListSessionsStillCountsThem()
    {
        var store = new SessionStore(_root);
        var model = new ModelSelection
        {
            ProviderId = "p", ModelId = "m", Protocol = Protocols.ClaudeMessages,
            Effort = "medium", MaxTokens = 1024,
        };

        SessionSpec Spec(string id, DateTimeOffset? archivedAt = null) => new()
        {
            SessionId = id,
            ProjectKey = "pk",
            WorkingDirectory = _work,
            CreatedAt = DateTimeOffset.UnixEpoch,
            ParentSessionId = "parent",
            ArchivedAt = archivedAt,
            Model = model,
        };

        store.WriteSpec("pk", Spec("a"));
        store.WriteSpec("pk", Spec("b"));
        store.WriteSpec("pk", Spec("c", DateTimeOffset.UnixEpoch));

        // 视野：归档的那个不在。
        Assert.Equal(["a", "b"], store.ChildrenOf("parent").Select(x => x.Spec.SessionId));

        // 账：三个都还在（`ListSessions` 的取舍没被这一条改掉）。
        Assert.Equal(["a", "b", "c"], store.ListSessions("pk"));
    }

    /// <summary>父记录里**全部**工具结果的正文（上面那条断言用它）。</summary>
    private static string ParentResults(CoreHost host, string parentId) =>
        string.Concat(host.Store.ReadTimeline(
                host.Store.FindSession(parentId)!.Value.ProjectKey, parentId)
            .OfType<TimelineEvent.ToolResultEvent>()
            .SelectMany(r => r.Content).OfType<Block.TextBlock>().Select(b => b.Text));
}
