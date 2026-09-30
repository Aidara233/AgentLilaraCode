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
    public void CanCarry_IsKeyedOnTheOnDiskProtocolStrings()
    {
        // ⚠️ 同样故意写字面量：这两个字符串会落盘，改动即迁移。
        // 若这里改用 Protocols.* 常量，篡改常量就没人发现了（见 AdapterFactoryTests 的同名理由）。
        Assert.False(ReasoningPolicy.CanCarry("openai-chat"));
        Assert.False(ReasoningPolicy.CanCarry("openai-responses"));
        Assert.True(ReasoningPolicy.CanCarry("claude-messages"));
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
    public void CanReplayBlock_RejectsAnEmptySignatureToo()
    {
        // ⚠️ 空串与 null 在上游眼里同义：**没有签名**。
        // `is not null` 会让它通过，于是发出 `"signature":""`——
        // 值逃出了它的约束，正是这个项目已经付过五次代价的那类缺陷。
        Assert.False(ReasoningPolicy.CanReplayBlock(new Block.ReasoningBlock("", "想"), "claude-messages"));
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
        // ⚠️ 原测试断言的是 ["reasoning","text","reasoning"]——**回文**，
        // 反转实现照样绿，等于没测「保序」。
        // 所以按**载荷**断言，且做成不对称的形状。
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
        // ⚠️ 这条覆盖原测试漏掉的那半：**丢弃之后仍然保序**。
        // 无签名的那块被丢掉，但剩下的相对顺序不能乱。
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

    /// <summary>把块映射回它自己的载荷文本——比 kind 标签更能区分顺序。</summary>
    private static string Label(Block b) => b switch
    {
        Block.TextBlock t => t.Text,
        Block.ReasoningBlock r => r.Text,
        _ => "?",
    };

    [Fact]
    public void FilterFor_DoesNotFabricateReasoningForProtocolsThatLackIt()
    {
        // ⚠️ ApiPlan spec §10.2：不能回传的协议**不伪造**——
        // 丢就是丢，不能把推理文本塞进 text 块冒充正文。
        var blocks = new List<Block> { new Block.ReasoningBlock(null, "悄悄想的话") };

        Assert.Empty(ReasoningPolicy.FilterFor(blocks, "openai-chat"));
    }
}
