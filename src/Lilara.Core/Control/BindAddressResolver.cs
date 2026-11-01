using System.Net;

namespace Lilara.Core.Control;

/// <summary>监听地址的来源。调用方**必须**把它显示出来——与 <see cref="AllowedOriginResolver"/> 同一条规矩（永不静默接管）。</summary>
public enum BindAddressSource
{
    /// <summary>环境变量 <c>LILARA_BIND</c> 显式给定。</summary>
    Configured,

    /// <summary>默认值：只监听本机。</summary>
    Default,
}

public sealed record ResolvedBindAddress(string Address, BindAddressSource Source);

/// <summary>
/// 控制面的监听地址从哪来。
///
/// <para>⚠️ <b>默认已改成对局域网开放（<c>0.0.0.0</c>，2026-11-05 用户裁定）</b>：
/// 原先是 <c>127.0.0.1</c>，理由是「核心没有鉴权，绑到对外地址等于把模型、工具与可读文件一起交出去」。
/// 现在的取舍是：<b>默认能用（同一局域网的手机/平板直接开页面）</b>，把代价写成两句人话
/// （启动横幅 + <c>LILARA_AUTH</c> 的开关），而不是默认不可用。
/// ⇒ <b>这个默认值不再提供任何保护</b>：要收回本机就显式设 <c>LILARA_BIND=127.0.0.1</c>。</para>
///
/// <para>⚠️ <b>真正拦得住人的是 token（<see cref="ControlAuth"/>），不是监听地址</b>：
/// 默认不校验 token ⇒ 默认状态下，任何能路由到这个端口的人/程序都能驱动本机的模型与工具、
/// 读本机能读到的文件。启动横幅必须把这句话说出来，不许只印「已监听 0.0.0.0」。</para>
///
/// <para>⚠️ <b>主机名 / 通配符判不出「是不是只在本机」</b>：<see cref="IsLoopbackOnly"/> 那时按
/// 「对外」回答——警告宁可多响一次，也不要漏报一次静默暴露。</para>
/// </summary>
public static class BindAddressResolver
{
    /// <summary>显式指定监听地址（IP 字面量、主机名，或 <c>0.0.0.0</c> / <c>*</c> 这类通配符）。</summary>
    public const string EnvVar = "LILARA_BIND";

    /// <summary>横幅要印的来源——与 <see cref="BindAddressSource"/> 的定义同处一地（照 `Axes.LabelOf` 的先例）。</summary>
    public static string Describe(BindAddressSource source) => source switch
    {
        BindAddressSource.Configured => $"来自环境变量 {EnvVar}",
        // ⚠️ 这句话**不许**说成「仅本机」：默认值已经是 0.0.0.0，说错就是给用户一个假的安全感。
        _ => "默认（对局域网开放）",
    };

    /// <summary>
    /// 解析顺序：显式环境变量 &gt; 默认。
    ///
    /// <para>getEnv 可注入 ⇒ 测试不必动真环境变量（照 <see cref="AllowedOriginResolver.Resolve"/> 的形状）。</para>
    /// </summary>
    public static ResolvedBindAddress Resolve(Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;

        var configured = getEnv(EnvVar);
        if (!string.IsNullOrWhiteSpace(configured))
            return new ResolvedBindAddress(configured.Trim(), BindAddressSource.Configured);

        return new ResolvedBindAddress(ControlServer.DefaultBindAddress, BindAddressSource.Default);
    }

    /// <summary>
    /// 该地址是否只在本机可达。
    ///
    /// <para>⚠️ 判定不了的一律回答 <c>false</c>（= 对外）：通配符、主机名都落到这里，
    /// 而漏判的代价是「横幅不响、用户以为还锁着本机」。</para>
    /// </summary>
    public static bool IsLoopbackOnly(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return true;

        var a = address.Trim();
        if (a.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (a.Length > 2 && a[0] == '[' && a[^1] == ']') a = a[1..^1];

        return IPAddress.TryParse(a, out var ip) && IPAddress.IsLoopback(ip);
    }

    /// <summary>
    /// URL 里的 host 写法：IPv6 字面量**必须**带方括号，否则 <c>http://::1:17988</c> 解析不出来
    /// ⇒ 横幅和 `/info` 交出去的地址会是坏的。
    /// </summary>
    public static string HostForUrl(string address)
    {
        var a = address.Trim();
        if (a.Length > 2 && a[0] == '[' && a[^1] == ']') return a;
        return a.Contains(':') ? $"[{a}]" : a;
    }
}
