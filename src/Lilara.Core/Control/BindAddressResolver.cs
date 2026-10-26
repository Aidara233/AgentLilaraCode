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
/// <para>⚠️ <b>默认仍然是 <c>127.0.0.1</c>，而且必须保持是它</b>：核心**没有鉴权**（`03` §10），
/// 绑到对外地址等于「任何能路由到这个端口的人/程序都能驱动本机的模型与工具、读本机能读到的文件」。
/// ⇒ 对外是一条<b>显式</b>的决定（设 <c>LILARA_BIND</c>），不是默认行为；启动横幅会把代价说出来。</para>
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
        _ => "默认（仅本机）",
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
