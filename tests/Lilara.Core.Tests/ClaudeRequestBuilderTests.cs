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
        SystemBlocks = [],
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

    private static string FirstText(MessageParam m)
    {
        Assert.True(BlocksOf(m)[0].TryPickText(out var text));
        return text!.Text;
    }

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
            SystemBlocks = [new CanonicalBlock.TextBlock("你是助手")],
        };

        var p = ClaudeRequestBuilder.Build(request);

        // MessageCreateParamsSystem 是 union（string | 块列表），只能 TryPick 出来。
        Assert.True(p.System!.TryPickTextBlockParams(out var systemBlocks));
        Assert.Equal("你是助手", systemBlocks![0].Text);
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
