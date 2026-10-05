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
}
