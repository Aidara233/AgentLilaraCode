namespace Lilara.Core.Control;

/// <summary>
/// WS 握手的 Origin 判据：<b>模式</b>对<b>具体 origin</b> 匹配。
///
/// <para>⚠️ 这个类存在，是因为「默认只绑本机 + 只放行头自己的那个 origin」是一个**成对**的设定：
/// 一旦监听地址开到局域网（<see cref="BindAddressResolver"/> 默认 <c>0.0.0.0</c>），
/// 页面就可能从 <c>http://192.168.1.5:17990</c>、<c>http://10.0.0.7:17990</c>、
/// <c>http://localhost:17990</c> 打开，而**逐字相等**的判据会把这些**全部静默拒绝**
/// （握手 403 时浏览器不把响应体交给页面 JS，页面只会「已断开·重连中」）。</para>
///
/// <para>⚠️ <b>默认放开的只是「主机」这一维，端口仍然钉死</b>：模式 <c>http://*:17990</c>
/// 表示「任何主机名/地址，只要端口是 17990」。这条边界的意义是——一个随便的网页
/// （<c>https://evil.example</c>）端口对不上，因此**照样进不来**；而换一台设备用局域网 IP
/// 打开头，origin 的端口仍是头端口，于是进得来。</para>
///
/// <para>⚠️ <b>要收回成「只认一个 origin」</b>：设 <c>LILARA_ALLOWED_ORIGIN=http://127.0.0.1:17990</c>
/// （不含星号的字面量走逐字相等）。<c>*</c> 单独一个字符表示放行任意 origin——它只该在调试时用。</para>
/// </summary>
public static class OriginMatcher
{
    /// <summary>整个模式就是它 ⇒ 放行任意 origin。</summary>
    public const string AnyOrigin = "*";

    /// <summary>模式里的通配主机段。</summary>
    public const string AnyHost = "*";

    /// <summary>
    /// 该 origin 是否被这个模式接受。
    ///
    /// <para>模式或 origin 解析不出来时**回落到逐字相等**（大小写不敏感）——
    /// 解析失败不许变成「放行」：那是把一条守卫变成一句空话的最短路径。</para>
    /// </summary>
    public static bool Matches(string? pattern, string origin)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;

        var p = pattern.Trim();
        if (p == AnyOrigin) return true;
        if (string.IsNullOrEmpty(origin)) return false;

        if (!TrySplit(p, out var pScheme, out var pHost, out var pPort)
            || !TrySplit(origin.Trim(), out var oScheme, out var oHost, out var oPort))
            return string.Equals(p, origin.Trim(), StringComparison.OrdinalIgnoreCase);

        if (!string.Equals(pScheme, oScheme, StringComparison.OrdinalIgnoreCase)) return false;
        if (pPort != oPort) return false;
        return pHost == AnyHost || string.Equals(pHost, oHost, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 把 <c>scheme://host[:port]</c> 拆开。**没有端口的写法按 scheme 的默认端口算**
    /// （http 80 / https 443）——否则 <c>https://example.com</c> 与
    /// <c>https://example.com:443</c> 会被判成两个 origin，而浏览器认为它们是同一个。
    /// </summary>
    /// <remarks>
    /// ⚠️ 不走 <see cref="Uri"/>：<c>new Uri("http://*:17990")</c> 对通配主机不可靠，
    /// 而这里的输入是**模式**（可能带星号），不是真实地址。IPv6 只认带方括号的写法
    /// （<c>http://[::1]:17990</c>）——浏览器交出来的 origin 就是这个形状。
    /// </remarks>
    public static bool TrySplit(string value, out string scheme, out string host, out int port)
    {
        scheme = "";
        host = "";
        port = -1;

        var sep = value.IndexOf("://", StringComparison.Ordinal);
        if (sep <= 0) return false;

        scheme = value[..sep].ToLowerInvariant();
        var rest = value[(sep + 3)..];

        // origin 不带路径，但宽容一点：遇到 '/' 就截断（多接的那段不参与判据）。
        var slash = rest.IndexOf('/');
        if (slash >= 0) rest = rest[..slash];
        if (rest.Length == 0) return false;

        string portText;
        if (rest[0] == '[')
        {
            var close = rest.IndexOf(']');
            if (close < 0) return false;
            host = rest[1..close];
            var tail = rest[(close + 1)..];
            if (tail.Length == 0) portText = "";
            else if (tail[0] == ':') portText = tail[1..];
            else return false;
        }
        else
        {
            var colon = rest.LastIndexOf(':');
            // 不带方括号又有多于一个 ':' ⇒ 是 IPv6 字面量，不猜。
            if (colon >= 0 && rest.IndexOf(':') != colon) return false;
            host = colon < 0 ? rest : rest[..colon];
            portText = colon < 0 ? "" : rest[(colon + 1)..];
        }

        if (host.Length == 0) return false;

        if (portText.Length == 0)
        {
            port = scheme switch { "http" => 80, "https" => 443, _ => -1 };
            return port > 0;
        }

        return int.TryParse(portText, out port) && port is > 0 and <= 65535;
    }

    /// <summary>把模式说成人话（横幅要印它）——星号不解释就等于没印。</summary>
    public static string Describe(string pattern)
    {
        var p = (pattern ?? "").Trim();
        if (p == AnyOrigin) return "任意 origin（调试用）";

        if (TrySplit(p, out var scheme, out var host, out var port) && host == AnyHost)
            return $"任意主机，端口 {port}（{scheme}）";

        return $"只认 {p}";
    }
}
