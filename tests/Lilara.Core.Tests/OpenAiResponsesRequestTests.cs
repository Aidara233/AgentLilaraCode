using Lilara.Core.Session;
using System.Text.Json;
using Lilara.Core.Protocol;
using Lilara.Core.Protocol.OpenAi;
using Lilara.Core.Kernel;

namespace Lilara.Core.Tests;

public class OpenAiResponsesRequestTests
{
    private static TimelineEvent Msg(Speaker from, params Block[] blocks) =>
        new TimelineEvent.MessageEvent
        {
            Id = "r1", At = DateTimeOffset.UnixEpoch, From = from, Blocks = blocks };

    private static ModelRequest Req(params TimelineEvent[] tl) => new()
    {
        Protocol = Protocols.OpenAiResponses,
        ModelId = "deepseek-flash", MaxTokens = 16000, Effort = "high", Temperature = null,
        Slots = PromptSlots.Empty, Timeline = tl,
        Tools = [],
    };

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Build_UsesMaxOutputTokensNotMaxTokens()
    {
        // Responses 的字段名是 max_output_tokens。
        var root = Parse(OpenAiResponsesRequest.Build(Req(Msg(Speaker.User, new Block.TextBlock("hi")))));

        Assert.Equal(16000, root.GetProperty("max_output_tokens").GetInt32());
        Assert.False(root.TryGetProperty("max_tokens", out _));
    }

    [Fact]
    public void Build_EveryInputItemCarriesTypeMessage()
    {
        // 少了 "type":"message" 上游直接报 upstream_error。
        var root = Parse(OpenAiResponsesRequest.Build(Req(Msg(Speaker.User, new Block.TextBlock("hi")))));

        Assert.Equal("message", root.GetProperty("input")[0].GetProperty("type").GetString());
    }

    [Fact]
    public void Build_UserBlocksAreInputText()
    {
        var root = Parse(OpenAiResponsesRequest.Build(Req(Msg(Speaker.User, new Block.TextBlock("问")))));

        var content = root.GetProperty("input")[0].GetProperty("content")[0];
        Assert.Equal("input_text", content.GetProperty("type").GetString());
        Assert.Equal("问", content.GetProperty("text").GetString());
    }

    [Fact]
    public void Build_AssistantBlocksAreOutputText()
    {
        // 助手回放用 output_text（两种都能收，但 output_text 语义正确）。
        var root = Parse(OpenAiResponsesRequest.Build(Req(
            Msg(Speaker.User, new Block.TextBlock("问")),
            Msg(Speaker.Model, new Block.TextBlock("答")))));

        var assistant = root.GetProperty("input")[1];
        Assert.Equal("assistant", assistant.GetProperty("role").GetString());
        Assert.Equal("output_text", assistant.GetProperty("content")[0].GetProperty("type").GetString());
    }

    [Fact]
    public void Build_KeepsReasoningOutOfMessageContent()
    {
        // ⚠️ 2026-10-17 改判据：推理**不再丢**（它走 reasoning 项），但它仍然**不许**进 content。
        var root = Parse(OpenAiResponsesRequest.Build(Req(Msg(Speaker.Model,
            new Block.ReasoningBlock("sig", "想想"), new Block.TextBlock("答案")))));

        var input = root.GetProperty("input");
        Assert.Equal("reasoning", input[0].GetProperty("type").GetString());
        var content = input[1].GetProperty("content");
        Assert.Equal(1, content.GetArrayLength());
        Assert.Equal("答案", content[0].GetProperty("text").GetString());
    }

    [Fact]
    public void Build_UsesInstructionsForSystem()
    {
        // Responses 的系统提示是顶层 instructions，不是一条消息。
        var request = Req(Msg(Speaker.User, new Block.TextBlock("hi"))) with
        {
            Slots = new PromptSlots([new Block.TextBlock("你是助手")], [], []),
        };

        Assert.Equal("你是助手", Parse(OpenAiResponsesRequest.Build(request)).GetProperty("instructions").GetString());
    }

    [Fact]
    public void Build_OmitsInstructionsWhenThereAreNoSystemBlocks()
    {
        // 空时不发 instructions，而不是发个空串。
        var root = Parse(OpenAiResponsesRequest.Build(Req(Msg(Speaker.User, new Block.TextBlock("hi")))));

        Assert.False(root.TryGetProperty("instructions", out _));
    }

