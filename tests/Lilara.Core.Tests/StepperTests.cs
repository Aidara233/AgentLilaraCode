using Lilara.Core.Adapters;
using Lilara.Core.Canonical;
using Lilara.Core.Persistence;
using Lilara.Core.Records;
using Lilara.Core.Session;
using Lilara.Core.Tests.Fakes;

namespace Lilara.Core.Tests;

public class StepperTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-step-" + Guid.NewGuid().ToString("N"));
    private readonly SessionStore _store;
    private readonly FakeAdapter _adapter = new();
    private readonly Stepper _stepper;

    public StepperTests()
    {
        _store = new SessionStore(_root);
        _stepper = new Stepper(_store, _adapter);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static ModelSelection Model() => new()
    {
        ProviderId = "claude",
        ModelId = "claude-opus-5",
        Protocol = "claude-messages",
        Effort = "high",
        MaxTokens = 16000,
    };

    private SessionHandle NewSession() => SessionHandle.Create(_store, @"C:\work", Model());

    private static string TextOf(TimelineEvent e) =>
        ((Block.TextBlock)((TimelineEvent.MessageEvent)e).Blocks[0]).Text;

    [Fact]
    public async Task Step_AppendsBothTurnsToDisk()
    {
        var handle = NewSession();
        _adapter.Reply("你好！");

        await _stepper.StepAsync(handle, "你好", CancellationToken.None);

        var timeline = _store.ReadTimeline(handle.ProjectKey, handle.SessionId);
        Assert.Equal(2, timeline.Count);
        Assert.Equal(Speaker.User, ((TimelineEvent.MessageEvent)timeline[0]).From);
        Assert.Equal(Speaker.Model, ((TimelineEvent.MessageEvent)timeline[1]).From);
        Assert.Equal("你好", TextOf(timeline[0]));
        Assert.Equal("你好！", TextOf(timeline[1]));
    }

    [Fact]
    public async Task Step_SendsFullCanonicalTimelineEachTime()
    {
        var handle = NewSession();
        _adapter.Reply("一").Reply("二");

        await _stepper.StepAsync(handle, "问一", CancellationToken.None);
        await _stepper.StepAsync(handle, "问二", CancellationToken.None);

        // 第二次调用必须带上完整历史（00 §6：核心永远发全量 canonical）
        Assert.Single(_adapter.Calls[0].Timeline);
        Assert.Equal(3, _adapter.Calls[1].Timeline.Count);
    }

    [Fact]
    public async Task Step_AdvancesTurnCounter()
    {
        var handle = NewSession();
        _adapter.Reply("ok");

        await _stepper.StepAsync(handle, "hi", CancellationToken.None);

        Assert.Equal(1, handle.State.Turn);
        Assert.Equal(1, _store.ReadState(handle.ProjectKey, handle.SessionId).Turn);
    }

    [Fact]
    public async Task Step_DoesNotWriteModelTurnWhenRefused()
    {
        // Review Focus #2：拒答不落空模型消息
        var handle = NewSession();
        _adapter.Reply("", stopReason: "refusal");

        var reply = await _stepper.StepAsync(handle, "hi", CancellationToken.None);

        Assert.True(reply.Refused);
        var timeline = _store.ReadTimeline(handle.ProjectKey, handle.SessionId);
        Assert.Single(timeline); // 只有用户那条
        Assert.Equal(Speaker.User, ((TimelineEvent.MessageEvent)timeline[0]).From);
    }

    [Fact]
    public async Task Step_LeavesHistoryIntactWhenCallFails()
    {
        // Review Focus #3：调用失败时历史保持可解析
        var handle = NewSession();
        _adapter.Throws(new ModelCallFailedException("上游 503"));

        await Assert.ThrowsAsync<ModelCallFailedException>(
            () => _stepper.StepAsync(handle, "hi", CancellationToken.None));

        var timeline = _store.ReadTimeline(handle.ProjectKey, handle.SessionId);
        Assert.Single(timeline);
        Assert.Equal(Speaker.User, ((TimelineEvent.MessageEvent)timeline[0]).From);
        Assert.Equal(0, handle.State.Turn);
    }

    [Fact]
    public async Task Step_PassesEffortAndMaxTokensFromSession()
    {
        var handle = NewSession();
        _adapter.Reply("ok");

        await _stepper.StepAsync(handle, "hi", CancellationToken.None);

        Assert.Equal("high", _adapter.Calls[0].Effort);
        Assert.Equal(16000, _adapter.Calls[0].MaxTokens);
        Assert.Equal("claude-opus-5", _adapter.Calls[0].ModelId);
    }

    [Fact]
    public async Task Step_KeepsSystemBlocksEmpty()
    {
        // 04 §1.3：裸核心没有系统提示词
        var handle = NewSession();
        _adapter.Reply("ok");

        await _stepper.StepAsync(handle, "hi", CancellationToken.None);

        Assert.Empty(_adapter.Calls[0].SystemBlocks);
    }

    [Fact]
    public async Task Step_FlipsStatusToIdleWhenDone()
    {
        var handle = NewSession();
        _adapter.Reply("ok");

        await _stepper.StepAsync(handle, "hi", CancellationToken.None);

        Assert.Equal("Idle", handle.State.Status);
    }

    [Fact]
    public async Task Step_StillAdvancesTurnWhenRefused()
    {
        // ⚠️ 拒答是**成功返回的、完成的一步**，只是没有内容可写。
        // 让它不计数，state.json 里「拒答过一次」与「从没跑过」就无法区分。
        var handle = NewSession();
        _adapter.Reply("", stopReason: "refusal");

        await _stepper.StepAsync(handle, "hi", CancellationToken.None);

        Assert.Equal(1, handle.State.Turn);
        Assert.Equal("Idle", handle.State.Status);
        Assert.Equal(1, _store.ReadState(handle.ProjectKey, handle.SessionId).Turn);
    }
}
