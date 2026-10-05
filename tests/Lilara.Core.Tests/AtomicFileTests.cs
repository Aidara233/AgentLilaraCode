using System.Text;
using Lilara.Core.Persistence;

namespace Lilara.Core.Tests;

public class AtomicFileTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "lilara-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Write_CreatesMissingDirectories()
    {
        var path = Path.Combine(_dir, "nested", "deep", "state.json");

        AtomicFile.Write(path, "{}");

        Assert.Equal("{}", File.ReadAllText(path));
    }

    [Fact]
    public void Write_LeavesNoTempFileBehind()
    {
        var path = Path.Combine(_dir, "state.json");

        AtomicFile.Write(path, "{}");

        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal("state.json", Path.GetFileName(Assert.Single(Directory.GetFiles(_dir))));
    }

    [Fact]
    public void Write_WritesUtf8WithoutBom()
    {
        var path = Path.Combine(_dir, "state.json");

        AtomicFile.Write(path, "你好");

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(Encoding.UTF8.GetBytes("你好"), bytes);
        Assert.NotEqual(0xEF, bytes[0]);
    }

    [Fact]
    public void Write_OverwritesExistingContent()
    {
        var path = Path.Combine(_dir, "state.json");
        AtomicFile.Write(path, "old");

        AtomicFile.Write(path, "new");

        Assert.Equal("new", File.ReadAllText(path));
    }
}

public class AtomicFileConcurrencyTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "lilara-atomic-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // 并发写同一路径是真实场景（SaveState 与 HTTP 快照端点同时碰 state.json）。
    [Fact]
    public async Task Write_ToleratesConcurrentWritersToTheSamePath()
    {
        var target = Path.Combine(_dir, "state.json");
        var errors = new List<Exception>();

        static Task Hammer(int offset, string target, List<Exception> errors) => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                try { AtomicFile.Write(target, $"{{\"n\":{i + offset}}}"); }
                catch (Exception e) { lock (errors) errors.Add(e); }
            }
        });

        await Task.WhenAll(Hammer(0, target, errors), Hammer(1000, target, errors));

        Assert.Empty(errors);
        Assert.True(File.Exists(target));
        // 不留残渣：失败未清的 tmp 会在磁盘上越积越多。
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }
}
