using System.Text.Json;
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
        // ⚠️ 字面量而非常量：`Protocols` 常量类要到 Task 4 才建，Task 4 顺手换掉。
        Protocol = "claude-messages",
        Effort = "high",
        SystemBlocks = [],
        Timeline = timeline,
    };

    /// <summary>
    /// 取出消息的内容块列表。
    /// ⚠️ 构造期**总是**块列表——单条文本的 string 快路径已经去掉了，
    /// 因为推理块必须和文本块一起按顺序回放，快路径表达不了这件事。
    /// </summary>
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

    /// <summary>
    /// ⚠️ 00 §5 铁律：推理块**逐字回传**（含签名），顺序不能乱。
    /// 这是多轮 tool use 的前提，也是严格上游的硬要求。
    /// </summary>
    [Fact]
    public void Build_ReplaysReasoningBlocksVerbatimBeforeText()
    {
        var modelTurn = new TimelineEvent.MessageEvent
        {
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

        // 签名一个字符都不能动
        Assert.True(blocks[0].TryPickThinking(out var thinking));
        Assert.Equal("想想", thinking!.Thinking);
        Assert.Equal("sig-==", thinking.Signature);

        Assert.True(blocks[1].TryPickText(out var text));
        Assert.Equal("答案", text!.Text);
    }

    /// <summary>
    /// 拒答不写模型事件，于是「问 → 拒答 → 再问」在时间线上是**连着两条 user**。
    /// API 参考自相矛盾（README 说同角色会被合并；错误表把 `roles must alternate`
    /// 列为 400）——不赌歧义，构造期自己合并成一条。
    /// </summary>
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

    /// <summary>合并只发生在**相邻**处——中间隔着模型回话的两条 user 不能并。</summary>
    [Fact]
    public void Build_DoesNotMergeAcrossADifferentRole()
    {
        var p = ClaudeRequestBuilder.Build(Request(
            Msg(Speaker.User, "一"),
            Msg(Speaker.Model, "二"),
            Msg(Speaker.User, "三")));

        Assert.Equal(3, p.Messages.Count);
    }

    /// <summary>纯函数（00 §5 铁律 1）：同一份输入必须构造出逐字节相同的请求。</summary>
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

        // MessageCreateParamsSystem 是 union（string | 块列表），只能 TryPick 出来
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
        // ⚠️ 静默降级会把 "xhihg" 这种拼写错误变成 High——最难查的一类 bug
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
        // Review Focus #1：空 messages 会被 API 以 400 拒绝，必须早失败
        var ex = Assert.Throws<ArgumentException>(
            () => ClaudeRequestBuilder.Build(Request()));

        Assert.Contains("时间线为空", ex.Message);
    }

    [Fact]
    public void Build_ThrowsOnMessageWithNoBlocks()
    {
        // 与空时间线同类：Content = [] 会被 API 400 拒绝，也必须早失败。
        // Blocks 是普通 required 列表，没有非空不变量，所以真能走到这里
        var empty = new TimelineEvent.MessageEvent
        {
            At = DateTimeOffset.UnixEpoch,
            From = Speaker.User,
            Blocks = [],
        };

        Assert.Throws<ArgumentException>(() => ClaudeRequestBuilder.Build(Request(empty)));
    }
}
