using System.Text.Json;
using Lilara.Core.Protocol;
using Lilara.Core.Protocol.OpenAi;
using Lilara.Core.Kernel;

namespace Lilara.Core.Tests;

public class OpenAiChatRequestTests
{
    private static TimelineEvent Msg(Speaker from, params Block[] blocks) =>
        new TimelineEvent.MessageEvent
        {
            Id = "r1",
            At = DateTimeOffset.UnixEpoch, From = from, Blocks = blocks,
        };

    private static ModelRequest Req(params TimelineEvent[] tl) => new()
    {
        Protocol = Protocols.OpenAiChat,
        ModelId = "deepseek-flash", MaxTokens = 16000, Effort = "high", Temperature = null,
        SystemBlocks = [], Timeline = tl,
    };

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Build_SetsModelAndMaxTokens()
    {
        var root = Parse(OpenAiChatRequest.Build(Req(Msg(Speaker.User, new Block.TextBlock("hi")))));

        Assert.Equal("deepseek-flash", root.GetProperty("model").GetString());
        Assert.Equal(16000, root.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public void Build_MapsRoles()
    {
        var root = Parse(OpenAiChatRequest.Build(Req(
            Msg(Speaker.User, new Block.TextBlock("问")),
            Msg(Speaker.Model, new Block.TextBlock("答")))));

        var messages = root.GetProperty("messages");
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
    }

    [Fact]
    public void Build_SingleTextBlock_UsesThePlainStringForm()
    {
        // 单文本走字符串形式（协议原生形状，最省字节）。
        var root = Parse(OpenAiChatRequest.Build(Req(Msg(Speaker.User, new Block.TextBlock("你好")))));

        Assert.Equal("你好", root.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public void Build_DropsReasoningBecauseChatCannotCarryIt()
    {
        // Chat 不能承载推理：丢，且不伪造。
        var root = Parse(OpenAiChatRequest.Build(Req(Msg(Speaker.Model,
            new Block.ReasoningBlock("sig", "想想"), new Block.TextBlock("答案")))));

        Assert.Equal("答案", root.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public void Build_OmitsSystemWhenThereAreNoSystemBlocks()
    {
        // 空时不发 system 消息，而不是发个空串。
        var root = Parse(OpenAiChatRequest.Build(Req(Msg(Speaker.User, new Block.TextBlock("hi")))));

        Assert.Equal(1, root.GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public void Build_PutsSystemFirstWhenPresent()
    {
        var request = Req(Msg(Speaker.User, new Block.TextBlock("hi"))) with
        {
            SystemBlocks = [new Block.TextBlock("你是助手")],
        };

        var messages = Parse(OpenAiChatRequest.Build(request)).GetProperty("messages");

        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("你是助手", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
    }

    [Fact]
    public void Build_ThrowsOnEmptyTimeline()
    {
        var ex = Assert.Throws<ArgumentException>(() => OpenAiChatRequest.Build(Req()));
        Assert.Contains("时间线为空", ex.Message);
    }

    [Fact]
    public void Build_ThrowsWhenAMessageHasNoReplayableBlocks()
    {
        // 全是推理块的消息过滤后为空——协议不接受空 content。
        var ex = Assert.Throws<ArgumentException>(() =>
            OpenAiChatRequest.Build(Req(Msg(Speaker.Model, new Block.ReasoningBlock(null, "只想不说")))));

        Assert.Contains("空消息", ex.Message);
    }

    // 两边是同一个表达式（自比），对「形状对不对」零信息量，只能抓不确定性（字典枚举序、时钟、随机数）。
    [Fact]
    public void Build_IsDeterministicAcrossRepeatedCalls()
    {
        var request = Req(Msg(Speaker.User, new Block.TextBlock("hi")));

        Assert.Equal(OpenAiChatRequest.Build(request), OpenAiChatRequest.Build(request));
    }

    [Fact]
    public void Build_PinsTheExactBytesInTheMeasuredKeyOrder()
    {
        // 钉死字面量（不引用常量、不重新序列化自己的输出）；载荷刻意各不相同，故也顺带钉住消息顺序。
        var json = OpenAiChatRequest.Build(Req(
            Msg(Speaker.User, new Block.TextBlock("问")),
            Msg(Speaker.Model, new Block.TextBlock("答"))));

        Assert.Equal(
            "{\"model\":\"deepseek-flash\",\"max_tokens\":16000,\"messages\":" +
            "[{\"role\":\"user\",\"content\":\"问\"},{\"role\":\"assistant\",\"content\":\"答\"}]}",
            json);
    }

    [Fact]
    public void Build_MultipleTextBlocksUseTypedParts()
    {
        // 网关实测：裸字符串数组 ["一","二"] 被拒（invalid_request_error），带类型的 part 才认。
        var root = Parse(OpenAiChatRequest.Build(Req(
            Msg(Speaker.User, new Block.TextBlock("一"), new Block.TextBlock("二")))));

        var content = root.GetProperty("messages")[0].GetProperty("content");

        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("一", content[0].GetProperty("text").GetString());
        Assert.Equal("二", content[1].GetProperty("text").GetString());
    }

    // 单块走字符串快路径，上游实测也认这个形状；写成显式 ValueKind 断言是为了读起来清楚。
    [Fact]
    public void Build_SingleTextBlockStaysAPlainString()
    {
        var root = Parse(OpenAiChatRequest.Build(Req(Msg(Speaker.User, new Block.TextBlock("一")))));

        Assert.Equal(JsonValueKind.String, root.GetProperty("messages")[0].GetProperty("content").ValueKind);
        Assert.Equal("一", root.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public void Build_KeepsTextOrderAroundADroppedReasoningBlock()
    {
        // 丢掉中间那块推理后，剩下的两块不能合并、顺序不能乱；形状必须是带类型的 part（上游只认这个）。
        var json = OpenAiChatRequest.Build(Req(Msg(Speaker.Model,
            new Block.TextBlock("前"), new Block.ReasoningBlock("s", "中"), new Block.TextBlock("后"))));

        Assert.Equal(
            "{\"model\":\"deepseek-flash\",\"max_tokens\":16000,\"messages\":" +
            "[{\"role\":\"assistant\",\"content\":[" +
            "{\"type\":\"text\",\"text\":\"前\"},{\"type\":\"text\",\"text\":\"后\"}]}]}",
            json);
    }

    [Fact]
    public void Build_NeverFabricatesReasoningIntoTheBody()
    {
        // 只断言第一个 text 块活下来不够，这条直接查整段 JSON 里没有推理痕迹。
        var json = OpenAiChatRequest.Build(Req(Msg(Speaker.Model,
            new Block.ReasoningBlock("sig-==", "想想"), new Block.TextBlock("答案"))));

        Assert.DoesNotContain("想想", json);
        Assert.DoesNotContain("sig-==", json);
    }

    [Fact]
    public void Build_OmitsSystemWhenSystemBlocksHaveNoText()
    {
        // SystemBlocks.Count != 0 但拼出来是空串，那就是一个空的 system 消息。
        var request = Req(Msg(Speaker.User, new Block.TextBlock("hi"))) with
        {
            SystemBlocks = [new Block.ReasoningBlock("sig", "想想")],
        };

        var messages = Parse(OpenAiChatRequest.Build(request)).GetProperty("messages");

        Assert.Equal(1, messages.GetArrayLength());
    }

    [Fact]
    public void Build_TakesTheProtocolFromTheOnDiskValue()
    {
        // 这条证明的是「值被透传了」，不是「拼写对」——过滤结果只被 OfType<TextBlock>() 消费，换任何拼写照样绿。
        var request = Req(Msg(Speaker.Model,
            new Block.ReasoningBlock("sig", "想想"), new Block.TextBlock("答案")))
            with { Protocol = "openai-chat" };

        var root = Parse(OpenAiChatRequest.Build(request));

        Assert.Equal("答案", root.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public void Path_PinsTheOnDiskWireValue()
    {
        // 路径和协议串一样是线上契约，必须由字面量守，不能引用自己的常量。
        Assert.Equal("/v1/chat/completions", OpenAiChatRequest.Path);
    }

    // ---- 温度 ----

    [Fact]
    public void Build_WithATemperature_WritesItAsANumber()
    {
        var req = Req(Msg(Speaker.User, new Block.TextBlock("hi"))) with { Temperature = 0.7 };

        var root = Parse(OpenAiChatRequest.Build(req));

        Assert.Equal(0.7, root.GetProperty("temperature").GetDouble());
    }

    // ⚠️ `null` 必须**整个键都不出现**——不是写成 JSON 的 `null`。
    // 手搓的 `JsonObject` 不走 `WhenWritingNull`，写成 `null` 上游会 400（那是「显式设为空」）。
    // 这条同时钉住了「没设过温度的请求与加这个特性之前逐字节相同」。
    [Fact]
    public void Build_WithoutATemperature_OmitsTheKeyEntirely()
    {
        var root = Parse(OpenAiChatRequest.Build(Req(Msg(Speaker.User, new Block.TextBlock("hi")))));

        Assert.False(root.TryGetProperty("temperature", out _));
    }
}
