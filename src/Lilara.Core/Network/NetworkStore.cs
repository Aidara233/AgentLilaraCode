using System.Text.Json;
using Lilara.Core.Kernel;
using Lilara.Core.Storage;

namespace Lilara.Core.Network;

/// <summary>
/// 联网配置的落点：`&lt;数据根&gt;/network.json`（与 `ProviderStore` 同形）。
///
/// <para>⚠️ <b>读不到就交回默认值</b>（默认 = 关闭），与 `ProviderStore.Load` 的
/// 「读不到 ⇒ null」不同：那边 null 的意思是「这个提供商不存在」，而配置只有一份，
/// 「不存在」与「还没配过」是同一件事。损坏与版本过新也回默认值——失败方向是
/// **更保守**（关着），不是「用一份猜的配置联网」。</para>
/// </summary>
public sealed class NetworkStore(string dataRoot)
{
    private readonly string _path = System.IO.Path.Combine(dataRoot, "network.json");

    public NetworkConfig Load()
    {
        if (!File.Exists(_path)) return new NetworkConfig();

        try
        {
            var cfg = JsonSerializer.Deserialize<NetworkConfig>(
                FileFormat.ReadChecked(_path), Json.Options);
            return Normalize(cfg);
        }
        catch (JsonException)
        {
            return new NetworkConfig();
        }
        catch (FileFormatException)
        {
            return new NetworkConfig();
        }
    }

    public void Save(NetworkConfig config) =>
        AtomicFile.Write(_path, JsonSerializer.Serialize(
            Normalize(config) with { Version = Json.FormatVersion }, Json.Options));

    /// <summary>
    /// 把边界收进合法区间，把显式 `null` 的子对象补回来，并清掉空白的域名。
    ///
    /// <para>⚠️ <b>越界的数值不报错，只夹紧</b>：配置文件是用户可以手改的，
    /// 而「maxBytes = 1 GB」这种值的目标不是被拒绝、是**别把内存吃光**。
    /// 控制面那条命令反而会**拒绝**越界（它是在交互里写的，当场说清更好）。</para>
    /// </summary>
    public static NetworkConfig Normalize(NetworkConfig? config)
    {
        config ??= new NetworkConfig();
        var search = config.Search ?? new NetworkSearchConfig();
        var fetch = config.Fetch ?? new NetworkFetchConfig();

        return config with
        {
            Search = search with
            {
                MaxResults = Math.Clamp(search.MaxResults, 1, NetworkLimits.MaxSearchResults),
                TimeoutSeconds = Math.Clamp(search.TimeoutSeconds,
                    NetworkLimits.MinTimeoutSeconds, NetworkLimits.MaxTimeoutSeconds),
            },
            Fetch = fetch with
            {
                TimeoutSeconds = Math.Clamp(fetch.TimeoutSeconds,
                    NetworkLimits.MinTimeoutSeconds, NetworkLimits.MaxTimeoutSeconds),
                MaxBytes = Math.Clamp(fetch.MaxBytes,
                    NetworkLimits.MinFetchBytes, NetworkLimits.MaxFetchBytes),
                AllowDomains = (fetch.AllowDomains ?? [])
                    .Select(d => d.Trim().TrimStart('.').ToLowerInvariant())
                    .Where(d => d.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .ToList(),
            },
        };
    }
}
