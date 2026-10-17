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
