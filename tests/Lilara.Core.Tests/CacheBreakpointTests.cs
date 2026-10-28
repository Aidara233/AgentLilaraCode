using System.Text.Json.Nodes;
using Lilara.Core.Kernel;
using Lilara.Core.Protocol;
using Lilara.Core.Protocol.Claude;
using Lilara.Core.Protocol.OpenAi;
using System.Text.Json;
using Lilara.Core.Session;

namespace Lilara.Core.Tests;

/// <summary>
/// **显式缓存断点**（2026-10-28）：三族协议各自的方言，以及一条共同的判据——
/// 断点必须落在**每步都变的东西之前**（今天唯一每步变的是末尾那对浮动读数）。
///
/// <para>⚠️ 这套能力**默认关**（`ProviderDefinition.CacheMode` 为 <c>null</c> / <c>auto</c>）：
/// 三族对缓存的默认行为差得很远（Anthropic 没隐式、OpenAI 有隐式但只有 5.6+ 认断点、
/// DeepSeek 系自动），按端点开才有意义。关着的时候三条链路发出的字节必须与加这个字段之前相同。</para>
/// </summary>
public sealed class CacheBreakpointTests
{
    private static ModelRequest Request(IReadOnlyList<TimelineEvent> timeline) => new()
    {
        ModelId = "m",
        MaxTokens = 100,
        Effort = "high",
        Temperature = null,
        Protocol = Protocols.ClaudeMessages,
        Slots = new PromptSlots(
            System: [new Block.TextBlock("SYS")],
            Premier: [new Block.TextBlock("PREMIER")],
            Tail: []),
        Tools = [],
        Timeline = timeline,
    };

    private static TimelineEvent.MessageEvent Say(string id, Speaker from, string text) => new()
    {
        Id = id, At = CoreClock.Now(), From = from, Blocks = [new Block.TextBlock(text)],
    };

    private static TimelineEvent.ToolResultEvent Result(string id, string text) => new()
    {
        Id = id, At = CoreClock.Now(), CallId = id, Content = [new Block.TextBlock(text)],
    };

    /// <summary>
    /// ⚠️ **位置判据**：末尾那对浮动读数（id 以 `floating-` 开头）不进断点，断点落在它之前那条消息上。
    /// 这条定理是整套东西的地基——断点落在浮动读数之后，等于把「每步都变」写进缓存前缀。
    /// </summary>
    [Fact]
    public void LastStableIndex_SkipsTheVolatileReadoutTail()
    {
        var timeline = new TimelineEvent[]
        {
            Say("u1", Speaker.User, "你好"),
            Say("m1", Speaker.Model, "在"),
            Say("floating-3-call", Speaker.Model, "读数"),
            Result("floating-3-result", "读数正文"),
        };

        Assert.Equal(1, CacheBreakpoint.LastStableIndex(timeline));
        Assert.True(CacheBreakpoint.IsVolatile(timeline[2]));
        Assert.False(CacheBreakpoint.IsVolatile(timeline[0]));
    }

    /// <summary>⚠️ 只有工具结果、没有可挂的消息 ⇒ -1（调用方据此**不开** explicit）。</summary>
    [Fact]
    public void LastStableIndex_ReturnsMinusOne_WhenNothingMarkable()
    {
        var timeline = new TimelineEvent[]
        {
            Result("r1", "工具输出"),
            Result("floating-2-result", "读数正文"),
        };

        Assert.Equal(-1, CacheBreakpoint.LastStableIndex(timeline));
    }

    // ---- 三族各自的方言 ----

    [Fact]
    public void Claude_MarksSystemAndLastStableMessage_ButNotTheReadout()
    {
        var parameters = ClaudeRequestBuilder.Build(Request([
                Say("u1", Speaker.User, "你好"),
                Say("floating-9-call", Speaker.Model, "读数"),
                Result("floating-9-result", "读数正文"),
        ]), explicitCache: true);
        // ⚠️ 只取**线上那一份**：SDK 的参数对象会把自己的属性再序列化一遍，直接 Serialize 会把断点数成两倍。
        var json = parameters.RawBodyData is { } raw ? JsonSerializer.Serialize(raw) : JsonSerializer.Serialize(parameters);

        // ⚠️ 数**带值的**那种：SDK 会把没设的 `cache_control` 也序列化成 `null` 一并吐出。
        // system 与断点消息各一个；浮动读数那两段一个都没有。
        const string Ephemeral = "\"cache_control\":{\"type\":\"ephemeral\"}";
        Assert.True(Count(json, Ephemeral) == 2, json.Substring(0, Math.Min(1200, json.Length)));
    }

