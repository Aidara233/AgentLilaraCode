using Lilara.Core.Kernel;
using Lilara.Core.Protocol;
using Lilara.Core.Session;
using Lilara.Core.Session.Tools;
using Lilara.Core.Storage;
using Lilara.Core.Tests.Fakes;

namespace Lilara.Core.Tests;

// 压缩 = 用户下发指令（状态）+ 模型调用 `compact`（工具）+ 推进器落检查点（2026-10-22）。
// ⚠️ 旧的 `Stepper.CompactAsync`（核心自己跑一次模型调用、摘要取回复正文）已经删掉了；
// 它要求的「回复必须是纯文本」在思考模型上必错（真实账本：会话 `6185a86fd329`）。
public sealed class CompactionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lilara-compaction-" + Guid.NewGuid().ToString("N"));
    private readonly SessionStore _store;
    private readonly SessionHandle _handle;

    public CompactionTests()
    {
        _store = new SessionStore(Path.Combine(_root, "data"));
        Directory.CreateDirectory(Path.Combine(_root, "work"));
        _handle = SessionHandle.Create(_store, Path.Combine(_root, "work"), new ModelSelection
        {
            ProviderId = "provider", ModelId = "model", Protocol = Protocols.ClaudeMessages,
            Effort = "high", MaxTokens = 1000,
        });
        _store.AppendEvent(_handle.ProjectKey, _handle.SessionId, new TimelineEvent.MessageEvent
        {
            Id = "user-1", At = CoreClock.Now(), From = Speaker.User,
            Blocks = [new Block.TextBlock("请记住这个目标")],
        });
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // ⚠️ 用 `Json.Options` 序列化：默认的 `JsonSerializer.Serialize(string)` 会把中文转成 `\uXXXX`，
    // 而真实上游发的是原样 UTF-8（参数逐字落盘，`01` §9）——用默认编码的测试会「绿着一个假的形状」。
    private static string Args(string summary, bool requested = true) =>
        $$"""{"requested":{{(requested ? "true" : "false")}},"summary":{{System.Text.Json.JsonSerializer.Serialize(summary, Json.Options)}}}""";

    private static ModelReply Calls(params (string CallId, string Name, string Args)[] calls) => new()
    {
        Blocks = calls.Select(c => (Block)new Block.ToolUseBlock(c.CallId, c.Name, c.Args)).ToList(),
        StopReason = "tool_use",
        Refused = false,
    };

    private Stepper NewStepper(FakeAdapter adapter) =>
        new(_store, adapter, BuiltInTools.All, pendingCompactText: CorePrompts.CompactDirective);

    private void RequestCompaction() =>
        _handle.UpdateStateAndPersist(_store, s => s with { PendingCompact = true }, diskStatus: SessionStatus.Idle);

    private CompactionCheckpoint? Latest() =>
        new CompactionStore(_store).ReadAll(_handle.ProjectKey, _handle.SessionId).LastOrDefault();

    // 指令 + 一个普通的 compact 调用 ⇒ 检查点落地；边界是本步之前最后一条；
    // 摘要只在参数里（检查点行上没有 summary 键）。
    [Fact]
    public async Task CompactTool_WithRequest_LandsCheckpoint_WhoseBoundaryIsTheStepBefore()
    {
        RequestCompaction();
        var adapter = new FakeAdapter().ReplyWith(_ => Calls(("c1", "compact", Args("目标：完成压缩闭环"))));

        var outcome = await NewStepper(adapter).StepAsync(_handle, [], CancellationToken.None);

        var checkpoint = Assert.IsType<CompactionCheckpoint>(outcome.Compacted);
        Assert.Equal(checkpoint, Latest());   // 值相等：落盘那份读回来是另一个实例
        // 边界 = 本步之前最后一条（`user-1`）——不是那条 compact 调用本身。
        Assert.Equal("user-1", checkpoint.CoveredThroughEventId);
        Assert.Equal("c1", checkpoint.SourceCallId);
        // 摘要只在参数里：检查点行上没有那一格（`WhenWritingNull` ⇒ 键不出现）。
        Assert.Null(checkpoint.Summary);
        // 指令被消费。
        Assert.False(_handle.State.PendingCompact);
        Assert.DoesNotContain("summary", File.ReadAllText(Path.Combine(
            _store.SessionDirectory(_handle.ProjectKey, _handle.SessionId), "compaction.jsonl")));
    }

    // 没有指令 ⇒ 一句人话、什么都不做：没有检查点、历史没被切、指令状态不变。
    [Fact]
    public async Task CompactTool_WithoutRequest_DoesNothing()
    {
        var adapter = new FakeAdapter().ReplyWith(_ => Calls(("c1", "compact", Args("不该生效"))));

        var outcome = await NewStepper(adapter).StepAsync(_handle, [], CancellationToken.None);

        Assert.Null(outcome.Compacted);
        Assert.Null(Latest());
        Assert.False(_handle.State.PendingCompact);
        var result = _store.ReadTimeline(_handle.ProjectKey, _handle.SessionId)
            .OfType<TimelineEvent.ToolResultEvent>().Last();
        Assert.Contains("没有压缩指令", string.Concat(result.Content.OfType<Block.TextBlock>().Select(b => b.Text)));
        // 投影仍然看得到全部历史。
        Assert.Null(ContextCompaction.Project(_store, _handle).Checkpoint);
    }

    // `requested: false` ⇒ 工具拒绝——这是「误触发」的显式闸，而摘要顶掉的历史不可逆。
    [Fact]
    public async Task CompactTool_RequestedFalse_IsRefused()
    {
        RequestCompaction();
        var adapter = new FakeAdapter().ReplyWith(_ => Calls(("c1", "compact", Args("x", requested: false))));

        var outcome = await NewStepper(adapter).StepAsync(_handle, [], CancellationToken.None);

        Assert.Null(outcome.Compacted);
        Assert.Null(Latest());
        Assert.True(_handle.State.PendingCompact);   // 指令还在：下一步还能再来
    }

    // 摘要超长 ⇒ 同样不压、指令保留（上限与 schema 的 maxLength 是同一个常量）。
    [Fact]
    public async Task CompactTool_OverlongSummary_IsRefused_AndRequestSurvives()
    {
        RequestCompaction();
        var longSummary = new string('x', CompactTool.MaxSummaryChars + 1);
        var adapter = new FakeAdapter().ReplyWith(_ => Calls(("c1", "compact", Args(longSummary))));

        var outcome = await NewStepper(adapter).StepAsync(_handle, [], CancellationToken.None);

        Assert.Null(outcome.Compacted);
        Assert.True(_handle.State.PendingCompact);
    }

    // 同一批两条 compact：全保留（记录里两条结果都在），检查点只有**最后一条**——
    // 覆盖边界含第一条调用，所以那条摘要也在保留区里。
    [Fact]
    public async Task TwoCompactCalls_KeepBothResults_AndCheckpointUsesTheLast()
    {
        RequestCompaction();
        var adapter = new FakeAdapter().ReplyWith(_ => Calls(
            ("c1", "compact", Args("第一份草稿")),
            ("c2", "compact", Args("第二份定稿"))));

        var outcome = await NewStepper(adapter).StepAsync(_handle, [], CancellationToken.None);

        Assert.Equal("c2", Assert.IsType<CompactionCheckpoint>(outcome.Compacted).SourceCallId);
        Assert.Single(new CompactionStore(_store).ReadAll(_handle.ProjectKey, _handle.SessionId));

        var timeline = _store.ReadTimeline(_handle.ProjectKey, _handle.SessionId);
        Assert.Equal(2, timeline.OfType<TimelineEvent.ToolResultEvent>().Count());

        // 第一步（边界 = `user-1`）只覆盖那条用户消息；两条调用都在保留区里。
        var suffix = ContextCompaction.Project(_store, _handle).Suffix;
        var calls = suffix.OfType<TimelineEvent.MessageEvent>()
            .SelectMany(m => m.Blocks.OfType<Block.ToolUseBlock>()).Select(c => c.CallId).ToList();
        Assert.Contains("c1", calls);
        Assert.Contains("c2", calls);
    }

    // 摘要住在参数里 ⇒ **不再插**那条合成的「历史摘要」用户消息（同一段话说两遍就是这么来的）。
    [Fact]
    public async Task Render_DoesNotInsertTheSyntheticSummary_WhenTheCallSurvives()
    {
        RequestCompaction();
        var adapter = new FakeAdapter().ReplyWith(_ => Calls(("c1", "compact", Args("目标：完成压缩闭环"))));
        await NewStepper(adapter).StepAsync(_handle, [], CancellationToken.None);

        var rendered = TimelineRender.Render(_store, _handle);

        Assert.DoesNotContain(rendered.OfType<TimelineEvent.MessageEvent>(),
            m => m.Blocks.OfType<Block.TextBlock>().Any(b => b.Text.Contains("[lilara 历史摘要]")));
        // 摘要在那条调用的参数里、模型看得见。
        Assert.Contains(rendered.OfType<TimelineEvent.MessageEvent>()
                .SelectMany(m => m.Blocks.OfType<Block.ToolUseBlock>()),
            c => c.Name == CompactTool.NameOf && c.Arguments.Contains("目标：完成压缩闭环"));
    }

    // 旧检查点（那一行里还带着 `summary`、记录里没有对应的 compact 调用）⇒ 前置摘要消息照旧插入。
    [Fact]
    public void Render_StillInsertsTheSyntheticSummary_ForALegacyCheckpoint()
    {
        new CompactionStore(_store).Append(_handle.ProjectKey, _handle.SessionId, new CompactionCheckpoint
        {
            CheckpointId = "old",
            CoveredThroughEventId = "user-1",
            HistoryDigest = CompactionStore.ComputeDigest(
                _store.ReadTimeline(_handle.ProjectKey, _handle.SessionId)),
            Summary = "这是旧版的摘要",
            CreatedAt = CoreClock.Now(),
            SourceCallId = "gone",
        });

        var rendered = TimelineRender.Render(_store, _handle);

        var summary = Assert.Single(rendered.OfType<TimelineEvent.MessageEvent>(),
            m => m.Blocks.OfType<Block.TextBlock>().Any(b => b.Text.Contains("[lilara 历史摘要]")));
        Assert.Contains("这是旧版的摘要",
            string.Concat(summary.Blocks.OfType<Block.TextBlock>().Select(b => b.Text)));
    }

    // 指令是**持久状态**：写盘、重新载入之后还在（核心崩在「下发之后、调用之前」的那一格）。
    [Fact]
    public void PendingCompact_SurvivesAReload()
    {
        RequestCompaction();

        var reloaded = SessionHandle.Load(_store, _handle.ProjectKey, _handle.SessionId);

        Assert.True(reloaded.State.PendingCompact);
    }

    // **`off` 档也压得成**（2026-10-22 用户裁定）：压缩是底层机制，闸门拦它只会造出
    // 「连这次会话本身都会撑爆」的那一格。⚠️ 同一步里 `read` 照样被 `off` 拒——
    // 放行的是**这个工具**，不是整张工具表。
    [Fact]
    public async Task CompactTool_RunsEvenAtPermissionOff()
    {
        RequestCompaction();
        _handle.UpdateState(s => s with
        {
            Axes = new Dictionary<string, string>(s.Axes!, StringComparer.Ordinal) { [Axes.Permission] = "off" },
        });
        var adapter = new FakeAdapter().ReplyWith(_ => Calls(
            ("r1", "read", """{"path":"x.txt"}"""),
            ("c1", "compact", Args("摘要"))));

        var outcome = await NewStepper(adapter).StepAsync(_handle, [], CancellationToken.None);

        Assert.NotNull(outcome.Compacted);
        // `read` 被拒：结果里是闸门的拒绝理由，不是「文件不存在」。
        var readResult = _store.ReadTimeline(_handle.ProjectKey, _handle.SessionId)
            .OfType<TimelineEvent.ToolResultEvent>().First(r => r.CallId == "r1");
        Assert.Contains("被拒绝", string.Concat(readResult.Content.OfType<Block.TextBlock>().Select(b => b.Text)));
    }

    // 压缩窗口照样开（`ReclaimEvent`），而它落在检查点之后、不参与投影。
    [Fact]
    public async Task Compaction_StillOpensAReclaimWindow()
    {
        RequestCompaction();
        var adapter = new FakeAdapter().ReplyWith(_ => Calls(("c1", "compact", Args("摘要"))));
        await NewStepper(adapter).StepAsync(_handle, [], CancellationToken.None);

        var timeline = _store.ReadTimeline(_handle.ProjectKey, _handle.SessionId);
        Assert.Equal("compaction", timeline.OfType<ReclaimEvent>().Last().Trigger);
        // 投影**滤掉**窗口记录：它是记账，不是被压掉的历史的一部分（也不是保留区里的正文）。
        Assert.Empty(ContextCompaction.Project(_store, _handle).Suffix.OfType<ReclaimEvent>());
    }

    // 压缩**不**是单独记账的调用：账本里那一行是普通步（`purpose: session`），
    // 而「压缩花了多少」从那条 `compact` 调用反推——不再有 `purpose: compaction`。
    [Fact]
    public async Task Compaction_IsRecordedAsAPlainStep()
    {
        RequestCompaction();
        var usage = new UsageStore(Path.Combine(_root, "data"));
        var adapter = new FakeAdapter().ReplyWith(_ => Calls(("c1", "compact", Args("摘要"))));
        await new Stepper(_store, adapter, BuiltInTools.All, usage: usage,
            pendingCompactText: CorePrompts.CompactDirective).StepAsync(_handle, [], CancellationToken.None);

        var call = Assert.Single(usage.ForSession(_handle.SessionId));
        Assert.Equal("session", call.Purpose);
    }
}
