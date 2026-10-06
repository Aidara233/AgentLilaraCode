using Lilara.Core.Kernel;
using Lilara.Core.Storage;

namespace Lilara.Core.Tests;

public class BlobStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-blob-" + Guid.NewGuid().ToString("N"));

    private readonly BlobStore _store;

    public BlobStoreTests() => _store = new BlobStore(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // 名字 = 内容的 sha256。这条是**内容寻址**的定义，不是实现细节。
    [Fact]
    public void Put_NamesTheObjectByItsContentHash()
    {
        var sha = _store.Put("你好"u8.ToArray());

        Assert.Equal("670d9743542cae3ea7ebe36af56bd53648b0a1126162e78d81a32934a711302e", sha);
        Assert.Equal("你好"u8.ToArray(), _store.TryGet(sha));
    }

    // 幂等：同样字节写两次**不产生第二个文件**。这就是去重，不是优化。
    [Fact]
    public void Put_IsIdempotent_AndDeduplicates()
    {
        var a = _store.Put("same"u8.ToArray());
        var b = _store.Put("same"u8.ToArray());

        Assert.Equal(a, b);
        Assert.Single(Directory.GetFiles(_store.Root));
    }

    // ⚠️ **崩在「算完哈希、还没写完」之间**只可能留一个孤儿 .tmp，**不可能**出现一个
    // 「名字说它是 A、内容是 B」的坏对象——因为名字是从内容算出来的。
    // 这条钉的是那个**顺序**（先算后写），今天不可能违反，但改 `Put` 的顺序会让它失去保证。
    [Fact]
    public void Put_LeavesNoStrayFiles()
    {
        _store.Put("x"u8.ToArray());

        // 只该有一个文件，且它的名字就是内容哈希（没有 .tmp 残留）。
        var file = Assert.Single(Directory.GetFiles(_store.Root));
        Assert.Equal(Path.Combine(_store.Root, _store.Put("x"u8.ToArray())), file);
    }

    [Fact]
    public void TryGet_MissingObject_ReturnsNullNotThrow()
    {
        Assert.Null(_store.TryGet(new string('a', 64)));
        Assert.False(_store.Has(new string('a', 64)));
    }

    /// <summary>
    /// ⚠️ **这条是安全守卫，不是格式检查**：sha256 会**直接变成路径段**，
    /// 而 `Path.Combine(_root, "../../../etc/passwd")` 会读出数据根之外的东西。
    /// 判据是「**它是不是 64 位小写 hex**」——完全确定的形状，比 provider id 那条还严。
    /// </summary>
    [Theory]
    [InlineData("../../../etc/passwd")]   // 穿越
    [InlineData("..")]                    // 上跳
    [InlineData(".")]
    [InlineData("")]
    [InlineData("/abs")]                  // 绝对路径
    [InlineData("C:\\abs")]
    [InlineData("a/b")]                   // 分隔符
    [InlineData("a\\b")]
    [InlineData("ABC")]                   // 大写 hex 不收（形状不匹配）
    [InlineData("zzzz")]                  // 长度对但字符不对
    public void TryGet_WithAMalformedName_IsRejected(string bad)
    {
        Assert.Null(_store.TryGet(bad));
        Assert.False(_store.Has(bad));
    }

    // 合法形状但不存在 ⇒ 也是 null（两种「没有」在返回值上分不开，这是刻意的：
    // 调用方只该关心「拿不到」）。
    [Fact]
    public void TryGet_WithAWellFormedButAbsentName_ReturnsNull()
    {
        Assert.Null(_store.TryGet(new string('0', 64)));
    }
}

public class BlockShapeTests
{
    // ⚠️ **工具参数必须逐字回传**（01 §9 铁律）。这条钉的就是「逐字」——
    // 期望串里的键序 / 空白 / 转义都是**刻意写的乱样子**，规范化一下就红。
    // 实测过：`JsonElement` 做不到这件事（解析再序列化会规范化），所以用的是原文 + JsonRawConverter。
    [Fact]
    public void ToolUseBlock_KeepsArgumentsByteForByte()
    {
        // 键序逆字母序、有多余空白、有需要转义的 & —— 全是规范化会改动的地方。
        const string args = """{ "zeta" : 1, "alpha": "a&b" }""";

        var block = new Block.ToolUseBlock("call1", "read", args);

        var json = Json.StableSerialize(block);

        Assert.Contains(args, json);
        // 顺带钉住判别式与两个字段名。
        Assert.Contains("\"kind\":\"toolUse\"", json);
        Assert.Contains("\"callId\":\"call1\"", json);
    }

    // 往返也必须是逐字的（不只是「结构相同」）。
    [Fact]
    public void ToolUseBlock_RoundTripsVerbatim()
    {
        const string args = """{ "zeta" : 1, "alpha": "a&b" }""";
        var json = Json.StableSerialize(new Block.ToolUseBlock("c1", "read", args));

        var back = System.Text.Json.JsonSerializer.Deserialize<Block>(json, Json.Options);

        Assert.Equal(new Block.ToolUseBlock("c1", "read", args), back);
    }