    [Fact]
    public void Build_ThrowsOnEmptyTimeline()
    {
        Assert.Contains("时间线为空", Assert.Throws<ArgumentException>(() => OpenAiResponsesRequest.Build(Req())).Message);
    }

    [Fact]
    public void Build_ThrowsOnAnAssistantTurnWithNoContentAtAll()
    {
        // ⚠️ 2026-10-17 改形状：推理块**不再**被丢（它走 reasoning 项），所以「只有推理块」
        // 不再是空消息。真正空的是**一条块都没有**的消息。
        Assert.Contains("空消息", Assert.Throws<ArgumentException>(() =>
            OpenAiResponsesRequest.Build(Req(Msg(Speaker.User, new Block.TextBlock("hi")),
                Msg(Speaker.Model)))).Message);
    }

    [Fact]
    public void Build_EmitsReasoningAsItsOwnItem_NotAsMessageContent()
    {
        // 推理项排在消息项之前（先思考、再说话），并且正文那条 content 里没有它。
        var root = Parse(OpenAiResponsesRequest.Build(Req(
            Msg(Speaker.User, new Block.TextBlock("hi")),
            Msg(Speaker.Model, new Block.ReasoningBlock(null, "想想"), new Block.TextBlock("答")))));

        var input = root.GetProperty("input");
        Assert.Equal("reasoning", input[1].GetProperty("type").GetString());
        Assert.Equal("想想", input[1].GetProperty("summary")[0].GetProperty("text").GetString());
        Assert.Equal("message", input[2].GetProperty("type").GetString());
        Assert.Equal("答", input[2].GetProperty("content")[0].GetProperty("text").GetString());

        // ⚠️ 探针：正文项里不能夹推理——那条消息的 content 只有一个 part。
        Assert.Equal(1, input[2].GetProperty("content").GetArrayLength());
    }

    [Fact]
    public void Build_ReasoningOnlyAssistantMessageEmitsOnlyTheReasoningItem()
    {
        // 过滤掉推理块之后没有正文也没有调用 ⇒ 不造空 message 项（与「只调工具」那条同一条判据）。
        var root = Parse(OpenAiResponsesRequest.Build(Req(
            Msg(Speaker.User, new Block.TextBlock("hi")),
            Msg(Speaker.Model, new Block.ReasoningBlock(null, "只想不说")))));

        var input = root.GetProperty("input");
        Assert.Equal(2, input.GetArrayLength());
        Assert.Equal("reasoning", input[1].GetProperty("type").GetString());
    }

    // 两边是同一个表达式（自比），对「形状对不对」零信息量，只能抓不确定性（字典枚举序、时钟、随机数）。
    [Fact]
    public void Build_IsDeterministicAcrossRepeatedCalls()
    {
        var request = Req(Msg(Speaker.User, new Block.TextBlock("hi")));

        Assert.Equal(OpenAiResponsesRequest.Build(request), OpenAiResponsesRequest.Build(request));
    }

    [Fact]
    public void Build_PinsTheExactBytesInTheMeasuredKeyOrder()
    {
        // 钉死字面量（不引用常量、不重新序列化自己的输出）；载荷刻意各不相同，反转消息次序即红。
        var json = OpenAiResponsesRequest.Build(Req(
            Msg(Speaker.User, new Block.TextBlock("问")),
            Msg(Speaker.Model, new Block.TextBlock("答"))));

        Assert.Equal(
            "{\"model\":\"deepseek-flash\",\"max_output_tokens\":16000,\"input\":" +
            "[{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"问\"}]}," +
            "{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"答\"}]}]}",
            json);
    }

    [Fact]
    public void Build_PinsTheInstructionsPositionInTheKeyOrder()
    {
        // instructions 只在有系统文本时才出现，且插在 input 之前。
        var request = Req(Msg(Speaker.User, new Block.TextBlock("hi"))) with
        {
            Slots = new PromptSlots([new Block.TextBlock("你是助手")], [], []),
        };

        Assert.Equal(
            "{\"model\":\"deepseek-flash\",\"max_output_tokens\":16000,\"instructions\":\"你是助手\"," +
            "\"input\":[{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"hi\"}]}]}",
            OpenAiResponsesRequest.Build(request));
    }

