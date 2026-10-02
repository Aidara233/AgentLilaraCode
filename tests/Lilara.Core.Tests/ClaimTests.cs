using Lilara.Core.Session;

namespace Lilara.Core.Tests;

/// <summary>
/// 声明的冲突判据（`01` §12.1）。
/// ⚠️ 规则只有一句：**一个路径是另一个的前缀就是冲突**——但它有两个真实世界的陷阱
/// （Windows 大小写不敏感、`C:\a` 不该被 `C:\ab` 命中），两条都钉在下面。
/// </summary>
public class ClaimTests
{
    private const string Work = @"C:\repo";

    /// <summary>
    /// ⚠️ **断言必须写小写字面量。** 实测（`Path.GetFullPath(@"C:\repo\src\..\a.cs")` 在 Windows 上
    /// 返回**原样的大小写**、**不**规范化它——规范化只存在于 `Normalize` 自己的 `ToLowerInvariant`）：
    /// 写成 `Assert.Equal(@"C:\repo\src\a.cs", …)` 会与实现的 `ToLowerInvariant` 契约冲突，
    /// **全绿不可能同时成立**。落盘格式是规范化的，所以钉小写才是钉那份契约。
    /// </summary>
    [Fact]
    public void Normalize_MakesRelativePathsAbsoluteAgainstTheWorkingDirectory() =>
        Assert.Equal(@"c:\repo\src\a.cs", Claims.Normalize(@"src\a.cs", Work));

    /// <summary>⚠️ 大小写不敏感是**无条件**的（实现里是无条件 `ToLowerInvariant`），不是「只在 Windows 上」。</summary>
    [Fact]
    public void Normalize_IsCaseInsensitive_OnTheDriveAndThePath() =>
        Assert.Equal(Claims.Normalize(@"C:\Repo\Src\A.cs", Work), Claims.Normalize(@"c:\repo\src\a.cs", Work));

    /// <summary>
    /// ⚠️ **这条钉的其实是「输入永远经由 `GetFullPath`」。** 实测：Windows 上
    /// `GetFullPath` **自己**就把 `/` 归一成 `\`（`C:/repo/src` → `C:\repo\src`），
    /// `C:\repo\src/` → `C:\repo\src\`——所以实现里那行
    /// `Replace(AltDirectorySeparatorChar, DirectorySeparatorChar)` **是冗余的**，
    /// 这条测试对**那半句没有区分力**（删掉它这条照样绿）。
    /// 留着它是因为它钉的是**契约**（两种分隔符等价），而契约是对的。
    /// ⚠️ 拿掉 `GetFullPath` 换成手拼字符串，这条**必须**红。
    /// </summary>
    [Fact]
    public void Normalize_TreatsBothSeparatorsTheSame()
    {
        Assert.Equal(Claims.Normalize(@"src/a.cs", Work), Claims.Normalize(@"src\a.cs", Work));
    }

    /// <summary>⚠️ 同样实测过：`GetFullPath` 顺手折叠 `.` / `..`，所以钉小写。</summary>
    [Fact]
    public void Normalize_ResolvesDotSegments() =>
        Assert.Equal(@"c:\repo\a.cs", Claims.Normalize(@"src\..\a.cs", Work));

