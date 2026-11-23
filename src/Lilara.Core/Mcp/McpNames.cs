using System.Security.Cryptography;
using System.Text;
using Lilara.Core.Plugins;

namespace Lilara.Core.Mcp;

/// <summary>
/// **MCP 工具名 → 我们的工具名**。
///
/// <para>⚠️ <b>为什么需要一层改名，而不是直接用远端的名字</b>：MCP 的工具名规范是
/// 「1–128 字符，建议 `[A-Za-z0-9_.-]`」，而 provider 那边（Anthropic / OpenAI）
/// 的硬约束是 `^[a-zA-Z0-9_-]{1,64}$`——**不允许点、且只有 64 个字符**。
/// 直接把远端名字发出去，会在上游变成一个 400，而那个 400 看起来与 MCP 毫无关系。</para>
///
/// <para>⚠️ <b>名字一旦发给模型就不能再变</b>：历史里的调用按名字查表
/// （`ITool.Name` 的注释：改了等于让历史里已有的调用变成死链）。⇒ 截断必须**确定**：
/// 同一对 `(serverId, toolName)` 永远得到同一个名字，跨进程、跨重启、跨机器都一样。</para>
///
/// <para>⚠️ <b>排序键也是这个名字</b>（`04` §9.1：工具表按名字 ordinal 升序）。
/// ⇒ 名字里带 server 前缀不只是防撞，它让「同一台 server 的工具排在一起」这件事
/// 由排序规则免费给出，而不是靠另一条规则维持。</para>
/// </summary>
public static class McpNames
{
    /// <summary>provider 侧的工具名上限。⚠️ 不是「我们的」上限——我们的上限是它。</summary>
    public const int MaxToolNameLength = 64;

    /// <summary>serverId 与远端工具名之间的分隔符。</summary>
    public const string Separator = "__";

    /// <summary>截断时附在末尾的稳定哈希长度（小写 hex）。</summary>
    private const int HashLength = 8;

    /// <summary>serverId 的规则与插件 id 相同——**共用同一个谓词**，不各写一份。</summary>
    public static bool IsValidServerId(string? value) => PluginHost.IsValidPluginId(value);

    /// <summary>
    /// 组合出一个发得出去的名字。
    ///
    /// <para>远端名里的非法字符（主要是 `.`）换成 `_`；超长时保留前缀、追加
    /// <c>_</c> + 8 位稳定哈希。⚠️ 哈希算的是**完整原名**，不是截断后的那一段
    /// ——否则两个只在尾部不同的长名字会撞在一起。</para>
    /// </summary>
    public static string Compose(string serverId, string remoteName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        ArgumentNullException.ThrowIfNull(remoteName);

        var raw = serverId + Separator + Sanitize(remoteName);
        if (raw.Length <= MaxToolNameLength) return raw;

        var hash = ShortHash(raw);
        var keep = MaxToolNameLength - HashLength - 1;
        return raw[..keep] + "_" + hash;
    }

    /// <summary>
    /// 把一个远端名字收成 `[A-Za-z0-9_-]`。
    /// ⚠️ 空名字（全是被替换掉的字符）会变成一个 `_`——**不能产出空串**：
    /// 空工具名在 provider 那边同样是 400。
    /// </summary>
    public static string Sanitize(string remoteName)
    {
        var builder = new StringBuilder(remoteName.Length);
        foreach (var c in remoteName)
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_');

        return builder.Length == 0 ? "_" : builder.ToString();
    }

    private static string ShortHash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..HashLength];
}
