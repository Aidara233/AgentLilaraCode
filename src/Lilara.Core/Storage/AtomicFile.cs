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
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(tmp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 3)
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