    /// <summary>
    /// ⚠️ **末尾分隔符必须去掉**（`C:\repo\` 与 `C:\repo` 是同一处）。
    /// 实测 `GetFullPath` 在两种写法下**都保留**末尾分隔符
    /// （`C:\repo\src\` → `C:\repo\src\`；`C:\repo\src/` → `C:\repo\src\`）
    /// ——它只在遇到 `.` 段时才把它折掉（`C:\repo\src\.` → `C:\repo\src`）。
    /// 所以这条**有**区分力：删掉 `TrimEnd` 它就红——
    /// 且红的方式正是要害：`c:\repo\src\` 的前缀判据多一个字符，
    /// 于是 `src\` **永远命中不了** `src\a.cs`，冲突判据整体失效。
    /// </summary>
    [Fact]
    public void Normalize_DropsTheTrailingSeparator() =>
        Assert.Equal(Claims.Normalize(@"C:\repo\src", Work), Claims.Normalize(@"C:\repo\src\", Work));

    /// <summary>同一路径 ⇒ 冲突。</summary>
    [Fact]
    public void SamePath_Conflicts() =>
        Assert.True(Claims.Conflicts(@"C:\repo\src", @"C:\repo\src"));

    /// <summary>一个占父目录 ⇒ 冲突（`src` 覆盖 `src/a.cs`）。</summary>
    [Fact]
    public void ParentCoversChild_Conflicts()
    {
        Assert.True(Claims.Conflicts(@"C:\repo\src", @"C:\repo\src\a.cs"));
        Assert.True(Claims.Conflicts(@"C:\repo\src\a.cs", @"C:\repo\src"));
    }

    /// <summary>
    /// ⚠️ **这个陷阱值得单独一条**：朴素的前缀比较会让 `C:\repo\src` 命中 `C:\repo\src2`，
    /// 于是两个**毫不相干**的目录被判成冲突，声明功能从此没人敢用。
    /// 判据必须是「前缀 + 分隔符边界」。
    /// </summary>
    [Fact]
    public void SiblingWithASharedNamePrefix_DoesNotConflict()
    {
        Assert.False(Claims.Conflicts(@"C:\repo\src", @"C:\repo\src2"));
        Assert.False(Claims.Conflicts(@"C:\repo\a.cs", @"C:\repo\a.cs.bak"));
    }

    [Fact]
    public void UnrelatedPaths_DoNotConflict() =>
        Assert.False(Claims.Conflicts(@"C:\repo\src", @"C:\repo\tests"));

    /// <summary>
    /// ⚠️ **守卫上面那句 `TrimEnd` 的边界行为。** `GetFullPath(@"C:\")` 返回 `C:\`，去尾后是 `c:`
    /// ——一个**不是合法路径**的字符串，而它会出现在 `Normalize` 的输出里（声明 `C:\` 时）。
    /// 看上去像该"顺手修"的怪值，**其实修了就坏**：实测
    /// `IsUnder("c:", "c:\repo")` → True，而 `IsUnder("c:\", "c:\repo")` → False。
    /// 所以特例保留根的反斜杠会让**根路径与它下面的一切**都判不出冲突（静默失效）。
    /// 这条钉住那个方向，防止后来者把 `c:` 清理掉。
    /// </summary>
    [Fact]
    public void Normalize_RootPath_StillConflictsWithEverythingUnderIt()
    {
        var root = Claims.Normalize(@"C:\", Work);
        Assert.True(Claims.Conflicts(root, Claims.Normalize(@"C:\repo\a.cs", Work)));
        Assert.True(Claims.Conflicts(root, Claims.Normalize(@"C:\repo", Work)));
    }

    /// <summary>⚠️ 自己占的地方不算冲突，否则一个人无法重复声明同一片区域（幂等必需）。</summary>
    [Fact]
    public void FindConflict_IgnoresTheSameSession()
    {
        var existing = new[] { new Claim("s1", "pk", @"C:\repo\src", DateTimeOffset.UnixEpoch) };

        Assert.Null(Claims.FindConflict(existing, "s1", @"C:\repo\src\a.cs"));
        Assert.Equal("s1", Claims.FindConflict(existing, "s2", @"C:\repo\src\a.cs")!.SessionId);
    }

    [Fact]
    public void FindConflict_ReturnsNullWhenNothingOverlaps()
    {
        var existing = new[] { new Claim("s1", "pk", @"C:\repo\src", DateTimeOffset.UnixEpoch) };

        Assert.Null(Claims.FindConflict(existing, "s2", @"C:\repo\tests"));
    }
}
