using Lilara.Core.Adapters.Claude;
using Anthropic.Models.Messages;
using CanonicalBlock = Lilara.Core.Canonical.Block;

namespace Lilara.Core.Tests;

public class ClaudeResponseMapperTests
{
    // ⚠️ SDK 12.51.0 的 Usage 把 9 个成员全标了 required（含可空的缓存/服务层字段）。
    // 测试只关心 token 计数，其余按「没有」显式置空，别让对象初始化器漏项。
    private static Usage UsageOf(long outputTokens) => new()
    {
        InputTokens = 1,
        OutputTokens = outputTokens,
        CacheCreation = null,
        CacheCreationInputTokens = null,
        CacheReadInputTokens = null,
        InferenceGeo = null,
        OutputTokensDetails = null,
        ServerToolUse = null,
        ServiceTier = null,
    };

    private static Message Response(string text, string stopReason) => new()
    {
        ID = "msg_test",
        Model = "claude-opus-5",
        StopReason = stopReason,
        Content = [new ContentBlock(new TextBlock { Text = text, Citations = [] })],
        Usage = UsageOf(1),
        Container = null,
        Diagnostics = null,
        StopDetails = null,
        StopSequence = null,
    };

    [Fact]
    public void Map_ExtractsTextBlocks()
    {
        var reply = ClaudeResponseMapper.Map(Response("你好！", "end_turn"));

        Assert.Single(reply.Blocks);
        Assert.Equal("你好！", ((CanonicalBlock.TextBlock)reply.Blocks[0]).Text);
        Assert.False(reply.Refused);
    }

    [Fact]
    public void Map_SurfacesStopReason()
    {
        var reply = ClaudeResponseMapper.Map(Response("hi", "max_tokens"));

        Assert.Equal("max_tokens", reply.StopReason);
    }

    [Fact]
    public void Map_FlagsRefusalAndToleratesEmptyContent()
    {
        // Review Focus #2：拒答时 content 可能为空，绝不能在这里炸
        var refusal = new Message
        {
            ID = "msg_test",
            Model = "claude-opus-5",
            StopReason = "refusal",
            Content = [],
            Usage = UsageOf(0),
            Container = null,
            Diagnostics = null,
            StopDetails = null,
            StopSequence = null,
        };

        var reply = ClaudeResponseMapper.Map(refusal);

        Assert.True(reply.Refused);
        Assert.Empty(reply.Blocks);
    }

    /// <summary>
    /// ⚠️ 这条测试原先断言的是**错的行为**（推理块被忽略）——它把 00 §5 铁律的违反钉住了。
    /// 现在断言相反的事实：推理块逐字保留，签名原样。
    /// </summary>
    [Fact]
    public void Map_KeepsReasoningBlocksWithTheirSignature()
    {
        var withThinking = new Message
        {
            ID = "msg_test",
            Model = "claude-opus-5",
            StopReason = "end_turn",
            Content =
            [
                new ContentBlock(new ThinkingBlock { Thinking = "想想", Signature = "sig" }),
                new ContentBlock(new TextBlock { Text = "答案", Citations = [] }),
            ],
            Usage = UsageOf(1),
            Container = null,
            Diagnostics = null,
            StopDetails = null,
            StopSequence = null,
        };

        var reply = ClaudeResponseMapper.Map(withThinking);

        // ⚠️ 顺序是 [reasoning, text]，且必须原样保住——上游会校验签名与顺序
        Assert.Equal(2, reply.Blocks.Count);
        var reasoning = Assert.IsType<CanonicalBlock.ReasoningBlock>(reply.Blocks[0]);
        Assert.Equal("想想", reasoning.Text);
        Assert.Equal("sig", reasoning.Signature);
        Assert.Equal("答案", ((CanonicalBlock.TextBlock)reply.Blocks[1]).Text);
    }
}
