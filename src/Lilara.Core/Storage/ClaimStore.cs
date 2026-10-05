using Lilara.Core.Kernel;

namespace Lilara.Core.Storage;

// 声明的落盘：<工作目录>/.lilara/claims.json——进工作目录不进数据根，
// 因为声明是可分享的项目信息，会话状态才是私有的。没有文件 = 空列表，不是错误。
public sealed class ClaimStore(string workingDirectory)
{
    private string Path_ => System.IO.Path.Combine(workingDirectory, ".lilara", "claims.json");

    public IReadOnlyList<Claim> All()
    {
        if (!File.Exists(Path_)) return [];

        try
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<List<Claim>>(
                File.ReadAllText(Path_), Kernel.Json.Options) ?? [];

            // 丢掉 Path 为 null 的条目：Json.Options 没开 RespectNullableAnnotations，
            // 所以 "path": null（乃至数组元素 null：[null]）都是合法 JSON、不进下面的 catch，
            // 不丢的话下游的 child.Length 直接 NRE。
            return parsed.Where(c => c is not null && c.Path is not null).ToList();
        }
        // 必须连 IOException 一起接：文件可手编、也可能被别的进程以 FileShare.None 独占，
        // 那时 ReadAllText 抛的是 IOException 而不是 JsonException。
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
        {
            // 坏文件不让它打死核心：声明是辅助信息。这里是静默的——本类没有任何告警出口。
            return [];
        }
    }

    public void Save(IEnumerable<Claim> claims) =>
        AtomicFile.Write(Path_, System.Text.Json.JsonSerializer.Serialize(claims.ToList(), Kernel.Json.Options));

    // 按路径的锁。键必须规范化到小写——Windows 大小写不敏感，两个只差大小写的
    // 工作目录必须拿到同一把锁，否则丢更新。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lock> PathLocks = new();

    // 锁的键：绝对路径 + 小写。GetFullPath 不改大小写。
    private string LockKey => System.IO.Path.GetFullPath(Path_).ToLowerInvariant();

    // Update 的产出：这次调用真的加了什么、删了什么（调用方只该为这些发事件）。
    public sealed record Updated(IReadOnlyList<Claim> Added, IReadOnlyList<Claim> Removed);

    // 把「读 → 改 → 写」放进同一把锁——这是与分开调 All()/Save() 的唯一区别，而那正是要害：
    // 分开调会静默丢更新（两个会话各自读旧列表、各自写，后写的盖掉前写的）；
    // AtomicFile.Write 只保证「文件不半截」「并发写不撞名」，不管读-改-写。
    // 读必须在锁内；调用方的判据（FindConflict）也要放进 change 里，否则照样超占。
    // change 的契约：恰好在锁内被调用一次、同步。返回按值 diff 出的 Added/Removed。
    public Updated Update(Func<IReadOnlyList<Claim>, IEnumerable<Claim>> change)
    {
        lock (PathLocks.GetOrAdd(LockKey, _ => new Lock()))
        {
            var before = All();
            var after = change(before).ToList();

            // 按值 diff，与顺序无关：顺序变了但成员没变时不该发事件。
            var added = after.Except(before).ToList();
            var removed = before.Except(after).ToList();

            if (added.Count == 0 && removed.Count == 0) return new Updated([], []);

            Save(after);
            return new Updated(added, removed);
        }
    }
}
