using Lilara.Core.Session;

namespace Lilara.Core.Tests;

// 冲突判据：一个路径是另一个的前缀就是冲突；两个陷阱是 Windows 大小写不敏感、
// C:\a 不该被 C:\ab 命中。
public class ClaimTests
{
    private const string Work = @"C:\repo";

    // 断言必须写小写字面量：GetFullPath 在 Windows 上返回原样大小写、不规范化，
    // 规范化只存在于 Normalize 自己的 ToLowerInvariant；落盘格式是规范化的，故钉小写。
    [Fact]
    public void Normalize_MakesRelativePathsAbsoluteAgainstTheWorkingDirectory() =>
        Assert.Equal(@"c:\repo\src\a.cs", Claims.Normalize(@"src\a.cs", Work));

    // 大小写不敏感是无条件的，不是「只在 Windows 上」。
    [Fact]
    public void Normalize_IsCaseInsensitive_OnTheDriveAndThePath() =>
        Assert.Equal(Claims.Normalize(@"C:\Repo\Src\A.cs", Work), Claims.Normalize(@"c:\repo\src\a.cs", Work));

    // GetFullPath 自己就把 / 归一成 \，故实现里那句 Replace 是冗余的；这条钉的是契约（两种分隔符等价）。
    [Fact]
    public void Normalize_TreatsBothSeparatorsTheSame()
    {
        Assert.Equal(Claims.Normalize(@"src/a.cs", Work), Claims.Normalize(@"src\a.cs", Work));
    }

    // GetFullPath 顺手折叠 . / ..，所以钉小写。
    [Fact]
    public void Normalize_ResolvesDotSegments() =>
        Assert.Equal(@"c:\repo\a.cs", Claims.Normalize(@"src\..\a.cs", Work));

    // 末尾分隔符必须去掉：GetFullPath 保留末尾分隔符（只在遇到 . 段时才折掉），
    // 没去掉的话前缀判据多一字符，src\ 永远命中不了 src\a.cs，冲突判据整体失效。
    [Fact]
    public void Normalize_DropsTheTrailingSeparator() =>
        Assert.Equal(Claims.Normalize(@"C:\repo\src", Work), Claims.Normalize(@"C:\repo\src\", Work));

    // 同一路径 ⇒ 冲突。
    [Fact]
    public void SamePath_Conflicts() =>
        Assert.True(Claims.Conflicts(@"C:\repo\src", @"C:\repo\src"));

    // 一个占父目录 ⇒ 冲突（src 覆盖 src/a.cs）。
    [Fact]
    public void ParentCoversChild_Conflicts()
    {
        Assert.True(Claims.Conflicts(@"C:\repo\src", @"C:\repo\src\a.cs"));
        Assert.True(Claims.Conflicts(@"C:\repo\src\a.cs", @"C:\repo\src"));
    }

    // 朴素的前缀比较会让 C:\repo\src 命中 C:\repo\src2；判据必须是「前缀 + 分隔符边界」。
    [Fact]
    public void SiblingWithASharedNamePrefix_DoesNotConflict()
    {
        Assert.False(Claims.Conflicts(@"C:\repo\src", @"C:\repo\src2"));
        Assert.False(Claims.Conflicts(@"C:\repo\a.cs", @"C:\repo\a.cs.bak"));
    }

    [Fact]
    public void UnrelatedPaths_DoNotConflict() =>
        Assert.False(Claims.Conflicts(@"C:\repo\src", @"C:\repo\tests"));

    // GetFullPath(@"C:\") 去尾后是 "c:"——不是合法路径，但 IsUnder("c:", "c:\repo") 为 True；
    // 修掉它会让根路径与它下面的一切都判不出冲突（静默失效）。
    [Fact]
    public void Normalize_RootPath_StillConflictsWithEverythingUnderIt()
    {
        var root = Claims.Normalize(@"C:\", Work);
        Assert.True(Claims.Conflicts(root, Claims.Normalize(@"C:\repo\a.cs", Work)));
        Assert.True(Claims.Conflicts(root, Claims.Normalize(@"C:\repo", Work)));
    }

    // 自己占的地方不算冲突（幂等必需）。
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
