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

    [Fact]
    public void Build_IsByteStable()
    {
        // ⚠️ 前缀稳定性的地基：同一输入两次构造必须逐字节相同（不依赖字典枚举序）
        var request = Req(Msg(Speaker.User, new Block.TextBlock("hi")));

        Assert.Equal(OpenAiChatRequest.Build(request), OpenAiChatRequest.Build(request));
    }

    // ─────────────────────────────────────────────────────────────────────
    // 以下为加强断言（brief 之外）。
    // 上面那条 `Build_IsByteStable` 是**自比**：两边是同一个表达式，同进程内
    // `JsonObject` 的插入序必然一致 → 它实际上**无法失败**（见报告）。
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
    public void Build_MultipleTextBlocks_UseTheArrayForm()
    {
        // ⚠️ 这条覆盖 brief 里**所有测试都没走到**的分支：一条消息含多块文本。
        // 单文本快路径被覆盖了 8 次，数组路径一次都没有——而它正是没被实测过的形状。
        // 实测字节：…"messages":[{"role":"user","content":["一","二"]}]
        var root = Parse(OpenAiChatRequest.Build(Req(Msg(Speaker.User,
            new Block.TextBlock("一"), new Block.TextBlock("二")))));

        var content = root.GetProperty("messages")[0].GetProperty("content");

        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal(2, content.GetArrayLength());
        Assert.Equal("一", content[0].GetString());
        Assert.Equal("二", content[1].GetString());
    }

    [Fact]
    public void Build_KeepsTextOrderAroundADroppedReasoningBlock()
    {
        // ⚠️ 丢掉中间那块推理之后，剩下的两块**不能并成一块**，顺序也不能乱。
        // 载荷不同（前/后），反转或合并都会红。实测：content:["前","后"]
        var json = OpenAiChatRequest.Build(Req(Msg(Speaker.Model,
            new Block.TextBlock("前"), new Block.ReasoningBlock("s", "中"), new Block.TextBlock("后"))));

        Assert.Equal(
            "{\"model\":\"deepseek-flash\",\"max_tokens\":16000,\"messages\":" +
            "[{\"role\":\"assistant\",\"content\":[\"前\",\"后\"]}]}",
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
        // 落盘的 `"openai-chat"`，而不是代码里的常量。用常量构造的测试在
        // 「常量被改错」时仍然自洽（见 AdapterFactoryTests 同名理由）。
        // ⚠️ 诚实说明：这条**证明不了**「协议真的被读取了」——实测两条协议产出
        // 逐字节相同（见报告里的惰性分析）。它守的是「调用点契约」：带着落盘值的
        // 请求确实得到 Chat 形状。
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
