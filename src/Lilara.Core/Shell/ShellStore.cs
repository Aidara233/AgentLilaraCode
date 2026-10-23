using System.Text.Json;
using Lilara.Core.Kernel;
using Lilara.Core.Storage;

namespace Lilara.Core.Shell;

/// <summary>
/// shell 配置的落点：`&lt;数据根&gt;/shell.json`（与 `NetworkStore` 同形）。
///
/// <para>⚠️ <b>读不到就交回默认值</b>，损坏与版本过新也一样——失败方向是
/// **默认那份**（启用、自动探测、120 秒），而不是「拒绝起任何进程」：
/// 配置文件的读失败不该让一个 `trusted` 档的会话突然做不了事，
/// 而真正的安全边界在权限轴，不在这份配置的解析结果上。
/// （这与 `NetworkStore` 的「坏文件 ⇒ 关掉」不同，因为那边的失败方向是向外发数据。）</para>
/// </summary>
public sealed class ShellStore(string dataRoot)
{
    private readonly string _path = System.IO.Path.Combine(dataRoot, "shell.json");

    public ShellConfig Load()
    {
        if (!File.Exists(_path)) return new ShellConfig();

        try
        {
            var cfg = JsonSerializer.Deserialize<ShellConfig>(FileFormat.ReadChecked(_path), Json.Options);
            return Normalize(cfg);
        }
        catch (JsonException)
        {
            return new ShellConfig();
        }
        catch (FileFormatException)
        {
            return new ShellConfig();
        }
    }

    public void Save(ShellConfig config) =>
        AtomicFile.Write(_path, JsonSerializer.Serialize(
            Normalize(config) with { Version = Json.FormatVersion }, Json.Options));

    /// <summary>
    /// 把超时收进合法区间，把空白的可执行路径折成 `null`（= 自动探测）。
    /// ⚠️ 与 `NetworkStore.Normalize` 同一条纪律：**越界不报错、只夹紧**——
    /// 配置文件是用户可以手改的，目标不是拒绝他，是别让 `timeoutSeconds = 999999` 挂死一步。
    /// 控制面那条命令会**拒绝**越界（那是在交互里写的，当场说清更好）。
    /// </summary>
    public static ShellConfig Normalize(ShellConfig? config)
    {
        config ??= new ShellConfig();

        var executable = string.IsNullOrWhiteSpace(config.Executable) ? null : config.Executable.Trim();

        return config with
        {
            Executable = executable,
            TimeoutSeconds = Math.Clamp(config.TimeoutSeconds,
                ShellLimits.MinTimeoutSeconds, ShellLimits.MaxTimeoutSeconds),
        };
    }
}
