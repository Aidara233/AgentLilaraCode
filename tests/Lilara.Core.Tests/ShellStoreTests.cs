using Lilara.Core.Kernel;
using Lilara.Core.Shell;

namespace Lilara.Core.Tests;

/// <summary>
/// 配置只有一份，坏文件回默认值。⚠️ 与 `NetworkStoreTests` 的失败方向**刻意不同**：
/// 那边坏文件 ⇒ 关掉（因为失败方向是向外发数据），这边坏文件 ⇒ 默认那份（启用、自动探测）。
/// </summary>
public class ShellStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-shellcfg-" + Guid.NewGuid().ToString("N"));

    public ShellStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Load_MissingFile_ReturnsEnabledDefaults()
    {
        var cfg = new ShellStore(_root).Load();

        Assert.True(cfg.Enabled);
        Assert.Null(cfg.Executable);
        Assert.Equal(ShellLimits.DefaultTimeoutSeconds, cfg.TimeoutSeconds);
    }

    [Fact]
    public void SaveLoad_RoundTrips()
    {
        var store = new ShellStore(_root);
        store.Save(new ShellConfig
        {
            Enabled = false, Executable = @"D:\Tools\sh.exe", TimeoutSeconds = 45,
        });

        var cfg = store.Load();

        Assert.False(cfg.Enabled);
        Assert.Equal(@"D:\Tools\sh.exe", cfg.Executable);
        Assert.Equal(45, cfg.TimeoutSeconds);
    }

    [Fact]
    public void Load_CorruptFile_FallsBackToDefaults_InsteadOfThrowing()
    {
        File.WriteAllText(Path.Combine(_root, "shell.json"), "{ not json");

        var cfg = new ShellStore(_root).Load();

        Assert.True(cfg.Enabled);
        Assert.Null(cfg.Executable);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_BlankExecutable_MeansAutoDetect(string raw)
    {
        Assert.Null(ShellStore.Normalize(new ShellConfig { Executable = raw }).Executable);
    }

    [Theory]
    [InlineData(0, ShellLimits.MinTimeoutSeconds)]
    [InlineData(99_999, ShellLimits.MaxTimeoutSeconds)]
    public void Normalize_ClampsTimeout(int raw, int expected)
    {
        Assert.Equal(expected, ShellStore.Normalize(new ShellConfig { TimeoutSeconds = raw }).TimeoutSeconds);
    }
}
