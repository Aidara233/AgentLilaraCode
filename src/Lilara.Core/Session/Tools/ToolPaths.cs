using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lilara.Core.Session.Tools;

/// <summary>
/// 工具共用的**路径与指纹**那一小块。
///
/// <para>⚠️ <b>`FingerprintOf` 从两份合并成一份（2026-10-11）</b>。它原先在
/// <see cref="ReadTool"/> 里一份、<see cref="UpdateTool"/> 里一份，而那时的注释写着
/// 「与其抽出去选一个别扭的住址，不如两份各自带一条指向对方的注释」
/// ——**那个判断在只有两份时成立，加第三个 `write` 就不成立了**：
/// 三份手抄的哈希规则**一定会漂**，而漂的后果是同一个文件被三条记录用三个指纹指称，
/// 三态判定随即错乱（`00` §5）。⇒ 改口的判据是**副本数**，不是「哪种写法更漂亮」。</para>
/// </summary>
internal static class ToolPaths
{
    /// <summary>
    /// 当时的指纹（`00` §5 三态判定要比的那个）。
    ///
    /// <para>⚠️ <b>它把「路径」也哈希进去，这不是随手加的字段</b>：
    /// 只哈希内容的话，「把 <c>a.txt</c> 删掉、再把另一个文件改名成 <c>a.txt</c>」
    /// **换不出同一个指纹**——于是三态判定会说「世界没动」并沿用旧快照，
    /// 而那个旧快照记录的是**另一个文件**的语义。带路径把这个著名的一致性洞堵住。</para>
    ///
    /// <para>⚠️ <b>长度写在内容前面，也是为了堵洞</b>：不写长度的话，
    /// 「<c>ab</c> + 分隔符 + <c>c</c>」与「<c>a</c> + 分隔符 + <c>bc</c>」拼出来的字节相同
    /// ⇒ 两种不同的（路径, 内容）组合撞成同一个指纹。</para>
    /// </summary>
    public static string FingerprintOf(string rel, byte[] bytes)
    {
        var meta = Encoding.UTF8.GetBytes($"{bytes.Length}\n{rel}\n");
        var buf = new byte[meta.Length + bytes.Length];
        meta.CopyTo(buf, 0);
        bytes.CopyTo(buf, meta.Length);

        return Convert.ToHexStringLower(SHA256.HashData(buf));
    }

    /// <summary>参数里**显式出现了**这个键吗（哪怕值是个空串）。</summary>
    /// <remarks>
    /// ⚠️ <b>它与 <see cref="ToolArgs.String"/> 的分工是判据，不是啰嗦</b>：
    /// `String` 对「键缺失」与「值就是空串」给的是**同一个结果**，而
    /// `write`（`content: ""` = 清空一个文件）与 `update`（`new: ""` = 删掉那一段）
    /// 都要求这两件事**分得开**——否则一次漏参数会变成一次**静默的清空 / 删除**。
    /// </remarks>
    public static bool HasKey(string argumentsJson, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(key, out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
