using Lilara.Core.Protocol.OpenAi;
using Lilara.Core.Kernel;

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
        // 断言顺序是 content 守卫 → type 守卫，故只有「最内层也像 message」的非 message 项才试得出 type 守卫。
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
        // incomplete 是「没生成完」，不是拒答。
        Assert.False(OpenAiResponsesResponse.Map(Body("x", "incomplete")).Refused);
    }

    // TryGetProperty 在非对象根上抛 InvalidOperationException，「容忍缺失字段」必须从「根是对象」开始。
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

    // 空串 text 与 null 是两条不同分支：截断响应给的是空串，is not null 会造出空 TextBlock；判据必须是 Length > 0。
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

    // 空数组（output:[] / content:[]）是合法输入，必须解析得通；按例断言块数，否则三例在断言上无从区分。
    [Theory]
    [InlineData("""{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"ok"}]}]}""", 1)]
    [InlineData("""{"status":"completed","output":[{"type":"message","content":[]}]}""", 0)]
    [InlineData("""{"status":"completed","output":[]}""", 0)]
    public void Map_ToleratesEmptyArrays(string body, int expectedBlocks)
    {
        var reply = OpenAiResponsesResponse.Map(body);

        Assert.Equal(expectedBlocks, reply.Blocks.Count);
        Assert.False(reply.Refused);
        Assert.Equal("completed", reply.StopReason);
    }

    // output / content 不是数组时也必须不崩：少了 ValueKind == Array 守卫，
    // 非数组输入会在 EnumerateArray 上抛 InvalidOperationException。
    // 钉住一个守卫要跑两次（变异版必须红、正确版必须绿）；被钉住的守卫见
    // Map_ToleratesNonObjectRoot / Map_ToleratesNonStringStatus / Map_ToleratesNonObjectItemsAndBlocks /
    // Map_HandlesEmptyOrMissingTextAsNoBlocks / Map_ToleratesMissingKeys。
    [Theory]
    [InlineData("""{"status":"completed","output":5}""")]
    [InlineData("""{"status":"completed","output":"str"}""")]
    [InlineData("""{"status":"completed","output":null}""")]
    [InlineData("""{"status":"completed","output":[{"type":"message","content":5}]}""")]
    [InlineData("""{"status":"completed","output":[{"type":"message","content":"str"}]}""")]
    [InlineData("""{"status":"completed","output":[{"type":"message","content":null}]}""")]
    public void Map_ToleratesNonArrayOutputAndContent(string body)
    {
        var reply = OpenAiResponsesResponse.Map(body);

        Assert.Empty(reply.Blocks);
        Assert.Equal("completed", reply.StopReason);
    }

    // status 不是字符串时也必须不崩且落到 "unknown"；删掉 ValueKind == String 那半句会在 GetString() 上抛。
    // status:null 不列入——GetString() 对 Null 返回 null 走 ?? "unknown"，两条路径同值，钉不住。
    [Theory]
    [InlineData("""{"status":5,"output":[]}""")]
    [InlineData("""{"status":["a"],"output":[]}""")]
    [InlineData("""{"status":{"a":1},"output":[]}""")]
    [InlineData("""{"status":true,"output":[]}""")]
    public void Map_ToleratesNonStringStatus(string body)
    {
        var reply = OpenAiResponsesResponse.Map(body);

        Assert.Equal("unknown", reply.StopReason);
    }

    // 缺键（不是缺值、不是类型错）也必须不崩：TryGetProperty 换成 GetProperty 会抛 KeyNotFoundException。
    // 这些形状的依据是「schema 允许」而不是「观测到的」——网关当时全返 upstream_error，这些形状从未被观测到过。
    // 断言只取共同不变量（不产出块、不判拒答）：缺 status 时 StopReason 是 "unknown"、不缺时是 "completed"。
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"id":"r"}""")]
    [InlineData("""{"status":"completed"}""")]
    [InlineData("""{"output":[]}""")]
    [InlineData("""{"status":"completed","output":[{}]}""")]
    [InlineData("""{"status":"completed","output":[{"type":"message"}]}""")]
    [InlineData("""{"status":"completed","output":[{"type":"message","content":[{}]}]}""")]
    [InlineData("""{"status":"completed","output":[{"type":"message","content":[{"type":"input_text","text":"x"}]}]}""")]
    public void Map_ToleratesMissingKeys(string body)
    {
        var reply = OpenAiResponsesResponse.Map(body);

        Assert.Empty(reply.Blocks);
        Assert.False(reply.Refused);
    }

    // output 数组里的非对象项会让 item.TryGetProperty 抛 InvalidOperationException；
    // OpenAiChatResponse 对 choices[0] 有同样的守卫，这是对齐，不是新增严格性。
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