    // ⚠️ 参数不是合法 JSON ⇒ **当场抛**，不许把垃圾写进只追加的历史里（写进去就一直在）。
    [Fact]
    public void ToolUseBlock_WithInvalidArguments_ThrowsOnSerialize()
    {
        var block = new Block.ToolUseBlock("c1", "read", "这不是 JSON");

        Assert.ThrowsAny<System.Text.Json.JsonException>(() => Json.StableSerialize(block));
    }

    // 图文混排：文本与图片是**兄弟块**，不许折进一段文本（01 §3.6）。
    [Fact]
    public void MixedTextAndImage_StaySiblingBlocks()
    {
        var blocks = new List<Block>
        {
            new Block.TextBlock("报错在这里"),
            new Block.ImageBlock("ab" + new string('0', 62), "image/png"),
        };

        var json = System.Text.Json.JsonSerializer.Serialize(blocks, Json.Options);

        Assert.Contains("\"kind\":\"text\"", json);
        Assert.Contains("\"kind\":\"image\"", json);
        Assert.Contains("\"mediaType\":\"image/png\"", json);
        // 两块各自独立 —— 折成一段的话这里只会有一个 kind。
        Assert.Equal(2, json.Split("\"kind\":\"").Length - 1);
    }
}

public class TimelineEventShapeTests
{
    // 工具结果的内容是**一串块**，可混排、也可纯图（01 §3.6）。
    [Fact]
    public void ToolResultEvent_CarriesAStringOfBlocks()
    {
        var evt = new TimelineEvent.ToolResultEvent
        {
            Id = "r1",
            At = DateTimeOffset.UnixEpoch,
            CallId = "c1",
            Content = [new Block.TextBlock("ok"), new Block.ImageBlock(new string('a', 64), "image/png")],
        };

        var json = TimelineEvent.StableSerialize(evt);

        Assert.Contains("\"kind\":\"toolResult\"", json);
        Assert.Contains("\"callId\":\"c1\"", json);
        Assert.Contains("\"kind\":\"image\"", json);
    }

    /// <summary>
    /// 空的 `Content` 是**合法**形态：「这个工具什么都没返回」。
    ///
    /// <para>⚠️ **这条测试换过一次靶子**（2026-10-06）：它原先钉的是「`Ref` 非空时
    /// 序列化形状」，而 <c>Ref</c> 那条机制**已经作废**（见
    /// <see cref="TimelineEvent.ToolResultEvent.Ref"/> 的注释——它零调用方，
    /// 而它换来的三样东西全是负的）。字段留着不动格式，但**没有产出方**，
    /// 所以不值得再为它钉一条测试。</para>
    /// </summary>
    [Fact]
    public void ToolResultEvent_WithNoContent_KeepsAnEmptyContentList()
    {
        var evt = new TimelineEvent.ToolResultEvent
        {
            Id = "r2",
            At = DateTimeOffset.UnixEpoch,
            CallId = "c9",
            Content = [],
        };

        var json = TimelineEvent.StableSerialize(evt);

        // ⚠️ `Content` 是 `required` 的**非可空**列表 ⇒ 它是 `[]`，**不会**被 WhenWritingNull 省掉。
        // （省掉的话，读回来就是「缺一个必填字段」 = 一条坏行。）
        Assert.Contains("\"content\":[]", json);
        // ⚠️ 作废的字段**不出现**（`WhenWritingNull`）——这正是留字段不破格式的原因。
        Assert.DoesNotContain("\"ref\"", json);
    }

    // ⚠️ 稳定 id 是**取回 / 改历史 / 头引用**三处共用的那个（01 §8.0.2 第七节）。
    // 它必须**往返保真**——否则 `fetch` 的引用在重启后就是死链。
    // ⚠️ 它必须是 `required` 而不是「属性初始化器给默认值」：后者会让**没有 id 的老行**
    // 在每次反序列化时生成一个新随机 id（同一个文件读两次得到两组 id，静默失效）。
    [Fact]
    public void Id_RoundTrips_AndIsNotRegeneratedOnRead()
    {
        const string json = """{"kind":"message","from":"user","blocks":[],"id":"r7f3a","at":"1970-01-01T00:00:00+00:00"}""";

        var first = System.Text.Json.JsonSerializer.Deserialize<TimelineEvent>(json, Json.Options)!;
        var second = System.Text.Json.JsonSerializer.Deserialize<TimelineEvent>(json, Json.Options)!;

        Assert.Equal("r7f3a", first.Id);
        Assert.Equal(first.Id, second.Id);   // 两次读必须得到同一个 id
    }

    // 缺 id 的老行 ⇒ **当场抛**，不是静默编一个。抛是响亮失败；编一个是静默失效。
    [Fact]
    public void Event_WithoutAnId_IsRejectedLoudly()
    {
        const string old = """{"kind":"message","from":"user","blocks":[],"at":"1970-01-01T00:00:00+00:00"}""";

        Assert.ThrowsAny<System.Text.Json.JsonException>(
            () => System.Text.Json.JsonSerializer.Deserialize<TimelineEvent>(old, Json.Options));
    }
}
