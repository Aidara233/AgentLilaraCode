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
/// **`floating_context`：真实读数在对话最末，历史里每步只留一条已回收占位**
/// （2026-10-26 定形态；**2026-10-28 用户裁定把位置从「最前面」改到「最末」**）。
///
/// <para>⚠️ <b>位置为什么改</b>：这一对的 id 带步号、正文带每步都变的数 ⇒ 它待在**历史之前**时，
/// 历史段就从它的第一个 token 起与上一步不匹配 ⇒ 除 system/premier/tools 之外
/// **一次都不命中前缀缓存**（实测：21 万上下文只命中 8k）。挪到最末之后，共同前缀 = 头部 +
/// 整段历史（纯追加），变化被关在最后一对里、不挡任何东西。</para>
///
/// <para>⚠️ 这套契约四句话，每句都对应一个会被「看起来能用」掩盖的失败：</para>
/// <list type="number">
///   <item><b>真实读数插在最末</b>：它是「此刻的状态」，是读者**最后读到**的那一份；
///     放在最前面既要求回头找，又会让整段历史落在它之后（缓存视角下等于每步全变）。</item>
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
    /// ⚠️ **真实读数在请求的最末，而且恰好一对**：`tool_use(floating_context)` 紧跟着
    /// 配对的 `tool_result`。位置与配对两条都要钉——
    /// 「紧挨着」不是排版：Claude 要求 `tool_result` **immediately after** 它的调用。
    /// </summary>
    [Fact]
    public async Task Step_InsertsTheRealPairAtTheVeryEnd()
    {
        var handle = NewSession();
        _adapter.ReplyWith(_ => Say("好"));
        await _stepper.StepAsync(handle, "你好", default);

        var request = Assert.Single(_adapter.Calls);
        Assert.True(request.Timeline.Count >= 3);

        // ⚠️ **真实读数在请求的最末尾**（2026-10-28 用户裁定）：它是「此刻的读数」，
        // 排在整个历史之后；也因此**不挡前缀缓存**（它变了，前面的历史一个字节都没动）。
        var call = Assert.IsType<TimelineEvent.MessageEvent>(request.Timeline[^2]);
        Assert.Equal(Speaker.Model, call.From);
        var use = Assert.Single(call.Blocks.OfType<Block.ToolUseBlock>());
        Assert.Equal(FloatingContextTool.NameOf, use.Name);
        Assert.Equal("{}", use.Arguments);

        var result = Assert.IsType<TimelineEvent.ToolResultEvent>(request.Timeline[^1]);
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

        // 真实读数**只有一份**，而且**在末尾**：它是最后读到的那一份，不是历史里的一段。
        Assert.Equal(1, Results(request).Count(r => TextOf(r).Contains(CorePrompts.TailOpen, StringComparison.Ordinal)));
        Assert.Same(result, request.Timeline[^1]);
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

        var firstMsg = Assert.IsType<TimelineEvent.MessageEvent>(_adapter.Calls[0].Timeline[^2]);
        var first = Assert.Single(firstMsg.Blocks.OfType<Block.ToolUseBlock>());
        Assert.EndsWith("-call", firstMsg.Id);
        Assert.EndsWith("-result", first.CallId);
        Assert.Equal(first.CallId, Assert.IsType<TimelineEvent.ToolResultEvent>(_adapter.Calls[0].Timeline[^1]).CallId);

        // ② 下一步换号：同一步内重复渲染要逐字节相同（`00` §5 铁律 1），
        // 而跨步必须不同——否则两步的读数会撞成同一条，配对会串。
        // ⚠️ 挑的时候要**排除占位那条**（它同名，只是号段不同）。
        var second = Assert.Single(Uses(_adapter.Calls[1]),
            u => u.Name == FloatingContextTool.NameOf
                 && !u.CallId.Contains("-reclaim-", StringComparison.Ordinal));
        Assert.NotEqual(first.CallId, second.CallId);
    }

    /// <summary>
    /// ⚠️ **下一步：末尾一条新的真实读数 + 历史里一条占位**，形状固定在这个数目上。
    /// 三条判据：① 真实那条在**最末**；② 占位那条来自上一轮；③ **没有同号重复**——
    /// 「一组调用里出现两个块」正是这次改动要防的形态。
    /// </summary>
    [Fact]
    public async Task NextStep_PutsTheFreshReadingAtTheEnd_AndKeepsOneReclaimedPairInHistory()
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

        // ① 真实那条在**最末**（倒数第二条是调用，最后一条是它的结果）。
        var tailMsg = Assert.IsType<TimelineEvent.MessageEvent>(request.Timeline[^2]);
        var tailUse = Assert.Single(tailMsg.Blocks.OfType<Block.ToolUseBlock>());
        Assert.Equal(FloatingContextTool.NameOf, tailUse.Name);
        Assert.DoesNotContain("-reclaim-", tailUse.CallId);

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

    /// <summary>
    /// ⚠️ **模型收到的读数里有计划树与上下文用量**（2026-10-27）：这两样从前只在这一处传参，
    /// 所以它们在这里是绿的、在头那一侧是缺的。今天三处共用 `TailContext.For`，
    /// 这条与 `ControlServerTests.State_FloatingSlotCarriesThePlanTreeAndTheContextLine`
    /// 成对存在——**两边都断言同一批事实**，任何一处漏传都会让其中一条变红。
    /// </summary>
    [Fact]
    public async Task Step_ReadoutCarriesThePlanTreeAndTheContextLine()
    {
        var handle = NewSession();
        var usage = new UsageStore(_store.DataRoot);
        usage.Append(new UsageCall
        {
            CallId = "previous", SessionId = handle.SessionId, ProviderId = "p", ModelId = "m",
            Protocol = Protocols.ClaudeMessages, StartedAt = CoreClock.Now(), Status = "completed",
            Usage = new TokenUsage { InputTokens = 100, CacheReadTokens = 20, CacheWriteTokens = 5 },
        });
        var stepper = new Stepper(_store, _adapter, BuiltInTools.All, usage: usage);
        handle.UpdateState(s => s with
        {
            Plan = new SessionPlan
            {
                Nodes =
                [
                    new PlanNode { Id = "a", Title = "读代码", Body = "先读一遍" },
                    new PlanNode
                    {
                        Id = "b", Title = "改代码",
                        Children = [new PlanNode { Id = "b1", Title = "改一个文件", Body = "改 executor" }],
                    },
                ],
                UpdatedAt = CoreClock.Now(),
            },
        });

        _adapter.ReplyWith(_ => Say("好"));
        await stepper.StepAsync(handle, "你好", default);

        var readout = TextOf(Assert.IsType<TimelineEvent.ToolResultEvent>(_adapter.Calls[0].Timeline[^1]));
        Assert.Contains("计划（剩余树", readout);
        Assert.Contains("▶ 读代码", readout);
        Assert.Contains("改一个文件", readout);
        Assert.Contains("上次调用上下文总长度：125 tokens", readout);
    }

    /// <summary>
    /// ⚠️ **改位置的全部意义**（2026-10-28）：第二步请求必须**以第一步请求的历史部分为前缀**。
    ///
    /// <para>位置 0 时这条**不可能**成立：那对合成调一变，它后面的整段历史跟着失配。
    /// 挪到最末之后，共同前缀 = 头部 + 第一步的全部历史，**只有末尾那一对**（约 1–2 KB）每步新写
    /// —— 从「除头部外全不命中」变成「只有末尾一小段不命中」。</para>
    ///
    /// <para>⚠️ 断言刻意写成「第一步去掉末尾两条」而不是「第一步全部」：浮动对**不落库**，
    /// 第一步把那对放在末尾，第二步的位置上是那一步的真实记录——所以它俩本来就不该相等。
    /// 把这一点钉住，才不会有人为了「让前缀完全相等」去把这玩意塞进记录。</para>
    /// </summary>
    [Fact]
    public async Task SecondStep_ReusesTheWholeFirstHistoryAsPrefix()
    {
        var handle = NewSession();
        _adapter.ReplyWith(_ => Say("一")).ReplyWith(_ => Say("二"));
        await _stepper.StepAsync(handle, "a", default);
        await _stepper.StepAsync(handle, [], default);

        var first = Shape(_adapter.Calls[0]).ToList();
        var second = Shape(_adapter.Calls[1]).ToList();

        // 第一步末尾那两条是浮动对（不落库），所以历史部分的长度是 Count - 2。
        var history = first.Count - 2;
        Assert.True(second.Count > history);

        for (var i = 0; i < history; i++)
            Assert.Equal(first[i], second[i]);

        // 而第二步末尾那两条**是**这一次的浮动对（新号段，不是第一步那份）。
        Assert.NotEqual(first[^2], second[^2]);
        Assert.DoesNotContain(first[^2], second);

        static IEnumerable<string> Shape(ModelRequest request) =>
            request.Timeline.Select(e => e switch
            {
                TimelineEvent.MessageEvent m =>
                    $"{m.Id}|{m.From}|" + string.Join("\u0001", m.Blocks.Select(BlockText)),
                TimelineEvent.ToolResultEvent r =>
                    $"{r.Id}|{r.CallId}|" + string.Join("\u0001", r.Content.Select(BlockText)),
                _ => e.Id,
            });

        static string BlockText(Block block) => block switch
        {
            Block.TextBlock t => "t:" + t.Text,
            Block.ToolUseBlock u => $"u:{u.CallId}:{u.Name}:{u.Arguments}",
            Block.StoreBlock s => "s:" + s.Sha256,
            _ => block.GetType().Name,
        };
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

        // 而真实读数仍在本次请求的最末，一个字都没少。
        var injected = Assert.IsType<TimelineEvent.ToolResultEvent>(_adapter.Calls[0].Timeline[^1]);
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
