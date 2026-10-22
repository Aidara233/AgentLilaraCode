using Lilara.Core.Kernel;
using Lilara.Core.Network;

namespace Lilara.Core.Tests;

/// <summary>
/// 配置只有一份。⚠️ 读不到 / 坏掉 / 太新一律回**默认值（关闭）**——
/// 失败方向必须是「不联网」，不能是「用一份猜的配置联网」。
/// </summary>
public class NetworkStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-netcfg-" + Guid.NewGuid().ToString("N"));

    public NetworkStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDisabledDefaults()
    {
        var cfg = new NetworkStore(_root).Load();

        Assert.False(cfg.Enabled);
        Assert.Null(cfg.Search.ApiKey);
        Assert.Equal("https://api.tavily.com/search", cfg.Search.Endpoint);
        Assert.Equal(NetworkLimits.DefaultSearchResults, cfg.Search.MaxResults);
        Assert.Empty(cfg.Fetch.AllowDomains);
    }

    [Fact]
    public void SaveLoad_RoundTrips()
    {
        var store = new NetworkStore(_root);
        store.Save(new NetworkConfig
        {
            Enabled = true,
            Search = new NetworkSearchConfig
            {
                ApiKey = "tvly-secret", Endpoint = "https://example.test/search",
                MaxResults = 7, TimeoutSeconds = 33,
            },
            Fetch = new NetworkFetchConfig
            {
                MaxBytes = 123456, TimeoutSeconds = 11,
                AllowDomains = ["Wiki.Intra", ".local.test"],
            },
        });

        var cfg = store.Load();

        Assert.True(cfg.Enabled);
        Assert.Equal("tvly-secret", cfg.Search.ApiKey);
        Assert.Equal("https://example.test/search", cfg.Search.Endpoint);
        Assert.Equal(7, cfg.Search.MaxResults);
        Assert.Equal(33, cfg.Search.TimeoutSeconds);
        Assert.Equal(123456, cfg.Fetch.MaxBytes);
        // 域名落盘前会规范化（小写、去前导点）。
        Assert.Equal(["wiki.intra", "local.test"], cfg.Fetch.AllowDomains);
    }

    [Fact]
    public void Load_CorruptFile_ReturnsDefaults()
    {
        File.WriteAllText(Path.Combine(_root, "network.json"), "{ not json");

        Assert.False(new NetworkStore(_root).Load().Enabled);
    }

    [Fact]
    public void Load_TooNewFile_ReturnsDefaults()
    {
        File.WriteAllText(Path.Combine(_root, "network.json"),
            $$"""{"version":{{Json.FormatVersion + 1}},"enabled":true}""");

        Assert.False(new NetworkStore(_root).Load().Enabled);
    }

    [Fact]
    public void Normalize_ClampsNumbersAndDropsEmptyDomains()
    {
        var cfg = NetworkStore.Normalize(new NetworkConfig
        {
            Search = new NetworkSearchConfig { MaxResults = 9999, TimeoutSeconds = 0 },
            Fetch = new NetworkFetchConfig
            {
                TimeoutSeconds = 9999,
                MaxBytes = 1,
                AllowDomains = [" ", "", "a.test", "a.test"],
            },
        });

        Assert.Equal(NetworkLimits.MaxSearchResults, cfg.Search.MaxResults);
        Assert.Equal(NetworkLimits.MinTimeoutSeconds, cfg.Search.TimeoutSeconds);
        Assert.Equal(NetworkLimits.MaxTimeoutSeconds, cfg.Fetch.TimeoutSeconds);
        Assert.Equal(NetworkLimits.MinFetchBytes, cfg.Fetch.MaxBytes);
        Assert.Equal(["a.test"], cfg.Fetch.AllowDomains);
    }
}
