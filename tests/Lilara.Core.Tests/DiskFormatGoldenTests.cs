using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lilara.Core.Kernel;
using Lilara.Core.Storage;

namespace Lilara.Core.Tests;

// 磁盘格式：**往返测试对形状变化（改字段名、换判别式、属性顺序）是瞎的**，
// 故这里钉精确字节——期望字节是实测出来的，不是猜的。
//
// ⚠️ **这些断言真正在钉的是四件事，不是「每条记录各自的性质」**：
//   ① `Json.Options` 的设置（`WhenWritingNull` / camelCase / 不转义非 ASCII / 枚举按名）
//      ② 字段的**声明顺序**（序列化顺序 = 声明顺序）
//   ③ 字段名与判别式串
//   ④ 格式版本号
// ⇒ 所以「加一个字段」不该来这里改期望串（除了版本号那一段），**改 `Json.Options` 才该让一大片一起红**。
// 把它们分散写成「某条记录逐字节不变」会让人以为它们是四道独立防线，其实是一道说了几遍。
//
// ⚠️ **版本号 3 → 4（2026-10-07，预设批）**：`SessionSpec` 加了 `systemPromptPreset`。
// ⚠️ 注意下面那几条 `SessionSpec` 金样**没有**出现这个键——那是对的，不是漏了：
// 这些用例都不给预设（`null`），而 `WhenWritingNull` 会整个省掉它 ⇒ **老文件的形状逐字节不变**。
// 要钉「给了预设时长什么样」，见 `SessionSpec_WithPreset_...`。
public class DiskFormatGoldenTests
{
    private static ModelSelection Sel() => new()
    {
        ProviderId = "p", ModelId = "m", Protocol = "claude-messages", Effort = "high", MaxTokens = 16000,
    };

    // ---- ① 序列化器设置本身 ----
    //
    // ⚠️ 这一段是**那个真正的锚点**。下面所有的「某个键整个不出现」都是这一条设置的效果；
    // 改它而不动这里，会让一大片金样同时变红，而红的原因不易看出来。
    [Fact]
    public void SerializerSettings_ArePinned()
    {
        Assert.Equal(JsonIgnoreCondition.WhenWritingNull, Json.Options.DefaultIgnoreCondition);
        Assert.Equal(JsonNamingPolicy.CamelCase, Json.Options.PropertyNamingPolicy);
        Assert.Equal(JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Json.Options.Encoder);
        Assert.False(Json.Options.WriteIndented);

        // 枚举按名、且是小写 camelCase：换成整数会让成员重排**静默重解释**既有历史。
        // ⚠️ 断言写成**行为**而不是 `converter.NamingPolicy`——那个属性不是公开成员。
        // 顺带这也是更好的判据：它钉的是「落盘长什么样」，不是「转换器怎么配的」。
        // `Speaker` 是磁盘模型里唯一的枚举（`ReasoningKind` 是字符串常量类，不是枚举）。
        Assert.NotNull(Assert.Single(Json.Options.Converters.OfType<JsonStringEnumConverter>()));
        Assert.Equal("\"user\"", JsonSerializer.Serialize(Speaker.User, Json.Options));
        Assert.Equal("\"model\"", JsonSerializer.Serialize(Speaker.Model, Json.Options));
    }

