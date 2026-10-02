namespace Lilara.Core.Session;

/// <summary>一条声明（`01` §12.2）。</summary>
/// <param name="Path">**已规范化**的绝对路径。别把原始输入存进来——判据会当场失效。</param>
public sealed record Claim(string SessionId, string ProjectKey, string Path, DateTimeOffset At);

/// <summary>
/// 资源声明的冲突判据（`01` §12）。
///
/// <para>⚠️ **母题是文件系统，而单核心里它降级成一次路径比较**（§12.1）：
/// 核心是本设备唯一的 ⇒ 多 agent 的文件冲突问题不存在，只剩「两个声明是否重叠」。</para>
///
/// <para>⚠️ **本计划只做「声明主动」那一格**（§12.2 的两格之一）：
/// 「写入时自动检查」要求有写入路径（工具），而工具不存在（见计划的 D3）。
/// 所以声明现在是**纯可见性机制**——它不阻塞任何东西，它让别人**看见**。
/// 这不是缩水：§12.2 说声明保护的是「我的工作集不被作废」，
/// 而在没有工具的执行环境里，唯一会作废工作集的就是**另一个会话**——
/// 而那个会话看得见这里。</para>
///
/// <para>⚠️ **不做「同一派生树内不阻塞」那个例外**（§12.1）：它需要派生树概念，
/// 而 `child.spawned` 不存在。单会话下该例外不可能触发；多会话下会**过阻塞**——
/// 保守方向，且冲突是**可见**的。见计划的 D4。</para>
/// </summary>
public static class Claims
{
    /// <summary>
    /// 规范化：绝对化 → 折叠 `.` / `..` → 统一分隔符 → 大小写不敏感。
    ///
    /// <para>⚠️ **大小写不敏感是无条件的**，不是「只在 Windows 上」。理由：
    /// 规范化结果**要落盘**，而同一份声明可能被 Windows 与（将来）别的平台的实例读到；
    /// 两边按不同规则规范化 ⇒ 同一个路径两个字符串 ⇒ 冲突判不出来。</para>
    ///
    /// <para>⚠️ 存成**规范形式（小写）**，而不是「保留原样 + 比较时忽略大小写」：
    /// 判据是前缀比较，而前缀比较每次都要走对比较器——存成规范形式，判据就是一句 <c>Ordinal</c>。
    /// 实现用 <see cref="string.ToLowerInvariant"/>，**不是** `StringComparison.OrdinalIgnoreCase`
    /// （后者是**比较**用的，存不成小写）。</para>
    ///
    /// <para>⚠️ **不做符号链接解析，这是 `01` §12.1 的一处显式缺口。**
    /// §12.1 写「Windows 下需处理大小写不敏感**与符号链接**」，本计划只做前半。
    /// 理由不是「不重要」，而是**做不到可验证**：本机实测
    /// <c>New-Item -ItemType SymbolicLink</c> 失败于「此操作需要管理员权限」，
    /// 于是任何符号链接测试**无法在开发环境跑**——而 G7 要求变异先确认落上再跑，
    /// 写一条跑不了的测试等于没有测试。
    /// ⇒ **记为已知偏离（本任务 D9）**，不是已知正确：两个路径经不同链接指向同一处时，
    /// 判据会**漏报**冲突（方向是漏，不是误报）。修法与判据改写见 T7 之后。</para>
    /// </summary>
    public static string Normalize(string path, string workingDirectory)
    {
        // ⚠️ 相对路径折叠成**绝对**，用的是进程当前目录（`GetFullPath` 的语义）。
        // `workingDirectory` 只在 `path` 不是绝对路径时用来拼前缀。
        var combined = Path.IsPathRooted(path) ? path : Path.Combine(workingDirectory, path);

        // ⚠️ `Path.GetFullPath` 顺手折叠掉 `.` 与 `..`——自己拼字符串做这件事会漏掉
        // `a/../../b` 这类多层回溯。它不碰文件系统（不要求路径存在），所以是纯函数。
        var full = Path.GetFullPath(combined);

        // ⚠️ 统一分隔符。**实测这行在 Windows 上是冗余的**：
        // `GetFullPath` 自己就把 `/` 归一成 `\`（`C:/repo/src` → `C:\repo\src`），
        // 删掉它没有任何测试会红。留着是因为它**不改变契约**、且在非 Windows
        // 上（`DirectorySeparatorChar` 是 `/` 而 `AltDirectorySeparatorChar` 是 `\`）方向正好相反。
        full = full.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

        // ⚠️ **根路径也一律去，这是刻意的，别"顺手修"。** `GetFullPath(@"C:\")` 返回 `C:\`，
        // 去尾后是 `c:`——那**不是**合法的绝对路径字符串，但本方法的结果**只用于比较与落盘**
        //（从不送回文件系统：T7 只把它存进 `claims.json` 并拿来判据），而 `c:` 恰好让前缀判据
        // **正确工作**。实测（`IsUnder` 逐字照抄本实现跑的）：
        //   `IsUnder("c:",  "c:\repo")`  → **True**   ← 去尾这一版，判对
        //   `IsUnder("c:\", "c:\repo")`  → **False**  ← 保留反斜杠那一版，判错
        // ⚠️ 即：保留根的反斜杠会让根路径与它下面的**一切**都判不出冲突——**静默失效**，
        // 正是 `01` §12 要防的。守卫在 `Normalize_RootPath_StillConflictsWithEverythingUnderIt`。
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar);
        return (trimmed.Length == 0 ? full : trimmed).ToLowerInvariant();
    }

    /// <summary>
    /// 两个**已规范化**的路径是否冲突（`01` §12.1：一个是另一个的前缀）。
    ///
    /// <para>⚠️ **朴素前缀比较是错的**：`C:\repo\src` 会是 `C:\repo\src2` 的前缀，
    /// 于是两个毫不相干的目录被判成冲突。边界必须是**分隔符**——
    /// 相等，或者「前缀 + 分隔符」。这两条一起说完了所有情况。</para>
    /// </summary>
    public static bool Conflicts(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal)) return true;

        return IsUnder(a, b) || IsUnder(b, a);

        static bool IsUnder(string parent, string child) =>
            child.Length > parent.Length
            && child.StartsWith(parent, StringComparison.Ordinal)
            && child[parent.Length] == Path.DirectorySeparatorChar;
    }

    /// <summary>
    /// 在既有声明里找一条与它冲突的（**忽略自己的**——否则一个人无法幂等地重复声明）。
    /// 返回 null 表示没有冲突。
    /// </summary>
    public static Claim? FindConflict(IEnumerable<Claim> existing, string sessionId, string normalizedPath)
    {
        foreach (var c in existing)
        {
            if (string.Equals(c.SessionId, sessionId, StringComparison.Ordinal)) continue;
            if (Conflicts(c.Path, normalizedPath)) return c;
        }
        return null;
    }
}
