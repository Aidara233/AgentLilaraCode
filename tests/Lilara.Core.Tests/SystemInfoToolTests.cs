using Lilara.Core.Kernel;
using Lilara.Core.Session.Tools;
using Lilara.Core.Storage;
using Block = Lilara.Core.Kernel.Block;

namespace Lilara.Core.Tests;

public sealed class SystemInfoToolTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "lilara-info-" + Guid.NewGuid().ToString("N"));
    private readonly string _work;

    public SystemInfoToolTests()
    {
        _work = Path.Combine(_temp, "work");
        Directory.CreateDirectory(_work);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true);
    }

    [Fact]
    public async Task SystemInfo_IsReadOnlyAndReturnsSnapshotSections()
    {
        var tool = new SystemInfoTool(_ => Task.FromResult(new SystemInfoTool.NvidiaStatus("不可用", "测试中未探测")));
        Assert.Equal(PermissionClass.ReadOnly, tool.PermissionClass);
        Assert.Equal(Verdict.Allow, Gate.Decide(tool.PermissionClass, "readonly"));

        var context = new SessionToolContext(_work, new BlobStore(Path.Combine(_temp, "data")));
        var outcome = await tool.RunAsync(new ToolCall("c", tool.Name, "{}"), context, CancellationToken.None);
        var text = string.Join("", outcome.Content.OfType<Block.TextBlock>().Select(b => b.Text));

        Assert.Contains("CPU:", text);
        Assert.Contains("内存:", text);
        Assert.Contains("磁盘卷:", text);
        Assert.Contains("NVIDIA GPU: 不可用", text);
        Assert.DoesNotContain("进程", text);
    }

    [Fact]
    public async Task SystemInfo_ReportsGpuQueryFailureWithoutTreatingItAsIdle()
    {
        var tool = new SystemInfoTool(_ => Task.FromResult(new SystemInfoTool.NvidiaStatus("查询超时", "超过限制")));
        var outcome = await tool.RunAsync(new ToolCall("c", tool.Name, "{}"),
            new SessionToolContext(_work, new BlobStore(Path.Combine(_temp, "data"))), CancellationToken.None);
        var text = string.Join("", outcome.Content.OfType<Block.TextBlock>().Select(b => b.Text));

        Assert.Contains("NVIDIA GPU: 查询超时", text);
        Assert.DoesNotContain("GPU 空闲", text);
    }
}
