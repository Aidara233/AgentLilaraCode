using System.Text.Json;
using Lilara.Core.Kernel;
using AnthropicRole = Anthropic.Models.Messages.Role;
using Lilara.Core.Protocol;
using Lilara.Core.Protocol.Claude;
using Lilara.Core.Session;
using Lilara.Core.Session.Tools;
using Lilara.Core.Storage;
using Lilara.Core.Tests.Fakes;
using Block = Lilara.Core.Kernel.Block;

namespace Lilara.Core.Tests;

/// <summary>
/// **`floating_context`：真实读数在对话最上面，历史里每步只留一条已回收占位**（2026-10-26 用户裁定）。
///
/// <para>⚠️ 这套契约四句话，每句都对应一个会被「看起来能用」掩盖的失败：</para>
/// <list type="number">
///   <item><b>真实读数插在最前面</b>：它是「此刻的状态」，不是历史里的一段——追加在末尾会随对话
///     增长越走越远，也会与「最上面就是当前状态」的位置直觉相反。</item>
///   <item><b>记录里每步恰好一对占位</b>：无论模型这一轮真的调了没有，记录里都有「一次调用 +
///     一句已回收」。缺口会让下一步看到「上一轮我没调用」，而补那个缺口唯一可行的动作就是再调一次。</item>
///   <item><b>一组调用里不许出现两个块</b>：真实那对的 id 由 turn 决定，占位那对用另一个号段；
///     同一轮里更不能出现两条同名调用（配对会歪）。</item>
///   <item><b>没有该工具时不合成，尾块退回尾槽位</b>：纯 chat 会话（`04` §1.3 推论 1）
///     与测试替身推进器都走这条路，内容不许丢。</item>
/// </list>
///
/// <para>⚠️ 这里走**真推进器**（真执行器、真句柄、真盘面），与 `StepperToolTests` 同一个理由：
/// 假适配器脚本模型回复，但注入发生在**核心自己的装配**里，只有真 `Stepper` 才走到它。</para>
/// </summary>
public sealed class FloatingContextTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "lilara-floating-" + Guid.NewGuid().ToString("N"));
    private readonly string _work;
    private readonly SessionStore _store;
    private readonly FakeAdapter _adapter = new();
    private readonly Stepper _stepper;

    public FloatingContextTests()
    {
        _work = Path.Combine(_tmp, "work");
        Directory.CreateDirectory(_work);
        _store = new SessionStore(Path.Combine(_tmp, "data"));
        _stepper = new Stepper(_store, _adapter, BuiltInTools.All);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true);
    }

    private SessionHandle NewSession() => SessionHandle.Create(_store, _work, new ModelSelection
    {
        ProviderId = "p", ModelId = "m", Protocol = Protocols.ClaudeMessages, Effort = "high", MaxTokens = 1000,
    });

    private static ModelReply Say(string text) => new()
    {
        Blocks = [new Block.TextBlock(text)],
        StopReason = "end_turn",
        Refused = false,
    };

    private static ModelReply Calls(params (string CallId, string Name, string Args)[] calls) => new()
    {
        Blocks = calls.Select(c => (Block)new Block.ToolUseBlock(c.CallId, c.Name, c.Args)).ToList(),
        StopReason = "tool_use",
        Refused = false,
    };

    private static IReadOnlyList<TimelineEvent.ToolResultEvent> Results(ModelRequest request) =>
        [.. request.Timeline.OfType<TimelineEvent.ToolResultEvent>()];

    private static string TextOf(TimelineEvent.ToolResultEvent r) =>
        string.Concat(r.Content.OfType<Block.TextBlock>().Select(b => b.Text));

    private static IEnumerable<Block.ToolUseBlock> Uses(ModelRequest request) =>
        request.Timeline.OfType<TimelineEvent.MessageEvent>().SelectMany(m => m.Blocks.OfType<Block.ToolUseBlock>());

    // ---- 真实读数在哪 ----

    /// <summary>
    /// ⚠️ **真实读数在请求的最前面，而且恰好一对**：`tool_use(floating_context)` 紧跟着
    /// 配对的 `tool_result`。位置与配对两条都要钉——
    /// 「紧挨着」不是排版：Claude 要求 `tool_result` **immediately after** 它的调用。
    /// </summary>
    [Fact]
    public async Task Step_InsertsTheRealPairAtTheVeryTop()
    {
        var handle = NewSession();
        _adapter.ReplyWith(_ => Say("好"));
        await _stepper.StepAsync(handle, "你好", default);

        var request = Assert.Single(_adapter.Calls);
        Assert.True(request.Timeline.Count >= 3);

        var call = Assert.IsType<TimelineEvent.MessageEvent>(request.Timeline[0]);
        Assert.Equal(Speaker.Model, call.From);
        var use = Assert.Single(call.Blocks.OfType<Block.ToolUseBlock>());
        Assert.Equal(FloatingContextTool.NameOf, use.Name);
        Assert.Equal("{}", use.Arguments);

        var result = Assert.IsType<TimelineEvent.ToolResultEvent>(request.Timeline[1]);
        Assert.Equal(use.CallId, result.CallId);
        Assert.Contains(CorePrompts.TailOpen, TextOf(result));
        Assert.Contains(CorePrompts.TailDeclaration, TextOf(result));

        // ⚠️ **形状：框架头独占一行，包边在下一行**。两个文本块在文本化链路上是
        // `string.Concat` 拼的，中间没有换行就会得到 `[floating_context][lilara 上下文]`——
        // 两个方括号贴成一个复合 token，「一个块套另一个块」当场失效（`01` §3.3）。
        Assert.StartsWith(
            FloatingContextTool.Framing.Render() + "\n" + CorePrompts.TailOpen + "\n",
            TextOf(result));
        Assert.Contains(Axes.LabelOf(Axes.Permission), TextOf(result));

        // 真实读数**只有一份**，而且**不在末尾**（末尾是这一步的记录，不是读数）。
        Assert.Equal(1, Results(request).Count(r => TextOf(r).Contains(CorePrompts.TailOpen, StringComparison.Ordinal)));
        Assert.Same(result, request.Timeline[1]);
    }

    /// <summary>
    /// ⚠️ **用户消息里没有尾块**：用户输入与核心注入不许住在同一个 content 数组里，
    /// 否则下一步按记录重渲染时那条消息**不带尾块**，前缀当场分叉。
    /// </summary>
    [Fact]
    public async Task Step_LeavesTheUserMessageUntouched()
    {
        var handle = NewSession();
        _adapter.ReplyWith(_ => Say("好"));
        await _stepper.StepAsync(handle, "你好", default);

        var user = Assert.Single(
            Assert.Single(_adapter.Calls).Timeline.OfType<TimelineEvent.MessageEvent>(),
            m => m.From == Speaker.User);

        Assert.Equal("你好", Assert.IsType<Block.TextBlock>(Assert.Single(user.Blocks)).Text);
        // 槽位里尾块是空的——它整块搬去了顶上那一对。
        Assert.Empty(_adapter.Calls[0].Slots.Tail);
    }

    // ---- 记录里留什么 ----

    /// <summary>
    /// ⚠️ **记录里只剩占位**：一步落下**恰好一对**，内容是 `ReclaimedBody`；
    /// 记录里**绝不出现真实读数**（`TailOpen` 那一段）。判据两条缺一不可：
    /// 只钉「有一对占位」的话，把真实读数一起写进去照样绿——而那会让旧档位复活。
    /// </summary>
    [Fact]
    public async Task Step_PersistsExactlyOneReclaimedPair()
    {
        var handle = NewSession();
        _adapter.ReplyWith(_ => Say("一")).ReplyWith(_ => Say("二"));
        await _stepper.StepAsync(handle, "a", default);
        await _stepper.StepAsync(handle, [], default);

        var timeline = _store.ReadTimeline(handle.ProjectKey, handle.SessionId);

        // ① 每一步各一对，共两对。
        var results = timeline.OfType<TimelineEvent.ToolResultEvent>()
            .Where(r => r.CallId.StartsWith("floating-", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.EndsWith("-reclaim-result", r.Id, StringComparison.Ordinal));

        // ② 内容恒为占位，且点出了读数在哪。
        Assert.All(results, r => Assert.Contains(FloatingContextTool.ReclaimedBody, TextOf(r)));
        Assert.All(results, r => Assert.Contains(FloatingContextTool.ReclaimedFraming.Render(), TextOf(r)));

        // ③ 记录里**没有**真实读数。
        Assert.DoesNotContain(results, r => TextOf(r).Contains(CorePrompts.TailOpen, StringComparison.Ordinal));
        Assert.DoesNotContain(results, r => TextOf(r).Contains(Axes.LabelOf(Axes.Permission), StringComparison.Ordinal));

        // ④ 每条占位调用都有配对的结果（悬空的调用会让下一步请求非法）。
        var uses = timeline.OfType<TimelineEvent.MessageEvent>()
            .SelectMany(m => m.Blocks.OfType<Block.ToolUseBlock>())
            .Where(u => u.Name == FloatingContextTool.NameOf).ToList();
        Assert.Equal(2, uses.Count);
        Assert.All(uses, u => Assert.Contains(results, r => r.CallId == u.CallId));
    }

    /// <summary>
    /// ⚠️ **两条号段互不相干**：真实那对是 `floating-{turn}-call/result`，由 turn 决定；
    /// 占位那对是 `floating-{turn}-reclaim-…`。它们说的是两件事，**同号会让下一步的
    /// 去重判据把真实块整条挡掉**。这里钉 ① 成对、② 逐步变化。
    /// ⚠️ **号段里的数字不钉死**：`State.Turn` 从几开始是另一处的契约，钉死只会腐。
    /// </summary>
    [Fact]
    public async Task RealIdsAreDeterministic_AndFollowTheTurn()
    {
        var handle = NewSession();
        _adapter.ReplyWith(_ => Say("一")).ReplyWith(_ => Say("二"));
        await _stepper.StepAsync(handle, "a", default);
        await _stepper.StepAsync(handle, [], default);

        var firstMsg = Assert.IsType<TimelineEvent.MessageEvent>(_adapter.Calls[0].Timeline[0]);
        var first = Assert.Single(firstMsg.Blocks.OfType<Block.ToolUseBlock>());
        Assert.EndsWith("-call", firstMsg.Id);
        Assert.EndsWith("-result", first.CallId);
        Assert.Equal(first.CallId, Assert.IsType<TimelineEvent.ToolResultEvent>(_adapter.Calls[0].Timeline[1]).CallId);

        // ② 下一步换号：同一步内重复渲染要逐字节相同（`00` §5 铁律 1），
        // 而跨步必须不同——否则两步的读数会撞成同一条，配对会串。
        // ⚠️ 挑的时候要**排除占位那条**（它同名，只是号段不同）。
        var second = Assert.Single(Uses(_adapter.Calls[1]),
            u => u.Name == FloatingContextTool.NameOf
                 && !u.CallId.Contains("-reclaim-", StringComparison.Ordinal));
        Assert.NotEqual(first.CallId, second.CallId);
    }

    /// <summary>
    /// ⚠️ **下一步：顶上一条新的真实读数 + 历史里一条占位**，形状固定在这个数目上。
    /// 三条判据：① 真实那条在最前面；② 占位那条来自上一轮；③ **没有同号重复**——
    /// 「一组调用里出现两个块」正是这次改动要防的形态。
    /// </summary>
    [Fact]
    public async Task NextStep_PutsTheFreshReadingOnTop_AndKeepsOneReclaimedPairInHistory()
    {
        var handle = NewSession();
        _adapter.ReplyWith(_ => Say("一")).ReplyWith(_ => Say("二"));
        await _stepper.StepAsync(handle, "a", default);
        await _stepper.StepAsync(handle, [], default);

        var request = _adapter.Calls[1];
        var floating = Uses(request).Where(u => u.Name == FloatingContextTool.NameOf).ToList();

        // 真实那条 + 上一轮的占位，恰好两条。
        Assert.Equal(2, floating.Count);
        Assert.Equal(floating.Count, floating.Select(u => u.CallId).Distinct(StringComparer.Ordinal).Count());

        // ① 真实那条在最前面。
        var top = Assert.IsType<TimelineEvent.MessageEvent>(request.Timeline[0]);
        var topUse = Assert.Single(top.Blocks.OfType<Block.ToolUseBlock>());
        Assert.Equal(FloatingContextTool.NameOf, topUse.Name);
        Assert.DoesNotContain("-reclaim-", topUse.CallId);

        // ② 占位那条来自上一轮，且读的是占位。
        var reclaimed = Assert.Single(floating, u => u.CallId.Contains("-reclaim-", StringComparison.Ordinal));
        Assert.Contains(Results(request), r => r.CallId == reclaimed.CallId
            && TextOf(r).Contains(FloatingContextTool.ReclaimedBody, StringComparison.Ordinal));

        // ③ 每条调用都有配对结果。
        Assert.All(floating, u => Assert.Contains(Results(request), r => r.CallId == u.CallId));
    }

    /// <summary>
    /// ⚠️ **三条协议都收得下这一对**：形状是「assistant 消息里一条 `tool_use` + 紧接着一条
    /// `ToolResultEvent`」，与模型自己调工具时**逐字相同**——所以适配器一个都不用改。
    /// ⚠️ 这里只钉「这条形状出现在产物里」，**不钉位置**：位置由 `01` §3.2 管，
    /// 而三条链路的容器形状不同（Chat 是 `role:"tool"`，Responses 是 `function_call_output`）。
    /// </summary>
    [Fact]
    public async Task EveryProtocolAcceptsTheSyntheticPair()
    {
        var handle = NewSession();
        _adapter.ReplyWith(_ => Say("好"));
        await _stepper.StepAsync(handle, "你好", default);
        var body = _adapter.Calls[0];

        var claude = ClaudeRequestBuilder.Build(body);
        Assert.Contains(claude.Messages, m =>
            m.Content.TryPickContentBlockParams(out var blocks) && blocks!.Any(b => b.TryPickToolResult(out _)));

        var chat = JsonDocument.Parse(Protocol.OpenAi.OpenAiChatRequest.Build(body)).RootElement;
        var chatMessages = chat.GetProperty("messages");
        Assert.Contains(Enumerable.Range(0, chatMessages.GetArrayLength()),
            i => chatMessages[i].GetProperty("role").GetString() == "tool");

        var responses = JsonDocument.Parse(Protocol.OpenAi.OpenAiResponsesRequest.Build(body)).RootElement;
        var input = responses.GetProperty("input");
        Assert.Contains(Enumerable.Range(0, input.GetArrayLength()),
            i => input[i].GetProperty("type").GetString() == "function_call_output");

        // ⚠️ 三条都必须**显式声明**这个工具，否则部分上游在 `tool_use` 引用了未声明的名字时 400。
        Assert.Contains(body.Tools, t => t.Name == FloatingContextTool.NameOf);
    }

    // ---- 工具本身 ----

    /// <summary>
    /// ⚠️ **模型自己调，拿到的也只是占位**：读数每轮重建在顶上，工具返回值不含它。
    /// 这条是「调用不会带来新信息」的**机械保证**——不是靠描述里的一句劝。
    /// </summary>
    [Fact]
    public async Task TheToolReturnsTheReclaimedPlaceholder()
    {
        var handle = NewSession();
        _adapter.ReplyWith(_ => Calls(("c1", FloatingContextTool.NameOf, "{}")));
        _adapter.ReplyWith(_ => Say("好"));
        await _stepper.StepAsync(handle, "读一下", default);

        var executed = _store.ReadTimeline(handle.ProjectKey, handle.SessionId)
            .OfType<TimelineEvent.ToolResultEvent>().Single(r => r.CallId == "c1");

        Assert.Equal(FloatingContextTool.ReclaimedBody, TextOf(executed));

        // 而真实读数仍在本次请求的最前面，一个字都没少。
        var injected = Assert.IsType<TimelineEvent.ToolResultEvent>(_adapter.Calls[0].Timeline[1]);
        Assert.Contains(CorePrompts.TailOpen, TextOf(injected));
        Assert.Contains(Axes.LabelOf(Axes.Permission), TextOf(injected));
    }

    /// <summary>
    /// ⚠️ **没有该工具的会话不合成，而且尾块退回尾槽位**（不丢内容）：
    /// 纯 chat 会话（`04` §1.3 推论 1）与测试替身推进器都走这条路。
    /// 两个判据都要钉——只钉「不合成」的话，「顺手把尾块也弄丢」照样绿
    /// （本批第一版就是这样，三条插件集成测试当场变红）。
    /// </summary>
    [Fact]
    public async Task WithoutTheTool_NoPairIsSynthesized_AndTheTailFallsBackToTheSlot()
    {
        var bare = new Stepper(_store, _adapter, tools: []);
        var handle = NewSession();
        _adapter.ReplyWith(_ => Say("好"));
        await bare.StepAsync(handle, "你好", default);

        var request = Assert.Single(_adapter.Calls);
        Assert.Empty(Uses(request));
        Assert.Empty(Results(request));
        var tail = Assert.IsType<Block.TextBlock>(Assert.Single(request.Slots.Tail)).Text;
        Assert.Contains(CorePrompts.TailOpen, tail);
        Assert.Contains(Axes.LabelOf(Axes.Permission), tail);
    }

    /// <summary>
    /// ⚠️ **答复路径不重复注入**：`ResumeAsync` 之后那一次请求里，真实读数仍然只有一份。
    /// 这条挡的是「两条装配路径各插一次」——那正是「一组调用里两个块」的另一种来源。
    /// </summary>
    [Fact]
    public async Task ResumePath_DoesNotInsertASecondRealBlock()
    {
        var handle = NewSession();
        _adapter.ReplyWith(_ => Calls(("c1", FloatingContextTool.NameOf, "{}")));
        _adapter.ReplyWith(_ => Say("好"));
        await _stepper.StepAsync(handle, "读一下", default);

        var second = _adapter.Calls[^1];
        Assert.Equal(1, Results(second)
            .Count(r => TextOf(r).Contains(CorePrompts.TailOpen, StringComparison.Ordinal)));
    }
}
