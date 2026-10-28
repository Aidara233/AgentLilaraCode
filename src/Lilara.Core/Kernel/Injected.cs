namespace Lilara.Core.Kernel;

/// <summary>
/// **核心替模型注入的那些记录**——统一登记它们的 id 前缀（2026-10-27）。
///
/// <para>⚠️ <b>为什么必须有这一处</b>：这些记录在时间线上**长得完全像一次真实的工具调用**
/// （那正是我们要的——三个适配器一个字都不用改）。但有一处判据必须把它们与真实调用分开：
/// <see cref="Interruption.WasInterrupted"/> 的配对扫描。没有前缀，
/// 一个会话正挂着询问时收到一条注入，那条挂着的调用会被判成**断裂**，
/// 于是核心补一条「上一次这一步被打断了」——而用户只是在想怎么回答。</para>
///
/// <para>⚠️ <b>2026-10-27 起它是<b>一张表</b>，不是一个常量</b>：注入有三种来源
/// （子会话回报 / 子会话提问 / 后台任务结束），而它们**必须被同一条判据一起忽略**。
/// 逐个 `<c>StartsWith("subagent-") || StartsWith("job-")</c>` 地写，加第四种时
/// 一定会有人只改一处——而那处漏掉的表现是「某个注入把挂着的调用判成了断裂」，
/// 只在特定时序下出现。</para>
///
/// <para>⚠️ <b>前缀写在 id 上，不写在内容里</b>：内容会进模型上下文（每步全价付），
/// 而 id 只在判定那一侧被读一眼（`00` §7 判据 5：能进机械层的，绝不靠嘴说）。</para>
/// </summary>
public static class Injected
{
    /// <summary>子会话 → 父会话的回报 / 提问（`subagent_report` 的合成对）。</summary>
    public const string SubagentPrefix = "subagent-";

    /// <summary>后台任务结束时的那一条（`jobs` 的合成对，2026-10-27）。</summary>
    public const string JobPrefix = "job-";

    /// <summary>全部前缀。<see cref="IsInjected"/> 只认这张表。</summary>
    public static readonly IReadOnlyList<string> Prefixes = [SubagentPrefix, JobPrefix];

    /// <summary>这条记录是不是核心替模型注入的。</summary>
    public static bool IsInjected(TimelineEvent evt) =>
        Prefixes.Any(prefix => evt.Id.StartsWith(prefix, StringComparison.Ordinal));
}
