using System.Text.Json;
using Lilara.Core.Kernel;
using Lilara.Core.Protocol;
using Lilara.Core.Protocol.OpenAi;
using Block = Lilara.Core.Kernel.Block;

namespace Lilara.Core.Tests;

/// <summary>
/// 工具在两条 OpenAI 协议上的四条线。
///
/// <para>⚠️ <b>这组测试的另一半价值是钉住「两条协议形状不同」</b>——Chat 与 Responses
/// 的字段名、嵌套、甚至「一条消息产出几项」都不一样，而互相抄的表现是**上游 400**
/// 或者更坏的**静默少发**。本仓栽过「多条副本只改一处」，这两条是并列的副本，
/// 所以各自钉一遍。</para>
/// </summary>
public class OpenAiToolTests
{
    private static ModelRequest Req(string protocol, IReadOnlyList<TimelineEvent> timeline, IReadOnlyList<ToolSpec>? tools = null) => new()
    {
        ModelId = "deepseek-flash",
        MaxTokens = 16000,
        Protocol = protocol,
        Effort = "high",
        Temperature = null,
        SystemBlocks = [],
        Tools = tools ?? [],
        Timeline = timeline,
    };

    private static TimelineEvent.MessageEvent ModelMsg(params Block[] blocks) => new()
    {
        Id = "m1", At = DateTimeOffset.UnixEpoch, From = Speaker.Model, Blocks = blocks,
    };

    private static TimelineEvent.MessageEvent UserMsg(string text) => new()
    {
        Id = "u1", At = DateTimeOffset.UnixEpoch, From = Speaker.User, Blocks = [new Block.TextBlock(text)],
    };

    private static TimelineEvent.ToolResultEvent Result(string callId, string text) => new()
    {
        Id = "r1", At = DateTimeOffset.UnixEpoch, CallId = callId, Content = [new Block.TextBlock(text)],
    };

    private static ToolSpec Spec() =>
        new("read", "读一个文件", """{"type":"object","properties":{"path":{"type":"string"}}}""");

    private static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement;

    // ---- Chat ----

