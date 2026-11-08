namespace Lilara.Core.Kernel;

/// <summary>
/// **一次工具调用的通用上限**的全局配置（`&lt;数据根&gt;/tools.json`，2026-11-07）。
///
/// <para>⚠️ <b>它管的是「工具自己忘了管时间」这件事</b>，不是「这个工具有多慢」。
/// 每个工具可以有自己的时限（`shell` 120 秒、`dotnet_build` 180 秒、`git` 8/30 秒、
/// 联网 20 秒…），而那些**都更短**，所以它们先到点。这一格是**兜底**：
/// 一个没有时限、又不响应取消的工具（或者将来新加的工具忘了写），
/// 从前会把整步挂到天荒地老——而唯一能救的是用户手动 `interrupt`。</para>
///
/// <para>⚠️ <b>到点之后有两种结局，判据是「这个工具有没有异步模式」</b>：
/// 能转后台的（`shell` / `dotnet_build` / `dotnet_test`，它们本来就经
/// <see cref="IBackgroundJobs"/> 起进程）→ **把活着的进程交给后台宿主继续**
/// （`IBackgroundJobs.Adopt`）；其余的 → **终止**，结果里写明超时。
/// ⚠️ 刻意**不是**「重跑成后台任务」：同一条命令跑两遍会写两次盘、改两次仓库，
/// 而那个症状看起来完全正常。</para>
///
/// <para>⚠️ <b>它不服</b>从轴值</b>：与 <see cref="ShellConfig"/> 同一条——配置不进工具声明，
/// 改这里工具表逐字节不变。</para>
/// </summary>
public sealed record ToolsConfig
{
    /// <summary>格式版本。⚠️ 语义同 <see cref="SessionSpec.Version"/>：只增不改。</summary>
    public int Version { get; init; } = Json.FormatVersion;

    /// <summary>
    /// 一次工具调用**最长**等多久（秒）。⚠️ 默认 <see cref="ToolCallLimits.DefaultTimeoutSeconds"/>。
    ///
    /// <para>⚠️ <b>它是「上限的上限」，不是「每个工具都等这么久」</b>：
    /// 工具自己的时限更短时，先到点的那个算数。</para>
    /// </summary>
    public int CallTimeoutSeconds { get; init; } = ToolCallLimits.DefaultTimeoutSeconds;
}

/// <summary>
/// 「一次工具调用能跑多久」的**默认值与合法区间**（2026-11-07）。
///
/// <para>⚠️ <b>为什么这些数住在这里、不住在 <c>ToolLimits</c></b>：那个类装的是**容量**
/// （结果多少字节、列多少条），而这里是**时间**；更要紧的是它住在 `Session/Tools` 且是
/// `internal`，而配置（<see cref="ToolsConfig"/>）在 Kernel ⇒ 数只能住这一侧。
/// ⚠️ 与 <see cref="ShellLimits"/> / <see cref="NetworkLimits"/> 同一个形状：
/// **默认值与区间只有这一份**，工具侧不许再抄一个。</para>
///
/// <para>⚠️ <b>600 秒是占位值</b>，与 <c>ToolLimits.MaxResultBytes</c> 同一条纪律：
/// 只裁定「必须有兜底」，没有说多久。取 600 的理由：它**宽于**所有工具自己的时限
/// （最长的是 `shell` 的 600 / 后台的 3600），所以正常调用永远碰不到它。</para>
/// </summary>
public static class ToolCallLimits
{
    /// <summary>默认：一次工具调用最多 600 秒。⚠️ 占位值。</summary>
    public const int DefaultTimeoutSeconds = 600;

    /// <summary>
    /// 下限 10 秒。⚠️ 它挡的是「配成 1 秒把一切工具都打死」——
    /// 那不是调优，那是让核心看起来完全坏掉。
    /// </summary>
    public const int MinTimeoutSeconds = 10;

    /// <summary>
    /// 上限 3600 秒。⚠️ 与 <see cref="BackgroundJobLimits.MaxTimeoutSeconds"/> 同一个数：
    /// 后台任务能跑一小时，那么「前台等你」也不该比它更宽。
    /// </summary>
    public const int MaxTimeoutSeconds = 3600;

    /// <summary>
    /// 到点之后**给工具的收尾宽限**（秒）。
    ///
    /// <para>⚠️ <b>它必须存在</b>：到点先发取消令牌，而工具收拾现场需要时间
    /// （杀进程树、把已读到的输出排空、写一条像样的结果）。⚠️ 但它也不能长：
    /// 一个**不听取消**的工具会让这一步多挂这么久，而那时结果是「超时」——
    /// 宽限比超时本身还长就荒唐了。</para>
    /// </summary>
    public const int GraceSeconds = 5;
}