    [Fact]
    public void Claude_WithoutExplicitCache_HasNoCacheControl()
    {
        var parameters = ClaudeRequestBuilder.Build(
            Request([Say("u1", Speaker.User, "你好")]), explicitCache: false);
        var json = parameters.RawBodyData is { } raw ? JsonSerializer.Serialize(raw) : JsonSerializer.Serialize(parameters);

        Assert.DoesNotContain("cache_control", json, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenAiChat_MarksSystemAndLastStableMessage_AndEnablesExplicitMode()
    {
        var json = OpenAiChatRequest.Build(Request([
            Say("u1", Speaker.User, "你好"),
            Say("floating-9-call", Speaker.Model, "读数"),
            Result("floating-9-result", "读数正文"),
        ]), explicitCache: true);

        Assert.Contains("prompt_cache_options", json, StringComparison.Ordinal);
        Assert.Contains("\"mode\":\"explicit\"", json, StringComparison.Ordinal);
        // system + 那条 user 消息，两个断点。
        Assert.Equal(2, Count(json, "prompt_cache_breakpoint"));
    }

    [Fact]
    public void OpenAiChat_WithoutExplicitCache_KeepsImplicitCaching()
    {
        var json = OpenAiChatRequest.Build(Request([Say("u1", Speaker.User, "你好")]), explicitCache: false);

        Assert.DoesNotContain("prompt_cache_options", json, StringComparison.Ordinal);
        Assert.DoesNotContain("prompt_cache_breakpoint", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ **打不上历史断点就不开 explicit**：那个模式会关掉隐式缓存，只留 header 断点
    /// 反而比隐式更差。这里用「只有工具结果」的时间线模拟。
    /// </summary>
    [Fact]
    public void OpenAiChat_WithoutAMarkableMessage_LeavesImplicitCachingAlone()
    {
        var json = OpenAiChatRequest.Build(Request([Result("r1", "工具输出")]), explicitCache: true);

        Assert.DoesNotContain("prompt_cache_options", json, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenAiResponses_MarksTheLastStableMessagePart_AndEnablesExplicitMode()
    {
        var json = OpenAiResponsesRequest.Build(Request([
            Say("u1", Speaker.User, "你好"),
            Say("floating-9-call", Speaker.Model, "读数"),
            Result("floating-9-result", "读数正文"),
        ]), explicitCache: true);

        Assert.Contains("prompt_cache_options", json, StringComparison.Ordinal);
        Assert.Equal(1, Count(json, "prompt_cache_breakpoint"));
    }

    [Fact]
    public void OpenAiResponses_WithoutExplicitCache_HasNoBreakpoint()
    {
        var json = OpenAiResponsesRequest.Build(Request([Say("u1", Speaker.User, "你好")]), explicitCache: false);

        Assert.DoesNotContain("prompt_cache_options", json, StringComparison.Ordinal);
        Assert.DoesNotContain("prompt_cache_breakpoint", json, StringComparison.Ordinal);
    }

    /// <summary>⚠️ `function_call_output` 上的断点官方说「接受但不写缓存」⇒ 这条路径必须跳过它。</summary>
    [Fact]
    public void OpenAiResponses_DoesNotMarkFunctionCallOutput()
    {
        var input = new JsonArray(
            new JsonObject
            {
                ["type"] = "function_call_output",
                ["call_id"] = "c1",
                ["output"] = "[{\"type\":\"input_text\",\"text\":\"x\"}]",
            });

        Assert.False(OpenAiResponsesCache.Mark(input));
    }

    private static int Count(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            n++;
        return n;
    }
}
