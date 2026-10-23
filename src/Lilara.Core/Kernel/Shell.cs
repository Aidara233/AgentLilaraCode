namespace Lilara.Core.Kernel;

/// <summary>
/// **shell 能力的全局配置**（`&lt;数据根&gt;/shell.json`）。
///
/// <para>⚠️ <b>它只管「基础设施」——这台机器上跑哪个 `sh`、能跑多久；
/// 「这个会话能不能跑命令」归权限轴</b>（`01` §3.5）。两件事分开，理由与
/// <see cref="NetworkConfig"/> 那一段逐字同源：可执行路径是设备级的，
/// 而「允不允许」是会话级的。</para>
///
/// <para>⚠️ <b>`Enabled` 默认 <c>true</c>，与联网的默认关刻意相反</b>：联网那道闸后面是
/// 「把数据送出去」，而 shell 后面是**已经由权限轴把过的那道门**——它只在 `trusted`
/// 档放行（<see cref="PermissionClass.Destructive"/>）。设备级开关在这里的意思只有一句：
/// **这台机器完全不许跑命令**（服务器、共享机）。默认开，是因为把它默认关会让
/// 「用户明确拨到 trusted 之后 shell 依然不放行」变成一次看着像 bug 的静默拒绝。</para>
///
/// <para>⚠️ <b>它<b>不服</b>从轴值</b>（同 `01` §9.2 那条「改闸门零前缀代价」）：
/// 配置与轴值都不进工具声明，所以改这里、拨轴，工具表<b>逐字节不变</b>。</para>
/// </summary>
public sealed record ShellConfig
{
    public int Version { get; init; } = Json.FormatVersion;

    /// <summary>总开关。关着时 `shell` 只回一句人话，一个进程都不起。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// `sh` 的可执行路径。⚠️ <b><c>null</c> / 空串 = 自动探测</b>（PATH，然后是 Windows 上
    /// Git for Windows 的已知安装位置）。
    /// </summary>
    public string? Executable { get; init; }

    public int TimeoutSeconds { get; init; } = ShellLimits.DefaultTimeoutSeconds;
}

/// <summary>
/// shell 相关的**硬上限**。⚠️ 与 <see cref="NetworkLimits"/> 同族：默认值与合法区间住在这里
/// （配置要用），工具侧的容量别名在 `ToolLimits`——**数值只有这一份**。
/// </summary>
public static class ShellLimits
{
    /// <summary>默认超时（秒）。占位值：够一次构建或一段长循环，又不是「挂到天荒地老」。</summary>
    public const int DefaultTimeoutSeconds = 120;

    /// <summary>
    /// 超时的允许区间（秒）。
    /// ⚠️ 上限与 <see cref="NetworkLimits.MaxTimeoutSeconds"/> **刻意不同**（那边 120）：
    /// 网络请求拖过两分钟基本是端点坏了，而一条构建 / 测试命令跑十分钟是常态。
    /// </summary>
    public const int MinTimeoutSeconds = 1;
    public const int MaxTimeoutSeconds = 600;
}

/// <summary>
/// 一次工具执行能看见的 shell 能力——**配置快照**。
///
/// <para>⚠️ <b>按步冻结</b>（与 `NetworkAccess` / 权限档位同一条）：一步之内两条 `shell`
/// 调用必须按同一份配置判定，否则记录里看不出为什么两条的命运不同。</para>
/// </summary>
public sealed class ShellAccess(ShellConfig config)
{
    public ShellConfig Config { get; } = config;

    /// <summary>
    /// 这一格为什么不能跑——**一段说人话的理由**；返回 `null` 表示没有额外阻挡
    /// （权限闸是另一道，不在这里说）。
    /// </summary>
    public string? DenialFor() => Config.Enabled
        ? null
        : "shell 在这台机器上被关掉了。请在头的「调音 · shell」页开启，或运行 `shell set --enabled on`。";
}
