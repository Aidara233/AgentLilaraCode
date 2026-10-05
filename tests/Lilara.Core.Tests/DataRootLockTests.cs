using Lilara.Core.Storage;

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

    // 「一设备一核心」的真正守卫在数据根上，不在端口上。
    [Fact]
    public void Acquire_SecondHolderOnTheSameRootFails()
    {
        using var first = DataRootLock.Acquire(_root);

        var ex = Assert.Throws<DataRootHeldException>(() => DataRootLock.Acquire(_root));

        // 错误信息要点名数据根，不能说成「端口被占」。
        Assert.Contains(_root, ex.Message);
    }

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

    [Fact]
    public void Acquire_IsReusableAfterRelease()
    {
        using (DataRootLock.Acquire(_root)) { }

        using var again = DataRootLock.Acquire(_root);
    }
}