    [Fact]
    public void Build_MultipleTextBlocksKeepOrderAndType()
    {
        // 一条消息里两块以上文本：每个 part 都要带 type，次序不能乱、不能并成一块。
        var root = Parse(OpenAiResponsesRequest.Build(Req(
            Msg(Speaker.Model, new Block.TextBlock("一"), new Block.TextBlock("二")))));

        var content = root.GetProperty("input")[0].GetProperty("content");
        Assert.Equal(2, content.GetArrayLength());
        Assert.Equal("output_text", content[0].GetProperty("type").GetString());
        Assert.Equal("一", content[0].GetProperty("text").GetString());
        Assert.Equal("二", content[1].GetProperty("text").GetString());
    }

    [Fact]
    public void Build_KeepsTextOrderWithAReasoningItemAroundThem()
    {
        // 推理块变成独立项后，两段正文仍然留在**同一条**消息里、顺序不变（与改动之前同一条判据）。
        var json = OpenAiResponsesRequest.Build(Req(Msg(Speaker.Model,
            new Block.TextBlock("前"), new Block.ReasoningBlock("s", "中"), new Block.TextBlock("后"))));

        Assert.Equal(
            "{\"model\":\"deepseek-flash\",\"max_output_tokens\":16000,\"input\":" +
            "[{\"type\":\"reasoning\",\"summary\":[{\"type\":\"summary_text\",\"text\":\"中\"}]}," +
            "{\"type\":\"message\",\"role\":\"assistant\",\"content\":" +
            "[{\"type\":\"output_text\",\"text\":\"前\"},{\"type\":\"output_text\",\"text\":\"后\"}]}]}",
            json);
    }

    [Fact]
    public void Build_NeverFabricatesReasoningIntoTheBody()
    {
        // ⚠️ 2026-10-17 改判据：推理文本现在**可以**出现在线上（reasoning 项里，原样回传），
        // 但**绝不许**冒充正文——「原样回传」与「伪造」的差别就在它待在哪个项里。
        var json = OpenAiResponsesRequest.Build(Req(Msg(Speaker.Model,
            new Block.ReasoningBlock("sig-==", "想想"), new Block.TextBlock("答案"))));

        Assert.Contains("\"type\":\"reasoning\"", json);
        Assert.Contains("\"text\":\"想想\"", json);

        // 正文项里只有「答案」——推理一个字都不在里面。
        var root = Parse(json);
        var messageContent = root.GetProperty("input")[1].GetProperty("content");
        Assert.Equal(1, messageContent.GetArrayLength());
        Assert.Equal("答案", messageContent[0].GetProperty("text").GetString());

        // 签名（Claude 的凭据）不属于 Responses 的 reasoning 项——不发。
        Assert.DoesNotContain("sig-==", json);
    }

    [Fact]
    public void Build_OmitsInstructionsWhenSystemBlocksHaveNoText()
    {
        // 块数 != 0 但拼出来是空串，那就是一个空的 instructions。
        var request = Req(Msg(Speaker.User, new Block.TextBlock("hi"))) with
        {
            Slots = new PromptSlots([new Block.ReasoningBlock("sig", "想想")], [], []),
        };

        var root = Parse(OpenAiResponsesRequest.Build(request));

        Assert.False(root.TryGetProperty("instructions", out _));
    }

