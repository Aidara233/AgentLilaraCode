using Lilara.Core.Canonical;
using Lilara.Core.Persistence;
using Lilara.Core.Records;

namespace Lilara.Core.Tests;

/// <summary>
/// ⚠️ **磁盘格式已冻结，而这是唯一可执行的证据。**
///
/// <para>为什么值钱：`history.jsonl` **只追加、永不重写**（`01` §12.4）。
/// 改一次序列化行为 = 既有历史读不回来 = **一次数据迁移**，而没有任何回滚路径。
/// 在此之前，「格式已冻结」只是一句话——`TimelineEventTests` 等钉的是**往返**，
/// 而往返**对形状变化是瞎的**：字段改名、判别式换词、属性顺序变化，往返全都仍然成立。</para>
///
/// <para>⚠️ **下面的期望字节是实测出来的，不是猜的**（改这一条之前先跑一遍看真实输出）。
/// 猜字节的代价是：你写下的「期望」和真实输出同时错成一样，测试照样绿。</para>
/// </summary>
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

    /// <summary>
    /// ⚠️ 判别式 `"kind":"message"` 必须在，且 `from` 是**枚举按名小写**（`"user"` 不是 `0`）。
    /// 枚举换成整数会让**成员重排**静默重解释既有历史——那是最阴的一类迁移。
    /// </summary>
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

    /// <summary>
    /// ⚠️ 线上名是 **`reasoningKind`** 不是 `kind`——判别式已经占了 `kind`，
    /// 撞名会让 `System.Text.Json` 对**整个类型**抛异常（不是只影响那个属性）。
    /// </summary>
    [Fact]
    public void ReasoningBlock_KeepsItsExactBytes()
    {
        Assert.Equal(
            """{"kind":"reasoning","signature":"sig","text":"想","reasoningKind":"text"}""",
            Json.StableSerialize(new Block.ReasoningBlock("sig", "想")));
    }

    /// <summary>
    /// ⚠️ 无签名时 **`signature` 键整个不出现**（`WhenWritingNull`），
    /// 而不是 `"signature":null`。回读时靠 record 的归一表达式补回默认值。
    /// </summary>
    [Fact]
    public void ReasoningBlockWithoutSignature_OmitsTheKey()
    {
        Assert.Equal(
            """{"kind":"reasoning","text":"想","reasoningKind":"text"}""",
            Json.StableSerialize(new Block.ReasoningBlock(null, "想")));
    }

    /// <summary>
    /// ⚠️ **`TimelineEvent.StableSerialize` 必须按基类型序列化。**
    /// 这条钉住那个静态类型陷阱：写成泛型 `Serialize(evt)` 时判别式会**静默消失**，
    /// 而错误**只在反序列化时才炸**。
    /// </summary>
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

        // ⚠️ 反面对照：静态类型是派生类时，**外层的**判别式确实会丢。
        // 注意必须钉 `"kind":"message"` 这个**完整子串**，不能写成 `DoesNotContain("\"kind\"")`——
        // 后者会匹配到**嵌套 Block 的** `"kind":"text"`（控制器第一版就是这么写错的，
        // 测试当场红了）。**粗粒度的否定断言在嵌套结构上是不可靠的。**
        var typed = System.Text.Json.JsonSerializer.Serialize(evt, Json.Options);
        Assert.DoesNotContain("\"kind\":\"message\"", typed);
        Assert.Contains("\"kind\":\"text\"", typed);   // 嵌套块的判别式**仍在**——这正是粗断言会误伤的地方
    }

    /// <summary>
    /// ⚠️ **老 `state.json` 里没有 `axes` 键，它必须仍然是合法的。**
    /// `Json.Options` 的 `WhenWritingNull` 让「没设轴」的 state 序列化结果**逐字节不变**——
    /// 这是本计划唯一一次磁盘迁移，而它之所以安全正是因为这条。
    /// 把它钉成测试：将来谁把 `Axes` 改成 `required` 或给它一个非 null 默认值，这条会红。
    ///
    /// <para>⚠️ 实测（本任务 Step 1，加属性前后各跑一次同一个探针，落文件比 SHA256）：
    /// 加属性前 60 字节 / `EEF69F9314CDFC8DCC7CED8ED634A43B211053FA21F2808CB2AEA270A2AD0C0C`，
    /// 加属性后**逐字节相同**。下面这句断言就是那次测量的固化形态。</para>
    /// </summary>
    [Fact]
    public void SessionState_WithoutAxes_IsByteIdenticalToBeforeTheAxisFeature()
    {
        var state = new SessionState { SessionId = "abc123", Status = "Idle", Turn = 0, Pending = [] };

        Assert.Equal(
            """{"sessionId":"abc123","status":"Idle","turn":0,"pending":[]}""",
            System.Text.Json.JsonSerializer.Serialize(state, Json.Options));
    }

    /// <summary>设了轴就写出来——形状要能一眼看出来，别嵌套成对象套对象。</summary>
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
