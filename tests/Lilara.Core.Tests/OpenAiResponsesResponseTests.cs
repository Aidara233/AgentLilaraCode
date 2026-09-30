using Lilara.Core.Adapters.OpenAi;
using Lilara.Core.Canonical;

namespace Lilara.Core.Tests;

public class OpenAiResponsesResponseTests
{
    private static string Body(string? text, string status = "completed") =>
        "{\"id\":\"resp_1\",\"object\":\"response\",\"status\":\"" + status
        + "\",\"model\":\"deepseek-flash\",\"output\":[{\"type\":\"message\",\"role\":\"assistant\","
        + "\"content\":[{\"type\":\"output_text\",\"text\":" + Json(text) + "}]}],"
        + "\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"total_tokens\":2}}";

    private static string Json(string? s) =>
        s is null ? "null" : System.Text.Json.JsonSerializer.Serialize(s);

    [Fact]
    public void Map_ExtractsOutputText()
    {
        var reply = OpenAiResponsesResponse.Map(Body("READY"));

        Assert.Equal("READY", ((Block.TextBlock)reply.Blocks[0]).Text);
        Assert.False(reply.Refused);
    }

    [Fact]
    public void Map_SurfacesStatus()
    {
        Assert.Equal("incomplete", OpenAiResponsesResponse.Map(Body("x", "incomplete")).StopReason);
    }

    [Fact]
    public void Map_SkipsNonMessageOutputItems()
    {
        // ⚠️ 这个非 message 项的形状是**构造出来的，不是网关实测形状**——目的是让
        // `type == "message"` 这条检查**成为唯一挡得住它的东西**。
        // 控制器实测过三代形状，只有这一代能钉住该检查：
        //   · `{"type":"reasoning","summary":[]}`（brief 原版，无 content 键）
        //     → 删掉 `type` 过滤后**照样绿**（被 content 守卫吸收，`type` 是死代码）
        //   · `summary:[{"type":"summary_text",…}]` → 仍绿（被 `output_text` 守卫吸收）
        //   · 本版：非 message 项、content 里装 `output_text` → 删掉 `type` 过滤后**翻红** ✓
        // ⇒ 断言顺序是 content 守卫 → type 守卫，所以只有「最内层也像 message」才试得出 type 守卫。
        var json = """
        {"id":"r","status":"completed","output":[
          {"type":"reasoning","id":"rs_1","content":[{"type":"output_text","text":"内部推理"}]},
          {"type":"message","role":"assistant","content":[{"type":"output_text","text":"答案"}]}]}
        """;

        var reply = OpenAiResponsesResponse.Map(json);

        Assert.Single(reply.Blocks);
        Assert.Equal("答案", ((Block.TextBlock)reply.Blocks[0]).Text);
    }

    [Fact]
    public void Map_HandlesEmptyOutputWithoutThrowing()
    {
        var reply = OpenAiResponsesResponse.Map("""{"id":"r","status":"incomplete","output":[]}""");

        Assert.Empty(reply.Blocks);
        Assert.Equal("incomplete", reply.StopReason);
    }

    [Fact]
    public void Map_TreatsIncompleteAsNotRefused()
    {
        // ⚠️ `incomplete` 是「没生成完」，不是拒答——别混为一谈
        Assert.False(OpenAiResponsesResponse.Map(Body("x", "incomplete")).Refused);
    }

    /// <summary>
    /// ⚠️ **实测**：`JsonElement.TryGetProperty` 在**非对象根**上抛
    /// <c>InvalidOperationException</c>（`[]` / `null` / `5` / `"hi"` 全抛，
    /// 控制器已用 brief 原版实现复现，4/4 红）。
    /// 「容忍缺失字段」必须从「根是对象」这一步就开始——否则第一个非对象响应就是一次崩溃。
    /// 这与 <see cref="OpenAiChatResponseTests.Map_ToleratesNonObjectRoot"/> 是同一条实测事实。
    /// </summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("5")]
    [InlineData("\"hi\"")]
    public void Map_ToleratesNonObjectRoot(string body)
    {
        var reply = OpenAiResponsesResponse.Map(body);

        Assert.Empty(reply.Blocks);
        Assert.Equal("unknown", reply.StopReason);
        Assert.False(reply.Refused);
    }

    /// <summary>
    /// ⚠️ 空串 `text` 与 null 是**两条不同的分支**。Task 6 实测过截断响应给的是**空串**
    /// （Chat：`max_tokens=4` → `content:""`）。`is not null` 会在这里造出一个空的 TextBlock
    /// 写进时间线——正是 `04` 要防的「空模型消息」。判据必须是 `Length > 0`。
    /// </summary>
    [Theory]
    [InlineData("""{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":""}]}]}""")]
    [InlineData("""{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":null}]}]}""")]
    [InlineData("""{"status":"completed","output":[{"type":"message","content":[{"type":"output_text"}]}]}""")]
    [InlineData("""{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":42}]}]}""")]
    public void Map_HandlesEmptyOrMissingTextAsNoBlocks(string body)
    {
        var reply = OpenAiResponsesResponse.Map(body);

        Assert.Empty(reply.Blocks);
        Assert.Equal("completed", reply.StopReason);
    }

    /// <summary>
    /// ⚠️ **实测**：`output` 数组里的**非对象项**（数字等）会让 `item.TryGetProperty` 抛
    /// `InvalidOperationException`。兄弟实现 `OpenAiChatResponse` 对 `choices[0]` 有同样的守卫，
    /// 且实测 `{"choices":[5]}` 不崩——**这是对齐，不是新增严格性**。
    /// 同一条也适用于 `content` 数组里的非对象块。
    /// </summary>
    [Theory]
    [InlineData("""{"status":"completed","output":[5]}""")]
    [InlineData("""{"status":"completed","output":[{"type":"message","content":[5]}]}""")]
    [InlineData("""{"status":"completed","output":[null]}""")]
    [InlineData("""{"status":"completed","output":[{"type":"message","content":[null]}]}""")]
    public void Map_ToleratesNonObjectItemsAndBlocks(string body)
    {
        var reply = OpenAiResponsesResponse.Map(body);

        Assert.Empty(reply.Blocks);
        Assert.Equal("completed", reply.StopReason);
    }
}
