using Lilara.Core.Kernel;
using Lilara.Core.Protocol;
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

    // 端点必须能落盘再读回。
    [Fact]
    public void RoundTrip_PreservesEndpoint()
    {
        _store.Save(Def() with { Endpoint = "https://gateway.example.com/anthropic" });

        Assert.Equal("https://gateway.example.com/anthropic", _store.Load("claude")!.Endpoint);
    }

    // 一次模型调用的超时是按提供商的配置，必须落盘再读回（重试靠它，重启后要还在）。
    [Fact]
    public void RoundTrip_PreservesTimeout()
    {
        _store.Save(Def() with { TimeoutSeconds = 600 });

        Assert.Equal(600, _store.Load("claude")!.TimeoutSeconds);
    }

    // 老 providers/*.json 没有 timeoutSeconds ⇒ 读回来是 null（= 没配 = 用协议默认），
    // 不是 0（0 会被当成「立刻超时」的一次真实配置）。
    [Fact]
    public void Timeout_IsOptional_OldFilesStillLoad()
    {
        Directory.CreateDirectory(Path.Combine(_root, "providers"));
        File.WriteAllText(Path.Combine(_root, "providers", "old.json"),
            """{"id":"old","protocol":"claude-messages","apiKey":"k","defaultModel":"m","modality":"vision"}""");

        Assert.Null(_store.Load("old")!.TimeoutSeconds);
    }

    // 加法式改动：既有 providers/*.json 没有 endpoint 字段，必须照旧反序列化（Endpoint 为 null）。
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
        // 认证方式是后端怪癖，归提供商配置：本机网关只认 Authorization: Bearer，官方只认 x-api-key。
        _store.Save(Def() with { AuthScheme = AuthSchemes.Bearer });

        Assert.Equal(AuthSchemes.Bearer, _store.Load("claude")!.AuthScheme);
    }

    [Fact]
    public void AuthScheme_IsOptional_OldFilesStillLoad()
    {
        // 老 providers/*.json 没有这个字段，必须照常反序列化（可空 + 不 required）。
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
        // 损坏的文件不该让核心起不来（与 SessionStore.TryReadSpec 同一个坑）。
        _store.Save(Def("good"));

        File.WriteAllText(Path.Combine(_root, "providers", "bad.json"), "{ this is not json");

        Assert.Equal(new[] { "good" }, _store.List().Select(d => d.Id));
        Assert.Null(_store.Load("bad"));
    }

    // Delete 的判据在文件层（有没有真删掉文件），不是 Load(id) != null——否则损坏的 provider 永远删不掉。
    [Fact]
    public void Delete_RemovesTheFile_AndReportsThatItDid()
    {
        _store.Save(Def("gw"));

        Assert.True(_store.Delete("gw"));                       // ⚠️ 真删了 ⇒ true
        Assert.Null(_store.Load("gw"));                         // 读不到了
        Assert.False(File.Exists(Path.Combine(_root, "providers", "gw.json")));
        Assert.Empty(_store.List());
    }

    // 不存在 ⇒ false（调用方据此回 rejected，不是幂等成功）；别用 File.Delete 的静默 no-op 当判据。
    [Fact]
    public void Delete_OnAnAbsentId_ReportsThatItDidNothing()
    {
        Assert.False(_store.Delete("nope"));
    }

    // 损坏的 provider 文件必须删得掉——用 Load != null 判存在性的实现在这里会返回 false（Load 吞掉 JsonException）。
    [Fact]
    public void Delete_RemovesACorruptFileToo()
    {
        Directory.CreateDirectory(Path.Combine(_root, "providers"));
        var bad = Path.Combine(_root, "providers", "bad.json");
        File.WriteAllText(bad, "{ this is not json");

        Assert.True(_store.Delete("bad"));
        Assert.False(File.Exists(bad));
    }

    // ProviderStore.Save 直接用 Kernel.Json.Options 序列化本类型（没有 IgnoreReadOnlyProperties），
    // 加派生属性会多出键、静默变更磁盘格式（T6 的 hasApiKey 就是这么进的盘）。
    // 两份清单缺一不可，都按 Name:Type 整串 Ordinal 定序（反射属性顺序不保证）：
    // ① 声明的属性（加属性即红）；② 会被写盘的属性（摘掉 [JsonIgnore] 即红——① 盖不住的那一格）。
    // 两份都纯反射，与序列化选项无关。
    [Fact]
    public void ProviderDefinition_HasExactlyTheseProperties()
    {
        // ① 声明的属性（含带 [JsonIgnore] 的）——「加了属性」红这一条。
        static string[] DeclaredShape(Type t) =>
            t.GetProperties()
                .Select(p => $"{p.Name}:{p.PropertyType.Name}")
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();

        // ② 会被写盘的属性（滤掉带 [JsonIgnore] 的）——「摘掉 [JsonIgnore]」红这一条。
        static string[] PersistedShape(Type t) =>
            t.GetProperties()
                .Where(p => !p.IsDefined(
                    typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), inherit: true))
                .Select(p => $"{p.Name}:{p.PropertyType.Name}")
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();

        Assert.Equal(
            ["ApiKey:String", "AuthScheme:String", "DefaultModel:String", "EffectiveTimeoutSeconds:Nullable`1",
             "Endpoint:String",
             "HasApiKey:Boolean", "Id:String", "Modality:String", "Models:IReadOnlyList`1",
             "Protocol:String", "TimeoutSeconds:Nullable`1", "Version:Int32"],
            DeclaredShape(typeof(ProviderDefinition)));

        // HasApiKey / EffectiveTimeoutSeconds 刻意不在这条里——它们带 [JsonIgnore]。
        Assert.Equal(
            ["ApiKey:String", "AuthScheme:String", "DefaultModel:String", "Endpoint:String",
             "Id:String", "Modality:String", "Models:IReadOnlyList`1",
             "Protocol:String", "TimeoutSeconds:Nullable`1", "Version:Int32"],
            PersistedShape(typeof(ProviderDefinition)));
    }
}
