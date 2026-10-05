namespace Lilara.Core.Hosting;

/// <summary>数据根已被另一个核心持有。</summary>
public sealed class DataRootHeldException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// 「一设备一核心」的**真正**守卫。
///
/// **它保护的是数据根，不是端口。** 拿端口当单例守卫是错的：
/// 换个端口号就绕过去了，而**写路径依旧无锁**——两个核心共用一个数据根会互相破坏
/// （这个代码库的写路径一把锁都没有）。端口那条检查是**独立的另一条**，两条各报各的。
///
/// 实现：以 <see cref="FileShare.None"/> 独占打开 <c>&lt;dataRoot&gt;/.lilara.lock</c>。
/// 进程崩溃时句柄随之关闭，锁**自动释放**——不需要陈锁清理逻辑。
/// </summary>
public static class DataRootLock
{
    public const string FileName = ".lilara.lock";

    public static IDisposable Acquire(string dataRoot)
    {
        Directory.CreateDirectory(dataRoot);
        var path = Path.Combine(dataRoot, FileName);

        try
        {
            var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new Held(fs);
        }
        catch (IOException ex)
        {
            // 这不是端口问题——说成「端口被占」会把用户引向「去杀另一个核心」，
            // 而其实可能是他自己开了两个，或者上一次没退干净。
            throw new DataRootHeldException(
                $"数据根 '{dataRoot}' 已被另一个核心持有（{FileName} 被独占打开）。"
                + "一个数据根同时只能有一个核心——写路径没有跨进程锁。"
                + "若确认没有别的核心在跑，删掉该文件即可（进程崩溃时它会自动释放）。", ex);
        }
    }

    private sealed class Held(FileStream fs) : IDisposable
    {
        private FileStream? _fs = fs;

        public void Dispose()
        {
            _fs?.Dispose();
            _fs = null;
        }
    }
}
