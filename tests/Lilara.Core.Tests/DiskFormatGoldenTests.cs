using Lilara.Core.Kernel;
using Lilara.Core.Persistence;

namespace Lilara.Core.Tests;

// 磁盘格式已冻结：往返测试对形状变化（改字段名、换判别式、属性顺序）是瞎的，
// 故这里钉精确字节——期望字节是实测出来的，不是猜的。
public class DiskFormatGoldenTests
{
    private static ModelSelection Sel() => new()
    {
        ProviderId = "p", ModelId = "m", Protocol = "claude-messages", Effort = "high", MaxTokens = 16000,
    };

    [Fact]
    public void SessionSpec_KeepsItsExactBytes()
    {
        var spec = new SessionSpec
        {
            SessionId = "abc123",
            ProjectKey = "pk1",
            WorkingDirectory = @"C:\work",
            CreatedAt = DateTimeOffset.UnixEpoch,
            Model = Sel(),
        };

        Assert.Equal(
            """{"sessionId":"abc123","projectKey":"pk1","workingDirectory":"C:\\work","createdAt":"1970-01-01T00:00:00+00:00","model":{"providerId":"p","modelId":"m","protocol":"claude-messages","effort":"high","maxTokens":16000}}""",
            System.Text.Json.JsonSerializer.Serialize(spec, Json.Options));
    }

    [Fact]
    public void SessionState_KeepsItsExactBytes()
    {
        var state = new SessionState { SessionId = "abc123", Status = "Idle", Turn = 0, Pending = [] };

        Assert.Equal(
            """{"sessionId":"abc123","status":"Idle","turn":0,"pending":[]}""",
            System.Text.Json.JsonSerializer.Serialize(state, Json.Options));
    }

    // 判别式 "kind":"message" 必须在；from 是枚举按名小写——换成整数会让成员重排静默重解释既有历史。
    [Fact]
    public void MessageEvent_KeepsItsExactBytes()
    {
        var evt = new TimelineEvent.MessageEvent
        {
            At = DateTimeOffset.UnixEpoch,
            From = Speaker.User,
            Blocks = [new Block.TextBlock("你好")],
        };

        Assert.Equal(
            """{"kind":"message","from":"user","blocks":[{"kind":"text","text":"你好"}],"at":"1970-01-01T00:00:00+00:00"}""",
            TimelineEvent.StableSerialize(evt));
    }

    [Fact]
    public void TextBlock_KeepsItsExactBytes()
    {
        Assert.Equal("""{"kind":"text","text":"hi"}""", Json.StableSerialize(new Block.TextBlock("hi")));
    }

    // 线上名是 reasoningKind 不是 kind：判别式已占 kind，撞名会让 STJ 对整个类型抛异常。
    [Fact]
    public void ReasoningBlock_KeepsItsExactBytes()
    {
        Assert.Equal(
            """{"kind":"reasoning","signature":"sig","text":"想","reasoningKind":"text"}""",
            Json.StableSerialize(new Block.ReasoningBlock("sig", "想")));
    }

    // 无签名时 signature 键整个不出现（WhenWritingNull），不是 "signature":null。
    [Fact]
    public void ReasoningBlockWithoutSignature_OmitsTheKey()
    {
        Assert.Equal(
            """{"kind":"reasoning","text":"想","reasoningKind":"text"}""",
            Json.StableSerialize(new Block.ReasoningBlock(null, "想")));
    }

    // StableSerialize 必须按基类型序列化：写成泛型 Serialize(evt) 判别式会静默消失，只在反序列化时才炸。
    [Fact]
    public void StableSerialize_KeepsTheDiscriminator_UnlikeATypedCall()
    {
        var evt = new TimelineEvent.MessageEvent
        {
            At = DateTimeOffset.UnixEpoch,
            From = Speaker.Model,
            Blocks = [new Block.TextBlock("x")],
        };

        Assert.Contains("\"kind\":\"message\"", TimelineEvent.StableSerialize(evt));

        // 反面对照：静态类型是派生类时，外层的判别式确实会丢。
        // 必须钉完整子串 "kind":"message"：写成 DoesNotContain("\"kind\"") 会匹配到嵌套 Block 的 "kind":"text"。
        var typed = System.Text.Json.JsonSerializer.Serialize(evt, Json.Options);
        Assert.DoesNotContain("\"kind\":\"message\"", typed);
        Assert.Contains("\"kind\":\"text\"", typed);   // 嵌套块的判别式仍在
    }

    // 老 state.json 没有 axes 键，必须仍然合法——WhenWritingNull 让没设轴的 state 逐字节不变。
    [Fact]
    public void SessionState_WithoutAxes_IsByteIdenticalToBeforeTheAxisFeature()
    {
        var state = new SessionState { SessionId = "abc123", Status = "Idle", Turn = 0, Pending = [] };

        Assert.Equal(
            """{"sessionId":"abc123","status":"Idle","turn":0,"pending":[]}""",
            System.Text.Json.JsonSerializer.Serialize(state, Json.Options));
    }

    // 设了轴就写出来，形状要平（不要对象套对象）。
    [Fact]
    public void SessionState_WithAxes_SerializesThemAsAFlatObject()
    {
        var state = new SessionState
        {
            SessionId = "abc123", Status = "Idle", Turn = 0, Pending = [],
            Axes = new Dictionary<string, string> { ["safety"] = "prod" },
        };

        Assert.Equal(
            """{"sessionId":"abc123","status":"Idle","turn":0,"pending":[],"axes":{"safety":"prod"}}""",
            System.Text.Json.JsonSerializer.Serialize(state, Json.Options));
    }
}
