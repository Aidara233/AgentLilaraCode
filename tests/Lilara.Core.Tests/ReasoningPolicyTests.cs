using Lilara.Core.Protocol;
using Lilara.Core.Kernel;

namespace Lilara.Core.Tests;

public class ReasoningPolicyTests
{
    [Theory]
    [InlineData("claude-messages", true)]
    [InlineData("openai-chat", false)]
    [InlineData("openai-responses", false)]
    public void CanCarry_MatchesWhatEachProtocolActuallyCarries(string protocol, bool expected)
    {
        // Claude 的 content 带 thinking 块；Chat/Responses 不暴露推理。
        Assert.Equal(expected, ReasoningPolicy.CanCarry(protocol));
    }

    [Fact]
    public void CanCarry_IsKeyedOnTheOnDiskProtocolStrings()
    {
        // CanCarry 对未知协议也返回 false（`_ => false` 兜底），故断言必须落在只有正确分支才给出的答案上。
        Assert.True(ReasoningPolicy.CanCarry("claude-messages"));
        Assert.False(ReasoningPolicy.CanCarry("openai-chat"));
        Assert.False(ReasoningPolicy.CanCarry("openai-responses"));
    }

    [Fact]
    public void CanCarry_DistinguishesAKnownProtocolFromAnUnknownOne()
    {
        // 已知协议的 false 与未知协议的 false 不可区分，故这条只钉「已知与未知行为是否不同」。
        Assert.NotEqual(
            ReasoningPolicy.CanCarry("claude-messages"),
            ReasoningPolicy.CanCarry("definitely-not-a-protocol"));
    }

    [Fact]
    public void CanReplayBlock_RequiresASignature()
    {
        // ThinkingBlockParam.Signature 是 required 且该类只有无参构造器 ⇒ 无签名的推理块构造不出来、发不出去。
        // 但上游其实收（约束在客户端库，不在上游）；本机网关的 deepseek-flash 不返回 signature。
        Assert.True(ReasoningPolicy.CanReplayBlock(new Block.ReasoningBlock("sig", "想"), "claude-messages"));
        Assert.False(ReasoningPolicy.CanReplayBlock(new Block.ReasoningBlock(null, "想"), "claude-messages"));
    }

    [Fact]
    public void CanReplayBlock_RejectsAnEmptySignatureToo()
    {
        // 空串与 null 在上游眼里同义：没有签名。
        Assert.False(ReasoningPolicy.CanReplayBlock(new Block.ReasoningBlock("", "想"), "claude-messages"));
    }

    [Fact]
    public void FilterFor_DropsUnsignedReasoningEvenForClaude()
    {
        // 本机网关的 deepseek-flash 落在这个分支里——每一条推理都被丢。
        var blocks = new List<Block>
        {
            new Block.ReasoningBlock(null, "无签名的思考"),
            new Block.TextBlock("答案"),
        };

        var filtered = ReasoningPolicy.FilterFor(blocks, "claude-messages");

        Assert.Single(filtered);
        Assert.IsType<Block.TextBlock>(filtered[0]);
    }

    [Fact]
    public void FilterFor_DropsReasoningWhenTheProtocolCannotCarryIt()
    {
        var blocks = new List<Block>
        {
            new Block.ReasoningBlock("sig", "想想"),
            new Block.TextBlock("答案"),
        };

        var filtered = ReasoningPolicy.FilterFor(blocks, "openai-chat");

        Assert.Single(filtered);
        Assert.IsType<Block.TextBlock>(filtered[0]);
    }

    [Fact]
    public void FilterFor_KeepsReasoningForClaude()
    {
        var blocks = new List<Block>
        {
            new Block.ReasoningBlock("sig", "想想"),
            new Block.TextBlock("答案"),
        };

        Assert.Equal(2, ReasoningPolicy.FilterFor(blocks, "claude-messages").Count);
    }

    [Fact]
    public void FilterFor_PreservesOrder()
    {
        // 断言按载荷、做成不对称的形状，否则反转实现照样绿。
        var blocks = new List<Block>
        {
            new Block.ReasoningBlock("s1", "甲"),
            new Block.TextBlock("乙"),
            new Block.ReasoningBlock("s2", "丙"),
            new Block.TextBlock("丁"),
        };

        var filtered = ReasoningPolicy.FilterFor(blocks, "claude-messages");

        Assert.Equal(["甲", "乙", "丙", "丁"], filtered.Select(Label));
    }

