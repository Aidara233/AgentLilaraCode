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
/// **`floating_context`：核心每步在时间线末尾合成一对调用 + 结果**（`01` §3.2，2026-10-17）。
///
/// <para>⚠️ 本文件钉四件事，每件都对应一个会被「看起来能用」掩盖的失败：</para>
/// <list type="number">
///   <item><b>旧内容逐字节不动</b>：用户消息里**不再**混进尾块——Claude 那条链路原先
///     把尾块拼进那条 user 消息，合成对上线的同时必须确认那个拼法真的消失了。</item>
///   <item><b>不落盘</b>：记录只追加，一条合成调用写进去就永久在；它必须只在请求里活着。</item>
///   <item><b>下一步能接上</b>：从前一步的合成对末尾，接到下一步请求的**历史**里——
///     形状是「assistant `tool_use` + `tool_result`」，配对照样完整。</item>
///   <item><b>没有该工具时不合成</b>：纯 chat 会话请求逐字节与加它之前相同
///     （`04` §1.3 推论 1；同时避免「名字没声明就发一条调用」在上游 400）。</item>
/// </list>
///
/// <para>⚠️ 这里走**真推进器**（真执行器、真句柄、真盘面），与 `StepperToolTests` 同一个理由：
/// 假适配器脚本模型回复，但合成注入发生在**核心自己的装配**里，只有真 `Stepper` 才走到它。</para>
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

    // ---- 合成对本身 ----

    /// <summary>
    /// ⚠️ **请求末尾恰好一对**：`tool_use(floating_context)` 且**紧跟着**配对的 `tool_result`。
    /// 「紧挨着」不是排版：Claude 要求 `tool_result` **immediately after** 它的调用，
    /// 中间插一条任何消息都是 400。
    /// </summary>
    [Fact]
    public async Task Step_AppendsExactlyOnePairedFloatingCallAtTheEnd()
    {
        var handle = NewSession();
        _adapter.ReplyWith(_ => Say("好"));
        await _stepper.StepAsync(handle, "你好", default);

        var request = Assert.Single(_adapter.Calls);
        var n = request.Timeline.Count;
        Assert.True(n >= 2);

        var call = Assert.IsType<TimelineEvent.MessageEvent>(request.Timeline[n - 2]);
        Assert.Equal(Speaker.Model, call.From);
        var use = Assert.Single(call.Blocks.OfType<Block.ToolUseBlock>());
        Assert.Equal(FloatingContextTool.NameOf, use.Name);
        Assert.Equal("{}", use.Arguments);

        var result = Assert.IsType<TimelineEvent.ToolResultEvent>(request.Timeline[n - 1]);
        Assert.Equal(use.CallId, result.CallId);
        Assert.Contains(CorePrompts.TailOpen, TextOf(result));
        Assert.Contains(CorePrompts.TailDeclaration, TextOf(result));

        // ⚠️ **形状：框架头独占一行，包边在下一行**（2026-10-23 实测）。
        // 两个文本块在文本化链路上是 `string.Concat` 拼的，中间没有换行就会得到
        // `[floating_context][lilara 上下文]`——两个方括号贴成一个复合 token，
        // 「一个块套另一个块」当场失效（`01` §3.3）。这条断言是那个 bug 的哨兵。
        Assert.StartsWith(
            FloatingContextTool.Framing.Render() + "\n" + CorePrompts.TailOpen + "\n",
            TextOf(result));
        Assert.Contains(Axes.LabelOf(Axes.Permission), TextOf(result));

        // 尾块**只有一份**：它在这条结果里，不在别的任何地方。
        Assert.Equal(1, Results(request).Count(r => TextOf(r).Contains(CorePrompts.TailOpen, StringComparison.Ordinal)));
    }

    /// <summary>
    /// ⚠️ **用户消息里不再有尾块**（本次改动的**因**）：Claude 那条链路原先把尾块拼进
    /// 末尾那条 user 消息 ⇒ 用户输入与核心注入在同一个 content 数组里，
    /// 而下一步按记录重渲染时那条消息**不带尾块**，前缀当场分叉。
    /// 这里直接看**请求的时间线本身**：用户那条的块只有它自己写的话。
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
        // 槽位里尾块是空的——尾块只走合成那一对。
        Assert.Empty(_adapter.Calls[0].Slots.Tail);
    }

    /// <summary>
    /// ⚠️ **合成对不落盘**。判据不是「盘上没有 `floating_context` 这个**字符串**」——
    /// 工具表里的描述本来就会带上它；判据是**盘上一条 `tool_use` 都没有**，
    /// 且记录条数 == 用户 + 模型（模型那条没有工具调用）。
    /// </summary>
    [Fact]
    public async Task Step_DoesNotPersistTheSyntheticPair()
    {
        var handle = NewSession();
        _adapter.ReplyWith(_ => Say("一")).ReplyWith(_ => Say("二"));
        await _stepper.StepAsync(handle, "a", default);
        await _stepper.StepAsync(handle, [], default);

        var timeline = _store.ReadTimeline(handle.ProjectKey, handle.SessionId);

        // ⚠️ **2026-10-23：落盘试过又退回了**（理由见 `Stepper` 落盘点那条注释与
        // `docs/superpowers/plans/2026-10-23-floating-as-history.md`）：它让 19 条
        // 「记录里的事件条数」断言当场红，而那是成规模的改写，不该顺手做。
        // ⇒ 今天仍然是「不进记录」：记录里既没有浮动结果，也没有浮动调用。
        Assert.Empty(timeline.OfType<TimelineEvent.ToolResultEvent>());
        Assert.DoesNotContain(
            timeline.OfType<TimelineEvent.MessageEvent>().SelectMany(m => m.Blocks.OfType<Block.ToolUseBlock>()),
            u => u.Name == FloatingContextTool.NameOf);
    }

    /// <summary>
    /// ⚠️ **合成对的 id 是确定性的，而且每一步换一段**：若用 `Ids.New()` 现造，
    /// 同一步内重复渲染会给出不同字节（`00` §5 铁律 1 里唯一真会出事的那条）。
    /// 这里钉两件：① 同一步里 call / result 的 id 成对（`…-call` / `…-result`）；
    /// ② 下一步的 id **与上一步不同**（否则两次渲染撞成同一条，配对会串）。
    /// ⚠️ **号段里的数字不钉**：`State.Turn` 是几由 `SessionHandle` 的初始值决定，
    /// 那是另一处的契约，钉死只会腐。
    /// </summary>
    [Fact]
    public async Task SyntheticIdsAreDeterministic_AndStayTheSameWhileAxesDoNotChange()
    {
        var handle = NewSession();
        _adapter.ReplyWith(_ => Say("一")).ReplyWith(_ => Say("二"));
        await _stepper.StepAsync(handle, "a", default);
        await _stepper.StepAsync(handle, [], default);

        var firstMsg = _adapter.Calls[0].Timeline.OfType<TimelineEvent.MessageEvent>()
            .Single(m => m.Blocks.OfType<Block.ToolUseBlock>().Any(u => u.Name == FloatingContextTool.NameOf));
        var first = Assert.Single(firstMsg.Blocks.OfType<Block.ToolUseBlock>());
        var tailFirst = Results(_adapter.Calls[0]).Last(r => r.CallId == first.CallId);

        // ① 成对：记录 id 在**包含调用的那条消息**上，结果那条与 `CallId` 同号。
        Assert.EndsWith("-call", firstMsg.Id);
        Assert.EndsWith("-result", first.CallId);
        Assert.Equal(first.CallId, tailFirst.Id);

        // ⚠️ **2026-10-23 改口：下一步不该换一段**。轴没变 ⇒ 不注入新的一份，
        // 于是第二步看到的是**同一条**（它是历史，不是新造的）。⇒ id 必须相同。
        // 旧断言钉的是「每步换一段」，那正是「每步一份」的化石，而那个节律已经被降频删掉了。
        var second = Assert.Single(Uses(_adapter.Calls[1]), u => u.Name == FloatingContextTool.NameOf);
        Assert.Equal(first.CallId, second.CallId);
        Assert.Single(Results(_adapter.Calls[1]), r => r.CallId == first.CallId);
    }

    // ---- 下一步能接上 ----

    /// <summary>
    /// ⚠️ **前一步的合成对不会进下一步**——浮动是**状态**，每步一份、旧的作废
    /// （`00` §2.4「替换而非追加」）。判据两条，缺一都不成立：
    /// ① 下一步请求里**恰好一条** `floating_context` 调用（新造的那条）；
    /// ② 上一步那条的 id **根本没出现**（它没落盘，也就不可能作为历史被重放）。
    /// ⚠️ 而我第一版写的断言是反的——我以为「下一步会看到旧的那一对」，
    /// 实测直接红了：合成对只活在**生成它的那一次请求**里。
    /// </summary>
    [Fact]
    public async Task NextStep_DoesNotCarryThePreviousPair_AndGetsAFreshOne()
    {
        var handle = NewSession();
        _adapter.ReplyWith(_ => Say("一")).ReplyWith(_ => Say("二"));
        await _stepper.StepAsync(handle, "a", default);
        await _stepper.StepAsync(handle, [], default);

        var old = Assert.Single(Uses(_adapter.Calls[0]), u => u.Name == FloatingContextTool.NameOf);
        var second = _adapter.Calls[1].Timeline;

        // ⚠️ **2026-10-23 改口（整条反转）**：旧契约是「下一步不带旧的那一对，换新的」——
        // 那是「浮动 = 不落盘的状态」时代的判据。现在它**落盘**，而注入是「变化才发生」：
        // ⇒ 轴没变时，下一步看到的就是**同一对**（作为历史被重放），且**不新增**。
        // 这正是「上下文里的浮动块不随步数增长」的可观测形态。
        var same = Assert.Single(Uses(_adapter.Calls[1]), u => u.Name == FloatingContextTool.NameOf);
        Assert.Equal(old.CallId, same.CallId);

        // ② 恰好一份：没有第二对（没有重复注入，也没有孤儿结果）。
        Assert.Single(Results(_adapter.Calls[1]), r => r.CallId.StartsWith("floating-", StringComparison.Ordinal));
    }

    /// <summary>
    /// ⚠️ **三条协议都收得下这一对**：形状是「模型消息里一条 `tool_use` + 紧接着一条
    /// `ToolResultEvent`」，与模型自己调工具时**逐字相同**——所以适配器一个都不用改。
    /// 这里用三条真实的构造器各跑一遍，确认没有一条在该形状上抛。
    /// </summary>
    [Fact]
    public async Task EveryProtocolAcceptsTheSyntheticPair()
    {
        var handle = NewSession();
        _adapter.ReplyWith(_ => Say("好"));
        await _stepper.StepAsync(handle, "你好", default);
        var body = _adapter.Calls[0];

        // Claude：合并进末尾那条 user 消息的 tool_result 块（**不是**再起一条 user 消息）。
        var claude = ClaudeRequestBuilder.Build(body);
        var last = claude.Messages[^1];
        Assert.Equal(AnthropicRole.User, last.Role.Value());
        Assert.True(last.Content.TryPickContentBlockParams(out var blocks));
        Assert.Contains(blocks!, b => b.TryPickToolResult(out _));

        // 两条 OpenAI 链路：工具结果在自己的位置（Chat = role:"tool"，Responses = function_call_output）。
        // ⚠️ `messages` / `input` 是 `JsonArray`，没有 `Index` 索引器 ⇒ 用 `Count - 1`。
        var chat = JsonDocument.Parse(Protocol.OpenAi.OpenAiChatRequest.Build(body)).RootElement;
        var chatMessages = chat.GetProperty("messages");
        Assert.Equal("tool", chatMessages[chatMessages.GetArrayLength() - 1].GetProperty("role").GetString());

        var responses = JsonDocument.Parse(Protocol.OpenAi.OpenAiResponsesRequest.Build(body)).RootElement;
        var input = responses.GetProperty("input");
        Assert.Equal("function_call_output", input[input.GetArrayLength() - 1].GetProperty("type").GetString());

        // ⚠️ 三条都必须**显式声明**这个工具，否则部分上游在 `tool_use` 引用了未声明的名字时 400。
        Assert.Contains(body.Tools, t => t.Name == FloatingContextTool.NameOf);
    }

    // ---- 工具本身（兜底路径）----

    /// <summary>
    /// ⚠️ **模型自己真调一次时，返回的就是注入的那份**——这是 `IToolContext.FloatingContext`
    /// 存在的全部理由：另渲染一遍会让「核心注入的」与「模型自己要来的」在同一步里分叉。
    /// </summary>
    [Fact]
    public async Task TheToolReturnsTheSameTextTheCoreInjected()
    {
        var handle = NewSession();
        _adapter.ReplyWith(_ => Calls(("c1", FloatingContextTool.NameOf, "{}")));
        _adapter.ReplyWith(_ => Say("好"));
        await _stepper.StepAsync(handle, "读一下", default);

        // ⚠️ 2026-10-23：注入的那一份**多一个首块**（`[floating_context]` 框架头，署名在形状上），
        // 所以这里比的是**正文**——「核心注入的」与「模型自己要来的」不许分叉的是那段字。
        // ⚠️ 别改成比整份文本：框架头由合成对现渲染（它追加在渲染之后，走不到 `WithFraming`），
        // 而模型自己调回来的那条靠 `Framing` 字段在渲染期加——两者在**落盘的记录**里本来就不等长。
        var injected = Results(_adapter.Calls[0]).Last();
        var injectedBody = Assert.IsType<Block.TextBlock>(injected.Content[^1]).Text;
        var executed = _store.ReadTimeline(handle.ProjectKey, handle.SessionId)
            .OfType<TimelineEvent.ToolResultEvent>().Single(r => r.CallId == "c1");

        Assert.Equal(injectedBody, TextOf(executed));
        // 署名在形状上：合成那份的首块是框架头（连同换行），而模型自调那份由 `Framing` 字段
        // 在渲染期加同一个字串——两处只借 `FloatingContextTool.Framing` 一个常量。
        Assert.Equal(FloatingContextTool.Framing.Render() + "\n",
            Assert.IsType<Block.TextBlock>(injected.Content[0]).Text);
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
    /// ⚠️ **答复路径（`ResumeAsync`）没有步开始的槽位快照**：`floating_context` 要说的话
    /// 是「答复那一刻」的那份。这里只钉「拿得到、而且是有内容的那份」——
    /// 具体内容随预设与轴走，不在这里重复钉（那是 `SlotTests` 的职责）。
    /// </summary>
    [Fact]
    public async Task ResumePath_ContextCarriesTheFloatingText()
    {
        var handle = NewSession();
        _adapter.ReplyWith(_ => Calls(("c1", FloatingContextTool.NameOf, "{}")));
        _adapter.ReplyWith(_ => Say("好"));
        await _stepper.StepAsync(handle, "读一下", default);

        var executed = _store.ReadTimeline(handle.ProjectKey, handle.SessionId)
            .OfType<TimelineEvent.ToolResultEvent>().Single(r => r.CallId == "c1");

        Assert.Contains(CorePrompts.TailOpen, TextOf(executed));
        Assert.Contains(Axes.LabelOf(Axes.Permission), TextOf(executed));
    }
}
