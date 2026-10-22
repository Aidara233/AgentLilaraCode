namespace Lilara.Core.Kernel;

/// <summary>
/// **联网能力的全局配置**（`<数据根>/network.json`）。
///
/// <para>⚠️ <b>它只管「基础设施」——凭据、端点、上限；「这个会话能不能联网」归
/// <see cref="Axes.Network"/> 那根轴</b>。两件事分开，是因为它们的生命周期不同：
/// 端点与 key 是设备级的（换一家搜索服务是一次配置），而联网开关是**会话级**的
/// （同一个核心上，一个会话在查资料、另一个会话不该能出网）。</para>
///
/// <para>⚠️ <b>`enabled = false` 是默认值，而且是安全的那一侧</b>：新装的核心不该
/// 在一次默认会话里悄悄联网。工具仍然常驻工具表（见 `BuiltInTools`），调用时只回
/// 一句「未启用」——工具表因此逐字节稳定，不破前缀。</para>
/// </summary>
public sealed record NetworkConfig
{
    public int Version { get; init; } = Json.FormatVersion;

    /// <summary>总开关。关着时两个联网工具都只回一句人话，不发出任何请求。</summary>
    public bool Enabled { get; init; }

    public NetworkSearchConfig Search { get; init; } = new();

    public NetworkFetchConfig Fetch { get; init; } = new();
}

/// <summary>搜索那一半。今天只有 Tavily；`Provider` 留着是为了将来加一家时不用改文件形状。</summary>
public sealed record NetworkSearchConfig
{
    public string Provider { get; init; } = "tavily";

    /// <summary>
    /// API key。⚠️ <b>只写不读</b>：控制面的 `GET /network-config` 只回 `hasApiKey`，
    /// 与 `/providers` 同一条纪律。
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>可改，是为了让测试与自建网关能指到别处（默认 Tavily 官方端点）。</summary>
    public string Endpoint { get; init; } = "https://api.tavily.com/search";

    public int MaxResults { get; init; } = NetworkLimits.DefaultSearchResults;

    public int TimeoutSeconds { get; init; } = NetworkLimits.DefaultTimeoutSeconds;
}

/// <summary>抓取那一半。</summary>
public sealed record NetworkFetchConfig
{
    public int TimeoutSeconds { get; init; } = NetworkLimits.DefaultTimeoutSeconds;

    public int MaxBytes { get; init; } = NetworkLimits.DefaultFetchBytes;

    /// <summary>
    /// 域名白名单。⚠️ <b>非空 ⇒ 只许名单内</b>（后缀匹配，带点边界）；
    /// 为空 ⇒ 公网可访问，但**仍拒绝** loopback / 私网 / link-local 等。
    /// ⚠️ 显式写进名单的域名**可以**解析到私网地址——那是用户的明确选择
    /// （本机服务、内网 wiki、e2e 的 mock 都需要它）。这是一条刻意的逃生口，
    /// 不是漏判。
    /// </summary>
    public IReadOnlyList<string> AllowDomains { get; init; } = [];
}

/// <summary>
/// 联网相关的**硬上限**。⚠️ 与 `ToolLimits` 是同一族：这里放 `Kernel` 能看见的那几个
/// （配置默认值要用），`ToolLimits` 再把它们别名出来给工具读——**数值只有这一份**。
/// </summary>
public static class NetworkLimits
{
    /// <summary>一次搜索默认要几条结果。占位值。</summary>
    public const int DefaultSearchResults = 5;

    /// <summary>一次搜索最多要几条结果（配置里填再大也按这个封顶）。占位值。</summary>
    public const int MaxSearchResults = 10;

    /// <summary>默认超时（秒）。</summary>
    public const int DefaultTimeoutSeconds = 20;

    /// <summary>超时的允许区间（秒）。低于 1 秒几乎一定失败；高于 120 秒会把一步拖得太久。</summary>
    public const int MinTimeoutSeconds = 1;
    public const int MaxTimeoutSeconds = 120;

    /// <summary>抓取时默认最多读多少字节。占位值。</summary>
    public const int DefaultFetchBytes = 2 * 1024 * 1024;

    /// <summary>抓取字节数的允许区间。上限防「一次 fetch 撑爆内存 / 盘」，不是防上下文。</summary>
    public const int MinFetchBytes = 4 * 1024;
    public const int MaxFetchBytes = 8 * 1024 * 1024;

