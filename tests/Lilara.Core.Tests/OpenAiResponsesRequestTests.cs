using System.Text.Json;
using Lilara.Core.Protocol;
using Lilara.Core.Protocol.OpenAi;
using Lilara.Core.Kernel;

namespace Lilara.Core.Tests;

public class OpenAiResponsesRequestTests
{
    private static TimelineEvent Msg(Speaker from, params Block[] blocks) =>
        new TimelineEvent.MessageEvent { At = DateTimeOffset.UnixEpoch, From = from, Blocks = blocks };

    private static ModelRequest Req(params TimelineEvent[] tl) => new()
    {
        Protocol = Protocols.OpenAiResponses,
        ModelId = "deepseek-flash", MaxTokens = 16000, Effort = "high", Temperature = null,
        SystemBlocks = [], Timeline = tl,
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
    public void Build_DropsReasoningBecauseResponsesCannotCarryIt()
    {
        var root = Parse(OpenAiResponsesRequest.Build(Req(Msg(Speaker.Model,
            new Block.ReasoningBlock("sig", "想想"), new Block.TextBlock("答案")))));

        var content = root.GetProperty("input")[0].GetProperty("content");
        Assert.Equal(1, content.GetArrayLength());
        Assert.Equal("答案", content[0].GetProperty("text").GetString());
    }

    [Fact]
    public void Build_UsesInstructionsForSystem()
    {
        // Responses 的系统提示是顶层 instructions，不是一条消息。
        var request = Req(Msg(Speaker.User, new Block.TextBlock("hi"))) with
        {
            SystemBlocks = [new Block.TextBlock("你是助手")],
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
    public void Build_ThrowsWhenAMessageHasNoReplayableBlocks()
    {
        Assert.Contains("空消息", Assert.Throws<ArgumentException>(() =>
            OpenAiResponsesRequest.Build(Req(Msg(Speaker.Model, new Block.ReasoningBlock(null, "只想不说"))))).Message);
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
            SystemBlocks = [new Block.TextBlock("你是助手")],
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
    public void Build_KeepsTextOrderAroundADroppedReasoningBlock()
    {
        // 丢掉中间那块推理后，剩下的两块不能合并、顺序也不能乱。
        var json = OpenAiResponsesRequest.Build(Req(Msg(Speaker.Model,
            new Block.TextBlock("前"), new Block.ReasoningBlock("s", "中"), new Block.TextBlock("后"))));

        Assert.Equal(
            "{\"model\":\"deepseek-flash\",\"max_output_tokens\":16000,\"input\":" +
            "[{\"type\":\"message\",\"role\":\"assistant\",\"content\":" +
            "[{\"type\":\"output_text\",\"text\":\"前\"},{\"type\":\"output_text\",\"text\":\"后\"}]}]}",
            json);
    }

    [Fact]
    public void Build_NeverFabricatesReasoningIntoTheBody()
    {
        // 只断言第一个 text 块活下来不够，这条直接查整段 JSON 里没有推理痕迹。
        var json = OpenAiResponsesRequest.Build(Req(Msg(Speaker.Model,
            new Block.ReasoningBlock("sig-==", "想想"), new Block.TextBlock("答案"))));

        Assert.DoesNotContain("想想", json);
        Assert.DoesNotContain("sig-==", json);
    }

    [Fact]
    public void Build_OmitsInstructionsWhenSystemBlocksHaveNoText()
    {
        // SystemBlocks.Count != 0 但拼出来是空串，那就是一个空的 instructions。
        var request = Req(Msg(Speaker.User, new Block.TextBlock("hi"))) with
        {
            SystemBlocks = [new Block.ReasoningBlock("sig", "想想")],
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

        Assert.Equal("答案", root.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString());
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
}
