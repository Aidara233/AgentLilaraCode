using Lilara.Core.Adapters;
using Lilara.Core.Adapters.OpenAi;
using Lilara.Core.Canonical;

namespace Lilara.Core.Tests;

/// <summary>
/// OpenAiChatResponse.Map —— 纯函数，喂硬编码 JSON 串，不碰网络。
/// ⚠️ 每条测试都必须能在行为被改坏时变红；无法变红的断言不如不写。
/// </summary>
public class OpenAiChatResponseTests
{
    // ⚠️ 这里**故意不用** brief 里的原始字符串内插形状，它编不过，而且不是换几个 `$` 就完事：
    //   · `$$"""`：JSON 末尾 `"total_tokens":2}}` 里的 `}}` 被当成内插洞收尾
    //     → CS9007「内插原始字符串字面量的开头没有足够的 "$" 字符」。
    //   · 改成 `$$$"""` 后洞是 `{{{ }}}`，可 `"content":{{{…}}}` 后面紧跟的
    //     message 收尾 `}` 会**被当成洞的一部分吃掉**，产出的 JSON 少一个 `}`，
    //     测试全数 JsonReaderException（实测 BytePositionInLine 91–139）。
    // 拼接字符串没有花括号歧义，读起来也更清楚。
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

    /// <summary>
    /// ⚠️ 实测形状：`max_tokens=4` → `content:""`（**空串，不是 null**）、
    /// `finish_reason:"length"`、`completion_tokens:4`。
    /// 空串与 null 是**两条不同的分支**——`is not null` 会在这里造出一个空的
    /// TextBlock，只有 `Length > 0` 才挡得住。断言 Blocks 为空即钉住这一点。
    /// </summary>
    [Fact]
    public void Map_HandlesEmptyStringContentAsNoBlocks()
    {
        var reply = OpenAiChatResponse.Map(Body("", "length"));

        Assert.Empty(reply.Blocks);
        Assert.Equal("length", reply.StopReason);
        Assert.False(reply.Refused);
    }

    /// <summary>
    /// ⚠️ 实测：被截断时**正文可能已经写了一部分**（英文拒答说明就被截在
    /// `…Making it at home is`）。截断不等于没正文，正文必须留住。
    /// </summary>
    [Fact]
    public void Map_KeepsPartialTextWhenTruncated()
    {
        var reply = OpenAiChatResponse.Map(Body("2 + 2 = 4", "length"));

        Assert.Equal("2 + 2 = 4", TextOf(reply));
        Assert.Equal("length", reply.StopReason);
        Assert.False(reply.Refused);
    }

    /// <summary>
    /// ⚠️ 实测形状：带 tools 请求时网关返回 `finish_reason:"tool_calls"`。
    /// 这是**正常完成**，不是拒答——把它当成拒答会让工具链永远空转。
    /// </summary>
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
        // OpenAI 文档里的拒答信号是 finish_reason: "content_filter"。
        // ⚠️ 本机网关**从未**观测到它（实测只有 stop / length / tool_calls），
        // 但它是协议定义值，保留为唯一的拒答判据——见报告。
        var reply = OpenAiChatResponse.Map(Body("", "content_filter"));

        Assert.True(reply.Refused);
        Assert.Empty(reply.Blocks);
    }

    [Fact]
    public void Map_HandlesNullContentWithoutThrowing()
    {
        // ⚠️ 实测见过：推理模型把 token 烧在思考上，content 为 **null**、
        // finish_reason 为 "length"。绝不能在这里 NRE。
        var reply = OpenAiChatResponse.Map(Body(null, "length"));

        Assert.Empty(reply.Blocks);
        Assert.Equal("length", reply.StopReason);
        Assert.False(reply.Refused);
    }

    [Fact]
    public void Map_HandlesEmptyChoicesWithoutThrowing()
    {
        // ⚠️ 上游异常时可能给空 choices——不崩
        var reply = OpenAiChatResponse.Map("""{"id":"x","choices":[]}""");

        Assert.Empty(reply.Blocks);
        Assert.Equal("unknown", reply.StopReason);
        Assert.False(reply.Refused);
    }

    /// <summary>
    /// ⚠️ 实测两种错误体，**都**没有 choices：
    ///   <c>{"error":{"type":"invalid_request_error","message":"Model 'x' not available…"}}</c>
    ///   <c>{"error":"Authentication required"}</c>
    /// 上游出什么都不能让核心崩。
    /// </summary>
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

    /// <summary>
    /// ⚠️ 缺字段的三个变体：没有 message、message 没有 content、choices[0] 根本不是对象。
    /// 任一条没有守住都会 InvalidOperationException / NRE。
    /// </summary>
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

    /// <summary>
    /// ⚠️ **实测**：`JsonElement.TryGetProperty` 在**非对象根**上抛
    /// <c>InvalidOperationException: The requested operation requires an element of type
    /// 'Object', but the target element has type 'Array'/'Null'/'Number'/'String'</c>。
    /// 也就是说 brief 里那版实现遇到 `[]` / `null` / `5` / `"hi"` 会直接崩——
    /// 「容忍缺失字段」必须先从「根是对象」开始。
    /// </summary>
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

    /// <summary>
    /// ⚠️ finish_reason 缺失 / 是 null / 不是字符串时，一律落到 "unknown"——
    /// 不能把「不知道」伪造成 "stop"。
    /// </summary>
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

    /// <summary>
    /// ⚠️ content 不是字符串（数组 / 数字）时不能崩，也不该硬塞个假文本。
    /// Chat 的 typed-part 数组形态本机网关没给过，这里只保证「不崩且不伪造」。
    /// </summary>
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

    /// <summary>
    /// 纯函数：同一 JSON 进，同一 <see cref="ModelReply"/> 出，且**不依赖调用历史**。
    /// ⚠️ 交错喂入「会改状态」的输入——先给一段非空正文，再喂空正文 / 拒答——专门钓
    /// 「静态可变状态 / 记住上一次结果」这类把纯函数做坏的写法。
    /// ⚠️ **中间那两次的结果必须断言**。实测一个「粘性正文」的坏实现（把上次的非空正文
    /// 留给下一次）：只断言首尾两次调用的话，这条测试**照样是绿的**（末尾那次本来就有
    /// 真正文）；是 `Assert.Empty(empty.Blocks)` 这一步让它翻红。
    /// ⚠️ 也不能写成 <c>Assert.Equal(first, again)</c>：<c>Blocks</c> 是
    /// <c>IReadOnlyList</c>，record 的结构相等对 List 退化成**引用**比较，那样写恒为假。
    /// ⚠️ 这条的价值在于**与测试执行顺序无关**：`Map_HandlesEmptyStringContentAsNoBlocks`
    /// 单独跑（没有前置的非空调用）时是钓不到粘性正文的（实测：单跑为绿）。
    /// </summary>
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
        // ⚠️ 写成「必须为 false」，不是 `Assert.Equal(first.Refused, again.Refused)`：
        // 后者在两边都被粘成 true 时照样通过（上面那次拒答调用正好会把它粘成 true）。
        Assert.False(again.Refused);
    }

    /// <summary>
    /// ⚠️ 推理不暴露：Chat 没有推理字段（实测返回体里没有），
    /// **绝不能**把 content 伪造成 ReasoningBlock。断言块的具体类型，不只是数量。
    /// </summary>
    [Fact]
    public void Map_NeverFabricatesReasoningBlocks()
    {
        var reply = OpenAiChatResponse.Map(Body("想想看，答案是 4。"));

        Assert.Single(reply.Blocks);
        Assert.IsType<Block.TextBlock>(reply.Blocks[0]);
    }
}
