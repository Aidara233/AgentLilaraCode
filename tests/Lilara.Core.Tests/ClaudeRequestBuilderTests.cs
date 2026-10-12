using Lilara.Core.Session;
using System.Text.Json;
using Lilara.Core.Protocol;
using Lilara.Core.Protocol.Claude;
using Lilara.Core.Kernel;
using Anthropic.Models.Messages;
using AnthropicRole = Anthropic.Models.Messages.Role;
using CanonicalBlock = Lilara.Core.Kernel.Block;

namespace Lilara.Core.Tests;

public class ClaudeRequestBuilderTests
{
    private static TimelineEvent Msg(Speaker from, string text) => new TimelineEvent.MessageEvent
    {
            Id = "r1",
        At = DateTimeOffset.UnixEpoch,
        From = from,
        Blocks = [new CanonicalBlock.TextBlock(text)],
    };

    private static ModelRequest Request(params TimelineEvent[] timeline) => new()
    {
        ModelId = "claude-opus-5",
        MaxTokens = 16000,
        Protocol = Protocols.ClaudeMessages,
        Effort = "high",
        Temperature = null,
        Slots = PromptSlots.Empty,
        Tools = [],
        Timeline = timeline,
    };

    // ---- 温度 ----
    // ⚠️ 这里用的是 SDK 的 `Temperature`——它在 12.51.0 里被标了 `[Obsolete]`
    // （原文：4.6 之后的模型不支持设温度，只有 1.0 被接受，其余 400）。
    // 而本项目实际用的**网关实测接受**它，所以适配器照发（理由见 `ClaudeRequestBuilder.Build` 的注释）。
    // 测试跟着那条决定走：这里断的就是「发得出去」。
#pragma warning disable CS0618
    [Fact]
    public void Build_PassesTemperature()
    {
        var built = ClaudeRequestBuilder.Build(
            Request(Msg(Speaker.User, "hi")) with { Temperature = 0.3 });

        Assert.Equal(0.3, built.Temperature);
    }

    [Fact]
    public void Build_WithoutATemperature_LeavesItNull()
    {
        var built = ClaudeRequestBuilder.Build(Request(Msg(Speaker.User, "hi")));

        Assert.Null(built.Temperature);
    }
#pragma warning restore CS0618

    // 取出消息的内容块列表；推理块必须与文本块按顺序一起回放，string 快路径表达不了。
    private static IReadOnlyList<ContentBlockParam> BlocksOf(MessageParam m)
    {
        Assert.True(m.Content.TryPickContentBlockParams(out var blocks));
        return blocks!;
    }

    private static string TextOf(ContentBlockParam b)
    {
        Assert.True(b.TryPickText(out var text));
        return text!.Text;
    }

    private static string FirstText(MessageParam m) => TextOf(BlocksOf(m)[0]);

    [Fact]
    public void Build_PassesModelAndMaxTokens()
    {
        var p = ClaudeRequestBuilder.Build(Request(Msg(Speaker.User, "hi")));

        Assert.Equal("claude-opus-5", p.Model.Raw());
        Assert.Equal(16000, p.MaxTokens);
    }

    [Fact]
    public void Build_MapsUserToUserRole()
    {
        var p = ClaudeRequestBuilder.Build(Request(Msg(Speaker.User, "你好")));

        Assert.Single(p.Messages);
        Assert.Equal(AnthropicRole.User, p.Messages[0].Role.Value());
    }

    [Fact]
    public void Build_MapsModelToAssistantRole()
    {
        var p = ClaudeRequestBuilder.Build(Request(
            Msg(Speaker.User, "你好"),
            Msg(Speaker.Model, "你好！")));

        Assert.Equal(2, p.Messages.Count);
        Assert.Equal(AnthropicRole.Assistant, p.Messages[1].Role.Value());
    }

    [Fact]
    public void Build_PreservesOrder()
    {
        var p = ClaudeRequestBuilder.Build(Request(
            Msg(Speaker.User, "一"),
            Msg(Speaker.Model, "二"),
            Msg(Speaker.User, "三")));

        var texts = p.Messages.Select(FirstText).ToList();

        Assert.Equal(["一", "二", "三"], texts);
    }