    [Fact]
    public void FilterFor_PreservesOrderAcrossADrop()
    {
        // 丢弃之后剩下的相对顺序不能乱。
        var blocks = new List<Block>
        {
            new Block.TextBlock("甲"),
            new Block.ReasoningBlock(null, "被丢的"),   // 无签名 → 丢
            new Block.TextBlock("乙"),
            new Block.ReasoningBlock("s", "丙"),
        };

        var filtered = ReasoningPolicy.FilterFor(blocks, "claude-messages");

        Assert.Equal(["甲", "乙", "丙"], filtered.Select(Label));
    }

    // 把块映射回它自己的载荷文本，比 kind 标签更能区分顺序。
    private static string Label(Block b) => b switch
    {
        Block.TextBlock t => t.Text,
        Block.ReasoningBlock r => r.Text,
        _ => "?",
    };

    [Fact]
    public void FilterFor_DoesNotFabricateReasoningForProtocolsThatLackIt()
    {
        // 不能回传的协议不伪造：丢就是丢，不把推理文本塞进 text 块冒充正文。
        var blocks = new List<Block> { new Block.ReasoningBlock(null, "悄悄想的话") };

        Assert.Empty(ReasoningPolicy.FilterFor(blocks, "openai-chat"));
    }

    // ---- reasoning_content：**字段**回不回传，与上面的「块进不进 content」是两回事 ----
    //
    // 把这两个问题并在一个判据里，就是 DeepSeek 那条 400 的来源（2026-10-17 用户实测）。

    [Theory]
    [InlineData("openai-chat", true, true)]    // 带工具的 Chat：必须回传
    [InlineData("openai-chat", false, false)]  // 纯聊天的 Chat：不需要
    [InlineData("openai-responses", true, false)]   // Responses 走 reasoning 项，没这个字段
    [InlineData("claude-messages", true, false)]    // Claude 走 thinking 块
    [InlineData("definitely-not-a-protocol", true, false)]
    public void CanReplayAsReasoningContent_IsKeyedOnChatPlusTools(
        string protocol, bool carriesTools, bool expected)
    {
        Assert.Equal(expected, ReasoningPolicy.CanReplayAsReasoningContent(protocol, carriesTools));
    }

    [Fact]
    public void ReasoningContentOf_ConcatenatesTheReasoningBlocksInOrder()
    {
        var blocks = new List<Block>
        {
            new Block.ReasoningBlock(null, "先想"),
            new Block.TextBlock("答"),
            new Block.ReasoningBlock(null, "再想"),
        };

        Assert.Equal("先想再想", ReasoningPolicy.ReasoningContentOf(blocks, "openai-chat", carriesTools: true));
    }

    [Fact]
    public void ReasoningContentOf_ReturnsAnEmptyStringWhenTheHistoryHasNoReasoning()
    {
        // 兜底那一半：不是 null（null = 不写这个键），而是空串（=「这一轮没留下思考」）。
        var blocks = new List<Block> { new Block.TextBlock("答") };

        Assert.Equal("", ReasoningPolicy.ReasoningContentOf(blocks, "openai-chat", carriesTools: true));
    }

    [Fact]
    public void ReasoningContentOf_SkipsRedactedPayloads()
    {
        // redacted 是加密载荷，不能当正文回传——与 Claude 的 redacted_thinking 同理。
        var blocks = new List<Block> { new Block.ReasoningBlock(null, "ZW5j", ReasoningKind.Redacted) };

        Assert.Equal("", ReasoningPolicy.ReasoningContentOf(blocks, "openai-chat", carriesTools: true));
    }

    [Fact]
    public void ReasoningContentOf_ReturnsNullWhenTheFieldMustNotBeWritten()
    {
        Assert.Null(ReasoningPolicy.ReasoningContentOf(
            [new Block.ReasoningBlock(null, "想")], "openai-chat", carriesTools: false));
        Assert.Null(ReasoningPolicy.ReasoningContentOf(
            [new Block.ReasoningBlock(null, "想")], "claude-messages", carriesTools: true));
    }
}
