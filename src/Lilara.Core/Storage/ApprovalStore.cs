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
    /// 参数原文的哈希。
    ///
    /// <para>⚠️ <b>它是这套东西里唯一「判两个调用是不是同一次」的东西</b>——
    /// 所以它<b>只许哈希那段原文</b>：先 <c>JsonDocument.Parse</c> 再重新序列化
    /// 会让 `{"a":1,"b":2}` 与 `{"b":2,"a":1}` 撞成同一个哈希，于是**用户批的这一条
    /// 会替另一条放行**。逐字比较才是对的。</para>
    /// </summary>
    public static string HashArgs(string argumentsJson) =>
        Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(argumentsJson)));
}
