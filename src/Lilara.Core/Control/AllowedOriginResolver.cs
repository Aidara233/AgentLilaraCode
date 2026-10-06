namespace Lilara.Core.Control;

/// <summary>allowedOrigin 的来源。调用方**必须**把它显示出来——「永不静默接管」。</summary>
public enum AllowedOriginSource
{
    /// <summary>环境变量 <c>LILARA_ALLOWED_ORIGIN</c> 显式给定（最高优先）。</summary>
    Configured,

    /// <summary>由环境变量 <c>LILARA_HEAD_PORT</c> 推导出来——跟随头自己的端口配置。</summary>
    Environment,

    /// <summary>头端口的默认值（兜底）。</summary>
    Default,
}

public sealed record ResolvedAllowedOrigin(string Origin, AllowedOriginSource Source);

/// <summary>
/// WS 的 Origin 白名单从哪来。
///
/// <para>⚠️ **这个类存在，是因为「头的端口」曾经只有头那一侧知道**：头从
/// <c>LILARA_HEAD_PORT</c> 取端口，而核心把 <c>127.0.0.1:7790</c> 硬编码放行（那个数后来因保留端口段改到了 17990）。
/// 两份副本无同步机制 ⇒ 用户改头端口后，浏览器 origin 变了，核心**静默拒绝** WS。</para>
///
/// <para>⚠️ **症状是静默的，所以修法也必须「说得出来谁赢了」**：WS 握手被拒时浏览器
/// **不把响应体交给页面 JS**，页面只走 <c>onclose</c> ⇒ 永远「已断开·重连中」，一句原因都没有。
/// ⇒ 照 <see cref="Protocol.EndpointResolver"/> 的先例：返回值带 <see cref="AllowedOriginSource"/>，
/// 调用方必须打印它（核心启动横幅）。</para>
///
/// <para>⚠️ **不默认接管**：不设环境变量的机器上，**谁赢的判据**与从前一致（默认 → 兜底），
/// 解析仍然是纯函数、仍然把 <see cref="AllowedOriginSource"/> 交出来。
/// ⚠️ **但兜底那个值变了**（2026-10-06，因保留端口段：7790 → 17990）——
/// 所以「与从前逐字节相同」这句话**不再成立**，别照抄。变的是**一个字面量**，
/// 不是这条规则；而它必须与头的默认端口同步，这就是 §1 那个类的存在理由。</para>
/// </summary>
public static class AllowedOriginResolver
{
    /// <summary>显式指定 origin（最高优先）。</summary>
    public const string OriginEnvVar = "LILARA_ALLOWED_ORIGIN";

    /// <summary>**头那一侧读的同一个变量**——这里推导出核心该放行的 origin。</summary>
    public const string HeadPortEnvVar = "LILARA_HEAD_PORT";

    /// <summary>与 <see cref="AllowedOriginSource"/> 的定义同处一地（照 <c>Axes.LabelOf</c> 的先例）。</summary>
    /// <remarks>
    /// ⚠️ **默认那一条<b>不重抄那个数</b>**（2026-10-06）：它原先硬写着「头端口 7790」，
    /// 于是改默认头端口时这里会**静默留在旧值上**——而这句话是打印给用户看的，
    /// 说错了比不说更坏（用户会照着它去设环境变量）。同一事实的第二副本，删掉。
    /// ⚠️ 也不必把它换成常量插值：**具体是哪个 origin，上一行横幅刚印过**
    /// （「WS 只接受来自 … 的页面」）⇒ 这里只该说**来源**。
    /// </remarks>
    public static string Describe(AllowedOriginSource source) => source switch
    {
        AllowedOriginSource.Configured => $"来自环境变量 {OriginEnvVar}",
        AllowedOriginSource.Environment => $"跟随环境变量 {HeadPortEnvVar}",
        _ => "默认（头端口）",
    };

    /// <summary>
    /// 解析顺序：显式 origin &gt; 由头端口推导 &gt; 默认。
    ///
    /// <para>getEnv 可注入 ⇒ 测试不必动真环境变量（照 <see cref="Protocol.EndpointResolver.Resolve"/> 的形状）。</para>
    /// </summary>
    public static ResolvedAllowedOrigin Resolve(Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;

        var explicitOrigin = getEnv(OriginEnvVar);
        if (!string.IsNullOrWhiteSpace(explicitOrigin))
            return new ResolvedAllowedOrigin(explicitOrigin, AllowedOriginSource.Configured);

        // ⚠️ **判据与头逐字相同**（`head/Program.cs` 里也是裸 `int.TryParse`、而且没有范围检查）——
        // 「两边对同一个输入给出同一个答案」正是这个类的全部目的，所以这里**刻意不加**头的判据里没有的检查。
        // ⇒ 非数字（含空串）两边一起回落到各自默认；数字（含越界）两边一起采用，由 `UseUrls` 去抱怨。
        var headPort = getEnv(HeadPortEnvVar);
        if (int.TryParse(headPort, out var port))
            return new ResolvedAllowedOrigin($"http://127.0.0.1:{port}", AllowedOriginSource.Environment);

        return new ResolvedAllowedOrigin(ControlServer.DefaultAllowedOrigin, AllowedOriginSource.Default);
    }
}
