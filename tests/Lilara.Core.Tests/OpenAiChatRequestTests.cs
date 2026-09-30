using System.Text.Json;
using Lilara.Core.Adapters;
using Lilara.Core.Adapters.OpenAi;
using Lilara.Core.Canonical;
using Lilara.Core.Records;

namespace Lilara.Core.Tests;

public class OpenAiChatRequestTests
{
    private static TimelineEvent Msg(Speaker from, params Block[] blocks) =>
        new TimelineEvent.MessageEvent
        {
            At = DateTimeOffset.UnixEpoch, From = from, Blocks = blocks,
        };

    private static ModelRequest Req(params TimelineEvent[] tl) => new()
    {
        Protocol = Protocols.OpenAiChat,
        ModelId = "deepseek-flash", MaxTokens = 16000, Effort = "high",
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
        // 单文本走字符串形式（协议原生形状，最省字节）
        var root = Parse(OpenAiChatRequest.Build(Req(Msg(Speaker.User, new Block.TextBlock("你好")))));

        Assert.Equal("你好", root.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public void Build_DropsReasoningBecauseChatCannotCarryIt()
    {
        // ⚠️ 实测：Chat 返回体里没有推理字段 → 不能承载 → 丢，且不伪造
        var root = Parse(OpenAiChatRequest.Build(Req(Msg(Speaker.Model,
            new Block.ReasoningBlock("sig", "想想"), new Block.TextBlock("答案")))));

        Assert.Equal("答案", root.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public void Build_OmitsSystemWhenThereAreNoSystemBlocks()
    {
        // ⚠️ 核心自带零提示词——空时不发 system 消息，而不是发个空串
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
        // ⚠️ 全是推理块的消息，过滤后为空——协议不接受空 content
        var ex = Assert.Throws<ArgumentException>(() =>
            OpenAiChatRequest.Build(Req(Msg(Speaker.Model, new Block.ReasoningBlock(null, "只想不说")))));

        Assert.Contains("空消息", ex.Message);
    }

    /// <summary>
    /// ⚠️ **这条测试证明不了多少，名字已经改成它真正能证明的事。**
    /// 两边是**同一个表达式**（自比），同进程内 `JsonObject` 的插入序必然一致，
    /// 所以它对「形状对不对」零信息量——键换序、漏字段、形状写错，它全绿。
    /// 它唯一能抓的是**不确定性**：字典枚举序漂移、时钟、随机数。
    /// <para>形状的守卫是 <see cref="Build_PinsTheExactBytesInTheMeasuredKeyOrder"/>（钉死字面量）。</para>
    /// </summary>
    [Fact]
    public void Build_IsDeterministicAcrossRepeatedCalls()
    {
        var request = Req(Msg(Speaker.User, new Block.TextBlock("hi")));

        Assert.Equal(OpenAiChatRequest.Build(request), OpenAiChatRequest.Build(request));
    }

    // ─────────────────────────────────────────────────────────────────────
    // 以下为加强断言（brief 之外）。
    // 真正守住「键序 + 逐字节形状」的是下面那条**钉死字面量**的测试。
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Build_PinsTheExactBytesInTheMeasuredKeyOrder()
    {
        // ⚠️ 钉死**字面量**（不引用常量、不重新序列化自己的输出）：
        // 键序写成 model → max_tokens → messages，消息内 role → content。
        // 任何一处换序、加空格、换形状，这条都会红。载荷刻意各不相同，
        // 因此它也顺带钉住消息顺序（反转即红）。
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
        // ⚠️ 这条覆盖的是**原先没人测的分支**——而 bug 就藏在里面。
        // 控制器对网关实测：裸字符串数组 `["一","二"]` 被拒
        // （invalid_request_error: Invalid JSON request body），带类型的 part 才认。
        // brief 的 9 条测试里每条消息都只有一块文本，全走字符串快路径，
        // 所以这个分支一次都没被执行过 —— 缺陷在无覆盖的代码里躲了整整一轮。
        var root = Parse(OpenAiChatRequest.Build(Req(
            Msg(Speaker.User, new Block.TextBlock("一"), new Block.TextBlock("二")))));

        var content = root.GetProperty("messages")[0].GetProperty("content");

        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("一", content[0].GetProperty("text").GetString());
        Assert.Equal("二", content[1].GetProperty("text").GetString());
    }

    /// <summary>
    /// 单块走字符串快路径——上游实测也认这个形状，且最省字节。
    /// <para>⚠️ **诚实说明（实测，非推断）**：这条**不是**新增覆盖。
    /// 把实现临时改成「永远发数组」后跑，红的不是 1 条而是 **5 条**，其中就包括 brief 原有的
    /// <c>Build_SingleTextBlock_UsesThePlainStringForm</c>（它对数组调 `GetString()` 会抛）。
    /// 「永远发数组」这个退化**本来就被守着**。</para>
    /// <para>留下它是因为把快路径写成**显式的 `ValueKind` 断言**，读的人不必反推
    /// `GetString()` 在数组上的失败语义。价值是清晰度，不是覆盖。</para>
    /// </summary>
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
        // ⚠️ 丢掉中间那块推理之后，剩下的两块**不能并成一块**，顺序也不能乱。
        // 载荷不同（前/后），反转或合并都会红。形状必须是带类型的 part（实测上游只认这个）。
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
        // ⚠️ `Assert.Equal("答案", …)` 只证明「第一个 text 块活下来了」，
        // 不证明推理文本没被塞进别处。这条直接查**整段 JSON 的字节**里没有推理痕迹。
        var json = OpenAiChatRequest.Build(Req(Msg(Speaker.Model,
            new Block.ReasoningBlock("sig-==", "想想"), new Block.TextBlock("答案"))));

        Assert.DoesNotContain("想想", json);
        Assert.DoesNotContain("sig-==", json);
    }

    [Fact]
    public void Build_OmitsSystemWhenSystemBlocksHaveNoText()
    {
        // ⚠️ `SystemBlocks.Count != 0` 但拼出来是空串 —— 那就是一个**空的 system 消息**，
        // 正是 Review Focus #3 要防的「发空串」而不是「不发」。
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
        // ⚠️ 字面量故意写死：真正流进 `Build` 的 `Protocol` 来自 `providers/*.json`
        // 落盘的 `"openai-chat"`，而不是代码里的常量。
        //
        // ⚠️ **这条证明的是「值被透传了」，不是「拼写对」。** 实测：同一时间线分别按
        // `"openai-chat"` 与 `"claude-messages"` 过滤，产出**逐字节相同**——因为过滤结果
        // 只被 `OfType<TextBlock>()` 消费，而推理块永远不是 TextBlock。所以把 `Protocol`
        // 换成任何拼写，这条照样绿。
        // ⇒ **落盘拼写的唯一守卫是 `AdapterFactoryTests.Protocols_PinTheOnDiskWireValues`**，
        //   不在本文件，也不可能有。
        var request = Req(Msg(Speaker.Model,
            new Block.ReasoningBlock("sig", "想想"), new Block.TextBlock("答案")))
            with { Protocol = "openai-chat" };

        var root = Parse(OpenAiChatRequest.Build(request));

        Assert.Equal("答案", root.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public void Path_PinsTheOnDiskWireValue()
    {
        // 路径和协议串一样是**线上契约**，必须由字面量守，不能引用自己的常量。
        Assert.Equal("/v1/chat/completions", OpenAiChatRequest.Path);
    }
}