    /// <summary>重定向最多跟几跳。超过就停，并把「停在哪一跳」说清楚。</summary>
    public const int MaxRedirects = 5;

    /// <summary>搜索响应最多读多少字节（防一个坏端点把内存撑爆）。占位值。</summary>
    public const int MaxSearchResponseBytes = 2 * 1024 * 1024;
}

/// <summary>联网工具能做的那两件事。用来对 <see cref="Axes.Network"/> 的档位。</summary>
public enum NetworkCapability
{
    Search,
    Fetch,
}

/// <summary>
/// 一次工具执行能看见的联网能力——**配置 + 轴值的快照**。
///
/// <para>⚠️ <b>它是按步冻的</b>（与 `now` / 权限档位同一个道理）：一步之内两条联网调用
/// 必须按同一份配置与同一个轴值判定，否则记录里看不出为什么一条通、一条拒。</para>
/// </summary>
public sealed class NetworkAccess(NetworkConfig config, string level, INetworkTransport transport)
{
    public NetworkConfig Config { get; } = config;

    public INetworkTransport Transport { get; } = transport;

    /// <summary>当前 `network` 轴的值。未知值按 `off` 处理（宁可漏放）。</summary>
    public string Level { get; } = level;

    public bool Allows(NetworkCapability capability) => Level switch
    {
        // ⚠️ 用 ordinal 比，且未知值落到 `_ => false`：与 `Gate.Decide` 的「不知道怎么判就拒绝」同向。
        "all" => true,
        "search" => capability == NetworkCapability.Search,
        "fetch" => capability == NetworkCapability.Fetch,
        _ => false,
    };

    /// <summary>
    /// 这一格为什么不能跑——**一段说人话的理由**，由工具原样放进结果。
    /// 返回 `null` 表示「这一格没有额外阻挡」（仍要过权限闸）。
    /// </summary>
    public string? DenialFor(NetworkCapability capability)
    {
        if (!Allows(capability))
        {
            var need = capability == NetworkCapability.Search ? "search" : "fetch";
            return $"联网轴当前是 `{Level}`，不允许{(capability == NetworkCapability.Search ? "搜索" : "抓取网页")}。"
                 + $"要让这一类能跑，请把 `network` 轴拨到 `{need}` 或 `all`（session.setAxis）。";
        }

        if (!Config.Enabled)
            return "联网能力在核心侧未启用。请在头的「后厅 · 联网」页开启，或运行 `network set --enabled on`。";

        if (capability == NetworkCapability.Search && string.IsNullOrWhiteSpace(Config.Search.ApiKey))
            return "还没有配置搜索 API key。请在头的「后厅 · 联网」页填写 Tavily key。";

        return null;
    }
}

/// <summary>一次搜索的结果。`Answer` 是上游给的摘要（Tavily 可能不给）。</summary>
public sealed record NetworkSearchResult(string? Answer, IReadOnlyList<NetworkSearchHit> Hits);

public sealed record NetworkSearchHit(string Title, string Url, string? Snippet, double? Score);

/// <summary>
/// 一次抓取的结果。`Text` 是**已抽取的正文**（HTML 已剥标签），不是原始字节。
/// `Truncated` 为真时 `Text` 只是前一段——这一点必须原样告诉模型。
/// </summary>
public sealed record NetworkFetchResult(
    string RequestedUrl, string FinalUrl, int StatusCode, string? ContentType,
    string? Title, string Text, bool Truncated, long BytesRead);

/// <summary>
/// 联网请求的传输层。⚠️ 抽成接口只有一个理由：**SSRF 与超时必须有测试**，
/// 而测试不能真的去连公网。
/// </summary>
public interface INetworkTransport
{
    Task<NetworkSearchResult> SearchAsync(NetworkConfig config, string query, CancellationToken ct);

    Task<NetworkFetchResult> FetchAsync(NetworkConfig config, string url, CancellationToken ct);
}

/// <summary>
/// 联网失败。⚠️ <b>它只由传输层抛，工具负责转成一条说人话的结果</b>
/// ——失败是内容，不是异常（同 `ToolOutcome` 的注释）。
/// </summary>
public sealed class NetworkException(string message) : Exception(message);
