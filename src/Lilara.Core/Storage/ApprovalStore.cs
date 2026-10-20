using Lilara.Core.Kernel;

namespace Lilara.Core.Storage;

/// <summary>
/// **一次批准**（`01` §3.8）。⚠️ 它记的是「这一次调用」，不是「这个档位」——
/// 那是这套东西存在的全部理由：**档位说不出「批这次调用」的粒度**。
/// </summary>
/// <param name="ToolName">工具名。</param>
/// <param name="ArgsHash">
/// 参数原文（`ToolCall.ArgumentsJson`）的 SHA-256 十六进制小写。
/// ⚠️ <b>哈希的是<b>原文</b></b>——逐字保真是铁律（`01` §9），所以这个哈希是稳定的；
/// 重新序列化一遍再哈希会随 `JsonSerializer` 的选项漂。
/// </param>
/// <param name="ToolWide">
/// ⚠️ <b>粗的那一档</b>：<c>true</c> ⇒ 这个工具在本会话里的**任何**参数都放行
/// （<see cref="AskMemory.Tool"/>）。<c>false</c> ⇒ 只有 <paramref name="ArgsHash"/>
/// 一模一样的那些放行（<see cref="AskMemory.Args"/>）。
/// </param>
public sealed record Approval(string ToolName, string ArgsHash, bool ToolWide, DateTimeOffset At);

/// <summary>
/// 批准的落盘：`&lt;数据根&gt;/sessions/&lt;pk&gt;/&lt;sid&gt;/approvals.json`。
///
/// <para>⚠️ <b>会话级，不是项目级</b>——这是刻意的，而且它与会话同生共死：
/// 「我在这个会话里允许了 X」不该让**别的**会话跟着放行。</para>
///
/// <para>⚠️ <b>落点为什么在数据根、不在工作目录</b>：同族的 <c>ClaimStore</c> 放在
/// 工作目录（声明是**可分享的项目信息**），而批准不是——它是**这个会话的私有状态**，
/// 与 `state.json` 同级（那个也在会话目录里）。⇒ 换工作目录不该带走一批批准。</para>
///
/// <para>形状照抄 <see cref="ClaimStore"/>：没有文件 = 空列表（不是错误）· 坏文件不让它打死核心
/// · 写走 <see cref="AtomicFile.Write"/>。</para>
///
/// <para>⚠️ <b>今天是「只增不减」的</b>：批准会随会话目录一起被 `session delete` 带走，
/// 但没有别的清理路径。这与 `01` §11 的 GC 是两件事（那边收的是**工具结果**）——
/// **别顺手在这里加清理**，它需要先有一条「什么时候可以忘掉一个批准」的判据。</para>
/// </summary>
public sealed class ApprovalStore(string dataRoot, string projectKey, string sessionId)
{
    private string Path_ => System.IO.Path.Combine(
        dataRoot, "sessions", projectKey, sessionId, "approvals.json");

