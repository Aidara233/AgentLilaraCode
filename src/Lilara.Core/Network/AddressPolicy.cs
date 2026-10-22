using System.Net;
using System.Net.Sockets;

namespace Lilara.Core.Network;

/// <summary>
/// 公网可达性判定——**SSRF 闸**。
///
/// <para>⚠️ <b>它判的是「解析出来的地址」，不是「域名看起来像不像内网」</b>
/// ——与 `SessionToolContext.ResolvePath` 那条「形状校验」同一条纪律：
/// 域名是**不可信输入**，而 <c>127.0.0.1</c> / <c>localhost</c> / <c>10.x</c> /
/// <c>metadata.google.internal</c> 都只是它的一种写法。</para>
///
/// <para>⚠️ <b>显式白名单可以覆盖私网封锁</b>：用户把 <c>127.0.0.1</c> 写进
/// `allowDomains` 是一个明确的选择（本机服务、内网 wiki、e2e 的 mock）。
/// 那条判据在调用方（<see cref="HttpNetworkTransport"/>），不在这里。</para>
/// </summary>
internal static class AddressPolicy
{
    /// <summary>这个地址是不是「公网可访问」的那一类。</summary>
    public static bool IsPublic(IPAddress address)
    {
        // ⚠️ 先映射回 IPv4：`::ffff:127.0.0.1` 必须与 `127.0.0.1` 得到同一个答案，
        // 否则「换一种写法」就绕过去了。
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();

            return b[0] switch
            {
                0 => false,                                   // 0.0.0.0/8
                10 => false,                                  // 私网
                100 when b[1] >= 64 && b[1] <= 127 => false,  // CGNAT 100.64/10
                127 => false,                                 // loopback
                169 when b[1] == 254 => false,                // link-local（含云元数据）
                172 when b[1] >= 16 && b[1] <= 31 => false,   // 私网
                192 when b[1] == 0 && b[2] == 0 => false,     // IETF 保留
                192 when b[1] == 0 && b[2] == 2 => false,     // TEST-NET-1
                192 when b[1] == 168 => false,                // 私网
                198 when b[1] is 18 or 19 => false,           // 基准测试
                198 when b[1] == 51 && b[2] == 100 => false,  // TEST-NET-2
                203 when b[1] == 0 && b[2] == 113 => false,   // TEST-NET-3
                >= 224 => false,                              // 组播 + 保留 + 广播
                _ => true,
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None)
                || address.Equals(IPAddress.IPv6Loopback))
                return false;

            var b = address.GetAddressBytes();

            if ((b[0] & 0xFE) == 0xFC) return false;   // fc00::/7 唯一本地
            if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) return false; // fe80::/10 link-local
            if (b[0] == 0xFF) return false;            // 组播
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) return false; // 文档用

            return true;
        }

        return false;
    }

    /// <summary>按「等于或子域」匹配白名单。名单为空什么都不匹配。</summary>
    public static bool IsAllowlisted(string host, IReadOnlyList<string> allowDomains)
    {
        if (allowDomains.Count == 0) return false;

        host = host.TrimEnd('.').ToLowerInvariant();
        foreach (var raw in allowDomains)
        {
            var domain = raw.Trim().Trim('.');
            if (domain.Length == 0) continue;

            if (string.Equals(host, domain, StringComparison.Ordinal)) return true;
            if (host.EndsWith("." + domain, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>地址能不能连——白名单命中时只要求「解析得出地址」，否则每个地址都必须是公网。</summary>
    public static bool Allows(IPAddress address, bool allowlisted) =>
        allowlisted || IsPublic(address);
}
