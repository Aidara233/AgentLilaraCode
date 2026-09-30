using Lilara.Core.Hosting;

namespace Lilara.Core.Tests;

public class DataRootLockTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-lock-" + Guid.NewGuid().ToString("N"));

    public DataRootLockTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// ⚠️ **「一设备一核心」的真正守卫在数据根上，不在端口上**：
    /// 端口换个号就绕过了（`--port 7789`），而写路径依旧无锁。
    /// 两个核心共用一个数据根会互相破坏（这台机器上写路径连一把锁都没有）。
    /// </summary>
    [Fact]
    public void Acquire_SecondHolderOnTheSameRootFails()
    {
        using var first = DataRootLock.Acquire(_root);

        var ex = Assert.Throws<DataRootHeldException>(() => DataRootLock.Acquire(_root));

        // ⚠️ 错误信息要**说准**：不能说成「端口被占」——
        // 那会把用户引向「去杀另一个核心」，而其实可能是他自己开了两个。
        Assert.Contains(_root, ex.Message);
    }

    /// <summary>不同数据根互不影响——测试并行跑就靠这条。</summary>
    [Fact]
    public void Acquire_DifferentRootsBothSucceed()
    {
        var other = Path.Combine(Path.GetTempPath(), "lilara-lock2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(other);
        try
        {
            using var a = DataRootLock.Acquire(_root);
            using var b = DataRootLock.Acquire(other);
        }
        finally
        {
            if (Directory.Exists(other)) Directory.Delete(other, recursive: true);
        }
    }

    /// <summary>释放之后必须能再拿到——否则重启一次就把数据根永久锁死了。</summary>
    [Fact]
    public void Acquire_IsReusableAfterRelease()
    {
        using (DataRootLock.Acquire(_root)) { }

        using var again = DataRootLock.Acquire(_root);
    }
}