    public IReadOnlyList<Approval> All()
    {
        if (!File.Exists(Path_)) return [];

        try
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<List<Approval>>(
                File.ReadAllText(Path_), Kernel.Json.Options) ?? [];

            // ⚠️ 丢掉字段为 null 的条目：`Json.Options` 没开 RespectNullableAnnotations，
            // 所以 `"toolName": null`（乃至 `[null]`）都是合法 JSON、不进下面的 catch，
            // 不丢的话下游的 `ToolWide` 查表直接 NRE。同 `ClaimStore.All` 那条。
            return parsed.Where(a => a is not null && a.ToolName is not null && a.ArgsHash is not null).ToList();
        }
        // IOException 也要接：文件可手编，也可能被别的进程以 FileShare.None 独占，
        // 那时抛的是 IOException 而不是 JsonException。同 `ClaimStore.All`。
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
        {
            // 坏文件不让它打死核心：批准是辅助信息。⚠️ 静默——本类没有任何告警出口。
            return [];
        }
    }

    /// <summary>
    /// 这个调用被批准过了吗（`01` §3.8）。⚠️ <b>闸门问它<b>之前</b>才决定要不要打扰用户</b>——
    /// 判据是「批过就**不问**」，不是「批过就放行」。
    /// </summary>
    /// <param name="argsHash">参数原文的哈希（见 <see cref="Approval.ArgsHash"/>）。</param>
    public bool Covers(string toolName, string argsHash) =>
        All().Any(a => string.Equals(a.ToolName, toolName, StringComparison.Ordinal)
                    && (a.ToolWide || string.Equals(a.ArgsHash, argsHash, StringComparison.Ordinal)));

    /// <summary>记一条。⚠️ **追加**，不覆盖——同一工具的不同参数各记一条。</summary>
    public void Add(Approval approval) => Save([.. All(), approval]);

    public void Save(IEnumerable<Approval> approvals) =>
        AtomicFile.Write(Path_, System.Text.Json.JsonSerializer.Serialize(approvals.ToList(), Kernel.Json.Options));

    /// <summary>
    /// 参数的**规范化**指纹（`01` §3.8）。
    ///
    /// <para>⚠️ <b>它答的问题是「这两个字符串是不是同一次调用」</b>，所以判据是
    /// <b>「重排键序 / 加空格之后还是不是同一个请求」</b>——答案是**是**。
    /// ⇒ 先解析、按键序递归重排、再哈希；**解析不了**（不是合法 JSON）时退回哈希原文
    /// （形状坏了不该让两种不同的坏形状撞成一个）。</para>
    ///
    /// <para>⚠️ <b>它曾经是「只哈希原文」，而那是个错误——真模型实测逼出来的</b>
    /// （2026-10-11，真模型 + 真网关 + 隔离数据根）。原注释写的理由是
    /// 「规范化会让 <c>{"a":1,"b":2}</c> 与 <c>{"b":2,"a":1}</c> 撞成同一个哈希
    /// ⇒ 用户批的这一条替另一条放行」。</para>
    ///
    /// <para>⚠️ <b>那句断言是错的，而且它把方向搞反了</b>：JSON 对象**无序**，
    /// 那两串是**同一个请求**——模型重发一次、键序换一下，用户批的那一次**本来就该**
    /// 放行。按原文哈希的后果是**每次重发都重新问一遍**（实测：同一个 <c>update</c>
    /// 调用，第一次 <c>{"path":…,"old":…,"new":…}</c>、重发
    /// <c>{"new":…,"old":…,"path":…}</c> ⇒ 两个哈希 ⇒ 批准**不生效** ⇒ 活锁）。</para>
    ///
    /// <para>⚠️ <b>而它当年真正想防的那件事，靠的是另一条判据</b>：
    /// 「批的是这一次调用」表达在 <see cref="Approval.ToolWide"/> 上
    /// （<c>false</c> ⇒ 只有同一个工具 + 同一份参数才放行），
    /// **不是**靠哈希对原文的敏感性。哈希的职责是**认出同一个请求**，
    /// 不是拒绝承认它是同一个请求。⇒ 两个方向的差别只体现在
    /// <c>{"a":1,"b":2}</c> 与 <c>{"b":3,"a":1}</c> 这种**值真的不同**的情形上，
    /// 而那两串规范化之后照样不同。</para>
    ///
    /// <para>⚠️ <b>它<b>不是</b>「参数的语义等价」</b>：只做键序重排与空白规范化，
    /// **不碰值的表示**（<c>1</c> 与 <c>1.0</c> 是两串不同的原文 ⇒ 两个哈希）。
    /// 判据是「同一个请求的两种写法」，不是「算出来一样的东西」——
    /// 后者要懂每个工具的参数语义，而核心**不该懂**（`00` §5）。</para>
    /// </summary>
    public static string HashArgs(string argumentsJson)
    {
        string canonical;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(argumentsJson);
            canonical = Canonical(doc.RootElement);
        }
        catch (System.Text.Json.JsonException)
        {
            // ⚠️ 解析不了就哈希**原文**：这一支不是「兜底」，是判据的另一半——
            // 两串不同的坏 JSON 是两个不同的东西，压成一个会让一次写坏的参数**冒充**另一个。
            canonical = argumentsJson;
        }

        return Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>
    /// 递归规范化：对象**按键名 ordinal 排序**，数组**保序**（顺序是语义），
    /// 其余原样写回。⚠️ 输出是给人看不了的中间形态，只为哈希稳定——**它不落盘**。
    /// </summary>
    private static string Canonical(System.Text.Json.JsonElement e) => e.ValueKind switch
    {
        // ⚠️ 数组**不许排序**：`["a","b"]` 与 `["b","a"]` 是两个不同的参数。
        System.Text.Json.JsonValueKind.Array =>
            "[" + string.Join(",", e.EnumerateArray().Select(Canonical)) + "]",

        // ⚠️ ordinal 排序（不是文化序）：本仓那条「ordinal 不是文化序」的老规矩——
        // 文化序会让同两串在不同 locale 下算出不同哈希，而那会让批准**跨机器不成立**。
        System.Text.Json.JsonValueKind.Object =>
            "{" + string.Join(",", e.EnumerateObject()
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => System.Text.Json.JsonSerializer.Serialize(p.Name) + ":" + Canonical(p.Value)))
            + "}",

        // ⚠️ `GetRawText()` 而不是 `ToString()`：后者对数字会走格式化
        // （`1.50` 可能变成 `1.5`），而**值的表示不许动**——那属于语义等价，不在本方法职责内。
        _ => e.GetRawText(),
    };
}
