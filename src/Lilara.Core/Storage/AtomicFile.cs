using System.Text;

namespace Lilara.Core.Storage;

// 原子重写：先写 .tmp，再替换——崩溃在两步之间只留一个孤儿 .tmp，目标文件永不半截。
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void Write(string path, string content) =>
        WriteCore(path, tmp => File.WriteAllText(tmp, content, Utf8NoBom));

    /// <summary>
    /// 写**字节**。与 <see cref="Write(string, string)"/> 同一套原子性与重试——
    /// 抽出来是为了**不把那段重试抄第二遍**（抄了就会各自漂：一处改 3 次重试、另一处忘）。
    ///
    /// <para>⚠️ **两处都只 flush 到 OS，没有 fsync**（`File.WriteAllText` / `WriteAllBytes`
    /// 都不到盘）。这是**已知的 L0 耐久缺口**，不是这里忘了：`history.jsonl` 一样。
    /// 判据与范围见 `01` §8.0；修的时候要**一起**修，别只修一条路（那样更难查）。</para>
    /// </summary>
    public static void WriteBytes(string path, byte[] content) =>
        WriteCore(path, tmp => File.WriteAllBytes(tmp, content));

    private static void WriteCore(string path, Action<string> writeTmp)
    {
        var dir = Path.GetDirectoryName(path)
                  ?? throw new ArgumentException($"路径没有目录部分: {path}", nameof(path));
        Directory.CreateDirectory(dir);

        // tmp 名必须唯一：两个并发写共用同一个 tmp 时，第二个 Move 会抛。
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        writeTmp(tmp);

        // Windows 上目标被别的句柄占着时，File.Move 可能抛 IOException 或
        // UnauthorizedAccessException（取决于对方有没有带 FILE_SHARE_DELETE）——都是暂时的。
        //
        // ⚠️ **重试预算从「3 次 / 10-30ms」提到「10 次 / 10-100ms」**（2026-10-06，实测）：
        // 3 次那会儿总预算只有 60ms，而 `AtomicFileConcurrencyTests` 是**两个线程各写 100 次
        // 同一个文件**——机器一忙（尤其全量测试里同时跑着一堆并发测试时）就会超预算，
        // 于是它**间歇性失败**（实测：全量跑红过一次，单独跑 5 次全绿）。
        // ⚠️ **没有弱化那条测试**：它断言的就是「并发写不抛」，而那是这个类**该**有的性质
        // ——该改的是预算不够，不是断言太严（「误报的守卫最后一定会被改成永远通过」的反面）。
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(tmp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 10)
            {
                Thread.Sleep(10 * (attempt + 1));
            }
            catch
            {
                try { File.Delete(tmp); } catch (IOException) { /* 清不掉也不能盖住原异常 */ }
                throw;
            }
        }
    }
}