    // ---- ④ 格式版本 ----
    //
    // ⚠️ 版本号进**磁盘**；它**也**会漏进 `/state` 的响应，因为那个端点**直接序列化磁盘记录**
    //（`spec` / `state` 两个对象原样交出去），而不是逐字段手选。
    // ⚠️ **这与 provider 那半刻意不同**：`/providers` 是逐字段手选的
    //（key 就是那样被挡在外面的）⇒ 版本号到不了那里。
    // ⚠️ 漏出去**不是缺陷**：多头一个键无害，而且「这个会话是被更新的核心写的」是头**该知道**的事。
    // 但它是**线上表面的增长**，所以钉下来，别让它悄悄发生。
    [Fact]
    public void FormatVersion_IsWrittenToDisk_AndRidesAlongInTheStateSnapshot()
    {
        var spec = new SessionSpec
        {
            SessionId = "abc123",
            ProjectKey = "pk1",
            WorkingDirectory = @"C:\work",
            CreatedAt = DateTimeOffset.UnixEpoch,
            Model = Sel(),
        };

        var onDisk = JsonSerializer.Serialize(spec, Json.Options);
        Assert.StartsWith("""{"version":9,""", onDisk);

        // 线上那一份：同一批选项（`WireFormatTests` 钉着两份设置一致），端点又直接序列化这个类型
        // ⇒ 版本号跟着走。**这就是它出现在头那边的原因**，不是谁忘了挡。
        var onWire = JsonSerializer.Serialize(spec, Hosting.WireJson.Options);
        Assert.StartsWith("""{"version":9,""", onWire);
    }

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
            """{"version":9,"sessionId":"abc123","projectKey":"pk1","workingDirectory":"C:\\work","createdAt":"1970-01-01T00:00:00+00:00","model":{"providerId":"p","modelId":"m","protocol":"claude-messages","effort":"high","maxTokens":16000}}""",
            System.Text.Json.JsonSerializer.Serialize(spec, Json.Options));
    }

    // ⚠️ 上面那条是「不设 title / archivedAt 时它们整个键不出现」——**加可空字段不破格式的证据**。
    // 这一条钉的是设上之后的字节（顺序 = 声明顺序，追加在 `model` 之后）。
    [Fact]
    public void SessionSpec_WithTitleAndArchive_KeepsItsExactBytes()
    {
        var spec = new SessionSpec
        {
            SessionId = "abc123",
            ProjectKey = "pk1",
            WorkingDirectory = @"C:\work",
            CreatedAt = DateTimeOffset.UnixEpoch,
            Model = Sel(),
            Title = "重构会话",
            ArchivedAt = DateTimeOffset.UnixEpoch,
        };

        Assert.Equal(
            """{"version":9,"sessionId":"abc123","projectKey":"pk1","workingDirectory":"C:\\work","createdAt":"1970-01-01T00:00:00+00:00","model":{"providerId":"p","modelId":"m","protocol":"claude-messages","effort":"high","maxTokens":16000},"title":"重构会话","archivedAt":"1970-01-01T00:00:00+00:00"}""",
            System.Text.Json.JsonSerializer.Serialize(spec, Json.Options));
    }

    // `title` 没给（null）而 `archivedAt` 给了：两个键的取舍是**各自独立**的，
    // 别写成「一个在就两个都写」——那会让没起名的归档会话多出一个 "title":null。
    [Fact]
    public void SessionSpec_WithArchiveOnly_OmitsTitleEntirely()
    {
        var spec = new SessionSpec
        {
            SessionId = "abc123",
            ProjectKey = "pk1",
            WorkingDirectory = @"C:\work",
            CreatedAt = DateTimeOffset.UnixEpoch,
            Model = Sel(),
            ArchivedAt = DateTimeOffset.UnixEpoch,
        };

        var json = System.Text.Json.JsonSerializer.Serialize(spec, Json.Options);

        Assert.DoesNotContain("\"title\"", json);
        Assert.EndsWith("\"archivedAt\":\"1970-01-01T00:00:00+00:00\"}", json);
    }

    // ⚠️ 与上面那条 `SessionSpec_KeepsItsExactBytes` 是**两个不同的断言**，别合并：
    // 那条钉「不给预设 ⇒ 那个键整个不出现」（老文件的形状不变），
    // 这条钉「给了预设 ⇒ 它出现在 `title` 之后」（声明顺序 = 序列化顺序）。
    [Fact]
    public void SessionSpec_WithPreset_PutsTheKeyAfterTitle()
    {
        var spec = new SessionSpec
        {
            SessionId = "abc123",
            ProjectKey = "pk1",
            WorkingDirectory = @"C:\work",
            CreatedAt = DateTimeOffset.UnixEpoch,
            Model = Sel(),
            SystemPromptPreset = "task",
        };

        Assert.Equal(
            """{"version":9,"sessionId":"abc123","projectKey":"pk1","workingDirectory":"C:\\work","createdAt":"1970-01-01T00:00:00+00:00","model":{"providerId":"p","modelId":"m","protocol":"claude-messages","effort":"high","maxTokens":16000},"systemPromptPreset":"task"}""",
            JsonSerializer.Serialize(spec, Json.Options));
    }

    [Fact]
    public void SessionState_KeepsItsExactBytes()
    {
        var state = new SessionState { SessionId = "abc123", Status = "Idle", Turn = 0, Pending = [] };

        Assert.Equal(
            """{"version":9,"sessionId":"abc123","status":"Idle","turn":0,"pending":[],"pendingCompact":false}""",
            System.Text.Json.JsonSerializer.Serialize(state, Json.Options));
    }

    // 判别式 "kind":"message" 必须在；from 是枚举按名小写——换成整数会让成员重排静默重解释既有历史。
    [Fact]
    public void MessageEvent_KeepsItsExactBytes()
    {
        var evt = new TimelineEvent.MessageEvent
        {
            Id = "r1",
            At = DateTimeOffset.UnixEpoch,
            From = Speaker.User,
            Blocks = [new Block.TextBlock("你好")],
        };

        Assert.Equal(
            """{"kind":"message","from":"user","blocks":[{"kind":"text","text":"你好"}],"id":"r1","at":"1970-01-01T00:00:00+00:00"}""",
            TimelineEvent.StableSerialize(evt));
    }

    [Fact]
    public void TextBlock_KeepsItsExactBytes()
    {
        Assert.Equal("""{"kind":"text","text":"hi"}""", Json.StableSerialize(new Block.TextBlock("hi")));
    }

    // ⚠️ **看 `arguments` 那一格**：它原样嵌进来，**没有被再转义成字符串**。
    // 这是「逐字回传」在**字节层面**的样子——`JsonRawConverter` 一被摘掉，这里立刻红。
    [Fact]
    public void ToolUseBlock_KeepsItsExactBytes_IncludingRawArguments()
    {
        var block = new Block.ToolUseBlock("c1", "read", """{ "path" : "a/b" }""");

        Assert.Equal(
            """{"kind":"toolUse","callId":"c1","name":"read","arguments":{ "path" : "a/b" }}""",
            Json.StableSerialize(block));
    }

    // 图片的字节在库里，块里只有地址与媒体类型（01 §3.6）。
    [Fact]
    public void ImageBlock_KeepsItsExactBytes()
    {
        var block = new Block.ImageBlock("ab" + new string('0', 62), "image/png");

        Assert.Equal(
            $$"""{"kind":"image","sha256":"ab{{new string('0', 62)}}","mediaType":"image/png"}""",
            Json.StableSerialize(block));
    }

    // 工具结果：判别式 + callId + content（一串块）+ 基类的 id/at。
    // ⚠️ 顺序仍是「派生在前、基类在后」，与 MessageEvent 那条同一个规律。
    //
    // ⚠️ **这条同时是「加字段不破格式」的证据**：`Ref` / `DependsOn` / `Fingerprint`
    // 三个都没给 ⇒ 按 WhenWritingNull **整个键不出现**，期望串与本批之前逐字节相同。
    [Fact]
    public void ToolResultEvent_KeepsItsExactBytes()
    {
        var evt = new TimelineEvent.ToolResultEvent
        {
            Id = "r1",
            At = DateTimeOffset.UnixEpoch,
            CallId = "c1",
            Content = [new Block.TextBlock("ok")],
        };

        Assert.Equal(
            """{"kind":"toolResult","callId":"c1","content":[{"kind":"text","text":"ok"}],"id":"r1","at":"1970-01-01T00:00:00+00:00"}""",
            TimelineEvent.StableSerialize(evt));
    }

    // ⚠️ **这三件是三个不同的东西，这条钉的是它们各自的键名与顺序**（`00` §5 / `01` §3.6）：
    //   `content`    = 快照在哪（这一条是内联的小快照）
    //   `ref`        = 指向**另一条记录**（`fetch` 专用）—— 这条刻意不设
    //   `dependsOn`  = 依赖什么（重新解析回同一个东西的指称）
    //   `fingerprint`= 当时的指纹（三态判定要比的就是它）
    // 顺序 = 声明顺序；`ref` 不给 ⇒ 它那一段不出现，正好把「两件」与「三件」分开摆着。
    [Fact]
    public void ToolResultEvent_WithDependencyAndFingerprint_KeepsItsExactBytes()
    {
        var evt = new TimelineEvent.ToolResultEvent
        {
            Id = "r1",
            At = DateTimeOffset.UnixEpoch,
            CallId = "c1",
            Content = [new Block.TextBlock("ok")],
            DependsOn = "src/auth.cs",
            Fingerprint = "1f2e3d",
        };

        Assert.Equal(
            """{"kind":"toolResult","callId":"c1","content":[{"kind":"text","text":"ok"}],"dependsOn":"src/auth.cs","fingerprint":"1f2e3d","id":"r1","at":"1970-01-01T00:00:00+00:00"}""",
            TimelineEvent.StableSerialize(evt));
    }

    // ⚠️ **指纹为 null 不是「没填」，是「这个结果没有指纹」**——`00` §5 的不可重演类
    // （执行命令 / 发邮件 / 取时间）**永不可优化**，因为它们**无指纹**。
    // 这条钉住：`null` 与 `""` 在盘上**必须长得不一样**（前者键不出现、后者是空串），
    // 否则「没有指纹」与「指纹是空串」就分不开了。
    [Fact]
    public void ToolResultEvent_WithNullFingerprint_OmitsTheKey_UnlikeAnEmptyString()
    {
        static string Ser(string? fp) => TimelineEvent.StableSerialize(new TimelineEvent.ToolResultEvent
        {
            Id = "r1",
            At = DateTimeOffset.UnixEpoch,
            CallId = "c1",
            Content = [],
            DependsOn = "x",
            Fingerprint = fp,
        });

        Assert.DoesNotContain("\"fingerprint\"", Ser(null));
        Assert.Contains("\"fingerprint\":\"\"", Ser(""));
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
            Id = "r1",
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

    // 老 state.json 没有 axes 键，必须仍然合法——WhenWritingNull 让没设轴的 state 不写出那个键。
    //
    // ⚠️ **版本 9 的一个例外**（2026-10-22，压缩改成工具）：`pendingCompact` 是**布尔**，
    // `WhenWritingNull` 对 `false` 不生效 ⇒ 它**每次都会写出来**。这不是漏了可空化：
    // 把它写成 `bool?` 就多出「null / false」两种表达同一个意思的状态（本仓明令要躲的形状）。
    // 代价如实记在这里——老文件读进来再写出去会多一个 `"pendingCompact":false`，
    // 语义不变（缺键与 false 都是「没有指令」）。
    [Fact]
    public void SessionState_WithoutAxes_OmitsTheKeyEntirely()
    {
        var state = new SessionState { SessionId = "abc123", Status = "Idle", Turn = 0, Pending = [] };

        Assert.Equal(
            """{"version":9,"sessionId":"abc123","status":"Idle","turn":0,"pending":[],"pendingCompact":false}""",
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
            """{"version":9,"sessionId":"abc123","status":"Idle","turn":0,"pending":[],"axes":{"safety":"prod"},"pendingCompact":false}""",
            System.Text.Json.JsonSerializer.Serialize(state, Json.Options));
    }
}
