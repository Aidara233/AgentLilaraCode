using Lilara.Core.Kernel;

namespace Lilara.Core.Tests.Fakes;

/// <summary>
/// 不联网的传输替身。默认两个方向都回一句可辨认的结果；测试按需覆盖委托。
/// </summary>
public sealed class FakeNetworkTransport : INetworkTransport
{
    public List<string> Queries { get; } = [];
    public List<string> Urls { get; } = [];

    public Func<NetworkConfig, string, CancellationToken, Task<NetworkSearchResult>> OnSearch { get; set; } =
        (_, query, _) => Task.FromResult(new NetworkSearchResult(
            "摘要内容",
            [new NetworkSearchHit("标题一", "https://example.test/1", "片段一", 0.9)]));

    public Func<NetworkConfig, string, CancellationToken, Task<NetworkFetchResult>> OnFetch { get; set; } =
        (_, url, _) => Task.FromResult(new NetworkFetchResult(
            url, url, 200, "text/html", "页面标题", "正文内容", false, 12));

    public Task<NetworkSearchResult> SearchAsync(NetworkConfig config, string query, CancellationToken ct)
    {
        Queries.Add(query);
        return OnSearch(config, query, ct);
    }

    public Task<NetworkFetchResult> FetchAsync(NetworkConfig config, string url, CancellationToken ct)
    {
        Urls.Add(url);
        return OnFetch(config, url, ct);
    }
}
