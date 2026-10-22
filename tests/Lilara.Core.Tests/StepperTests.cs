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

        var text = Assert.IsType<Block.TextBlock>(Assert.Single(_adapter.Calls[0].Slots.System));
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

// ---- 重试（RetryPolicy）：步的语义是「一次成功 = 一步」，失败只在账本里留痕 ----
//
// ⚠️ `Stepper` 自己调 `RetryPolicy.CallAsync`（见 `StepAsync`），而**适配器本身不重试**
// （`AdapterFactory` 的注释写了为什么不能做成包装层：两个调用点各套一层 = 121 次）。
public class StepperRetryTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-retrystep-" + Guid.NewGuid().ToString("N"));
    private readonly SessionStore _store;
    private readonly UsageStore _usage;
    private readonly FakeAdapter _adapter = new();

    public StepperRetryTests()
    {
        _store = new SessionStore(_root);
        _usage = new UsageStore(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private Stepper Stepper() => new(_store, _adapter, usage: _usage,
        retryDelay: static (_, _) => Task.CompletedTask);

    private static ModelReply Pong(string text = "好") => new()
    {
        Blocks = [new Block.TextBlock(text)],
        StopReason = "end_turn",
        Refused = false,
        Usage = new TokenUsage { InputTokens = 10, OutputTokens = 2 },
    };

    private static ModelCallFailedException Transient(string why = "上游 503") =>
        new(why) { Transient = true };

    private SessionHandle NewSession() => SessionHandle.Create(_store, @"C:\work", new ModelSelection
    {
        ProviderId = "claude",
        ModelId = "claude-opus-5",
        Protocol = Protocols.ClaudeMessages,
        Effort = "high",
        MaxTokens = 16000,
    });

    // 一次失败之后成功：**记录里只有一步**（用户 + 模型两条消息），Turn 只 +1。
    // ⚠️ 这正是「重试在适配器里、不在步上」的可观测后果：要是把失败抛到步上再重跑，
    // 这里会看到 Turn 抖动 / 账本里两条 completed，而两者的区别在盘上分得出来。
    [Fact]
    public async Task TransientFailure_RetriedInsideTheStep_LeavesExactlyOneTurn()
    {
        var handle = NewSession();
        _adapter.Throws(Transient()).Reply("好");

        var outcome = await Stepper().StepAsync(handle, "hi", CancellationToken.None);

        Assert.Equal("好", ((Block.TextBlock)outcome.Reply.Blocks[0]).Text);
        Assert.Equal(2, _adapter.Calls.Count);            // 两次真实请求
        Assert.Equal(1, handle.State.Turn);
        Assert.Equal(1, _store.ReadState(handle.ProjectKey, handle.SessionId).Turn);

        var timeline = _store.ReadTimeline(handle.ProjectKey, handle.SessionId);
        Assert.Equal(2, timeline.Count);                  // 用户 + 模型，**没有多出来一条**
    }

    // 账本：一次失败一行（failed，带原因）+ 成功一行（completed，带次数）；同一个 callId。
    [Fact]
    public async Task TransientFailure_RecordsEveryAttemptInTheLedger()
    {
        var handle = NewSession();
        _adapter.Throws(Transient("第一次 503")).ReplyWith(_ => Pong());

        await Stepper().StepAsync(handle, "hi", CancellationToken.None);

        var calls = _usage.ForSession(handle.SessionId);
        Assert.Equal(2, calls.Count);
        Assert.Single(calls.Select(c => c.CallId).Distinct());

        Assert.Equal("failed", calls[0].Status);
        Assert.Null(calls[0].Attempt);                     // 第一次请求不带这个键（缺 = 1）
        Assert.Contains("第一次 503", calls[0].Error);
        Assert.Null(calls[0].Usage);

        Assert.Equal("completed", calls[1].Status);
        Assert.Equal(2, calls[1].Attempt);
        Assert.Null(calls[1].Error);
        Assert.Equal(10, calls[1].Usage!.InputTokens);
    }

    // 一次就成：**账本与加这个功能之前逐字节相同**（不带 attempt / error 两个键）——
    // 格式版本不必 +1 的凭据就是这一条。
    [Fact]
    public async Task FirstTrySuccess_WritesTheSameShapeAsBefore()
    {
        var handle = NewSession();
        _adapter.ReplyWith(_ => Pong());

        await Stepper().StepAsync(handle, "hi", CancellationToken.None);

        var call = Assert.Single(_usage.ForSession(handle.SessionId));
        Assert.Equal("completed", call.Status);
        Assert.Null(call.Attempt);
        Assert.Null(call.Error);

        var line = File.ReadAllLines(_usage.Path).Single();
        Assert.DoesNotContain("\"attempt\"", line);
        Assert.DoesNotContain("\"error\"", line);
    }

    [Fact]
    public async Task NonTransientFailure_IsNotRetried_AndDiskStaysClean()
    {
        var handle = NewSession();
        _adapter.Throws(new ModelCallFailedException("请求被上游拒绝（400）"));

        await Assert.ThrowsAsync<ModelCallFailedException>(() =>
            Stepper().StepAsync(handle, "hi", CancellationToken.None));

        Assert.Single(_adapter.Calls);
        Assert.Single(_store.ReadTimeline(handle.ProjectKey, handle.SessionId));   // 只有用户那条
        Assert.Equal(0, handle.State.Turn);
        var call = Assert.Single(_usage.ForSession(handle.SessionId));
        Assert.Equal("failed", call.Status);
        Assert.Null(call.Attempt);                       // 一次都没重试过：缺键 = 1
    }

    // 十次重试用尽：**11 次请求**、句子里说清试过了、步没完成。
    // ⚠️ 这是用户裁定那条「最大重试十次」唯一的端到端证据。
    [Fact]
    public async Task RetriesExhausted_FailsTheStepAfterElevenCalls()
    {
        var handle = NewSession();
        for (var i = 0; i <= RetryPolicy.MaxRetries + 3; i++) _adapter.Throws(Transient());

        var ex = await Assert.ThrowsAsync<ModelCallFailedException>(() =>
            Stepper().StepAsync(handle, "hi", CancellationToken.None));

        Assert.Contains("已自动重试 10 次", ex.Message);
        Assert.Equal(11, _adapter.Calls.Count);
        Assert.Equal(0, handle.State.Turn);

        // 每一次都留了账（含最后那次失败）——不多不少。
        var calls = _usage.ForSession(handle.SessionId);
        Assert.Equal(11, calls.Count);
        Assert.All(calls, c => Assert.Equal("failed", c.Status));
        Assert.Equal([null, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11], calls.Select(c => c.Attempt));
    }
}
