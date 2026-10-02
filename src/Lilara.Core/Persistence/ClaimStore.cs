using Lilara.Core.Session;

namespace Lilara.Core.Persistence;

/// <summary>
/// 声明的落盘（`01` §8：`&lt;工作目录&gt;/.lilara/claims.json`）。
///
/// <para>⚠️ **声明进工作目录，不进数据根**——它是**可分享的项目信息**
/// （「这个仓库的这部分在做工程」团队里别人也该知道），而会话状态是私有的。
/// 这是 `01` §8 那条判据「可分享 vs 私有」的直接应用。</para>
///
/// <para>⚠️ **没有文件 = 空列表，不是错误**。大多数项目、大多数时候没有任何声明。</para>
/// </summary>
public sealed class ClaimStore(string workingDirectory)
{
    private string Path_ => System.IO.Path.Combine(workingDirectory, ".lilara", "claims.json");

    public IReadOnlyList<Claim> All()
    {
        if (!File.Exists(Path_)) return [];

        try
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<List<Claim>>(
                File.ReadAllText(Path_), Canonical.Json.Options) ?? [];

            // ⚠️ **丢掉 `Path` 为 null 的条目**——这是**回读边界**，不是多余的守卫。
            // `claims.json` 在**工作目录**里、可分享、**人手可编辑**，而
            // `Canonical.Json.Options` 没开 `RespectNullableAnnotations` ⇒
            // `"path": null` 是**合法 JSON**、能反序列化成 `Claim.Path = null`，
            // **不会**进下面的 `catch (JsonException)`。
            // ⇒ 不丢的话，`Claims.Conflicts` 里 `child.Length` 直接 **NRE**，
            // 冒泡出 `HandleClaimAcquire`（T7 没包 try/catch）⇒ **打死核心**——
            // 与本类注释「坏文件不让它打死核心」**正面冲突**。
            // ⚠️ 顺带：`WhenWritingNull` 会把 null 的 `path` **整个省略**，
            // 所以这种坏条目是**稳定**的（写回再读还是它），不会自愈。
            //
            // ⚠️ **`c is not null` 那半句是必须的，别删。** `[null]`（**数组元素**为 null）
            // 也是合法 JSON、也能手写进文件，而 `List<Claim>` 反序列化会给出**含 null 的列表**
            // ⇒ `c.Path` 对 `c == null` 直接 **NRE**，**且抛在这一行本身**，
            // 同样逃出下面那个 `catch (JsonException)`。只过滤 `Path is null` 关掉的是
            // F2 的**形状**，关不掉**同族**。
            return parsed.Where(c => c is not null && c.Path is not null).ToList();
        }
        catch (System.Text.Json.JsonException)
        {
            // ⚠️ 坏文件不让它打死核心：声明是**辅助**信息（可见性），
            // 而核心的职责是推进会话。宁可这一处暂时瞎，也不能整个进程起不来。
            // ⚠️ 返回空而不是抛——但**别静默**：写一条核心告警。
            return [];
        }
    }

    public void Save(IEnumerable<Claim> claims) =>
        AtomicFile.Write(Path_, System.Text.Json.JsonSerializer.Serialize(claims.ToList(), Canonical.Json.Options));
}
