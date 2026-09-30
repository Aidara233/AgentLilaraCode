using System.Text.Json;
using Lilara.Core.Adapters;
using Lilara.Core.Adapters.OpenAi;
using Lilara.Core.Canonical;
using Lilara.Core.Records;

namespace Lilara.Core.Tests;

public class OpenAiResponsesRequestTests
{
    private static TimelineEvent Msg(Speaker from, params Block[] blocks) =>
        new TimelineEvent.MessageEvent { At = DateTimeOffset.UnixEpoch, From = from, Blocks = blocks };

    private static ModelRequest Req(params TimelineEvent[] tl) => new()
    {
        Protocol = Protocols.OpenAiResponses,
        ModelId = "deepseek-flash", MaxTokens = 16000, Effort = "high",
        SystemBlocks = [], Timeline = tl,
    };

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Build_UsesMaxOutputTokensNotMaxTokens()
    {
        // ⚠️ Responses 的字段名是 max_output_tokens（实测）
        var root = Parse(OpenAiResponsesRequest.Build(Req(Msg(Speaker.User, new Block.TextBlock("hi")))));

        Assert.Equal(16000, root.GetProperty("max_output_tokens").GetInt32());
        Assert.False(root.TryGetProperty("max_tokens", out _));
    }

    [Fact]
    public void Build_EveryInputItemCarriesTypeMessage()
    {
        // ⚠️ 实测：少了 "type":"message" 上游直接报 upstream_error。
        // 这条是本任务最容易被漏掉的一行。
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
        // ⚠️ 助手回放用 output_text（实测两种都能收，但 output_text 是语义正确的那个）
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
        // ⚠️ Responses 的系统提示是顶层 `instructions`，不是一条消息
        var request = Req(Msg(Speaker.User, new Block.TextBlock("hi"))) with
        {
            SystemBlocks = [new Block.TextBlock("你是助手")],
        };

        Assert.Equal("你是助手", Parse(OpenAiResponsesRequest.Build(request)).GetProperty("instructions").GetString());
    }

    [Fact]
    public void Build_OmitsInstructionsWhenThereAreNoSystemBlocks()
    {
        // ⚠️ 空时不发 instructions，而不是发个空串
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

    /// <summary>
    /// ⚠️ 两边是**同一个表达式**（自比）：同进程内 <c>JsonObject</c> 的插入序必然一致，
    /// 所以它对「形状对不对」零信息量——键换序、漏字段、改形状，它全绿。
    /// 它唯一能抓的是**不确定性**（字典枚举序漂移、时钟、随机数）。
    /// <para>键名已从 brief 的 <c>Build_IsByteStable</c> 改成它真正能证明的事
    /// （与 Task 5 的收口一致，见该任务报告 §5）。形状的守卫是下面
    /// <see cref="Build_PinsTheExactBytesInTheMeasuredKeyOrder"/> 那条钉死字面量的测试。</para>
    /// </summary>
    [Fact]
    public void Build_IsDeterministicAcrossRepeatedCalls()
    {
        var request = Req(Msg(Speaker.User, new Block.TextBlock("hi")));

        Assert.Equal(OpenAiResponsesRequest.Build(request), OpenAiResponsesRequest.Build(request));
    }

    // ─────────────────────────────────────────────────────────────────────
    // 以下为加强断言（brief 之外）。
    // brief 的 9 条里**每条消息都只有一块文本**——多块那个形状一次都没被执行过
    // （Task 5 的 bug 正是躲在那种无覆盖里）。下面把形状钉死在字面量上。
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Build_PinsTheExactBytesInTheMeasuredKeyOrder()
    {
        // ⚠️ 钉死**字面量**（不引用常量、不重新序列化自己的输出）：
        // 顶层键序 model → max_output_tokens → input；input 项内 type → role → content；
        // part 内 type → text；assistant 用 output_text、user 用 input_text。
        // 任何换序/改名/换形状都会红。载荷刻意各不相同，反转消息次序即红。
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
        // ⚠️ `instructions` 只在有系统文本时才出现，且**插在 input 之前**。
        // 这条同时守住「有系统块」时的逐字节形状与键序。
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
        // ⚠️ 覆盖 brief 完全不碰的**多块形状**：一条消息里两块以上文本。
        // 这里每个 part 仍要带 type，次序不能乱、不能并成一块。
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
        // ⚠️ 丢掉中间那块推理之后，剩下的两块**不能并成一块**，顺序也不能乱。
        // 载荷不同（前/后），反转或合并都会红。
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
        // ⚠️ `Assert.Equal("答案", …)` 只证明「第一个 text 块活下来了」，
        // 不证明推理文本/签名没被塞进别处。这条直接查**整段 JSON 的字节**。
        var json = OpenAiResponsesRequest.Build(Req(Msg(Speaker.Model,
            new Block.ReasoningBlock("sig-==", "想想"), new Block.TextBlock("答案"))));

        Assert.DoesNotContain("想想", json);
        Assert.DoesNotContain("sig-==", json);
    }

    [Fact]
    public void Build_OmitsInstructionsWhenSystemBlocksHaveNoText()
    {
        // ⚠️ `SystemBlocks.Count != 0` 但拼出来是空串 —— 那就是一个**空的 instructions**，
        // 正是「空串 vs 不存在」要防的那类缺陷（Review Focus #3）。
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
        // ⚠️ 字面量故意写死：流进 `Build` 的 `Protocol` 来自 `providers/*.json`
        // 落盘的 `"openai-responses"`，而不是代码里的常量。
        //
        // ⚠️ **这条证明的是「值被透传了」，不是「拼写对」。** 与 Task 5 同因：
        // `FilterFor` 的结果只被 `OfType<TextBlock>()` 消费，而推理块永远不是 TextBlock，
        // 所以把 `Protocol` 换成任何拼写，产出**逐字节相同**，这条照样绿。
        // ⇒ 落盘拼写的唯一守卫是 `AdapterFactoryTests.Protocols_PinTheOnDiskWireValues`。
        var request = Req(Msg(Speaker.Model,
            new Block.ReasoningBlock("sig", "想想"), new Block.TextBlock("答案")))
            with { Protocol = "openai-responses" };

        var root = Parse(OpenAiResponsesRequest.Build(request));

        Assert.Equal("答案", root.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void Path_PinsTheOnDiskWireValue()
    {
        // 路径和协议串一样是**线上契约**，必须由字面量守，不能引用自己的常量。
        Assert.Equal("/v1/responses", OpenAiResponsesRequest.Path);
    }
}