    [Fact]
    public void Chat_Tools_AreSentNestedUnderFunction()
    {
        var tools = Body(OpenAiChatRequest.Build(Req(Protocols.OpenAiChat, [UserMsg("hi")], [Spec()])))
            .GetProperty("tools");

        var tool = tools[0];
        Assert.Equal("function", tool.GetProperty("type").GetString());
        // ⚠️ Chat **多一层 `function`**（Responses 是扁平的）——这是两条协议的第一处形状差异。
        Assert.Equal("read", tool.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("object",
            tool.GetProperty("function").GetProperty("parameters").GetProperty("type").GetString());
    }

    [Fact]
    public void Chat_NoTools_TheKeyIsAbsent()
    {
        Assert.False(Body(OpenAiChatRequest.Build(Req(Protocols.OpenAiChat, [UserMsg("hi")])))
            .TryGetProperty("tools", out _));
    }

    // ⚠️ Chat 把调用挂在 assistant 消息的 `tool_calls` 字段上，且 `arguments` 是**字符串**。
    [Fact]
    public void Chat_ToolCalls_HangOnTheAssistantMessage()
    {
        var call = new Block.ToolUseBlock("call_1", "read", """{"path":"a.txt"}""");

        var messages = Body(OpenAiChatRequest.Build(
            Req(Protocols.OpenAiChat, [UserMsg("读一下"), ModelMsg(call), Result("call_1", "内容")])))
            .GetProperty("messages");

        var assistant = messages.EnumerateArray().Single(m => m.GetProperty("role").GetString() == "assistant");
        var tc = assistant.GetProperty("tool_calls")[0];
        Assert.Equal("call_1", tc.GetProperty("id").GetString());
        // ⚠️ **字符串**，不是对象——逐字原文塞进去。
        Assert.Equal("""{"path":"a.txt"}""", tc.GetProperty("function").GetProperty("arguments").GetString());
    }

    // ⚠️ 只调工具、不说话的 assistant 消息：content 必须是 `null`（省略这个键上游会当成格式错误）。
    [Fact]
    public void Chat_AssistantWithOnlyToolCalls_HasNullContent()
    {
        var call = new Block.ToolUseBlock("call_1", "read", "{}");

        var messages = Body(OpenAiChatRequest.Build(
            Req(Protocols.OpenAiChat, [UserMsg("x"), ModelMsg(call), Result("call_1", "c")])))
            .GetProperty("messages");

        var assistant = messages.EnumerateArray().Single(m => m.GetProperty("role").GetString() == "assistant");
        Assert.Equal(JsonValueKind.Null, assistant.GetProperty("content").ValueKind);
    }

    // ⚠️ 结果在 Chat 里是**独立的一条消息**（role: tool），不是塞进 user 消息的块。
    [Fact]
    public void Chat_ToolResult_IsItsOwnToolMessage()
    {
        var call = new Block.ToolUseBlock("call_1", "read", "{}");

        var messages = Body(OpenAiChatRequest.Build(
            Req(Protocols.OpenAiChat, [UserMsg("x"), ModelMsg(call), Result("call_1", "内容")])))
            .GetProperty("messages");

        var tool = messages.EnumerateArray().Single(m => m.GetProperty("role").GetString() == "tool");
        Assert.Equal("call_1", tool.GetProperty("tool_call_id").GetString());   // ⚠️ 字段名是 tool_call_id
        Assert.Equal("内容", tool.GetProperty("content").GetString());
    }

    [Fact]
    public void Chat_Response_ToolCalls_BecomeCanonicalBlocks()
    {
        var reply = OpenAiChatResponse.Map("""
            {"choices":[{"finish_reason":"tool_calls","message":{
              "content":null,
              "tool_calls":[{"id":"call_1","type":"function",
                "function":{"name":"read","arguments":"{\"path\":\"a.txt\"}"}}]}}]}
            """);

        Assert.Equal("tool_calls", reply.StopReason);
        var use = Assert.IsType<Block.ToolUseBlock>(Assert.Single(reply.Blocks));
        Assert.Equal("call_1", use.CallId);
        Assert.Equal("read", use.Name);
        // ⚠️ **逐字**：协议给的就是字符串，中间没有人解析过它。
        Assert.Equal("""{"path":"a.txt"}""", use.Arguments);
    }

    // 文本与调用同时在 ⇒ 两者都要留下来，且文本在前（那是响应里的自然次序）。
    [Fact]
    public void Chat_Response_TextAndToolCalls_AreBothKept()
    {
        var reply = OpenAiChatResponse.Map("""
            {"choices":[{"finish_reason":"tool_calls","message":{
              "content":"我读一下",
              "tool_calls":[{"id":"c1","type":"function","function":{"name":"read","arguments":"{}"}}]}}]}
            """);

        Assert.Equal(2, reply.Blocks.Count);
        Assert.Equal("我读一下", Assert.IsType<Block.TextBlock>(reply.Blocks[0]).Text);
        Assert.IsType<Block.ToolUseBlock>(reply.Blocks[1]);
    }

    // 缺 id 的调用**跳过**：没有 id 就没法配对，落进记录是悬空的 tool_use ⇒ 下一步 400。
    [Fact]
    public void Chat_Response_ToolCallWithoutId_IsSkipped()
    {
        var reply = OpenAiChatResponse.Map("""
            {"choices":[{"finish_reason":"tool_calls","message":{
              "tool_calls":[{"type":"function","function":{"name":"read","arguments":"{}"}}]}}]}
            """);

        Assert.Empty(reply.Blocks);
    }

    // ---- Responses ----

    [Fact]
    public void Responses_Tools_AreFlat_NotNested()
    {
        var tools = Body(OpenAiResponsesRequest.Build(Req(Protocols.OpenAiResponses, [UserMsg("hi")], [Spec()])))
            .GetProperty("tools");

        var tool = tools[0];
        // ⚠️ **扁平**：`name` 直接在项上（Chat 是 `function.name`）——第二处形状差异。
        Assert.Equal("read", tool.GetProperty("name").GetString());
        Assert.Equal("object", tool.GetProperty("parameters").GetProperty("type").GetString());
    }

    /// <summary>
    /// ⚠️ **这条钉的是三条链路里唯一「一变多」的地方**：Responses 把工具调用做成
    /// **与消息并列的项** ⇒ 「说话 + 调两个工具」是**三项**，不是一项。
    /// </summary>
    [Fact]
    public void Responses_ToolCalls_AreSiblingItems()
    {
        var call = new Block.ToolUseBlock("c1", "read", """{"path":"a.txt"}""");

        var input = Body(OpenAiResponsesRequest.Build(
            Req(Protocols.OpenAiResponses, [UserMsg("x"), ModelMsg(call), Result("c1", "内容")])))
            .GetProperty("input");

        var items = input.EnumerateArray().ToList();
        // user 那条 + assistant 的 function_call 项 + 结果项 = 3（**没有**一条空的 message 项）。
        Assert.Equal(3, items.Count);
        Assert.Equal("function_call", items[1].GetProperty("type").GetString());
        Assert.Equal("c1", items[1].GetProperty("call_id").GetString());
        Assert.Equal("""{"path":"a.txt"}""", items[1].GetProperty("arguments").GetString());
    }

    // ⚠️ 结果的字段名是 call_id / output（Chat 是 tool_call_id / content）——第三处形状差异。
    [Fact]
    public void Responses_ToolResult_UsesItsOwnFieldNames()
    {
        var call = new Block.ToolUseBlock("c1", "read", "{}");

        var input = Body(OpenAiResponsesRequest.Build(
            Req(Protocols.OpenAiResponses, [UserMsg("x"), ModelMsg(call), Result("c1", "内容")])))
            .GetProperty("input");

        var output = input.EnumerateArray().Single(i => i.GetProperty("type").GetString() == "function_call_output");
        Assert.Equal("c1", output.GetProperty("call_id").GetString());
        Assert.Equal("内容", output.GetProperty("output").GetString());
    }

    [Fact]
    public void Responses_NoTools_TheKeyIsAbsent()
    {
        Assert.False(Body(OpenAiResponsesRequest.Build(Req(Protocols.OpenAiResponses, [UserMsg("hi")])))
            .TryGetProperty("tools", out _));
    }

    [Fact]
    public void Responses_Response_FunctionCall_BecomesACanonicalBlock()
    {
        var reply = OpenAiResponsesResponse.Map("""
            {"status":"completed","output":[
              {"type":"function_call","call_id":"c1","name":"read","arguments":"{\"path\":\"a.txt\"}"}]}
            """);

        var use = Assert.IsType<Block.ToolUseBlock>(Assert.Single(reply.Blocks));
        Assert.Equal("c1", use.CallId);
        Assert.Equal("""{"path":"a.txt"}""", use.Arguments);
    }

    // ⚠️ **按遇到次序收集**：先说后调 与 先调后说 在历史上是不同的东西。
    // 分成两个列表再拼接会把两种次序变成一样——那是改写了模型的输出顺序。
    [Fact]
    public void Responses_Response_PreservesInterleavedOrder()
    {
        var reply = OpenAiResponsesResponse.Map("""
            {"status":"completed","output":[
              {"type":"message","content":[{"type":"output_text","text":"先解释"}]},
              {"type":"function_call","call_id":"c1","name":"read","arguments":"{}"},
              {"type":"message","content":[{"type":"output_text","text":"再补充"}]}]}
            """);

        Assert.Equal(3, reply.Blocks.Count);
        Assert.IsType<Block.TextBlock>(reply.Blocks[0]);
        Assert.IsType<Block.ToolUseBlock>(reply.Blocks[1]);
        Assert.Equal("再补充", Assert.IsType<Block.TextBlock>(reply.Blocks[2]).Text);
    }

    [Fact]
    public void Responses_Response_FunctionCallWithoutCallId_IsSkipped()
    {
        var reply = OpenAiResponsesResponse.Map("""
            {"status":"completed","output":[{"type":"function_call","name":"read","arguments":"{}"}]}
            """);

        Assert.Empty(reply.Blocks);
    }
}
