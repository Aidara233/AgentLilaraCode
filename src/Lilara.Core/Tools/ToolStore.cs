using System.Text.Json;
using Lilara.Core.Kernel;
using Lilara.Core.Storage;

namespace Lilara.Core.Tools;

/// <summary>
/// 工具调用的全局配置落点：`&lt;数据根&gt;/tools.json`（与 `ShellStore` / `NetworkStore` 同形，2026-11-07）。
///
/// <para>⚠️ <b>读不到就交回默认值</b>（600 秒），损坏与版本过新也一样——失败方向同
/// <see cref="Shell.ShellStore"/>：配置文件的读失败不该让**所有**工具突然不能用。
/// ⚠️ 而失败方向若是「0 秒」就正好相反了：那会把每一次工具调用都判成超时。
/// ⇒ <b>兜底值必须是宽的那个</b>，这不是随手选的。</para>
/// </summary>
public sealed class ToolStore(string dataRoot)
{
    private readonly string _path = System.IO.Path.Combine(dataRoot, "tools.json");

    public ToolsConfig Load()
    {
        if (!File.Exists(_path)) return new ToolsConfig();

        try
        {
            var cfg = JsonSerializer.Deserialize<ToolsConfig>(FileFormat.ReadChecked(_path), Json.Options);
            return Normalize(cfg);
        }
        catch (JsonException)
        {
            return new ToolsConfig();
        }
        catch (FileFormatException)
        {
            return new ToolsConfig();
        }
    }

    public void Save(ToolsConfig config) =>
        AtomicFile.Write(_path, JsonSerializer.Serialize(
            Normalize(config) with { Version = Json.FormatVersion }, Json.Options));

    /// <summary>
    /// 把时限收进合法区间。⚠️ 与 <see cref="Shell.ShellStore.Normalize"/> 同一条纪律：
    /// **越界不报错、只夹紧**——配置文件是用户可以手改的，目标不是拒绝他，
    /// 而是别让一个手滑的 0 或 999999999 把每一步都弄坏。控制面那条命令会**拒绝**越界。
    /// </summary>
    public static ToolsConfig Normalize(ToolsConfig? config)
    {
        config ??= new ToolsConfig();

        return config with
        {
            CallTimeoutSeconds = Math.Clamp(config.CallTimeoutSeconds,
                ToolCallLimits.MinTimeoutSeconds, ToolCallLimits.MaxTimeoutSeconds),
        };
    }
}