    // 推理块逐字回传（含签名）且顺序不能乱——严格上游的硬要求。
    [Fact]
    public void Build_ReplaysReasoningBlocksVerbatimBeforeText()
    {
        var modelTurn = new TimelineEvent.MessageEvent
        {
            Id = "r1",
            At = DateTimeOffset.UnixEpoch,
            From = Speaker.Model,
            Blocks =
            [
                new CanonicalBlock.ReasoningBlock("sig-==", "想想"),
                new CanonicalBlock.TextBlock("答案"),
            ],
        };

        var p = ClaudeRequestBuilder.Build(Request(Msg(Speaker.User, "问"), modelTurn));

        var blocks = BlocksOf(p.Messages[1]);
        Assert.Equal(2, blocks.Count);

        // 签名一个字符都不能动。
        Assert.True(blocks[0].TryPickThinking(out var thinking));
        Assert.Equal("想想", thinking!.Thinking);
        Assert.Equal("sig-==", thinking.Signature);

        Assert.True(blocks[1].TryPickText(out var text));
        Assert.Equal("答案", text!.Text);
    }

    // 拒答不写模型事件，于是时间线上会连着两条 user；API 参考自相矛盾，故构造期自己合并成一条。
    [Fact]
    public void Build_MergesAdjacentSameRoleEvents()
    {
        var p = ClaudeRequestBuilder.Build(Request(
            Msg(Speaker.User, "一"),
            Msg(Speaker.User, "二")));

        Assert.Single(p.Messages);
        Assert.Equal(AnthropicRole.User, p.Messages[0].Role.Value());

        var blocks = BlocksOf(p.Messages[0]);
        Assert.Equal(2, blocks.Count);
        Assert.Equal("一", FirstText(p.Messages[0]));
        Assert.True(blocks[1].TryPickText(out var second));
        Assert.Equal("二", second!.Text);
    }

    // 合并只发生在相邻处。
    [Fact]
    public void Build_DoesNotMergeAcrossADifferentRole()
    {
        var p = ClaudeRequestBuilder.Build(Request(
            Msg(Speaker.User, "一"),
            Msg(Speaker.Model, "二"),
            Msg(Speaker.User, "三")));

        Assert.Equal(3, p.Messages.Count);
    }

