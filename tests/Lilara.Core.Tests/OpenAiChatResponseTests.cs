using Lilara.Core.Protocol;
using Lilara.Core.Protocol.OpenAi;
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
        Assert.Equal(1, reply.Usage?.InputTokens);
        Assert.Equal(1, reply.Usage?.OutputTokens);
        Assert.Equal(2, reply.Usage?.TotalTokens);
        Assert.Equal("deepseek-flash", reply.ReportedModel);
        Assert.Equal("chatcmpl-1", reply.ResponseId);
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

    // Chat 不把推理放进 content 块的位置——但 DeepSeek 的 `reasoning_content` 是**另一个字段**，
    // 必须原样读进记录，否则下一步回传不了（400 `must be passed back`，2026-10-17）。
    [Fact]
    public void Map_ReadsReasoningContentAsAReasoningBlock()
    {
        var reply = OpenAiChatResponse.Map(
            """{"choices":[{"message":{"role":"assistant","content":"答","reasoning_content":"想了"},"finish_reason":"stop"}]}""");

        Assert.Equal(2, reply.Blocks.Count);
        var reasoning = Assert.IsType<Block.ReasoningBlock>(reply.Blocks[0]);
        Assert.Equal("想了", reasoning.Text);
        Assert.Null(reasoning.Signature);          // Chat 没有签名这回事
        Assert.Equal(ReasoningKind.Text, reasoning.Kind);
        Assert.Equal("答", Assert.IsType<Block.TextBlock>(reply.Blocks[1]).Text);
    }

    [Fact]
    public void Map_KeepsAnEmptyReasoningContentAsNoBlock()
    {
        // 空串 = 「思考了但没有正文」⇒ 不造块（与 content 的 Length > 0 同一条判据）。
        // 它的回传兜底由请求侧给空串（ReasoningPolicy），不需要记录里有一个空块。
        var reply = OpenAiChatResponse.Map(
            """{"choices":[{"message":{"role":"assistant","content":"答","reasoning_content":""},"finish_reason":"stop"}]}""");

        Assert.Single(reply.Blocks);
        Assert.IsType<Block.TextBlock>(reply.Blocks[0]);
    }

    // ---- usage：缓存明细与输入口径（2026-10-20）----
    //
    // ⚠️ 这一组的形状来自**真实账本**（`~/.lilara/usage.jsonl`，本机网关，2026-10-18）：
    // 网关同时给 `prompt_tokens_details.cached_tokens` 与平级的 `prompt_cache_hit_tokens`。
    // 旧的解析器只认 `cached_tokens` / `cache_read_input_tokens`（Anthropic 的名字）⇒ **每一笔都是 null**，
    // 于是统计页把「缓存读取 0」当成事实显示出来。下面第一条抓的就是这个「看不见的 0」。

    [Fact]
    public void Map_ReadsDeepSeekCacheHitTokensFromTheDetailsObject()
    {
        var reply = OpenAiChatResponse.Map("""
            {"choices":[{"message":{"role":"assistant","content":"答"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":3867,"completion_tokens":78,"total_tokens":3945,
                      "prompt_tokens_details":{"cached_tokens":3456},
                      "completion_tokens_details":{"reasoning_tokens":69}}}
            """);

        // 归一后输入是**未命中**部分（3867 - 3456），与缓存读取不相交。
        Assert.Equal(411, reply.Usage?.InputTokens);
        Assert.Equal(3456, reply.Usage?.CacheReadTokens);
        Assert.Equal(3945, reply.Usage?.TotalTokens);   // 上游报的 total 原样保留（3867 全量 + 78）
        Assert.Equal(78, reply.Usage?.OutputTokens);
        // 推理明细的旧字段名是 `completion_tokens_details`（不是 `output_tokens_details`）——
        // 只认后者会让 DeepSeek 的推理 token 一直是 null。
        Assert.Equal(69, reply.Usage?.ReasoningTokens);
    }

    [Fact]
    public void Map_ReadsTheFlatPromptCacheHitTokens_WhenDetailsAreAbsent()
    {
        // 实测账本里并不是每一次都带 details：49 笔带 `prompt_cache_hit_tokens`，53 笔带 `cached_tokens`。
        // ⇒ 两条路都要读，缺一条就有一坨调用静默变成「未报告」。
        var reply = OpenAiChatResponse.Map("""
            {"choices":[{"message":{"role":"assistant","content":"答"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":1000,"completion_tokens":20,"total_tokens":1020,
                      "prompt_cache_hit_tokens":900,"prompt_cache_miss_tokens":100}}
            """);

        Assert.Equal(900, reply.Usage?.CacheReadTokens);
        Assert.Equal(100, reply.Usage?.InputTokens);
    }

    [Fact]
    public void Map_PrefersTheDetailsObject_WhenBothCacheFieldsArePresent()
    {
        var reply = OpenAiChatResponse.Map("""
            {"choices":[{"message":{"role":"assistant","content":"答"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":1000,"completion_tokens":20,"total_tokens":1020,
                      "prompt_tokens_details":{"cached_tokens":640},
                      "prompt_cache_hit_tokens":900}}
            """);

        // 「正式字段优先」：平级的是网关的附加信息，details 才是协议定义的形状。
        Assert.Equal(640, reply.Usage?.CacheReadTokens);
        Assert.Equal(360, reply.Usage?.InputTokens);
    }

    [Fact]
    public void Map_ReportsZeroInput_WhenEverythingWasCached()
    {
        var reply = OpenAiChatResponse.Map("""
            {"choices":[{"message":{"role":"assistant","content":"答"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":1200,"completion_tokens":10,"total_tokens":1210,
                      "prompt_tokens_details":{"cached_tokens":1200}}}
            """);

        // 全部命中 ⇒ 未命中输入是 **0**（上游确实这么报的），不是 null。
        // 两者的区别：0 能算命中率（100%），null 算不出来（未报告）。
        Assert.Equal(1200, reply.Usage?.CacheReadTokens);
        Assert.Equal(0, reply.Usage?.InputTokens);
    }

    [Fact]
    public void Map_KeepsCacheUnknown_WhenTheUpstreamDoesNotReportIt()
    {
        // 「没报」必须与「报了 0」区分：两者都让命中率算不出来 vs 算成 0%，含义完全不同。
        var reply = OpenAiChatResponse.Map(Body("答"));

        Assert.Null(reply.Usage?.CacheReadTokens);
        Assert.Equal(1, reply.Usage?.InputTokens);
        Assert.Null(new Lilara.Core.Storage.UsageCall
        {
            CallId = "c", ProviderId = "p", ModelId = "m", Protocol = Protocols.OpenAiChat,
            StartedAt = DateTimeOffset.UnixEpoch, Status = "completed", Usage = reply.Usage,
        }.CacheHitRate);
    }

    [Fact]
    public void Map_DerivesTotalFromInputAndOutput_WhenTheUpstreamOmitsIt()
    {
        var reply = OpenAiChatResponse.Map("""
            {"choices":[{"message":{"role":"assistant","content":"答"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":1000,"completion_tokens":20,
                      "prompt_tokens_details":{"cached_tokens":900}}}
            """);

        // 1000 是归一前的全量；归一后 100 未命中 + 20 输出 ≠ 1020 的直觉来源——
        // total 由**上游原值**推出（1000 + 20），不是由归一后的输入推出。见 UsageJson 的注释。
        Assert.Equal(1020, reply.Usage?.TotalTokens);
    }
}
