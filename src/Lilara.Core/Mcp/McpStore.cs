using System.Text.Json;
using Lilara.Core.Kernel;
using Lilara.Core.Storage;

namespace Lilara.Core.Mcp;

/// <summary>
/// MCP 配置的落点：`&lt;数据根&gt;/mcp.json`（与 `ShellStore` / `NetworkStore` 同形）。
///
/// <para>⚠️ <b>读不到就交回默认值</b>（`Enabled = false`、没有 server），损坏与版本过新也一样。
/// 失败方向是**更保守**的那一侧：一份读不懂的配置不该让任何子进程被拉起——
/// 这与 `NetworkStore` 的「坏文件 ⇒ 关掉」同源（那边的失败方向是向外发数据）。</para>
/// </summary>
public sealed class McpStore(string dataRoot)
{
    private readonly string _path = System.IO.Path.Combine(dataRoot, "mcp.json");

    public string Path => _path;

    public McpConfig Load()
    {
        if (!File.Exists(_path)) return new McpConfig();

        try
        {
            var cfg = JsonSerializer.Deserialize<McpConfig>(FileFormat.ReadChecked(_path), Json.Options);
            return Normalize(cfg);
        }
        catch (JsonException)
        {
            return new McpConfig();
        }
        catch (FileFormatException)
        {
            return new McpConfig();
        }
    }

    public void Save(McpConfig config) =>
        AtomicFile.Write(_path, JsonSerializer.Serialize(
            Normalize(config) with { Version = Json.FormatVersion }, Json.Options));

    /// <summary>
    /// 把字段收成可用的形状。⚠️ <b>越界不报错、只夹紧</b>：配置文件是用户可以手改的，
    /// 目标不是拒绝他，而是别让一个手滑的 0 或者 999999 把每一步弄坏
    /// （控制面那条命令反而会**拒绝**越界——那是在交互里写的，当场说清更好）。
    ///
    /// <para>⚠️ <b>这里不做「去重」</b>：同 id 的两条登记项会原样留着，由 <see cref="McpHost"/>
    /// 在装配时检出并报一条 problem。在这一层悄悄丢掉第二条，等于让用户以为它生效了。</para>
    /// </summary>
    public static McpConfig Normalize(McpConfig? config)
    {
        config ??= new McpConfig();

        var servers = (config.Servers ?? [])
            .Where(s => s is not null)
            .Select(NormalizeServer)
            .Take(McpLimits.MaxServers)
            .ToList();

        return config with { Servers = servers };
    }

    private static McpServerConfig NormalizeServer(McpServerConfig server)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in server.Environment ?? new Dictionary<string, string>())
        {
            var key = pair.Key?.Trim();
            if (string.IsNullOrEmpty(key)) continue;
            environment[key] = pair.Value ?? "";
        }

        var permission = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in server.ToolPermission ?? new Dictionary<string, string>())
        {
            var key = pair.Key?.Trim();
            if (string.IsNullOrEmpty(key)) continue;
            permission[key] = pair.Value?.Trim() ?? "";
        }

        return server with
        {
            Id = (server.Id ?? "").Trim(),
            Transport = string.IsNullOrWhiteSpace(server.Transport) ? "stdio" : server.Transport.Trim().ToLowerInvariant(),
            Command = (server.Command ?? "").Trim(),
            Arguments = (server.Arguments ?? []).Where(a => a is not null).ToList(),
            Endpoint = string.IsNullOrWhiteSpace(server.Endpoint) ? null : server.Endpoint.Trim(),
            BearerTokenEnvironmentVariable = string.IsNullOrWhiteSpace(server.BearerTokenEnvironmentVariable)
                ? null : server.BearerTokenEnvironmentVariable.Trim(),
            WorkingDirectory = string.IsNullOrWhiteSpace(server.WorkingDirectory) ? null : server.WorkingDirectory.Trim(),
            Environment = environment,
            ToolPermission = permission,
            TimeoutSeconds = Math.Clamp(server.TimeoutSeconds,
                McpLimits.MinTimeoutSeconds, McpLimits.MaxTimeoutSeconds),
        };
    }
}
