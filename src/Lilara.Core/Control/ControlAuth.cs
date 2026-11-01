using System.Security.Cryptography;
using System.Text;

namespace Lilara.Core.Control;

/// <summary>
/// 控制面的访问 token：**总是生成、总是打印，只有显式开启时才真的校验**。
///
/// <para>⚠️ 为什么不是「默认强制」：<c>05</c> / <c>09</c> 把本机进程当可信，
/// 页面与脚本（curl、测试、将来的 CLI 头）都直连控制面。默认强制会把所有这些入口
/// 一起挡在门外——那不是安全，那是不可用。所以默认是「不校验、但 token 已经在屏幕上」，
/// 用户决定要不要开：<c>LILARA_AUTH=1</c>。</para>
///
/// <para>⚠️ <b>为什么 token 总是打印</b>：一个「要求填 token」而**没人知道 token 是什么**的
/// 鉴权等于把自己锁在门外。打印它没有额外代价（能看到控制台的人本来就能驱动核心）。</para>
///
/// <para>⚠️ <b>它只是「拦得住随手连上来的人」，不是加密</b>：控制面是明文 HTTP。
/// 同一局域网里的嗅探者能看到 token 本身——要真的防这个，得上 TLS 或隧道，
/// 那不在本类的范围里。</para>
/// </summary>
public static class ControlAuth
{
    /// <summary>设成真值（<c>1</c> / <c>true</c> / <c>on</c> / <c>yes</c>）才**真的**校验。</summary>
    public const string EnforceEnvVar = "LILARA_AUTH";

    /// <summary>WS 用查询参数交 token 的名字（浏览器 `WebSocket` 不能自定义头）。</summary>
    public const string QueryParam = "token";

    /// <summary>HTTP 用 <c>Authorization: Bearer &lt;token&gt;</c>。</summary>
    public const string BearerPrefix = "Bearer ";

    /// <summary>
    /// 是否强制校验。
    ///
    /// <para>⚠️ 只认**明确的真值**：<c>LILARA_AUTH=0</c>、<c>off</c>、空串都是「不校验」。
    /// 反过来（把任何非空值当真）会让一个拼错的值悄悄把用户的脚本全挡掉，
    /// 而那种失败在页面上长得像「核心没起来」。</para>
    /// </summary>
    public static bool IsEnforced(Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        var raw = getEnv(EnforceEnvVar);
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var value = raw.Trim();
        return value.Equals("1", StringComparison.OrdinalIgnoreCase)
               || value.Equals("true", StringComparison.OrdinalIgnoreCase)
               || value.Equals("on", StringComparison.OrdinalIgnoreCase)
               || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 生成一个 token：32 字节随机数的 base64url（无填充）。
    ///
    /// <para>⚠️ 用 <see cref="RandomNumberGenerator"/>、不用 <c>Guid.NewGuid()</c>：
    /// 后者是 v4 UUID，122 位熵且**不保证**不可预测（部分实现来自可观测的状态）。
    /// 这个值是**唯一**的凭据，取熵要一次给够。</para>
    /// </summary>
    public static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>
    /// 比对 token。**定长比较**（<see cref="CryptographicOperations.FixedTimeEquals"/>）：
    /// 逐字节短路比较会把「前几位对了」这件事泄漏成时间差。
    ///
    /// <para>⚠️ 空/缺省一律 <c>false</c>：别让「没带 token」与「带了空串」变成放行的那一格。</para>
    /// </summary>
    public static bool Matches(string? presented, string? expected)
    {
        if (string.IsNullOrEmpty(presented) || string.IsNullOrEmpty(expected)) return false;

        var a = Encoding.UTF8.GetBytes(presented);
        var b = Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
