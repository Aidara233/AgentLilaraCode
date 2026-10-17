using Lilara.Core.Kernel;
using Lilara.Core.Protocol;
using Lilara.Core.Session;
using Lilara.Core.Session.Tools;
using Lilara.Core.Storage;
using Lilara.Core.Tests.Fakes;
using Block = Lilara.Core.Kernel.Block;

namespace Lilara.Core.Tests;

/// <summary>
/// **线**（`01` §12.4）——模型说「这批资源我用完了」，核心把这句话记成一条记录，
/// 而那条记录让**它之前读的东西**不再留在上下文里。
///
/// <para>⚠️ <b>这条链在 2026-10-11 之前一次都没通过</b>：`Reclaim.Decide` 只有
/// 「最近 N 条之外」一条判据，而 §12.4 那一整节是纸上的
/// （线上的 `claim.release` 只改 `ClaimStore`、**时间线一个字不写**）。</para>
///
/// <para>⚠️ <b>断言落在<b>请求</b>上，不落在盘面上</b>——回收只换渲染、记录一字不动
/// （`01` §8.0.2 一 / 八）。同 `StepLagTests` / `StaleTests` 那条纪律。</para>
/// </summary>
public class ReleaseLineTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "lilara-line-" + Guid.NewGuid().ToString("N"));
    private readonly string _work;
    private readonly SessionStore _store;
    private readonly FakeAdapter _adapter = new();
    private readonly Stepper _stepper;
    private readonly SessionHandle _handle;

    public ReleaseLineTests()
    {
        _work = Path.Combine(_tmp, "work");
        Directory.CreateDirectory(_work);

        _store = new SessionStore(Path.Combine(_tmp, "data"));
        _stepper = new Stepper(_store, _adapter, BuiltInTools.All);

        // ⚠️ 测试的脚手架，不是被测物（同 `StaleTests`）：这里要的是结果真的落下来。
        _handle = SessionHandle.Create(_store, _work, Model());
        _handle.UpdateStateAndPersist(_store, s => s with
        {
            Axes = new Dictionary<string, string>(s.Axes!, StringComparer.Ordinal)
            {
                [Axes.Permission] = "authorized",
            },
        }, diskStatus: SessionStatus.Idle);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true);
    }

    private static ModelSelection Model() => new()
    {
        ProviderId = "p", ModelId = "m", Protocol = Protocols.ClaudeMessages, Effort = "high", MaxTokens = 1000,
    };

    private static string TextOf(TimelineEvent e) =>
        string.Concat(((TimelineEvent.ToolResultEvent)e).Content.OfType<Block.TextBlock>().Select(b => b.Text));

    private static Block.ToolUseBlock ReadOf(string callId, string path) =>
        new(callId, "read", System.Text.Json.JsonSerializer.Serialize(new { path }));

    private static Block.ToolUseBlock ReleaseOf(string callId, params string[] scope) =>
        new(callId, "release", System.Text.Json.JsonSerializer.Serialize(new { scope }));

    private IReadOnlyList<TimelineEvent.ToolResultEvent> Sent() =>
        [.. _adapter.Calls[^1].Timeline.OfType<TimelineEvent.ToolResultEvent>()];

    /// <summary>
    /// ⚠️ **本批的核心一条**：读两个文件 → `release` 其中一个 → 下一步的请求里
    /// **只有那一个**换成了注记，另一个原样。
    ///
    /// <para>⚠️ 那一读一放之间**没有任何东西推进保护带**（两条结果而已），
    /// 所以这条绿**不可能**是「距离」那一档做的——它是线做的。
    /// 这正是本条与 `StepLagTests` 那条的区别：那里要靠垫 `ProtectedRecentResults` 条结果。</para>
    /// </summary>
    [Fact]
    public async Task ARelease_ReclaimsWhatItCovers_AndOnlyThat()
    {
        File.WriteAllText(Path.Combine(_work, "a.txt"), "甲");
        Directory.CreateDirectory(Path.Combine(_work, "src"));
        File.WriteAllText(Path.Combine(_work, "src", "b.txt"), "乙");

        // 第 1 步：读两个文件（一个在 src/ 下，一个不在）。
        _adapter.ReplyWith(_ => new ModelReply
        {
            Blocks = [ReadOf("c1", "a.txt"), ReadOf("c2", "src/b.txt")],
            StopReason = "tool_use", Refused = false,
        });
        await _stepper.StepAsync(_handle, "看一眼", default);

        var results = _store.ReadTimeline(_handle.ProjectKey, _handle.SessionId)
            .OfType<TimelineEvent.ToolResultEvent>().ToList();
        var aId = results.Single(r => r.DependsOn == "a.txt").Id;
        var bId = results.Single(r => r.DependsOn == "src/b.txt").Id;

        // 第 2 步：说「src 我用完了」。
        _adapter.ReplyWith(_ => new ModelReply
        {
            Blocks = [ReleaseOf("c3", "src")], StopReason = "tool_use", Refused = false,
        });
        var releaseStep = await _stepper.StepAsync(_handle, [], default);

        // ⚠️ **线的头出口**：`StepOutcome` 交回这条线，`CommandDispatcher` 据此发
        // `release.recorded`（头重拉历史、画分隔线）。⚠️ 判据是**同一条线的 id**
        // ——另起一条会让头画的线和判定读的线对不上。
        var recorded = Assert.Single(releaseStep.ReleasedLines!);
        Assert.Equal(["src"], recorded.Scope);
        Assert.Contains(_store.ReadTimeline(_handle.ProjectKey, _handle.SessionId)
            .OfType<TimelineEvent.ReleaseEvent>(), e => e.Id == recorded.Id);

        // 第 3 步：纯文本 ⇒ 这一趟的请求就是断言对象。
        _adapter.Reply("好。");
        await _stepper.StepAsync(_handle, [], default);

        var sent = Sent();
        Assert.Contains("[已回收]", TextOf(sent.Single(r => r.Id == bId)), StringComparison.Ordinal);
        Assert.DoesNotContain("[已回收]", TextOf(sent.Single(r => r.Id == aId)), StringComparison.Ordinal);
        Assert.Contains("甲", TextOf(sent.Single(r => r.Id == aId)), StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ **线不是「范围里的都收」，是「范围里<b>且取回得回</b>的才收」**——
    /// 那条判据（`01` §8.0.2 二）不是可选的：注记上写着 `fetch(id)`，取不回就是一句假话。
    /// </summary>
    [Fact]
    public async Task AReleasedResult_IsStillFetchable()
    {
        File.WriteAllText(Path.Combine(_work, "a.txt"), "原来的内容");

        _adapter.ReplyWith(_ => new ModelReply
        {
            Blocks = [ReadOf("c1", "a.txt")], StopReason = "tool_use", Refused = false,
        });
        await _stepper.StepAsync(_handle, "看一眼", default);

        var aId = _store.ReadTimeline(_handle.ProjectKey, _handle.SessionId)
            .OfType<TimelineEvent.ToolResultEvent>().Single().Id;

        _adapter.ReplyWith(_ => new ModelReply
        {
            Blocks = [ReleaseOf("c3", "a.txt")], StopReason = "tool_use", Refused = false,
        });
        await _stepper.StepAsync(_handle, [], default);

        // 模型照注记发出 fetch(id)。
        _adapter.ReplyWith(_ => new ModelReply
        {
            Blocks = [new Block.ToolUseBlock("c9", "fetch",
                System.Text.Json.JsonSerializer.Serialize(new { id = aId }))],
            StopReason = "tool_use", Refused = false,
        });
        await _stepper.StepAsync(_handle, [], default);

        _adapter.Reply("好。");
        await _stepper.StepAsync(_handle, [], default);

        var fetched = _adapter.Calls[^1].Timeline
            .OfType<TimelineEvent.ToolResultEvent>().Single(r => r.CallId == "c9");
        Assert.Equal("原来的内容", TextOf(fetched));
    }

    /// <summary>
    /// ⚠️ **第二次释放必须至少和第一次一样强**——多覆盖，**绝不撤回**已覆盖的。
    ///
    /// <para>⚠️ 这条钉的是一个**具体的错法**：把判据写成「**最后一条线**以下全收」。
    /// 那样放第二次线之后，第一条线的领地不再被覆盖 ⇒ 那批结果**又变新鲜**
    /// ⇒ 前缀每放一次线抖一次。⇒ 判据是**集合**（释放过就是释放过），不是**位置**。</para>
    /// </summary>
    [Fact]
    public async Task ASecondRelease_NeverUnReclaimsTheFirst()
    {
        File.WriteAllText(Path.Combine(_work, "a.txt"), "甲");
        File.WriteAllText(Path.Combine(_work, "c.txt"), "丙");

        _adapter.ReplyWith(_ => new ModelReply
        {
            Blocks = [ReadOf("c1", "a.txt")], StopReason = "tool_use", Refused = false,
        });
        await _stepper.StepAsync(_handle, "看一眼", default);

        var aId = _store.ReadTimeline(_handle.ProjectKey, _handle.SessionId)
            .OfType<TimelineEvent.ToolResultEvent>().Single().Id;

        // 第一次：释放 a。
        _adapter.ReplyWith(_ => new ModelReply
        {
            Blocks = [ReleaseOf("c2", "a.txt")], StopReason = "tool_use", Refused = false,
        });
        await _stepper.StepAsync(_handle, [], default);

        // 中间再读一个别的东西（它**不在**任何作用域里）。
        _adapter.ReplyWith(_ => new ModelReply
        {
            Blocks = [ReadOf("c3", "c.txt")], StopReason = "tool_use", Refused = false,
        });
        await _stepper.StepAsync(_handle, [], default);

        // 第二次：释放 c。⚠️ 注意它**没有**提 a。
        _adapter.ReplyWith(_ => new ModelReply
        {
            Blocks = [ReleaseOf("c4", "c.txt")], StopReason = "tool_use", Refused = false,
        });
        await _stepper.StepAsync(_handle, [], default);

        _adapter.Reply("好。");
        await _stepper.StepAsync(_handle, [], default);

        // ⚠️ **a 那条必须**仍然**在注记形态**——第二次释放没有撤回它。
        var sent = Sent();
        Assert.Contains("[已回收]", TextOf(sent.Single(r => r.Id == aId)), StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ **前缀那一条必须落在分隔符上**：`src` 不许盖住 `src-evil/`。
    ///
    /// <para>与 `SessionToolContext.ResolvePath` 和 `01` §12.1 的路径判据**同一条规则**
    /// （那里写着同一个例子：不加分隔符，`C:\work-evil` 会被判成在 `C:\work` 之下）。
    /// ⇒ 少了分隔符，一次「释放 src」会**静默地**把隔壁目录的内容也收掉。</para>
    /// </summary>
    [Fact]
    public async Task AScope_DoesNotCoverASiblingWithTheSamePrefix()
    {
        Directory.CreateDirectory(Path.Combine(_work, "src"));
        File.WriteAllText(Path.Combine(_work, "src", "a.txt"), "甲");
        Directory.CreateDirectory(Path.Combine(_work, "src-evil"));
        File.WriteAllText(Path.Combine(_work, "src-evil", "b.txt"), "乙");

        _adapter.ReplyWith(_ => new ModelReply
        {
            Blocks = [ReadOf("c1", "src/a.txt"), ReadOf("c2", "src-evil/b.txt")],
            StopReason = "tool_use", Refused = false,
        });
        await _stepper.StepAsync(_handle, "看一眼", default);

        var results = _store.ReadTimeline(_handle.ProjectKey, _handle.SessionId)
            .OfType<TimelineEvent.ToolResultEvent>().ToList();
        var inside = results.Single(r => r.DependsOn == "src/a.txt").Id;
        var sibling = results.Single(r => r.DependsOn == "src-evil/b.txt").Id;

        _adapter.ReplyWith(_ => new ModelReply
        {
            Blocks = [ReleaseOf("c3", "src")], StopReason = "tool_use", Refused = false,
        });
        await _stepper.StepAsync(_handle, [], default);

        _adapter.Reply("好。");
        await _stepper.StepAsync(_handle, [], default);

        var sent = Sent();
        Assert.Contains("[已回收]", TextOf(sent.Single(r => r.Id == inside)), StringComparison.Ordinal);
        Assert.DoesNotContain("[已回收]", TextOf(sent.Single(r => r.Id == sibling)), StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ **线本身不进模型看见的那串事件**——它是给我们自己看的锚。
    /// 那次释放的 `tool_use` 已经在记录里了，再喂一条是多余的字节。
    ///
    /// <para>⚠️ 而它**照样在记录里**（下面顺手核一下）——「不动记录」那条纪律的另一半。</para>
    /// </summary>
    [Fact]
    public async Task TheLine_IsInTheRecord_ButNotInWhatTheModelSees()
    {
        File.WriteAllText(Path.Combine(_work, "a.txt"), "甲");

        _adapter.ReplyWith(_ => new ModelReply
        {
            Blocks = [ReadOf("c1", "a.txt")], StopReason = "tool_use", Refused = false,
        });
        await _stepper.StepAsync(_handle, "看一眼", default);

        _adapter.ReplyWith(_ => new ModelReply
        {
            Blocks = [ReleaseOf("c2", "a.txt", "docs")], StopReason = "tool_use", Refused = false,
        });
        await _stepper.StepAsync(_handle, [], default);

        _adapter.Reply("好。");
        await _stepper.StepAsync(_handle, [], default);

        // 记录里有这条线（判定读的是它）。
        var recorded = _store.ReadTimeline(_handle.ProjectKey, _handle.SessionId)
            .OfType<TimelineEvent.ReleaseEvent>().Single();
        Assert.Equal(["a.txt", "docs"], recorded.Scope);

        // 而模型看见的那串事件里没有它。
        Assert.DoesNotContain(_adapter.Calls[^1].Timeline, e => e is TimelineEvent.ReleaseEvent);

        // ⚠️ 那次调用的结果照常在（配对不因为它是「标记类」工具而破）。
        Assert.Contains(_adapter.Calls[^1].Timeline,
            e => e is TimelineEvent.ToolResultEvent { CallId: "c2" });
    }

    /// <summary>
    /// ⚠️ **`scope` 给错形状 ⇒ 不画线**（而不是画一条空 scope 的）。
    ///
    /// <para>⚠️ 空 scope 的记录**会把所有没有指称的结果都收掉**（那条「归工作集整体退场」
    /// 的判据）——那是**凭一条坏参数做的全局决定**。⇒ 宁可什么也不做。</para>
    /// </summary>
    [Fact]
    public async Task AMalformedScope_DrawsNoLine()
    {
        File.WriteAllText(Path.Combine(_work, "a.txt"), "甲");

        _adapter.ReplyWith(_ => new ModelReply
        {
            Blocks = [ReadOf("c1", "a.txt")], StopReason = "tool_use", Refused = false,
        });
        await _stepper.StepAsync(_handle, "看一眼", default);

        // ⚠️ `scope` 是一个**裸字符串**，不是数组——模型真会这么发。
        _adapter.ReplyWith(_ => new ModelReply
        {
            Blocks = [new Block.ToolUseBlock("c2", "release", """{"scope":"a.txt"}""")],
            StopReason = "tool_use", Refused = false,
        });
        await _stepper.StepAsync(_handle, [], default);

        Assert.Empty(_store.ReadTimeline(_handle.ProjectKey, _handle.SessionId)
            .OfType<TimelineEvent.ReleaseEvent>());
    }

    /// <summary>
    /// ⚠️ **空 `<c>scope</c> 数组同样不画线，而且工具那侧也必须这么说**
    /// （2026-10-17）——两层判据必须共用一个谓词。
    ///
    /// <para>⚠️ <b>它钉的是一个具体的分叉</b>：`ToolArgs.StringArray` 对 `[]` 返回**空列表**
    /// （不是 `null`），而 `ReleaseTool` 原先只判 `null` ⇒ 回一句「已释放。」，
    /// 而 `Stepper.RecordRelease` 看到 `Count == 0` 不画线。⇒ 模型以为收掉了，
    /// 上下文一个字没变，而两边都看着正常。</para>
    /// </summary>
    [Fact]
    public async Task AnEmptyScope_IsRejectedByTheToolAndDrawsNoLine()
    {
        File.WriteAllText(Path.Combine(_work, "a.txt"), "甲");

        _adapter.ReplyWith(_ => new ModelReply
        {
            Blocks = [ReadOf("c1", "a.txt")], StopReason = "tool_use", Refused = false,
        });
        await _stepper.StepAsync(_handle, "看一眼", default);

        _adapter.ReplyWith(_ => new ModelReply
        {
            Blocks = [new Block.ToolUseBlock("c2", "release", """{"scope":[]}""")],
            StopReason = "tool_use", Refused = false,
        });
        await _stepper.StepAsync(_handle, [], default);

        Assert.Empty(_store.ReadTimeline(_handle.ProjectKey, _handle.SessionId)
            .OfType<TimelineEvent.ReleaseEvent>());

        var result = _store.ReadTimeline(_handle.ProjectKey, _handle.SessionId)
            .OfType<TimelineEvent.ToolResultEvent>().Single(r => r.CallId == "c2");
        var text = string.Concat(result.Content.OfType<Block.TextBlock>().Select(b => b.Text));
        Assert.DoesNotContain("已释放", text, StringComparison.Ordinal);
        Assert.Contains("非空", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ **恢复批次里的 `release` 也要画线**（2026-10-17，缺口 A）。
    ///
    /// <para>⚠️ <b>缺陷的形状</b>：一批里只要有一条要问（这里那条 `write_thing` 在
    /// `standard` 档是写入 ⇒ 问），整批挂起；用户批准后 `ToolExecutor` **全部执行**，
    /// `release` 照样返回「已释放。」——而 `RecordRelease` 原先只在 `StepAsync` 里调，
    /// `ResumeAsync` 一个字都不写 ⇒ 工具说释放了、记录里没有线、上下文也不回收。</para>
    ///
    /// <para>⚠️ <b>判据分两层</b>：① 记录里有 `ReleaseEvent`；② 下一次渲染真的把
    /// 释放点之前的结果换成了注记（线的**效果**，不只是那条记录）。</para>
    /// </summary>
    [Fact]
    public async Task ARelease_InAResumedBatch_DrawsTheLineAndReclaims()
    {
        File.WriteAllText(Path.Combine(_work, "a.txt"), "甲");
        var handle = HandleWith("standard");
        var tools = new ITool[] { new ReleaseTool(), new ReadTool(), new AskingWriter() };
        var stepper = new Stepper(_store, _adapter, tools);

        // 第 1 步：读一个文件（一条有效的结果，等着被线覆盖）。
        _adapter.ReplyWith(_ => new ModelReply
        {
            Blocks = [ReadOf("c1", "a.txt")], StopReason = "tool_use", Refused = false,
        });
        await stepper.StepAsync(handle, "看一眼", default);

        var readId = _store.ReadTimeline(handle.ProjectKey, handle.SessionId)
            .OfType<TimelineEvent.ToolResultEvent>().Single(r => r.CallId == "c1").Id;

        // 第 2 步：release + 一条会触发询问的写入调用 ⇒ 整批挂起，一条都没跑。
        _adapter.ReplyWith(_ => new ModelReply
        {
            Blocks =
            [
                ReleaseOf("c2", "a.txt"),
                new Block.ToolUseBlock("c3", "write_thing", """{"a":1}"""),
            ],
            StopReason = "tool_use", Refused = false,
        });
        var step = await stepper.StepAsync(handle, [], default);
        Assert.NotNull(step.Refused);
        Assert.Empty(_store.ReadTimeline(handle.ProjectKey, handle.SessionId)
            .OfType<TimelineEvent.ReleaseEvent>());

        // 用户批准 ⇒ 整批执行（`ResumeAsync` 那条路）。
        var calls = handle.AwaitingInput!.Calls;
        var reply = new ToolExecutor.Reply(
            AskKind.Permission,
            [.. calls.Select(c => c.CallId)],
            [],
            new Dictionary<string, string>());
        await stepper.ResumeAsync(
            handle, calls, reply, default,
            _store.ReadState(handle.ProjectKey, handle.SessionId).Ask!);

        var line = Assert.Single(_store.ReadTimeline(handle.ProjectKey, handle.SessionId)
            .OfType<TimelineEvent.ReleaseEvent>());
        Assert.Equal(["a.txt"], line.Scope);

        // 线的效果：下一次请求里那条读结果已经换成注记。
        _adapter.Reply("好。");
        await stepper.StepAsync(handle, [], default);
        var sent = _adapter.Calls[^1].Timeline.OfType<TimelineEvent.ToolResultEvent>()
            .Single(r => r.CallId == "c1");
        Assert.Contains("[已回收]", TextOf(sent), StringComparison.Ordinal);
        Assert.NotNull(readId);
    }

    /// <summary>
    /// ⚠️ **被闸门硬拒的 `release` 不画线**（2026-10-17，缺口 C）。
    ///
    /// <para>⚠️ `off` 档会拒绝只读类，而那条调用**一次都没跑**。原先的写法只看
    /// 「本批里有没有叫 `release` 的调用」⇒ 一次被拒绝的操作照样留下一条线，
    /// 而线是**单调的判据**：它会把那些结果永久收掉。⇒ 判据改为执行器的执行名单。</para>
    /// </summary>
    [Fact]
    public async Task ARelease_DeniedByTheGate_DrawsNoLine()
    {
        File.WriteAllText(Path.Combine(_work, "a.txt"), "甲");
        var handle = HandleWith("off");
        var stepper = new Stepper(_store, _adapter, BuiltInTools.All);

        _adapter.ReplyWith(_ => new ModelReply
        {
            Blocks = [ReadOf("c1", "a.txt")], StopReason = "tool_use", Refused = false,
        });
        await stepper.StepAsync(handle, "看一眼", default);

        _adapter.ReplyWith(_ => new ModelReply
        {
            Blocks = [ReleaseOf("c2", "a.txt")], StopReason = "tool_use", Refused = false,
        });
        await stepper.StepAsync(handle, [], default);

        Assert.Empty(_store.ReadTimeline(handle.ProjectKey, handle.SessionId)
            .OfType<TimelineEvent.ReleaseEvent>());

        // ⚠️ 配对不能因此破：拒绝**也是一条结果**（`01` §3.6 ①）。
        var result = _store.ReadTimeline(handle.ProjectKey, handle.SessionId)
            .OfType<TimelineEvent.ToolResultEvent>().Single(r => r.CallId == "c2");
        Assert.Contains("拒绝", TextOf(result), StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ **模型侧 `release` 摘掉自己的声明**（2026-10-17，§12.4 的解锁那一半）。
    ///
    /// <para>⚠️ <b>缺陷的形状</b>：`ClaimStore` 的写手原先只在命令层
    /// （`claim.release`），而工具够不着它 ⇒ 模型说「我用完了」，锁还在，
    /// 别的会话照样被挡。</para>
    ///
    /// <para>⚠️ <b>这里钉的是逐字路径</b>（`claim.release` 的判据），不是「整棵子树」——
    /// 文件条目与目录条目是两把不同的声明。⚠️ **「只摘自己的」不在这里测**：
    /// 它被一个既有事实挡住了——`claims.json` **同一个工作目录只有一份**，
    /// 第二个会话的第一条声明就会判成冲突、写不进去（`ClaimCommandTests` 钉着）。
    /// ⇒ 在同一个工作目录里造不出「别人的锁」，而 `ClaimStore.Release` 的
    /// `ownerSessionId` 筛选今天因此**没有可观测效果**；它的价值是记录意图，
    /// 等「每个会话独立 workingDirectory」或跨目录声明出现时再补测试。
    /// **别把这条读成「已经证明只摘自己的」。**</para>
    /// </summary>
    [Fact]
    public async Task ARelease_UnlocksTheSessionsOwnClaimsInScope()
    {
        var claims = new ClaimStore(_work);
        var key = ProjectKey.FromPath(_work);
        claims.Save(
        [
            // ⚠️ `ClaimStore.Release` 与 `claim.release` 同一条判据：**逐字路径**。
            // 所以这里声明的是目录本身（典型用法），而不是它下面的某个文件。
            new Claim(_handle.SessionId, key, Claims.Normalize("a.txt", _work), CoreClock.Now()),
            new Claim(_handle.SessionId, key, Claims.Normalize("src", _work), CoreClock.Now()),
        ]);

        File.WriteAllText(Path.Combine(_work, "a.txt"), "甲");
        _adapter.ReplyWith(_ => new ModelReply
        {
            Blocks = [ReadOf("c1", "a.txt")], StopReason = "tool_use", Refused = false,
        });
        await _stepper.StepAsync(_handle, "看一眼", default);

        var released = new List<Claim>();
        _adapter.ReplyWith(_ => new ModelReply
        {
            Blocks = [ReleaseOf("c2", "a.txt", "src")], StopReason = "tool_use", Refused = false,
        });
        var step = await _stepper.StepAsync(_handle, [], default);
        released.AddRange(step.ReleasedClaims ?? []);

        var remaining = claims.All().Select(c => c.Path).ToList();
        Assert.DoesNotContain(Claims.Normalize("a.txt", _work), remaining);
        Assert.DoesNotContain(Claims.Normalize("src", _work), remaining);
        Assert.Equal(2, released.Count);
    }

    private SessionHandle HandleWith(string permission)
    {
        var handle = SessionHandle.Create(_store, _work, Model());
        handle.UpdateStateAndPersist(_store, s => s with
        {
            Axes = new Dictionary<string, string>(s.Axes!, StringComparer.Ordinal)
            {
                [Axes.Permission] = permission,
            },
        }, diskStatus: SessionStatus.Idle);
        return handle;
    }

    /// <summary>一个声明为写入的测试替身——`standard` 档会让它触发询问，从而挂起整批。</summary>
    private sealed class AskingWriter : ITool
    {
        public string Name => "write_thing";
        public PermissionClass PermissionClass => PermissionClass.Writing;
        public string Description => "测试替身";
        public string ParametersSchemaJson => """{"type":"object","properties":{}}""";

        public Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct) =>
            Task.FromResult(new ToolOutcome { Content = [new Block.TextBlock("写了")] });
    }
}
