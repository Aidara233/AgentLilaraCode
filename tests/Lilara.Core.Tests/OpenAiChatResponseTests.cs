using Lilara.Core.Adapters;
using Lilara.Core.Adapters.OpenAi;
using Lilara.Core.Kernel;

namespace Lilara.Core.Tests;

// OpenAiChatResponse.Map —— 纯函数，喂硬编码 JSON 串，不碰网络。
public class OpenAiChatResponseTests
{
    // 不用原始字符串内插形状：$$""" 的 }} 会被当成内插洞收尾（CS9007），
    // $$$""" 又会把 message 收尾的 } 吃掉；拼接字符串没有花括号歧义。
    private static string Body(string? content, string finish = "stop") =>
        "{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion\",\"model\":\"deepseek-flash\","
        + "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":"
        + Json(content)
        + "},\"finish_reason\":\""
        + finish
        + "\"}],"
        + "\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}";

    private static string Json(string? s) =>
        s is null ? "null" : System.Text.Json.JsonSerializer.Serialize(s);

    private static string TextOf(ModelReply reply) =>
        string.Concat(reply.Blocks.OfType<Block.TextBlock>().Select(b => b.Text));

    [Fact]
    public void Map_ExtractsTheText()
    {
        var reply = OpenAiChatResponse.Map(Body("你好！"));

        Assert.Equal("你好！", ((Block.TextBlock)reply.Blocks[0]).Text);
        Assert.Equal("stop", reply.StopReason);
        Assert.False(reply.Refused);
    }

    [Fact]
    public void Map_SurfacesFinishReason()
    {
        Assert.Equal("length", OpenAiChatResponse.Map(Body("x", "length")).StopReason);
    }

    // max_tokens=4 → content:""（空串，不是 null）：is not null 会造出空 TextBlock，只有 Length > 0 挡得住。
    [Fact]
    public void Map_HandlesEmptyStringContentAsNoBlocks()
    {
        var reply = OpenAiChatResponse.Map(Body("", "length"));

        Assert.Empty(reply.Blocks);
        Assert.Equal("length", reply.StopReason);
        Assert.False(reply.Refused);
    }

    // 被截断时正文可能已经写了一部分，截断不等于没正文。
    [Fact]
    public void Map_KeepsPartialTextWhenTruncated()
    {
        var reply = OpenAiChatResponse.Map(Body("2 + 2 = 4", "length"));

        Assert.Equal("2 + 2 = 4", TextOf(reply));
        Assert.Equal("length", reply.StopReason);
        Assert.False(reply.Refused);
    }

    // finish_reason:"tool_calls" 是正常完成，不是拒答——当拒答会让工具链永远空转。
    [Fact]
    public void Map_TreatsToolCallsAsNormalCompletion()
    {
        var reply = OpenAiChatResponse.Map(Body("我查一下天气。", "tool_calls"));

        Assert.Equal("我查一下天气。", TextOf(reply));
        Assert.Equal("tool_calls", reply.StopReason);
        Assert.False(reply.Refused);
    }

    [Fact]
    public void Map_TreatsContentFilterAsRefused()
    {
        // 拒答信号是 finish_reason:"content_filter"；本机网关未观测到它，但它是协议定义值，保留为唯一的拒答判据。
        var reply = OpenAiChatResponse.Map(Body("", "content_filter"));

        Assert.True(reply.Refused);
        Assert.Empty(reply.Blocks);
    }

    [Fact]
    public void Map_HandlesNullContentWithoutThrowing()
    {
        // 分支依据是响应 schema：content 是可空字段；观测到的截断形状是空串，null 分支是防御性的。
        var reply = OpenAiChatResponse.Map(Body(null, "length"));

        Assert.Empty(reply.Blocks);
        Assert.Equal("length", reply.StopReason);
        Assert.False(reply.Refused);
    }

    [Fact]
    public void Map_HandlesEmptyChoicesWithoutThrowing()
    {
        // 上游异常时可能给空 choices——不崩。
        var reply = OpenAiChatResponse.Map("""{"id":"x","choices":[]}""");

        Assert.Empty(reply.Blocks);
        Assert.Equal("unknown", reply.StopReason);
        Assert.False(reply.Refused);
    }

