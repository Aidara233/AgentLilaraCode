using Lilara.Core.Adapters;
using Lilara.Core.Adapters.Claude;
using Lilara.Core.Canonical;
using Lilara.Core.Records;
using Anthropic.Models.Messages;
using AnthropicRole = Anthropic.Models.Messages.Role;
using CanonicalBlock = Lilara.Core.Canonical.Block;

namespace Lilara.Core.Tests;

public class ClaudeRequestBuilderTests
{
    private static TimelineEvent Msg(Speaker from, string text) => new TimelineEvent.MessageEvent
    {
        At = DateTimeOffset.UnixEpoch,
        From = from,
        Blocks = [new CanonicalBlock.TextBlock(text)],
    };

    private static ModelRequest Request(params TimelineEvent[] timeline) => new()
    {
        ModelId = "claude-opus-5",
        MaxTokens = 16000,
        Effort = "high",
        SystemBlocks = [],
        Timeline = timeline,
    };

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

        var texts = p.Messages
            .Select(m => m.Content.Value as string)
            .ToList();

        Assert.Equal(["一", "二", "三"], texts);
    }

    [Fact]
    public void Build_OmitsSystemWhenThereAreNoSystemBlocks()
    {
        // 04 §1.3：裸核心连系统提示词都没有
        var p = ClaudeRequestBuilder.Build(Request(Msg(Speaker.User, "hi")));

        Assert.Null(p.System);
    }

    [Fact]
    public void Build_EmitsSystemBlocksWhenPresent()
    {
        var request = Request(Msg(Speaker.User, "hi")) with
        {
            SystemBlocks = [new CanonicalBlock.TextBlock("你是助手")],
        };

        var p = ClaudeRequestBuilder.Build(request);

        Assert.NotNull(p.System);
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
        // Review Focus #1：空 messages 会被 API 以 400 拒绝，必须早失败
        var ex = Assert.Throws<ArgumentException>(
            () => ClaudeRequestBuilder.Build(Request()));

        Assert.Contains("时间线为空", ex.Message);
    }
}