    [Fact]
    public void Build_TakesTheProtocolFromTheOnDiskValue()
    {
        // 这条证明的是「值被透传了」，不是「拼写对」——过滤结果只被 OfType<TextBlock>() 消费，换任何拼写照样绿。
        var request = Req(Msg(Speaker.Model,
            new Block.ReasoningBlock("sig", "想想"), new Block.TextBlock("答案")))
            with { Protocol = "openai-responses" };

        var root = Parse(OpenAiResponsesRequest.Build(request));

        // 推理项在前、正文项在后（2026-10-17：推理不再被丢）。
        Assert.Equal("答案", root.GetProperty("input")[1].GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void Path_PinsTheOnDiskWireValue()
    {
        // 路径和协议串一样是线上契约，必须由字面量守，不能引用自己的常量。
        Assert.Equal("/v1/responses", OpenAiResponsesRequest.Path);
    }

    // ---- 温度 ----
    // ⚠️ 字段名两条 OpenAI 协议**都**是 `temperature`——别照抄 max_tokens→max_output_tokens 那次改名。

    [Fact]
    public void Build_WithATemperature_WritesItAsANumber()
    {
        var req = Req(Msg(Speaker.User, new Block.TextBlock("hi"))) with { Temperature = 1.5 };

        var root = Parse(OpenAiResponsesRequest.Build(req));

        Assert.Equal(1.5, root.GetProperty("temperature").GetDouble());
    }

    [Fact]
    public void Build_WithoutATemperature_OmitsTheKeyEntirely()
    {
        var root = Parse(OpenAiResponsesRequest.Build(Req(Msg(Speaker.User, new Block.TextBlock("hi")))));

        Assert.False(root.TryGetProperty("temperature", out _));
    }

    // ---- 空时间线 + 槽位 / 头两块的顺序 / 尾块（2026-10-11）----
    //
    // ⚠️ 判据与另两条链路**逐字相同**，而三条各测一遍**不是冗余**（见 Chat 那边同一条注释）。

    /// <summary>⚠️ 空时间线 + 有系统块 ⇒ 构造得出来（Responses 下 system 走顶层 `instructions`）。</summary>
    [Fact]
    public void Build_WithEmptyTimeline_ButASystemBlock_Succeeds()
    {
        var req = Req() with { Slots = new PromptSlots([new Block.TextBlock("你是助手")], [], []) };

        var root = Parse(OpenAiResponsesRequest.Build(req));

        Assert.Equal("你是助手", root.GetProperty("instructions").GetString());
        Assert.Equal(0, root.GetProperty("input").GetArrayLength());
    }

    /// <summary>⚠️ 反向控制：两个槽位都空 + 空时间线 ⇒ 仍然早失败。</summary>
    [Fact]
    public void Build_WithEmptyTimeline_AndNoSlotsAtAll_StillThrows()
    {
        var ex = Assert.Throws<ArgumentException>(() => OpenAiResponsesRequest.Build(Req()));

        Assert.Contains("时间线为空", ex.Message);
    }

    /// <summary>⚠️ 两块头槽位的顺序是契约（`01` §3.1）；Responses 下它们拼进同一个 `instructions`。</summary>
    [Fact]
    public void Build_PutsSystemBeforePremier_InTheSameInstructions()
    {
        var req = Req(Msg(Speaker.User, new Block.TextBlock("hi"))) with
        {
            Slots = new PromptSlots([new Block.TextBlock("系统")], [new Block.TextBlock("首要")], []),
        };

        var root = Parse(OpenAiResponsesRequest.Build(req));

        Assert.Equal("系统首要", root.GetProperty("instructions").GetString());
    }

    /// <summary>
    /// ⚠️ **尾块自己起一条 `message` 项**（工具结果在 Responses 下是并列的
    /// `function_call_output`，末尾通常不是 user，拼不进去）。
    /// ⚠️ 而且它**必须带 `"type":"message"`**——少了它上游报 upstream_error（本文件开头那条实测）。
    /// </summary>
    [Fact]
    public void Build_PutsTheTailInItsOwnMessageItem()
    {
        var req = Req(Msg(Speaker.User, new Block.TextBlock("hi"))) with
        {
            Slots = new PromptSlots([], [], [new Block.TextBlock("框架输入")]),
        };

        var root = Parse(OpenAiResponsesRequest.Build(req));

        var input = root.GetProperty("input");
        Assert.Equal(2, input.GetArrayLength());

        var tail = input[1];
        Assert.Equal("message", tail.GetProperty("type").GetString());
        Assert.Equal("user", tail.GetProperty("role").GetString());

        // ⚠️ 用户侧的 part 用 `input_text`（不是 `output_text`）——写错了是 400，不是静默降级。
        var part = tail.GetProperty("content")[0];
        Assert.Equal("input_text", part.GetProperty("type").GetString());
        Assert.Equal("框架输入", part.GetProperty("text").GetString());
    }

    /// <summary>⚠️ 空尾块 ⇒ 一个 input 项都不多。</summary>
    [Fact]
    public void Build_WithAnEmptyTail_AddsNoItem()
    {
        var root = Parse(OpenAiResponsesRequest.Build(Req(Msg(Speaker.User, new Block.TextBlock("hi")))));

        Assert.Equal(1, root.GetProperty("input").GetArrayLength());
    }
}
