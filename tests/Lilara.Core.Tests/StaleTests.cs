using Lilara.Core.Kernel;
using Lilara.Core.Protocol;
using Lilara.Core.Session;
using Lilara.Core.Session.Tools;
using Lilara.Core.Storage;
using Lilara.Core.Tests.Fakes;
using Block = Lilara.Core.Kernel.Block;

namespace Lilara.Core.Tests;

/// <summary>
/// **点杀**——「这条结果过时了，换成注记」（`01` §11.3 判据轴上的第一档）。
///
/// <para>⚠️ <b>它在 2026-10-11 之前是空集</b>：判据（`00` §5 的三态判定）写着，
/// 而 `DependsOn` / `Fingerprint` 两个字段**只被写、不被读**——
/// 全 `src/` 找不到一处比较它们的地方。这批补上了第一个消费者。</para>
///
/// <para>⚠️ <b>2026-10-17 形状更正：点杀只在<b>开窗那一刻</b>解析</b>——渲染发生在每一步，
/// 而回收改的是前缀中段（`00` §3：热路径「绝不事后清理」）。⇒ 本测试显式开窗
/// （同 `session.gc` 的逻辑），并单独钉住「不开窗就不收」。</para>
///
/// <para>⚠️ <b>断言落在<b>请求</b>上，不落在盘面上</b>：回收只换渲染、已有记录一字不动
/// （`01` §8.0.2 一 / 八）⇒ 盘面只看得到**窗口记录**，看不到被收内容的痕迹。
/// 与 `StepLagTests` 同一条纪律。</para>
/// </summary>
public class StaleTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "lilara-stale-" + Guid.NewGuid().ToString("N"));
    private readonly string _work;
    private readonly SessionStore _store;
    private readonly FakeAdapter _adapter = new();
    private readonly Stepper _stepper;
    private readonly SessionHandle _handle;

    public StaleTests()
    {
        _work = Path.Combine(_tmp, "work");
        Directory.CreateDirectory(_work);

        _store = new SessionStore(Path.Combine(_tmp, "data"));
        _stepper = new Stepper(_store, _adapter, BuiltInTools.All);

        // ⚠️ **测试的脚手架，不是被测物**：`standard` 档下 `read` 是「问」，这里要的是
        // 「结果真的落下来」。闸门本身在 `GateTests` 里测。同 `StepLagTests` 的构造。
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

    /// <summary>
    /// 点杀用的「开窗」：<see cref="Reclaim.Resolve"/> 这一刻解析一次（距离 + 点杀），
    /// 落成一条窗口记录——**这就是 `session.gc` 在做的事**（逻辑与 `CommandDispatcher` 相同，
    /// 但这里不需要起一个控制面）。
    ///
    /// <para>⚠️ **不开窗就不回收**（2026-10-17）：渲染发生在每一步，而回收只许在失效窗口里
    /// 改历史中段。⇒ 这条测试用它来**显式制造窗口**，与真实路径同形：
    /// 解析 → 落盘 → 下一步渲染读它。</para>
    /// </summary>
    private void OpenWindow()
    {
        var timeline = _store.ReadTimeline(_handle.ProjectKey, _handle.SessionId);
        var blobs = new BlobStore(_store.DataRoot);
        var ids = Reclaim.Resolve(timeline, blobs,
            new SessionToolContext(_handle.Spec.WorkingDirectory, blobs, timeline));
        _store.AppendEvents(_handle.ProjectKey, _handle.SessionId,
            [Reclaim.Window("test", ids, null)]);
    }

    /// <summary>
    /// ⚠️ **本批的核心一条**：模型读过 `a.txt`，之后那个文件**被改了** ⇒
    /// **下一次开窗**之后，那条结果换成注记。
    ///
    /// <para>⚠️ <b>「被改了」由测试自己制造，而且这是忠实的</b>：现实里改它的是
    /// 另一个会话、或坐在编辑器前的用户——`01` §12.6 那张表最后一行写着
    /// 「人类编辑 / 进程外改动」正是检测层要兜的那一格。⇒ 这里**不通过 `update` 工具**去改，
    /// 就是为了测那一格；走工具改是另一条路径（`Reclaim.Stale` 对它同样成立）。</para>
    ///
    /// <para>⚠️ <b>改在「第 2 步的模型调用期间」</b>：开窗发生在它之后，于是窗口里
    /// 重读到的就是**此刻**的磁盘。⚠️ 而「不开窗就不收」由
    /// <see cref="NoWindow_MeansTheChangedReadStaysAsItIs"/> 单独钉住。</para>
    /// </summary>
    [Fact]
    public async Task ARead_WhoseFileWasChanged_RendersAsTheNoteAfterAWindow()
    {
        File.WriteAllText(Path.Combine(_work, "a.txt"), "原来的内容");

        // 第 1 步：读它（这条结果**会被点杀**）。
        _adapter.ReplyWith(_ => new ModelReply
        {
            Blocks = [ReadOf("c1", "a.txt")], StopReason = "tool_use", Refused = false,
        });
        await _stepper.StepAsync(_handle, "看一眼", default);

        var readId = _store.ReadTimeline(_handle.ProjectKey, _handle.SessionId)
            .OfType<TimelineEvent.ToolResultEvent>().Single(r => !r.CallId.StartsWith("floating-", StringComparison.Ordinal)).Id;

        // 第 2 步：模型调用**期间**世界变了，然后模型**再读一次**（同一趟里就能看出差别）。
        _adapter.ReplyWith(_ =>
        {
            File.WriteAllText(Path.Combine(_work, "a.txt"), "换过的内容");
            return new ModelReply
            {
                Blocks = [ReadOf("c2", "a.txt")], StopReason = "tool_use", Refused = false,
            };
        });
        await _stepper.StepAsync(_handle, [], default);

        // 开窗（= `session.gc`）：此刻解析，a.txt 已变 ⇒ 第一条结果进窗口。
        OpenWindow();

        // 第 3 步：纯文本 ⇒ 这一趟的请求就是最终要断言的东西。
        _adapter.Reply("看完了。");
        await _stepper.StepAsync(_handle, [], default);

        var sent = _adapter.Calls[^1].Timeline
            .OfType<TimelineEvent.ToolResultEvent>().Single(r => r.Id == readId);

        Assert.Contains("[已回收]", TextOf(sent), StringComparison.Ordinal);
        Assert.Contains($"fetch(id=\"{readId}\")", TextOf(sent), StringComparison.Ordinal);

        // ⚠️ **反向**：原来那串内容**不在**请求里了——否则这条测的就不是「换掉了」。
        Assert.DoesNotContain("原来的内容", TextOf(sent), StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ **没有窗口就不许收**——哪怕源已经被改过（2026-10-17，这条是「回收只发生在
    /// 失效窗口」那个更正的直接证据）：模型第 2 步照常拿到原文。
    ///
    /// <para>⚠️ 缺了这条，「点杀判定在不在」与「回收发生在哪一刻」这两件事就分不开；
    /// 而后者正是这次要修的那个缺陷（渲染每步都在，回收就每步都在 ⇒ 每步破缓存）。</para>
    /// </summary>
    [Fact]
    public async Task NoWindow_MeansTheChangedReadStaysAsItIs()
    {
        File.WriteAllText(Path.Combine(_work, "a.txt"), "原来的内容");

        _adapter.ReplyWith(_ => new ModelReply
        {
            Blocks = [ReadOf("c1", "a.txt")], StopReason = "tool_use", Refused = false,
        });
        await _stepper.StepAsync(_handle, "看一眼", default);

        var readId = _store.ReadTimeline(_handle.ProjectKey, _handle.SessionId)
            .OfType<TimelineEvent.ToolResultEvent>().Single(r => !r.CallId.StartsWith("floating-", StringComparison.Ordinal)).Id;

        File.WriteAllText(Path.Combine(_work, "a.txt"), "换过的内容");

        // ⚠️ **不开窗**。
        _adapter.Reply("看完了。");
        await _stepper.StepAsync(_handle, [], default);

        var sent = _adapter.Calls[^1].Timeline
            .OfType<TimelineEvent.ToolResultEvent>().Single(r => r.Id == readId);

        Assert.DoesNotContain("[已回收]", TextOf(sent), StringComparison.Ordinal);
        Assert.Contains("原来的内容", TextOf(sent), StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ **反向对照，而且是本仓最该防的那个形状**：文件**没动**就**不许收**。
    ///
    /// <para>缺了这条，「点杀」可以退化成「什么都收」而上面那条照样绿——
    /// 而那样它就不是点杀，是换了个名字的距离带。⇒ 两条一起才构成一个判据。</para>
    /// </summary>
    [Fact]
    public async Task ARead_WhoseFileWasNotTouched_StaysAsItIs()
    {
        File.WriteAllText(Path.Combine(_work, "a.txt"), "一直没变");

        _adapter.ReplyWith(_ => new ModelReply
        {
            Blocks = [ReadOf("c1", "a.txt")], StopReason = "tool_use", Refused = false,
        });
        await _stepper.StepAsync(_handle, "看一眼", default);

        var readId = _store.ReadTimeline(_handle.ProjectKey, _handle.SessionId)
            .OfType<TimelineEvent.ToolResultEvent>().Single(r => !r.CallId.StartsWith("floating-", StringComparison.Ordinal)).Id;

        // 世界**没变**，再正经开一次窗——证明「不收」是判据的结果，不是「没开窗」。
        OpenWindow();

        _adapter.Reply("看完了。");
        await _stepper.StepAsync(_handle, [], default);

        var sent = _adapter.Calls[^1].Timeline
            .OfType<TimelineEvent.ToolResultEvent>().Single(r => r.Id == readId);

        Assert.DoesNotContain("[已回收]", TextOf(sent), StringComparison.Ordinal);
        Assert.Contains("一直没变", TextOf(sent), StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ **那条被点杀掉的结果，`fetch` 取得回**——点杀**不许**削弱 §8.0.2 二那条判据。
    ///
    /// <para>判据之间是**并集**，但「可恢复」是**前提**不是并列的一条：注记上写着
    /// 「`fetch(id)` 取回」，而取不回的东西换成注记就是**一句假话**。</para>
    /// </summary>
    [Fact]
    public async Task AStaleResult_IsStillFetchable()
    {
        var original = "原来的内容";
        File.WriteAllText(Path.Combine(_work, "a.txt"), original);

        _adapter.ReplyWith(_ => new ModelReply
        {
            Blocks = [ReadOf("c1", "a.txt")], StopReason = "tool_use", Refused = false,
        });
        await _stepper.StepAsync(_handle, "看一眼", default);

        var readId = _store.ReadTimeline(_handle.ProjectKey, _handle.SessionId)
            .OfType<TimelineEvent.ToolResultEvent>().Single(r => !r.CallId.StartsWith("floating-", StringComparison.Ordinal)).Id;

        File.WriteAllText(Path.Combine(_work, "a.txt"), "换过的内容");

        // 开窗（= `session.gc`）：点杀这一刻解析，a.txt 已变 ⇒ 进窗口。
        OpenWindow();

        _adapter.Reply("嗯。");
        await _stepper.StepAsync(_handle, [], default);

        // 模型照注记发出 `fetch(id)`。
        _adapter.ReplyWith(_ => new ModelReply
        {
            Blocks = [new Block.ToolUseBlock("c9", "fetch",
                System.Text.Json.JsonSerializer.Serialize(new { id = readId }))],
            StopReason = "tool_use", Refused = false,
        });
        await _stepper.StepAsync(_handle, [], default);

        // 再看下一步的请求（`fetch` 的结果在它自己那一步的渲染里当然还没有）。
        _adapter.Reply("好。");
        await _stepper.StepAsync(_handle, [], default);

        var fetched = _adapter.Calls[^1].Timeline
            .OfType<TimelineEvent.ToolResultEvent>().Single(r => r.CallId == "c9");

        Assert.Equal(original, TextOf(fetched));
    }

    /// <summary>
    /// ⚠️ **`list_dir` 的结果永不被点杀**——即使它引用的目录在它之后变了。
    ///
    /// <para>判据是 `00` §5 那条硬约束：**无指纹 ⇒ 永不可优化**。
    /// 而这不是「漏了给它算指纹」——目录清单随任何一个文件的增删就变，
    /// 没有「源」可依赖，它是**不可重演**那一类（`ListTool` 里写着同一条）。</para>
    /// </summary>
    [Fact]
    public async Task AListing_IsNeverStale_EvenThoughTheDirectoryChanged()
    {
        File.WriteAllText(Path.Combine(_work, "a.txt"), "x");

        _adapter.ReplyWith(_ => new ModelReply
        {
            Blocks = [new Block.ToolUseBlock("c1", "ls", "{}")], StopReason = "tool_use", Refused = false,
        });
        await _stepper.StepAsync(_handle, "列一下", default);

        var lsId = _store.ReadTimeline(_handle.ProjectKey, _handle.SessionId)
            .OfType<TimelineEvent.ToolResultEvent>().Single(r => !r.CallId.StartsWith("floating-", StringComparison.Ordinal)).Id;

        // 目录**真的**变了（多了一个文件）——再开窗，证明不收是判据的结果。
        File.WriteAllText(Path.Combine(_work, "b.txt"), "y");
        OpenWindow();

        _adapter.Reply("看到了。");
        await _stepper.StepAsync(_handle, [], default);

        var sent = _adapter.Calls[^1].Timeline
            .OfType<TimelineEvent.ToolResultEvent>().Single(r => r.Id == lsId);

        Assert.DoesNotContain("[已回收]", TextOf(sent), StringComparison.Ordinal);
    }

}
