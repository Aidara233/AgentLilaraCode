using Lilara.Core.Kernel;
using Lilara.Core.Protocol;
using Lilara.Core.Protocol.OpenAi;

namespace Lilara.Core.Tests;

/// <summary>
/// **流式与非流式必须给出同一个回复**（2026-11 流式批）。
///
/// <para>⚠️ <b>为什么这是这套改动的中心不变量</b>：流式只是「提前显示」，它**不许改变**
/// 这一步到底产出了什么。三条协议的实现都刻意把 SSE 帧**攒回一份与非流式响应同形的 JSON**、
/// 再交给**同一个** <c>Map</c>——这些用例钉的就是那一层等价性。</para>
///
/// <para>⚠️ 它喂的是**成对的样本**：同一段输出的一整份响应 / 它的 SSE 分片。
/// 分片形状按两条协议的实际线上格式写（Chat 的 `choices[].delta`、
/// Responses 的 `type: response.*`），不是我们自己发明的中间态。</para>
/// </summary>
public class StreamEquivalenceTests
{
    // ---- Chat ----

    private static string ChatWhole(string content, string? reasoning = null, string finish = "stop") =>
        "{\"id\":\"c1\",\"model\":\"m\",\"choices\":[{\"message\":{"
        + "\"role\":\"assistant\","
        + "\"content\":\"" + content + "\""
        + (reasoning is null ? "" : ",\"reasoning_content\":\"" + reasoning + "\"")
        + "},\"finish_reason\":\"" + finish + "\"}],"
        + "\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":2}}";

    private static string ChatChunk(string deltaJson, string finish = "null") =>
        "{\"id\":\"c1\",\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":" + deltaJson
        + ",\"finish_reason\":" + (finish == "null" ? "null" : "\"" + finish + "\"") + "}]}";

    [Fact]
    public void Chat_StreamingAndWholeBodyMapToTheSameReply()
    {
        var expected = OpenAiChatResponse.Map(ChatWhole("你好世界", finish: "stop"));

        var state = new ChatStreamState();
        OpenAiChatStream.Apply(ChatChunk("{\"content\":\"你好\"}"), state);
        OpenAiChatStream.Apply(ChatChunk("{\"content\":\"世界\"}"), state);
        OpenAiChatStream.Apply(
            "{\"id\":\"c1\",\"model\":\"m\",\"choices\":[]," +
            "\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":2}}", state);
        OpenAiChatStream.Apply(ChatChunk("{}", finish: "stop"), state);

        var actual = OpenAiChatResponse.Map(OpenAiChatStream.Finish(state));

        Assert.Equal(Text(expected), Text(actual));
        Assert.Equal(expected.StopReason, actual.StopReason);
        Assert.Equal(expected.Usage?.InputTokens, actual.Usage?.InputTokens);
        Assert.Equal(expected.Usage?.OutputTokens, actual.Usage?.OutputTokens);
        Assert.Equal(expected.ReportedModel, actual.ReportedModel);
        Assert.Equal(expected.ResponseId, actual.ResponseId);
    }

    [Fact]
    public void Chat_StreamingEmitsTheTextPiecesInOrder()
    {
        var state = new ChatStreamState();
        var pieces = new List<string>();

        foreach (var part in new[] { "你", "好", "世界" })
            if (OpenAiChatStream.Apply(ChatChunk("{\"content\":\"" + part + "\"}"), state) is { } delta)
            {
                Assert.Equal("text", delta.Kind);
                pieces.Add(delta.Text);
            }

        Assert.Equal(["你", "好", "世界"], pieces);
    }

    // 思考增量走另一个 kind：混进正文会让头把它当普通文本画出来。
    [Fact]
    public void Chat_ReasoningArrivesAsItsOwnKind()
    {
        var state = new ChatStreamState();
        var delta = OpenAiChatStream.Apply(ChatChunk("{\"reasoning_content\":\"想\"}"), state);

        Assert.NotNull(delta);
        Assert.Equal("reasoning", delta!.Kind);
        Assert.Equal("想", delta.Text);
    }

    // ⚠️ 工具参数**必须逐字拼回去**：半截 JSON 不显示，但它进的是最终回复的
    // `ToolUseBlock.Arguments`，而那条纪律是「逐字原文」（`01` §9）。
    [Fact]
    public void Chat_ToolArgumentsAreConcatenatedVerbatim()
    {
        var state = new ChatStreamState();

        OpenAiChatStream.Apply(ChatChunk(
            "{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"function\":{\"name\":\"read\",\"arguments\":\"{\\\"path\\\":\"}}]}"),
            state);

        // ⚠️ 参数分片的这一帧里**没有** content ⇒ 不该产生任何可见增量。
        Assert.Null(OpenAiChatStream.Apply(ChatChunk(
            "{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"\\\"/a\\\"}\"}}]}"), state));

        var reply = OpenAiChatResponse.Map(OpenAiChatStream.Finish(state));
        var call = reply.Blocks.OfType<Block.ToolUseBlock>().Single();

        Assert.Equal("call_1", call.CallId);
        Assert.Equal("read", call.Name);
        // 逐字：两次分片拼起来正好是一个合法 JSON 原文。
        Assert.Equal("{\"path\":\"/a\"}", call.Arguments);
    }

    [Fact]
    public void Chat_FinishReasonSurvivesTheStream()
    {
        var state = new ChatStreamState();
        OpenAiChatStream.Apply(ChatChunk("{\"content\":\"x\"}"), state);
        OpenAiChatStream.Apply(ChatChunk("{}", finish: "length"), state);

        Assert.Equal("length", OpenAiChatResponse.Map(OpenAiChatStream.Finish(state)).StopReason);
    }

    // ---- Responses ----

    [Fact]
    public void Responses_PrefersTheCompletedResponseObject()
    {
        // ⚠️ 上游在 `response.completed` 里给的那份是**权威**的：推理项、usage、内置工具项
        // 都只在那里出现（增量里根本没有）。拼一份「差不多的」会静默丢掉它们。
        const string completed =
            "{\"type\":\"response.completed\",\"response\":{" +
            "\"id\":\"r1\",\"model\":\"m\",\"status\":\"completed\"," +
            "\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"完整正文\"}]}]," +
            "\"usage\":{\"input_tokens\":5,\"output_tokens\":2}}}";

        var state = new ResponsesStreamState();
        OpenAiResponsesStream.Apply(
            "{\"type\":\"response.output_text.delta\",\"delta\":\"完整\"}", state);
        OpenAiResponsesStream.Apply(
            "{\"type\":\"response.output_text.delta\",\"delta\":\"正文\"}", state);
        OpenAiResponsesStream.Apply(completed, state);

        var reply = OpenAiResponsesResponse.Map(OpenAiResponsesStream.Finish(state));

        Assert.Equal("完整正文", Text(reply));
        Assert.Equal("completed", reply.StopReason);
        Assert.Equal("r1", reply.ResponseId);
        Assert.Equal(5, reply.Usage?.InputTokens);
    }

    // 没有 `response.completed`（老网关 / 提前断开）⇒ 用攒到的正文现拼，**不能丢掉这一段话**。
    [Fact]
    public void Responses_FallsBackToAccumulatedText()
    {
        var state = new ResponsesStreamState();
        OpenAiResponsesStream.Apply("{\"type\":\"response.output_text.delta\",\"delta\":\"只有\"}", state);
        OpenAiResponsesStream.Apply("{\"type\":\"response.output_text.delta\",\"delta\":\"增量\"}", state);

        var reply = OpenAiResponsesResponse.Map(OpenAiResponsesStream.Finish(state));

        Assert.Equal("只有增量", Text(reply));
        // ⚠️ usage 是 `null`（上游没报告），**不是 0**。
        Assert.Null(reply.Usage?.InputTokens);
    }

    [Fact]
    public void Responses_ReasoningSummaryArrivesAsItsOwnKind()
    {
        var state = new ResponsesStreamState();
        var delta = OpenAiResponsesStream.Apply(
            "{\"type\":\"response.reasoning_summary_text.delta\",\"delta\":\"想\"}", state);

        Assert.NotNull(delta);
        Assert.Equal("reasoning", delta!.Kind);
    }

    // ---- 请求体 ----

    // ⚠️ 开关关着时，请求体必须与**没有这个功能**时逐字节相同——
    // 这是本仓那条「没配过的东西，请求逐字节不变」的直接推论。
    [Fact]
    public void Chat_StreamFlagAddsNothingWhenOff()
    {
        var request = Request();

        var off = OpenAiChatRequest.Build(request);
        Assert.DoesNotContain("\"stream\"", off, StringComparison.Ordinal);

        var on = OpenAiChatRequest.Build(request, stream: true);
        Assert.Contains("\"stream\":true", on, StringComparison.Ordinal);
        // usage 不带它就没有 ⇒ 账本会静默归零。
        Assert.Contains("include_usage", on, StringComparison.Ordinal);
    }

    [Fact]
    public void Responses_StreamFlagAddsNothingWhenOff()
    {
        var request = Request();

        Assert.DoesNotContain("\"stream\"", OpenAiResponsesRequest.Build(request), StringComparison.Ordinal);
        Assert.Contains("\"stream\":true",
            OpenAiResponsesRequest.Build(request, stream: true), StringComparison.Ordinal);
    }

    private static string Text(ModelReply reply) =>
        string.Concat(reply.Blocks.OfType<Block.TextBlock>().Select(b => b.Text));

    private static ModelRequest Request() => new()
    {
        ModelId = "m",
        MaxTokens = 16,
        Effort = "high",
        Temperature = null,
        Protocol = Protocols.OpenAiChat,
        Slots = new Session.PromptSlots(
            System: [], Premier: [], Tail: []),
        Tools = [],
        Timeline = [new TimelineEvent.MessageEvent
        {
            Id = "h1",
            At = DateTimeOffset.UnixEpoch,
            From = Speaker.User,
            Blocks = [new Block.TextBlock("你好")],
        }],
    };
}
