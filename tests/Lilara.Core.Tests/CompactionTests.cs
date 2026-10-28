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
        }).DynamicCache();
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

    [Theory]
    [InlineData("goal", true)]
    [InlineData("iterate", true)]
    [InlineData("standard", false)]
    [InlineData("step", false)]
    public void FloatingContext_ExplainsAutonomousCompaction(string initiative, bool expected)
    {
        var axes = new Dictionary<string, string>(Axes.Defaults()) { [Axes.Initiative] = initiative };
        var text = string.Concat(SystemBlock.Build(null, null, new TailContext(axes))
            .Tail.OfType<Block.TextBlock>().Select(b => b.Text));
        Assert.Equal(expected, text.Contains(CorePrompts.AutonomousCompactionDirective));
    }

    /// <summary>
    /// ⚠️ **越过 900k 之后尾块里是那句命令式的「现在就压」，而且任何主动性都有**
    /// （2026-10-27 用户裁定：无论哪个模型都允许自动压缩）。
    ///
    /// <para>⚠️ 同时钉住「它是<b>替换</b>而不是并列」：goal / iterate 下那句「你可随时自行决定」
    /// 必须**消失**——两句并存时「随时」会把「现在」稀释掉，而这一格没有选择。</para>
    /// </summary>
    [Theory]
    [InlineData("standard")]
    [InlineData("step")]
    [InlineData("goal")]
    [InlineData("iterate")]
    public void FloatingContext_OverLimit_CommandsCompactionInEveryMode(string initiative)
    {
        SetInitiative(initiative);
        var text = TailTextAt(initiative, tokens: ContextUsage.AutoCompactThreshold);

        Assert.Contains(CorePrompts.ContextOverflowDirective, text, StringComparison.Ordinal);
        Assert.DoesNotContain(CorePrompts.AutonomousCompactionDirective, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ **边界下侧仍是老规矩**：899_999 不到线 ⇒ standard 里一句压缩话都没有，
    /// goal / iterate 里只有那句「可以自行决定」。这条挡的是「阈值写成 `>` 变成了 `>=` 以外
    /// 的任意误改」，也是「一直在喊压」的另一种失效形态。
    /// </summary>
    [Theory]
    [InlineData("standard", false)]
    [InlineData("goal", true)]
    public void FloatingContext_BelowLimit_KeepsTheOldRule(string initiative, bool autonomous)
    {
        SetInitiative(initiative);
        var text = TailTextAt(initiative, tokens: ContextUsage.AutoCompactThreshold - 1);

        Assert.DoesNotContain(CorePrompts.ContextOverflowDirective, text, StringComparison.Ordinal);
        Assert.Equal(autonomous, text.Contains(CorePrompts.AutonomousCompactionDirective, StringComparison.Ordinal));
    }

    /// <summary>按「上一次调用报了多少输入」装配一份尾块——走的是生产路径（`TailContext.For`）。</summary>
    private string TailTextAt(string initiative, long tokens)
    {
        var usage = new UsageStore(_store.DataRoot);
        usage.Append(new UsageCall
        {
            CallId = Ids.New(), SessionId = _handle.SessionId, ProviderId = "provider", ModelId = "model",
            Protocol = Protocols.ClaudeMessages, StartedAt = CoreClock.Now(), Status = "completed",
            Usage = new TokenUsage { InputTokens = tokens },
        });
        var axes = new Dictionary<string, string>(_handle.State.Axes!) { [Axes.Initiative] = initiative };
        return string.Concat(SystemBlock.Build(null, null,
                TailContext.For(_handle, axes, plugin: null,
                    new SubagentScope(_handle.SessionId, null, 0, 1, []), usage))
            .Tail.OfType<Block.TextBlock>().Select(b => b.Text));
    }

    [Theory]
    [InlineData(Protocols.ClaudeMessages, 600)]
    [InlineData(Protocols.OpenAiChat, 300)]
    public async Task FloatingContext_ReportsPreviousInputIncludingCache(string protocol, long expected)
    {
        var usage = new UsageStore(_store.DataRoot);
        var adapter = new FakeAdapter().ReplyWith(_ => new ModelReply
        {
            Blocks = [new Block.TextBlock("ok")], StopReason = "end_turn", Refused = false,
            Usage = new TokenUsage { InputTokens = 100, CacheReadTokens = 200, CacheWriteTokens = 300, OutputTokens = 99 },
        });
        usage.Append(new UsageCall
        {
            CallId = "previous", SessionId = _handle.SessionId, ProviderId = "provider", ModelId = "model",
            Protocol = protocol, StartedAt = CoreClock.Now(), Status = "completed",
            Usage = new TokenUsage { InputTokens = 100, CacheReadTokens = 200, CacheWriteTokens = 300, OutputTokens = 99 },
        });
        var stepper = new Stepper(_store, adapter, BuiltInTools.All, usage: usage);
        await stepper.StepAsync(_handle, [], CancellationToken.None);
        var request = adapter.Calls.Single();
        var text = string.Concat(request.Slots.Tail.OfType<Block.TextBlock>().Select(b => b.Text))
            + string.Concat(request.Timeline.OfType<TimelineEvent.ToolResultEvent>()
                .SelectMany(r => r.Content.OfType<Block.TextBlock>()).Select(b => b.Text));
        Assert.Contains($"上次调用上下文总长度：{expected} tokens", text);
    }

    [Fact]
    public void FloatingContext_MissingUsageDoesNotReuseOlderCount()
    {
        var usage = new UsageStore(_store.DataRoot);
        foreach (var known in new[] { true, false })
            usage.Append(new UsageCall
            {
                CallId = Ids.New(), SessionId = _handle.SessionId, ProviderId = "provider", ModelId = "model",
                Protocol = Protocols.ClaudeMessages, StartedAt = CoreClock.Now(), Status = "completed",
                Usage = known ? new TokenUsage { InputTokens = 123 } : null,
            });
        var stepper = new Stepper(_store, new FakeAdapter(), BuiltInTools.All, usage: usage);
        Assert.Contains("未知", stepper.PreviousContextUsage(_handle));
    }

    private void SetInitiative(string initiative) =>
        _handle.UpdateState(s => s with
        {
            Axes = new Dictionary<string, string>(s.Axes!, StringComparer.Ordinal)
            {
                [Axes.Initiative] = initiative,
                [Axes.Permission] = "off",
            },
        });

    [Theory]
    [InlineData("goal")]
    [InlineData("iterate")]
    public async Task CompactTool_AutonomousModes_CanCompactRepeatedlyWithoutRequest(string initiative)
    {
        SetInitiative(initiative);
        var adapter = new FakeAdapter().ReplyWith(_ => Calls(("c1", "compact", Args("第一份摘要"))));
        var first = await NewStepper(adapter).StepAsync(_handle, [], CancellationToken.None);
        Assert.NotNull(first.Compacted);
        Assert.False(_handle.State.PendingCompact);
        Assert.Equal(initiative, _handle.State.Axes![Axes.Initiative]);

        var next = new FakeAdapter().ReplyWith(_ => Calls(("c2", "compact", Args("累积摘要"))));
        var second = await NewStepper(next).StepAsync(_handle, [], CancellationToken.None);
        Assert.NotNull(second.Compacted);
        Assert.Equal(2, new CompactionStore(_store).ReadAll(_handle.ProjectKey, _handle.SessionId).Count);
        Assert.False(_handle.State.PendingCompact);
        var rendered = TimelineRender.Render(_store, _handle);
        Assert.DoesNotContain(rendered.OfType<TimelineEvent.MessageEvent>(), m => m.Id == "user-1");
        Assert.Contains(rendered.OfType<TimelineEvent.MessageEvent>()
            .SelectMany(m => m.Blocks.OfType<Block.ToolUseBlock>()), c => c.CallId == "c2");
    }

    [Theory]
    [InlineData("goal", false, "摘要")]
    [InlineData("iterate", false, "摘要")]
    [InlineData("goal", true, "")]
    [InlineData("iterate", true, "")]
    public async Task CompactTool_AutonomousModes_StillValidateArguments(string initiative, bool requested, string summary)
    {
        SetInitiative(initiative);
        var adapter = new FakeAdapter().ReplyWith(_ => Calls(("c1", "compact", Args(summary, requested))));
        var outcome = await NewStepper(adapter).StepAsync(_handle, [], CancellationToken.None);
        Assert.Null(outcome.Compacted);
        Assert.Null(Latest());
    }

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
    [Theory]
    [InlineData("step")]
    [InlineData("standard")]
    public async Task CompactTool_WithoutRequest_DoesNothing(string initiative)
    {
        SetInitiative(initiative);
        var adapter = new FakeAdapter().ReplyWith(_ => Calls(("c1", "compact", Args("不该生效"))));

        var outcome = await NewStepper(adapter).StepAsync(_handle, [], CancellationToken.None);

        Assert.Null(outcome.Compacted);
        Assert.Null(Latest());
        Assert.False(_handle.State.PendingCompact);
        var result = _store.ReadTimeline(_handle.ProjectKey, _handle.SessionId)
            .OfType<TimelineEvent.ToolResultEvent>()
            .Last(r => !r.CallId.StartsWith("floating-", StringComparison.Ordinal));
        Assert.Contains("没有压缩指令", string.Concat(result.Content.OfType<Block.TextBlock>().Select(b => b.Text)));
        // 投影仍然看得到全部历史。
        Assert.Null(ContextCompaction.Project(_store, _handle).Checkpoint);
    }

    /// <summary>
    /// ⚠️ **越过 900k ⇒ standard 也压得成**（2026-10-27 用户裁定：无论哪个模型、哪个模式都允许）。
    ///
    /// <para>⚠️ 它同时钉住「授权判据与尾块那句警告<b>同源</b>」：尾块刚喊完「现在就压」、
    /// 工具却回一句「没有指令」，是这一格最坏的分叉——模型照着做了，却被自己拒掉。</para>
    /// </summary>
    [Fact]
    public async Task CompactTool_AtOverLimit_CompactsEvenInStandardMode()
    {
        SetInitiative("standard");
        var usage = SeedUsage(ContextUsage.AutoCompactThreshold);
        var adapter = new FakeAdapter().ReplyWith(_ => Calls(("c1", "compact", Args("超限摘要"))));

        var outcome = await new Stepper(_store, adapter, BuiltInTools.All, usage: usage,
            pendingCompactText: CorePrompts.CompactDirective).StepAsync(_handle, [], CancellationToken.None);

        // ⚠️ 这一步自己的回复**没有报用量**（`FakeAdapter` 不带 usage），而超限判据取的是
        // 「最近一次**报过**输入的调用」⇒ 这一格恰好也钉住了那条不对称的取舍：
        // 不能用「最近一次调用没报 ⇒ 未知」把安全带松开。
        Assert.NotNull(outcome.Compacted);
        Assert.Null(outcome.Compacted.Summary);          // 摘要只在参数里（同其他用例）
        Assert.False(_handle.State.PendingCompact);      // 没有指令可清，也没凭空造一条
    }

    /// <summary>
    /// ⚠️ **边界下侧不放行**：少一个 token 就不算「必须压」，standard 仍要用户下发指令。
    /// 没有这条，把阈值写成 0 也会绿。
    /// </summary>
    [Fact]
    public async Task CompactTool_BelowLimit_StandardMode_IsStillRefused()
    {
        SetInitiative("standard");
        var usage = SeedUsage(ContextUsage.AutoCompactThreshold - 1);
        var adapter = new FakeAdapter().ReplyWith(_ => Calls(("c1", "compact", Args("不该生效"))));

        var outcome = await new Stepper(_store, adapter, BuiltInTools.All, usage: usage,
            pendingCompactText: CorePrompts.CompactDirective).StepAsync(_handle, [], CancellationToken.None);

        Assert.Null(outcome.Compacted);
        Assert.Null(Latest());
        var result = _store.ReadTimeline(_handle.ProjectKey, _handle.SessionId)
            .OfType<TimelineEvent.ToolResultEvent>()
            .Last(r => !r.CallId.StartsWith("floating-", StringComparison.Ordinal));
        Assert.Contains("没有压缩指令", string.Concat(result.Content.OfType<Block.TextBlock>().Select(b => b.Text)));
    }

    /// <summary>按「上一次调用报了多少输入」造一条账本行——超限判据读的就是它。</summary>
    private UsageStore SeedUsage(long inputTokens)
    {
        var usage = new UsageStore(_store.DataRoot);
        usage.Append(new UsageCall
        {
            CallId = Ids.New(), SessionId = _handle.SessionId, ProviderId = "provider", ModelId = "model",
            Protocol = Protocols.ClaudeMessages, StartedAt = CoreClock.Now(), Status = "completed",
            Usage = new TokenUsage { InputTokens = inputTokens },
        });
        return usage;
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
        Assert.Equal(2, timeline.OfType<TimelineEvent.ToolResultEvent>()
            .Count(r => !r.CallId.StartsWith("floating-", StringComparison.Ordinal)));

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
