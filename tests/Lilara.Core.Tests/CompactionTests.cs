using Lilara.Core.Kernel;
using Lilara.Core.Protocol;
using Lilara.Core.Session;
using Lilara.Core.Session.Tools;
using Lilara.Core.Storage;
using Lilara.Core.Tests.Fakes;

namespace Lilara.Core.Tests;

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

    [Fact]
    public async Task Compact_AppendsCheckpoint_AndProjectionSurvivesRestart()
    {
        var adapter = new FakeAdapter().ReplyWith(_ => new ModelReply
        {
            Blocks = [new Block.TextBlock("目标：完成压缩闭环")],
            StopReason = "end_turn", Refused = false,
            Usage = new TokenUsage { InputTokens = 20, OutputTokens = 6, TotalTokens = 26 },
        });
        var usage = new UsageStore(Path.Combine(_root, "data"));
        var result = await new Stepper(_store, adapter, usage: usage).CompactAsync(_handle, CancellationToken.None);

        Assert.Equal("目标：完成压缩闭环", result.Summary);
        Assert.Single(new CompactionStore(_store).ReadAll(_handle.ProjectKey, _handle.SessionId));
        // ⚠️ 压缩会追一条窗口记录（2026-10-17）：压缩立即生效 ⇒ 这个窗口里解析一次回收。
        // 时间线因此是「用户消息 + 窗口记录」两条——记录本身一字未动。
        var timeline = _store.ReadTimeline(_handle.ProjectKey, _handle.SessionId);
        Assert.Equal(2, timeline.Count);
        Assert.IsType<TimelineEvent.MessageEvent>(timeline[0]);
        Assert.Equal("compaction", Assert.IsType<ReclaimEvent>(timeline[1]).Trigger);
        Assert.Equal("compaction", Assert.Single(usage.ForSession(_handle.SessionId)).Purpose);

        var restarted = new SessionHandle
        {
            Spec = _handle.Spec, State = _handle.State, ProjectKey = _handle.ProjectKey,
            Directory = _handle.Directory,
        };
        var projection = ContextCompaction.Project(_store, restarted);
        Assert.Equal("目标：完成压缩闭环", projection.Checkpoint?.Summary);
        // ⚠️ 压缩自己落的窗口记录是**记账**、不是被压掉的历史的一部分
        // （摘要只总结到 `CoveredThroughEventId`）⇒ 投影把它滤掉，正文为空。
        Assert.Empty(projection.Suffix);
    }

    [Fact]
    public async Task Compact_InvalidReply_IsFailedAndDoesNotCreateCheckpoint()
    {
        var adapter = new FakeAdapter().ReplyBlocks(new Block.ReasoningBlock(null, "internal"));
        var usage = new UsageStore(Path.Combine(_root, "data"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new Stepper(_store, adapter, usage: usage).CompactAsync(_handle, CancellationToken.None));

        var call = Assert.Single(usage.ForSession(_handle.SessionId));
        Assert.Equal("compaction", call.Purpose);
        Assert.Equal("failed", call.Status);
        Assert.Empty(new CompactionStore(_store).ReadAll(_handle.ProjectKey, _handle.SessionId));
        Assert.Single(_store.ReadTimeline(_handle.ProjectKey, _handle.SessionId));
    }
}

// ---- 压缩也走同一条重试路（RetryPolicy）：被限流不该让一次压缩直接失败 ----
public sealed class CompactionRetryTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-compaction-retry-" + Guid.NewGuid().ToString("N"));
    private readonly SessionStore _store;
    private readonly SessionHandle _handle;

    public CompactionRetryTests()
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

    private static ModelReply Summary() => new()
    {
        Blocks = [new Block.TextBlock("目标：压缩成功")],
        StopReason = "end_turn", Refused = false,
        Usage = new TokenUsage { InputTokens = 20, OutputTokens = 6 },
    };

    // 一次 503 之后成功：压缩**完成**，账本里两行（failed + completed(attempt=2)）。
    // ⚠️ 一次就成的压缩仍然只落一行（出口互斥：成功那行只在四个终端出口之一被写）。
    [Fact]
    public async Task TransientFailure_IsRetried_ThenCompactionCompletes()
    {
        var adapter = new FakeAdapter();
        adapter.Throws(new ModelCallFailedException("上游 503") { Transient = true });
        adapter.ReplyWith(_ => Summary());

        var usage = new UsageStore(Path.Combine(_root, "data"));
        var stepper = new Stepper(_store, adapter, usage: usage,
            retryDelay: static (_, _) => Task.CompletedTask);

        var result = await stepper.CompactAsync(_handle, CancellationToken.None);

        Assert.Equal("目标：压缩成功", result.Summary);
        Assert.Equal(2, adapter.Calls.Count);

        var calls = usage.ForSession(_handle.SessionId);
        Assert.Equal(2, calls.Count);
        Assert.All(calls, c => Assert.Equal("compaction", c.Purpose));
        Assert.Equal("failed", calls[0].Status);
        Assert.Contains("上游 503", calls[0].Error);
        Assert.Equal("completed", calls[1].Status);
        Assert.Equal(2, calls[1].Attempt);
    }

    // 「回了但回错了」（不是纯文本摘要）仍是 `failed`——重试解决的是**网**，不是**内容**：
    // 所以它**不**走重试循环（`FakeAdapter.Throws` 没有打瞬态标），只落一行账。
    [Fact]
    public async Task NonTextSummary_IsNotRetried_AndIsRecordedOnce()
    {
        var adapter = new FakeAdapter();
        adapter.ReplyBlocks(new Block.ToolUseBlock("c1", "read", "{}"));

        var usage = new UsageStore(Path.Combine(_root, "data"));
        var stepper = new Stepper(_store, adapter, usage: usage,
            retryDelay: static (_, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            stepper.CompactAsync(_handle, CancellationToken.None));

        Assert.Single(adapter.Calls);
        var call = Assert.Single(usage.ForSession(_handle.SessionId));
        Assert.Equal("failed", call.Status);
        Assert.Null(call.Attempt);
    }
}
