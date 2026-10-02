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
            //
            // ⚠️ **这里是静默的，而且这是已知的缺口，不是疏忽。** 初版注释写「别静默：
            // 写一条核心告警」——**那句描述的是本类做不到的事**：`ClaimStore` 只拿得到
            // 一个工作目录字符串，**没有 `EventBus`、没有 logger**，它没有任何告警出口。
            // ⇒ 真要做可见性告警，得把出口**注入进来**（改构造函数签名 ⇒ 改 `ClaimStore`
            // 的全部调用点）。**代价与收益不成比例**：声明是辅助信息，而"坏文件被忽略了"
            // 这件事本身不阻塞任何动作。⇒ 记为**已知偏离**，留待将来需要时连告警出口一起加。
            return [];
        }
    }

    public void Save(IEnumerable<Claim> claims) =>
        AtomicFile.Write(Path_, System.Text.Json.JsonSerializer.Serialize(claims.ToList(), Canonical.Json.Options));

    // ⚠️ **按路径的锁**，形态**取自** `HistoryLog.PathLocks`（同一处代码库的既有做法）。
    // ⚠️ **但键必须规范化，`HistoryLog` 那处是 `Path.GetFullPath(path)`**——
    // 初版我直接键在 `Path_` 上，**漏了规范化**，于是同一个项目里两个**只有大小写不同**
    // 的工作目录会拿到**两把不同的锁**，丢更新照样发生。
    // ⚠️ 实测（复审者，真命令路径）：**100/100 轮丢一条**，盘上 100 而不是 200；
    // 同字符串对照 0/100。⇒ 键里必须有 `ToLowerInvariant`——
    // 与 `Claims.Normalize` 的大小写策略一致（`01` §12.1：Windows 大小写不敏感）。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lock> PathLocks = new();

    /// <summary>锁的键：**规范化**（绝对 + 小写）。⚠️ 别只 `GetFullPath`——它不改大小写。</summary>
    private string LockKey => System.IO.Path.GetFullPath(Path_).ToLowerInvariant();

    /// <summary>`Update` 的产出：**这次调用真的加了什么、真的删了什么**。</summary>
    /// <param name="Added">新增的条目（调用方**只该为这些**发事件）。</param>
    /// <param name="Removed">删掉的条目（同上）。</param>
    public sealed record Updated(IReadOnlyList<Claim> Added, IReadOnlyList<Claim> Removed);

    /// <summary>
    /// ⚠️ **把「读 → 改 → 写」放进同一把锁里** —— 这是本方法与
    /// 分开调 `All()` / `Save()` 的**唯一区别**，而那正是要害。
    ///
    /// <para>⚠️ **分开调用会丢更新，而且丢得静默。** 两个会话同时 acquire 时：
    /// 各自 `All()` 读到**同一份旧列表** → 各自改 → 各自 `Save()` ⇒ **后写的盖掉前写的**，
    /// 盘上少一条声明，而**两边都收到 `Accepted`**。
    /// ⚠️ `AtomicFile.Write` 只保证「文件不半截」与「并发写不撞名」——**它不管读-改-写**。</para>
    ///
    /// <para>⚠️ **实测过**（审阅者，8 会话并发占互不重叠的路径）：带 `Barrier` **40/40 轮只剩 1 条**；
    /// 无 `Barrier` **30/30 轮丢更新 + 19/30 轮抛 `UnauthorizedAccessException`**
    /// （`AtomicFile` 的重试耗尽后 rethrow ⇒ 逃出 `DispatchAsync`）。</para>
    ///
    /// <para>⚠️ **读必须在锁内**（就是这里的 `All()`）——把锁外的快照传进来等于锁没起作用。
    /// 调用方的**判据**（如 `FindConflict`）也要放进 <paramref name="change"/> 里，
    /// 否则「查了没冲突 → 别人占了 → 我写进去」照样超占。</para>
    ///
    /// <para>⚠️ **<paramref name="change"/> 的契约：恰好在锁内被调用一次、同步。**
    /// 调用方**可以**从它里面往外写变量（如「有没有冲突」）——今天的两个处理器就是这么做的。
    /// ⚠️ 若将来改成可能调用多次（重试），那两个调用方**会出错**，必须一起改。</para>
    ///
    /// <returns>
    /// ⚠️ **真的加了什么、删了什么**（按 `Claim` 的**值** diff，`record` ⇒ 值相等）。
    /// 调用方据此决定**为哪些路径**发事件——`01` §5：**无迁移则无事件**。
    /// ⚠️ 初版返回一个 `bool`（"有没有变"），于是调用方只能**猜**哪些路径变了，
    /// 猜的结果是「为 scope 里每条都发事件」，包括**没被删掉**的那些。
    /// </returns>
    /// </summary>
    public Updated Update(Func<IReadOnlyList<Claim>, IEnumerable<Claim>> change)
    {
        lock (PathLocks.GetOrAdd(LockKey, _ => new Lock()))
        {
            var before = All();
            var after = change(before).ToList();

            // 按值 diff。⚠️ 用 `Except` 而不是比索引：`record` 的值相等让这件事成立，
            // 且**与顺序无关**——顺序变了但成员没变时不该发事件。
            var added = after.Except(before).ToList();
            var removed = before.Except(after).ToList();

            if (added.Count == 0 && removed.Count == 0) return new Updated([], []);

            Save(after);
            return new Updated(added, removed);
        }
    }
}
