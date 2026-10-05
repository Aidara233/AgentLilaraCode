using Lilara.Core.Adapters;
using Lilara.Core.Providers;

namespace Lilara.Core.Tests;

public class ProviderStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-prov-" + Guid.NewGuid().ToString("N"));
    private readonly ProviderStore _store;

    public ProviderStoreTests() => _store = new ProviderStore(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static ProviderDefinition Def(string id = "claude") => new()
    {
        Id = id,
        Protocol = Protocols.ClaudeMessages,
        ApiKey = "sk-test-not-a-real-key",
        DefaultModel = "claude-opus-5",
        Modality = "vision",
    };

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        _store.Save(Def());

        Assert.Equal(Def(), _store.Load("claude"));
    }

    /// <summary>
    /// 端点必须能落盘再读回（01 §7：全局层持有「端点、key、协议类型」）。
    /// </summary>
    [Fact]
    public void RoundTrip_PreservesEndpoint()
    {
        _store.Save(Def() with { Endpoint = "https://gateway.example.com/anthropic" });

        Assert.Equal("https://gateway.example.com/anthropic", _store.Load("claude")!.Endpoint);
    }

    /// <summary>
    /// ⚠️ 加法式改动：既有 providers/*.json 里**没有** endpoint 字段，
    /// 它们必须照旧能反序列化（Endpoint 为 null），否则升级即炸。
    /// </summary>
    [Fact]
    public void Load_ToleratesFileWithoutEndpointField()
    {
        Directory.CreateDirectory(Path.Combine(_root, "providers"));
        File.WriteAllText(
            Path.Combine(_root, "providers", "legacy.json"),
            """
            {
              "id": "legacy",
              "protocol": "claude-messages",
              "apiKey": "sk-legacy",
              "defaultModel": "claude-opus-5",
              "modality": "vision"
            }
            """);

        var loaded = _store.Load("legacy");

        Assert.NotNull(loaded);
        Assert.Null(loaded!.Endpoint);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAuthScheme()
    {
        // ⚠️ 认证方式和端点一样，是**后端怪癖**——归提供商配置，不归代码。
        // 本机网关只认 Authorization: Bearer，官方只认 x-api-key。
        _store.Save(Def() with { AuthScheme = AuthSchemes.Bearer });

        Assert.Equal(AuthSchemes.Bearer, _store.Load("claude")!.AuthScheme);
    }

    [Fact]
    public void AuthScheme_IsOptional_OldFilesStillLoad()
    {
        // ⚠️ 老 providers/*.json 没有这个字段，必须照常反序列化（可空 + 不 required）
        Directory.CreateDirectory(Path.Combine(_root, "providers"));
        File.WriteAllText(Path.Combine(_root, "providers", "old.json"),
            """{"id":"old","protocol":"claude-messages","apiKey":"k","defaultModel":"m","modality":"vision"}""");

        Assert.Null(_store.Load("old")!.AuthScheme);
    }

    [Fact]
    public void Load_ReturnsNullWhenAbsent()
    {
        Assert.Null(_store.Load("nope"));
    }

    [Fact]
    public void Save_OverwritesExistingDefinition()
    {
        _store.Save(Def());
        _store.Save(Def() with { DefaultModel = "claude-sonnet-5" });

        Assert.Equal("claude-sonnet-5", _store.Load("claude")!.DefaultModel);
        Assert.Single(_store.List());
    }

    [Fact]
    public void List_ReturnsAllSaved()
    {
        _store.Save(Def("a"));
        _store.Save(Def("b"));

        Assert.Equal(2, _store.List().Count);
    }

    [Fact]
    public void ToSelection_UsesDefaultModelWhenNoOverride()
    {
        var selection = _store.ToSelection(Def(), null, "high", 16000);

        Assert.Equal("claude-opus-5", selection.ModelId);
        Assert.Equal("claude", selection.ProviderId);
        Assert.Equal("claude-messages", selection.Protocol);
        Assert.Equal("high", selection.Effort);
        Assert.Equal(16000, selection.MaxTokens);
    }

    [Fact]
    public void ToSelection_HonoursOverride()
    {
        var selection = _store.ToSelection(Def(), "claude-sonnet-5", "medium", 8000);

        Assert.Equal("claude-sonnet-5", selection.ModelId);
        Assert.Equal("medium", selection.Effort);
        Assert.Equal(8000, selection.MaxTokens);
    }

    [Fact]
    public void List_SkipsCorruptFileRatherThanCrashingStartup()
    {
        // ⚠️ 与 SessionStore.TryReadSpec 同一个坑：损坏的文件不该让核心起不来。
        // List() 正是 Task 11 启动时判断「有没有 provider」所调用的。
        _store.Save(Def("good"));

        File.WriteAllText(Path.Combine(_root, "providers", "bad.json"), "{ this is not json");

        Assert.Equal(new[] { "good" }, _store.List().Select(d => d.Id));
        Assert.Null(_store.Load("bad"));
    }

    /// <summary>
    /// ⚠️ **`Delete` 的返回值是「有没有真的删掉一个文件」，判据在**文件层**，不是 `Load(id) != null`。**
    ///
    /// <para>⚠️ 差别不是风格：`Load` 把**损坏**的文件也当作「不存在」。
    /// 若 dispatcher 用 `Load(...) is null` 来判「不存在 ⇒ 拒绝」，
    /// 那么一个**损坏的** `providers/&lt;id&gt;.json` **永远删不掉**——
    /// 而「删掉一个坏配置」正是用户最想做的事（也是本任务明确不许加闸的那件事）。</para>
    /// </summary>
    [Fact]
    public void Delete_RemovesTheFile_AndReportsThatItDid()
    {
        _store.Save(Def("gw"));

        Assert.True(_store.Delete("gw"));                       // ⚠️ 真删了 ⇒ true
        Assert.Null(_store.Load("gw"));                         // 读不到了
        Assert.False(File.Exists(Path.Combine(_root, "providers", "gw.json")));
        Assert.Empty(_store.List());
    }

    /// <summary>
    /// ⚠️ **不存在 ⇒ `false`**（调用方据此回 rejected，**不是**幂等成功）。
    /// 这条同时钉住「别用 `File.Delete` 的静默 no-op 当判据」：它对不存在的文件**不抛也不报**。
    /// </summary>
    [Fact]
    public void Delete_OnAnAbsentId_ReportsThatItDidNothing()
    {
        Assert.False(_store.Delete("nope"));
    }

    /// <summary>
    /// ⚠️ **损坏的 provider 文件必须删得掉**——这一条钉的是上面那条判据的**选取理由**。
    /// 用 `Load != null` 判存在性的实现，在这里会**返回 false**（`Load` 吞掉 `JsonException` 返回 null）
    /// ⇒ 坏配置永远删不掉。⇒ 这就是「判据一处、且在文件层」的可观测后果。
    /// </summary>
    [Fact]
    public void Delete_RemovesACorruptFileToo()
    {
        Directory.CreateDirectory(Path.Combine(_root, "providers"));
        var bad = Path.Combine(_root, "providers", "bad.json");
        File.WriteAllText(bad, "{ this is not json");

        Assert.True(_store.Delete("bad"));
        Assert.False(File.Exists(bad));
    }

    /// <summary>
    /// ⚠️ **`ProviderDefinition` 的落盘形状白名单**——T6 那次磁盘静默变更的根。
    ///
    /// <para>为什么有它：`ProviderStore.Save` 用 `Canonical.Json.Options` **直接**序列化本类型，
    /// 而那份设置**没有** `IgnoreReadOnlyProperties`。T6 往本类型加了个派生属性 `HasApiKey`，
    /// 于是每个 `providers/&lt;id&gt;.json` 多出一个 `hasApiKey` 键——**磁盘格式静默变更，
    /// 而全量测试全绿**：`DiskFormatGoldenTests` 只覆盖 `SessionSpec` / `SessionState` /
    /// `TimelineEvent` / `Block`（**不含本类型**），本文件其余测试是往返 / 旧格式加载、
    /// **从不断言写出的字节**，而记录相等又不看派生属性（它没有后备字段）⇒ 多出的键**不影响任何断言**。
    /// ⇒ 当时用 `[JsonIgnore]` 堵住了**那一个属性**，**没堵住那一类**：下次再加字段 / 派生属性，
    /// 磁盘格式会再次静默变更而**没有东西会红**。这条就是堵那一类的。</para>
    ///
    /// <para>⚠️ **纯反射，与序列化选项无关**：`PropertyNamingPolicy` / `WhenWritingNull` /
    /// `IgnoreReadOnlyProperties` 怎么改都动不了它——它钉的是「这个类型**声明了哪些属性**」，
    /// 于是「加字段」被强制成一次**有意识的动作**，而不是顺手。</para>
    ///
    /// <para>⚠️ **`HasApiKey` 也在白名单里**（它带 `[JsonIgnore]`，但仍是 CLR 属性 ⇒ 照样出现在
    /// `GetProperties()` 里）。**这是有意的**：白名单钉的是「**声明**了什么」，
    /// `[JsonIgnore]` 管的是「**写不写盘**」——**是另一层**。若把 `[JsonIgnore]` 的成员滤出白名单，
    /// 那「加/去属性」就要在这一处分两种情况判，而**把 `HasApiKey` 上的 `[JsonIgnore]` 摘掉**
    /// （**正是 T6 的原始形态**：派生属性 + 落盘）时白名单**反而不动** ⇒ 恰好漏掉本任务要防的那次改动。
    /// ⇒ 一概收进来，「声明」这一点才是一份完整、无条件的清单。</para>
    ///
    /// <para>⚠️ **名字 + 类型**，并按 Ordinal 定序（反射的属性顺序不保证 ⇒ 不定序会偶发红）。
    /// 只钉名字的话，把 `ApiKey: string` 换成**别的持有 key 的类型**不会红。</para>
    /// </summary>
    [Fact]
    public void ProviderDefinition_HasExactlyTheseProperties_SoTheOnDiskShapeIsPinned()
    {
        static string[] Shape(Type t) =>
            t.GetProperties()
                .Select(p => $"{p.Name}:{p.PropertyType.Name}")
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();

        Assert.Equal(
            ["ApiKey:String", "AuthScheme:String", "DefaultModel:String", "Endpoint:String",
             "HasApiKey:Boolean", "Id:String", "Modality:String", "Protocol:String"],
            Shape(typeof(ProviderDefinition)));
    }
}
