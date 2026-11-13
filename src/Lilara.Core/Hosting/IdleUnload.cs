namespace Lilara.Core.Hosting;

/// <summary>
/// **空闲会话卸载的阈值从哪来**（2026-11 性能批）。
///
/// <para>⚠️ <b>它是一条纯配置解析，策略一行没有</b>：判据（五条闸）住在
/// <see cref="CommandDispatcher.SweepIdleSessions"/>，谁在什么时候调它住在控制台的心跳
/// （`src/Lilara.Cli/Program.cs`）。这个类型只回答「阈值是多久」。</para>
///
/// <para>⚠️ <b>默认 10 分钟的理由</b>（说清才好改）：一次「摘掉 → 再载入」的代价是
/// 两个 JSON 文件（`spec.json` / `state.json`）的读与解析，实测是亚毫秒级；而留着的代价是
/// 每个会话握着 `SessionState` + 一份 stepper（含适配器）+ 事件总线那 512 条补发缓冲
/// **以及它的消息块正文**。⇒ 阈值该往「短」那边偏：用户离开十分钟的会话，
/// 几乎不会在下一秒被接着用；真被用了也只是多一次载入。
/// ⚠️ 反过来，阈值太短会让「一会儿回来接着问」的会话反复载入——那也是亚毫秒级，
/// 但头那边的 `Running` 判据多了一格可能看不见的窗口（见 `SweepIdleSessions` 的残余竞态）。</para>
/// </summary>
public static class IdleUnload
{
    /// <summary>环境变量名。⚠️ 与 `LILARA_BIND` / `LILARA_AUTH` 同一族：**只走环境变量**，
    /// 核心不解析命令行参数（见 `ControlServer` 里那段说明）。</summary>
    public const string EnvVar = "LILARA_IDLE_UNLOAD_MINUTES";

    /// <summary>没设环境变量时的阈值。见类型注释里那段「为什么是 10 分钟」。</summary>
    public static readonly TimeSpan DefaultIdle = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 解析阈值。**<see cref="TimeSpan.Zero"/> = 关闭**（用户明确关掉这条维护）。
    ///
    /// <para>⚠️ <b>值不合法 ⇒ 用默认值，不是「关掉」</b>：把 `"abc"` 读成「永不卸载」
    /// 会让一条手滑的配置**静默关掉**整条维护；而读成默认值最多是「按你以为的那样跑」。
    /// 控制台会把解析结果**印在启动横幅里**，所以「我设了没生效」是看得见的。</para>
    ///
    /// <para>⚠️ 单位是**分钟**、允许小数：调试时设 `0.1`（6 秒）比等十分钟现实得多。
    /// 负数与 `NaN`/`Infinity` 一并不合法（`TimeSpan` 会被它们弄成负的或抛）。</para>
    /// </summary>
    public static TimeSpan Resolve(Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;

        var raw = getEnv(EnvVar);
        if (string.IsNullOrWhiteSpace(raw)) return DefaultIdle;

        // ⚠️ 用 InvariantCulture：`"0,5"` 在逗号小数点的地方会解析成 5 分钟，
        // 而写这句配置的人想的是半分钟。宁可判它不合法。
        if (!double.TryParse(raw.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var minutes)
            || double.IsNaN(minutes) || double.IsInfinity(minutes) || minutes < 0)
            return DefaultIdle;

        // ⚠️ `TimeSpan.FromMinutes` 对超大值会抛 `OverflowException`；把上限关在一天之内
        // （比一天更长的「空闲」与「关闭」没有区别，而一个溢出的配置不该让核心起不来）。
        if (minutes > TimeSpan.FromDays(1).TotalMinutes) return TimeSpan.FromDays(1);

        return TimeSpan.FromMinutes(minutes);
    }

    /// <summary>横幅里那一行。⚠️ 关掉时**必须说出来**——「我设了 0」与「我没设」在日志里
    /// 长得一样的话，用户就没有任何办法发现自己在跑一份没有卸载的核心。</summary>
    public static string Describe(TimeSpan idle) => idle <= TimeSpan.Zero
        ? $"已关闭（设 {EnvVar} 为正数分钟可开启）"
        : $"{idle.TotalMinutes:0.##} 分钟没被碰就摘出内存";
}
