using Lilara.Core.Session.Tools;
using Lilara.Core.Storage;

namespace Lilara.Core.Tests;

public sealed class PlatformPathTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "lilara-path-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;

    public PlatformPathTests()
    {
        _root = Path.Combine(_temp, "work");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true);
    }

    [Fact]
    public void ResolvePath_UsesPlatformCaseSemanticsAndDirectoryBoundary()
    {
        var context = new SessionToolContext(_root, new BlobStore(Path.Combine(_temp, "data")));
        Directory.CreateDirectory(Path.Combine(_root, "Case"));

        Assert.NotNull(context.ResolvePath("Case/child"));
        Assert.NotNull(context.ResolvePath("Case/../Case/child"));
        Assert.Null(context.ResolvePath("../escape"));
        Assert.Null(context.ResolvePath(_root + "-sibling/file"));

        if (OperatingSystem.IsWindows())
            Assert.NotNull(context.ResolvePath("case/child"));
        else
            Assert.Null(context.ResolvePath("case/child"));
    }

    [Fact]
    public async Task PathTool_DoesNotTreatCaseDistinctFilesAsSameOnCaseSensitiveSystems()
    {
        if (OperatingSystem.IsWindows()) return;

        File.WriteAllText(Path.Combine(_root, "Case.txt"), "upper");
        File.WriteAllText(Path.Combine(_root, "case.txt"), "lower");
        var context = new SessionToolContext(_root, new BlobStore(Path.Combine(_temp, "data")));
        var outcome = await new PathTool().RunAsync(
            new Lilara.Core.Kernel.ToolCall("c", "path", """{"operation":"copy","source":"Case.txt","destination":"copy.txt"}"""),
            context, CancellationToken.None);

        Assert.Contains("已复制", string.Join("", outcome.Content.OfType<Lilara.Core.Kernel.Block.TextBlock>().Select(b => b.Text)));
        Assert.Equal("upper", File.ReadAllText(Path.Combine(_root, "copy.txt")));
        Assert.Equal("lower", File.ReadAllText(Path.Combine(_root, "case.txt")));
    }
}