    // 同一份输入必须构造出逐字节相同的请求。
    [Fact]
    public void Build_IsPure()
    {
        var request = Request(
            Msg(Speaker.User, "一"),
            Msg(Speaker.User, "二"),
            Msg(Speaker.Model, "三"));

        var first = JsonSerializer.Serialize(ClaudeRequestBuilder.Build(request), Json.Options);
        var second = JsonSerializer.Serialize(ClaudeRequestBuilder.Build(request), Json.Options);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Build_OmitsSystemWhenThereAreNoSystemBlocks()
    {
        // 裸核心连系统提示词都没有。
        var p = ClaudeRequestBuilder.Build(Request(Msg(Speaker.User, "hi")));

        Assert.Null(p.System);
    }

    [Fact]
    public void Build_EmitsSystemBlocksWhenPresent()
    {
        var request = Request(Msg(Speaker.User, "hi")) with
        {
            Slots = new PromptSlots([new CanonicalBlock.TextBlock("你是助手")], [], []),
        };

        var p = ClaudeRequestBuilder.Build(request);

        // MessageCreateParamsSystem 是 union（string | 块列表），只能 TryPick 出来。
        Assert.True(p.System!.TryPickTextBlockParams(out var systemBlocks));
        Assert.Equal("你是助手", systemBlocks![0].Text);
    }

    // ---- 空时间线：**有槽位撑着就是合法的**（2026-10-11）----
    //
    // ⚠️ 本批之前这里**无条件抛**（「空 messages 会被 API 400 拒绝」）。那句是对的一半，
    // 而它挡的正是「新会话的第一步」——虽然生产路径上用户消息先落了盘（所以时间线非空），
    // 但那条不变量的**依据**是调用次序，不是协议。
    // ⇒ 这两条把「什么算合法的空请求」钉在协议那一层，而不是钉在调用方碰巧的次序上。

    /// <summary>
    /// ⚠️ **空时间线 + 有系统块 ⇒ 构造得出来**。实测（2026-10-11，本机网关）：
    /// Claude 形状下 `system` + `messages: []` 回 HTTP 200 并正常作答。
    /// </summary>
    [Fact]
    public void Build_WithEmptyTimeline_ButASystemBlock_Succeeds()
    {
        var request = Request() with
        {
            Slots = new PromptSlots([new CanonicalBlock.TextBlock("你是助手")], [], []),
        };

        var p = ClaudeRequestBuilder.Build(request);

        Assert.Empty(p.Messages);
        Assert.True(p.System!.TryPickTextBlockParams(out var systemBlocks));
        Assert.Equal("你是助手", systemBlocks![0].Text);
    }

    /// <summary>
    /// ⚠️ **两个槽位都空 + 空时间线 ⇒ 仍然早失败**（裸的空请求发不出去）。
    /// 这条是上一条的**反向控制**：只钉「不抛」的话，把守卫整个删掉也绿。
    /// </summary>
    [Fact]
    public void Build_WithEmptyTimeline_AndNoSlotsAtAll_StillThrows()
    {
        var ex = Assert.Throws<ArgumentException>(() => ClaudeRequestBuilder.Build(Request()));

        Assert.Contains("时间线为空", ex.Message);
    }

    // ---- 尾槽位：拼进末尾那条 user 消息（2026-10-11）----

    /// <summary>
    /// ⚠️ **时间线以 user 结尾 ⇒ 尾块<b>拼进</b>那条消息**（不新开一条）。
    ///
    /// <para>判据是「末尾是不是 user」这**一条**，而这条测试钉的是它的**真支**。
    /// 假支在下面那条。</para>
    /// </summary>
    [Fact]
    public void Build_MergesTheTailBlock_IntoTheLastUserMessage()
    {
        var request = Request(Msg(Speaker.User, "hi")) with
        {
            Slots = new PromptSlots([], [], [new CanonicalBlock.TextBlock("框架输入")]),
        };

        var p = ClaudeRequestBuilder.Build(request);

        var last = Assert.Single(p.Messages);
        Assert.Equal(AnthropicRole.User, last.Role.Value());

        // ⚠️ 断言的是**两块**（原文 + 尾块）且**顺序**如此——
        // 只断言「尾块在」的话，「把尾块插到正文前面」那种实现照样绿，而那是另一件事。
        Assert.Equal(["hi", "框架输入"], BlocksOf(last).Select(TextOf).ToList());
    }

    /// <summary>
    /// ⚠️ **时间线以 assistant 结尾 ⇒ 尾块<b>自己起一条</b> user 消息**。
    ///
    /// <para>这条是上一条的**另一半判据**。⚠️ 而它今天**在生产里够不着**
    /// （每轮末尾是工具结果，工具结果住 user 角色）——够不着不等于可以不测：
    /// 「末尾是不是 user」若被写成恒 true，上面那条照样绿，而一旦哪天出现
    /// 以 assistant 结尾的时间线，尾块就会**被拼进 assistant 消息**
    /// ——那时它不再是「框架输入」，而是**模型自己说过的话**。</para>
    /// </summary>
    [Fact]
    public void Build_GivesTheTailItsOwnMessage_WhenTheTimelineEndsWithTheModel()
    {
        var request = Request(Msg(Speaker.User, "hi"), Msg(Speaker.Model, "你好")) with
        {
            Slots = new PromptSlots([], [], [new CanonicalBlock.TextBlock("框架输入")]),
        };

        var p = ClaudeRequestBuilder.Build(request);

        // 三条：用户那条 · 模型那条 · **尾块自己那条**。
        Assert.Equal(3, p.Messages.Count);
        Assert.Equal(AnthropicRole.User, p.Messages[0].Role.Value());
        Assert.Equal(AnthropicRole.Assistant, p.Messages[1].Role.Value());
        Assert.Equal(AnthropicRole.User, p.Messages[2].Role.Value());

        // ⚠️ 尾块那一条**只有它自己**——别把模型那条的内容一起卷进来。
        Assert.Equal("框架输入", FirstText(p.Messages[2]));
        // ⚠️ 而模型那条**不许**被追加（它是 assistant，尾块拼进去就变成「模型自己说的话」）。
        Assert.Equal("你好", FirstText(p.Messages[1]));
    }

    /// <summary>
    /// ⚠️ **空尾块 ⇒ 请求与加它之前相同**（与 `Temperature` / `Tools` 同一条判据：
    /// 没配过的东西不许改变前缀的字节）。这条是「尾块恒在」那个误解的解毒剂。
    /// </summary>
    [Fact]
    public void Build_WithAnEmptyTail_IsUnchanged()
    {
        var withEmptyTail = ClaudeRequestBuilder.Build(
            Request(Msg(Speaker.User, "hi")) with { Slots = new PromptSlots([], [], []) });

        var withoutAnySlots = ClaudeRequestBuilder.Build(Request(Msg(Speaker.User, "hi")));

        Assert.Equal(withoutAnySlots.Messages.Count, withEmptyTail.Messages.Count);
        Assert.Null(withEmptyTail.System);
        Assert.Equal("hi", FirstText(withEmptyTail.Messages[0]));
        Assert.Equal(FirstText(withoutAnySlots.Messages[0]), FirstText(withEmptyTail.Messages[0]));
    }

    /// <summary>
    /// ⚠️ **两块头槽位的顺序是契约**：系统块在前、首要块在后（`01` §3.1
    /// 「头槽位 = [系统提示词块][首要提示词块]」）。
    /// 倒过来会让「位置 0 是核心的字节、插件不可注入」那条前提**静默**作废。
    /// </summary>
    [Fact]
    public void Build_PutsSystemBeforePremier()
    {
        var request = Request(Msg(Speaker.User, "hi")) with
        {
            Slots = new PromptSlots(
                [new CanonicalBlock.TextBlock("系统")],
                [new CanonicalBlock.TextBlock("首要")],
                []),
        };

        var p = ClaudeRequestBuilder.Build(request);

        Assert.True(p.System!.TryPickTextBlockParams(out var blocks));
        Assert.Equal(["系统", "首要"], blocks!.Select(b => b.Text));
    }

    [Theory]
    [InlineData("low", Effort.Low)]
    [InlineData("medium", Effort.Medium)]
    [InlineData("high", Effort.High)]
    [InlineData("xhigh", Effort.Xhigh)]
    [InlineData("max", Effort.Max)]
    public void Build_MapsEveryEffortLevel(string effort, Effort expected)
    {
        var request = Request(Msg(Speaker.User, "hi")) with { Effort = effort };

        var p = ClaudeRequestBuilder.Build(request);

        Assert.Equal(expected, p.OutputConfig!.Effort!.Value());
    }

    [Fact]
    public void Build_RejectsUnknownEffortInsteadOfSilentlyDowngrading()
    {
        // 静默降级会把拼写错误（如 "xhihg"）变成 High——最难查的一类 bug。
        var request = Request(Msg(Speaker.User, "hi")) with { Effort = "xhihg" };

        Assert.Throws<ArgumentException>(() => ClaudeRequestBuilder.Build(request));
    }

    [Fact]
    public void Build_SetsEffortOnOutputConfig()
    {
        var p = ClaudeRequestBuilder.Build(Request(Msg(Speaker.User, "hi")));

        Assert.NotNull(p.OutputConfig);
    }

    [Fact]
    public void Build_ThrowsOnEmptyTimeline()
    {
        // 空 messages 会被 API 以 400 拒绝，必须早失败。
        var ex = Assert.Throws<ArgumentException>(
            () => ClaudeRequestBuilder.Build(Request()));

        Assert.Contains("时间线为空", ex.Message);
    }

    [Fact]
    public void Build_ThrowsOnMessageWithNoBlocks()
    {
        // Content = [] 会被 API 400 拒绝；Blocks 是普通 required 列表，没有非空不变量，能真走到这里。
        var empty = new TimelineEvent.MessageEvent
        {
            Id = "r1",
            At = DateTimeOffset.UnixEpoch,
            From = Speaker.User,
            Blocks = [],
        };

        Assert.Throws<ArgumentException>(() => ClaudeRequestBuilder.Build(Request(empty)));
    }
}
