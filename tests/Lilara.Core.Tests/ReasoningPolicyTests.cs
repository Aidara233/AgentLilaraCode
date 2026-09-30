using Lilara.Core.Canonical;

namespace Lilara.Core.Tests;

public class ReasoningPolicyTests
{
    [Theory]
    [InlineData("claude-messages", true)]
    [InlineData("openai-chat", false)]
    [InlineData("openai-responses", false)]
    public void CanCarry_MatchesWhatEachProtocolActuallyCarries(string protocol, bool expected)
    {
        // ⚠️ 实测：Claude 的 content 里有 thinking 块；Chat 只有 content；
        // Responses 的 output 只有 message/output_text。后两者**不暴露推理**。
        Assert.Equal(expected, ReasoningPolicy.CanCarry(protocol));
    }

    [Fact]
    public void CanReplayBlock_RequiresASignature()
    {
        // ⚠️ **实测发现，本任务的核心**：
        // `ThinkingBlockParam.Signature` 与 `.Thinking` 都是 `required`，且该类**只有无参构造器**
        // ——「不带 signature 的 thinking 块」在 Anthropic 线上**无法表达**。
        // 而本机网关的 deepseek-flash **不返回 signature**。
        // ⇒ 无签名的推理块，**发不出去**。这不是我们保守，是协议本身没有那个形状。
        Assert.True(ReasoningPolicy.CanReplayBlock(new Block.ReasoningBlock("sig", "想"), "claude-messages"));
        Assert.False(ReasoningPolicy.CanReplayBlock(new Block.ReasoningBlock(null, "想"), "claude-messages"));
    }

    [Fact]
    public void FilterFor_DropsUnsignedReasoningEvenForClaude()
    {
        // ⚠️ 修正后的行为：签名缺失 → 发不出去 → 丢。
        // 本机网关的 deepseek-flash 就落在这个分支里——**每一条推理都被丢**。
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
        var blocks = new List<Block>
        {
            new Block.ReasoningBlock("s", "想"),
            new Block.TextBlock("正文"),
            new Block.ReasoningBlock("s2", "再想"),
        };

        var filtered = ReasoningPolicy.FilterFor(blocks, "claude-messages");

        Assert.Equal(["reasoning", "text", "reasoning"],
            filtered.Select(b => b is Block.ReasoningBlock ? "reasoning" : "text"));
    }

    [Fact]
    public void FilterFor_DoesNotFabricateReasoningForProtocolsThatLackIt()
    {
        // ⚠️ ApiPlan spec §10.2：不能回传的协议**不伪造**——
        // 丢就是丢，不能把推理文本塞进 text 块冒充正文。
        var blocks = new List<Block> { new Block.ReasoningBlock(null, "悄悄想的话") };

        Assert.Empty(ReasoningPolicy.FilterFor(blocks, "openai-chat"));
    }
}
