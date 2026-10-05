using Lilara.Core.Kernel;

namespace Lilara.Core.Tests;

public class ProjectKeyTests
{
    [Fact]
    public void FromPath_IsStable()
    {
        var a = ProjectKey.FromPath(@"C:\work\proj");
        var b = ProjectKey.FromPath(@"C:\work\proj");

        Assert.Equal(a, b);
        Assert.Equal(16, a.Length);
    }

    [Fact]
    public void FromPath_IsCaseInsensitive()
    {
        Assert.Equal(ProjectKey.FromPath(@"C:\Work\Proj"),
                     ProjectKey.FromPath(@"c:\work\proj"));
    }

    [Fact]
    public void FromPath_IgnoresTrailingSeparator()
    {
        Assert.Equal(ProjectKey.FromPath(@"C:\work\proj"),
                     ProjectKey.FromPath(@"C:\work\proj\"));
    }

    [Fact]
    public void FromPath_DiffersForDifferentDirectories()
    {
        Assert.NotEqual(ProjectKey.FromPath(@"C:\work\a"),
                        ProjectKey.FromPath(@"C:\work\b"));
    }
}
