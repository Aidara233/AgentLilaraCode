using Lilara.Core.Protocol;
using Lilara.Core.Kernel;
using Lilara.Core.Storage;
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
        Protocol = Protocols.ClaudeMessages,
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

        // 第二次调用必须带上完整历史（核心永远发全量 canonical）。
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
        // 拒答不落空模型消息。
        var handle = NewSession();
        _adapter.Reply("", stopReason: "refusal");

        var reply = await _stepper.StepAsync(handle, "hi", CancellationToken.None);

        Assert.True(reply.Reply.Refused);
        var timeline = _store.ReadTimeline(handle.ProjectKey, handle.SessionId);
        Assert.Single(timeline); // 只有用户那条
        Assert.Equal(Speaker.User, ((TimelineEvent.MessageEvent)timeline[0]).From);
    }

    [Fact]
    public async Task Step_LeavesHistoryIntactWhenCallFails()
    {
        // 调用失败时历史保持可解析。
        var handle = NewSession();
        _adapter.Throws(new ModelCallFailedException("上游 503"));

        await Assert.ThrowsAsync<ModelCallFailedException>(
            () => _stepper.StepAsync(handle, "hi", CancellationToken.None));

        var timeline = _store.ReadTimeline(handle.ProjectKey, handle.SessionId);
        Assert.Single(timeline);
        Assert.Equal(Speaker.User, ((TimelineEvent.MessageEvent)timeline[0]).From);
        Assert.Equal(0, handle.State.Turn);
        // 内存断言不够——只断言内存会让磁盘上的偏差隐形。
        Assert.Equal(0, _store.ReadState(handle.ProjectKey, handle.SessionId).Turn);
    }

    // 内存里的 Status 也必须回到落盘的那份，否则用户看着一步失败、status 却显示 Running。
    [Fact]
    public async Task Step_RollsBackInMemoryStatusWhenCallFails()
    {
        var handle = NewSession();
        _adapter.Throws(new ModelCallFailedException("上游 503"));

        await Assert.ThrowsAsync<ModelCallFailedException>(
            () => _stepper.StepAsync(handle, "hi", CancellationToken.None));

        Assert.Equal("Idle", handle.State.Status);
        // 内存 == 落盘。
        Assert.Equal(
            _store.ReadState(handle.ProjectKey, handle.SessionId).Status,
            handle.State.Status);
    }

    // 推理块随助手回合一起落盘，顺序与签名原样；内存里对了不等于磁盘上对了。
    [Fact]
    public async Task Step_PersistsReasoningBlocksAlongsideText()
    {
        var handle = NewSession();
        _adapter.ReplyBlocks(
            new Block.ReasoningBlock("sig-==", "想了一会儿"),
            new Block.TextBlock("答案"));

        await _stepper.StepAsync(handle, "问", CancellationToken.None);

        var timeline = _store.ReadTimeline(handle.ProjectKey, handle.SessionId);
        var blocks = ((TimelineEvent.MessageEvent)timeline[1]).Blocks;

        Assert.Equal(2, blocks.Count);
        Assert.Equal(new Block.ReasoningBlock("sig-==", "想了一会儿"), blocks[0]);
        Assert.Equal(new Block.TextBlock("答案"), blocks[1]);
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

    // 协议的单一真相来源是适配器，不是会话 spec（建会话时的快照）——分叉会让
    // ReasoningPolicy 按快照判断，在唯一能承载推理的链路上把推理静默丢掉。
    [Fact]
    public async Task Step_UsesTheAdaptersProtocolNotTheSessionSpecSnapshot()
    {
        var handle = SessionHandle.Create(_store, @"C:\work", Model() with
        {
            Protocol = Protocols.OpenAiChat,   // 快照：建会话时是这个
        });
        _adapter.Protocol = Protocols.ClaudeMessages;   // 适配器：现在实际是这个
        _adapter.Reply("ok");

        await _stepper.StepAsync(handle, "hi", CancellationToken.None);

        Assert.Equal(Protocols.ClaudeMessages, _adapter.Calls[0].Protocol);
    }

    [Fact]
    public async Task Step_SendsTheBuiltInSystemPrompt_WhenNoPresetConfigured()
    {
        // ⚠️ **这条 2026-10-07 反过来了**：它原先叫 `Step_KeepsSystemBlocksEmpty`，
        // 钉的是「核心自带零内容」——那时系统块确实是空的（`SystemBlocks = []` 是硬编码的）。
        // 预设批之后它**不再空**：没有预设文件时回落 `CorePrompts.SystemFallback`，
        // 而那句话正是「回收是正常会发生的」——修模型自证的唯一一样东西。
        // ⇒ 保留这条的**位置与形状**（步的产物里确实带着系统块），但期望值反过来。
        var handle = NewSession();
        _adapter.Reply("ok");

        await _stepper.StepAsync(handle, "hi", CancellationToken.None);

        var text = Assert.IsType<Block.TextBlock>(Assert.Single(_adapter.Calls[0].SystemBlocks));
        Assert.Equal(CorePrompts.SystemFallback, text.Text);
    }

    [Fact]
    public async Task Step_FlipsStatusToIdleWhenDone()
    {
        var handle = NewSession();
        _adapter.Reply("ok");

        await _stepper.StepAsync(handle, "hi", CancellationToken.None);

        Assert.Equal("Idle", handle.State.Status);
        // 内存断言不够——只断言内存会让磁盘上的偏差隐形。
        Assert.Equal("Idle", _store.ReadState(handle.ProjectKey, handle.SessionId).Status);
    }

    [Fact]
    public async Task Step_StillAdvancesTurnWhenRefused()
    {
        // 拒答是成功返回的、完成的一步，只是没有内容可写；不计数则「拒答过一次」与「从没跑过」无法区分。
        var handle = NewSession();
        _adapter.Reply("", stopReason: "refusal");

        await _stepper.StepAsync(handle, "hi", CancellationToken.None);

        Assert.Equal(1, handle.State.Turn);
        Assert.Equal("Idle", handle.State.Status);
        Assert.Equal(1, _store.ReadState(handle.ProjectKey, handle.SessionId).Turn);
    }

    // ---- 温度：轴 → 请求 的那个**接缝**在本文件里验 ----
    // ⚠️ 这里断的是**真的 Stepper** 交出去的 `ModelRequest`（`FakeAdapter.Calls`），
    // 不是构造器的纯函数——「构造器会写 temperature」与「Stepper 真的把轴值交过去了」
    // 是两件事，中间那一跳（`Axes.NumberOrNull` + 轴名）正是最容易漏的地方。

    // 默认温度 1.0（两家 API 各自的默认）⇒ 值也是 1.0（不是 null——轴**总是**有值，
    // 「不发」的决定发生在构造器那一层，见 `Build_WithoutATemperature_OmitsTheKeyEntirely`）。
    [Fact]
    public async Task Step_PassesTheTemperatureAxisIntoTheRequest()
    {
        var handle = NewSession();
        _adapter.Reply("ok");

        await _stepper.StepAsync(handle, "hi", CancellationToken.None);

        Assert.Equal(1.0, _adapter.Calls[0].Temperature);
    }

    // 改轴之后**下一次**请求必须带上新值——这条才是「设了有用」的证明。
    [Fact]
    public async Task Step_AfterSetAxis_SendsTheNewTemperature()
    {
        var handle = NewSession();

        handle.UpdateStateAndPersist(_store, s => s with
        {
            Axes = new Dictionary<string, string>(s.Axes!, StringComparer.Ordinal)
            {
                [Axes.Temperature] = "0.3",
            },
        }, diskStatus: SessionStatus.Idle);

        _adapter.Reply("ok");
        await _stepper.StepAsync(handle, "hi", CancellationToken.None);

        Assert.Equal(0.3, _adapter.Calls[0].Temperature);
    }

    // ⚠️ 轴被写坏（手改 `state.json`）时**不发**那个字段，而不是发个 NaN 或抛异常。
    // `Axes.NumberOrNull` 对越界/非数字一律给 null，于是这一格退化成「没设过温度」——
    // 那是对的方向（漏发 ≠ 发个错值）。
    [Fact]
    public async Task Step_WithAGarbageTemperatureAxis_OmitsItInsteadOfThrowing()
    {
        var handle = NewSession();

        handle.UpdateStateAndPersist(_store, s => s with
        {
            Axes = new Dictionary<string, string>(s.Axes!, StringComparer.Ordinal)
            {
                [Axes.Temperature] = "NaN",
            },
        }, diskStatus: SessionStatus.Idle);

        _adapter.Reply("ok");
        await _stepper.StepAsync(handle, "hi", CancellationToken.None);

        Assert.Null(_adapter.Calls[0].Temperature);
    }
}