    // 实测两种错误体都没有 choices；上游出什么都不能让核心崩。
    [Theory]
    [InlineData("""{"error":{"type":"invalid_request_error","message":"Model 'x' not available"}}""")]
    [InlineData("""{"error":"Authentication required"}""")]
    public void Map_ToleratesErrorBodies(string body)
    {
        var reply = OpenAiChatResponse.Map(body);

        Assert.Empty(reply.Blocks);
        Assert.Equal("unknown", reply.StopReason);
        Assert.False(reply.Refused);
    }

    // 缺字段的三个变体：没有 message、message 没有 content、choices[0] 不是对象。
    [Theory]
    [InlineData("""{"choices":[{"finish_reason":"stop"}]}""")]
    [InlineData("""{"choices":[{"message":{"role":"assistant"},"finish_reason":"stop"}]}""")]
    [InlineData("""{"choices":[1]}""")]
    public void Map_ToleratesMissingFields(string body)
    {
        var reply = OpenAiChatResponse.Map(body);

        Assert.Empty(reply.Blocks);
        Assert.False(reply.Refused);
    }

    // TryGetProperty 在非对象根上抛 InvalidOperationException，「容忍缺失字段」必须先从「根是对象」开始。
    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("5")]
    [InlineData("\"hi\"")]
    public void Map_ToleratesNonObjectRoot(string body)
    {
        var reply = OpenAiChatResponse.Map(body);

        Assert.Empty(reply.Blocks);
        Assert.Equal("unknown", reply.StopReason);
        Assert.False(reply.Refused);
    }

    // finish_reason 缺失 / null / 不是字符串时一律落 "unknown"，不能把「不知道」伪造成 "stop"。
    [Theory]
    [InlineData("""{"choices":[{"message":{"content":"hi"}}]}""")]
    [InlineData("""{"choices":[{"message":{"content":"hi"},"finish_reason":null}]}""")]
    [InlineData("""{"choices":[{"message":{"content":"hi"},"finish_reason":7}]}""")]
    public void Map_FallsBackToUnknownFinishReason(string body)
    {
        var reply = OpenAiChatResponse.Map(body);

        Assert.Equal("unknown", reply.StopReason);
        Assert.Equal("hi", TextOf(reply));
    }

    // content 不是字符串（数组 / 数字）时不能崩，也不该硬塞个假文本。
    [Theory]
    [InlineData("""{"choices":[{"message":{"content":[{"type":"text","text":"hi"}]},"finish_reason":"stop"}]}""")]
    [InlineData("""{"choices":[{"message":{"content":42},"finish_reason":"stop"}]}""")]
    public void Map_ToleratesNonStringContent(string body)
    {
        var reply = OpenAiChatResponse.Map(body);

        Assert.Empty(reply.Blocks);
        Assert.Equal("stop", reply.StopReason);
        Assert.False(reply.Refused);
    }

    // 纯函数：不依赖调用历史；中间两次的结果必须断言，否则「粘性正文」的坏实现照样绿。
    // 也不能写 Assert.Equal(first, again)：Blocks 是 IReadOnlyList，record 的结构相等对 List 退化成引用比较。
    [Fact]
    public void Map_IsPureAcrossInterleavedCalls()
    {
        var first = OpenAiChatResponse.Map(Body("甲"));

        var empty = OpenAiChatResponse.Map(Body("", "length"));
        Assert.Empty(empty.Blocks);

        var refusal = OpenAiChatResponse.Map(Body("", "content_filter"));
        Assert.True(refusal.Refused);

        var again = OpenAiChatResponse.Map(Body("甲"));

        Assert.Equal("甲", TextOf(first));
        Assert.Equal(TextOf(first), TextOf(again));
        Assert.Single(again.Blocks);
        Assert.Equal(first.StopReason, again.StopReason);
        // 用 Assert.False，不用 Assert.Equal(first.Refused, again.Refused)：后者在两边都被粘成 true 时照样通过。
        Assert.False(again.Refused);
    }

    // Chat 不暴露推理，绝不能把 content 伪造成 ReasoningBlock；断言块的具体类型，不只是数量。
    [Fact]
    public void Map_NeverFabricatesReasoningBlocks()
    {
        var reply = OpenAiChatResponse.Map(Body("想想看，答案是 4。"));

        Assert.Single(reply.Blocks);
        Assert.IsType<Block.TextBlock>(reply.Blocks[0]);
    }
}
