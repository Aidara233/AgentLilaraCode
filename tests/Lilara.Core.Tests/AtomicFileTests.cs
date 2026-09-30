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
