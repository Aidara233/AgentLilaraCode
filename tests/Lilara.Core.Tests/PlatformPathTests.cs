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

    /// <summary>
    /// ⚠️ **发布检查发现的一处平台分叉**（2026-10-17）：`delete` 的「拒绝删工作目录本身」
    /// 原先写死 <c>OrdinalIgnoreCase</c>，与同文件里 <c>SamePath</c> 的按平台选择**是两份判据**。
    /// 在大小写敏感的系统上，这会让「删掉的路径恰好与工作目录只差大小写」被**误判成根**⇒
    /// 一次合法的删除被拒绝（误报）；反过来，两层判据漂开正是本仓栽过的那类形状。
    ///
    /// <para>⚠️ **它在 Windows 上恒绿**（两边都忽略大小写），所以这条测试能提供的证据
    /// 只到「Unix 上会红」为止——而本机跑不了 Unix。别把这里的绿读成「跨平台已经验过」。</para>
    /// </summary>
    [Fact]
    public async Task Delete_RejectsTheWorkingDirectoryRoot_OnEveryPlatform()
    {
        var context = new SessionToolContext(_root, new BlobStore(Path.Combine(_temp, "data")));
        var result = await new DeleteTool().RunAsync(
            new Lilara.Core.Kernel.ToolCall("c", "delete", """{"path":".","recursive":true}"""),
            context, CancellationToken.None);

        Assert.True(Directory.Exists(_root));
        Assert.Contains("工作目录本身", string.Join("", result.Content.OfType<Lilara.Core.Kernel.Block.TextBlock>().Select(b => b.Text)));
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
