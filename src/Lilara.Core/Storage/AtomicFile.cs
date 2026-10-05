using System.Text;

namespace Lilara.Core.Storage;

// 原子重写：先写 .tmp，再替换——崩溃在两步之间只留一个孤儿 .tmp，目标文件永不半截。
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void Write(string path, string content)
    {
        var dir = Path.GetDirectoryName(path)
                  ?? throw new ArgumentException($"路径没有目录部分: {path}", nameof(path));
        Directory.CreateDirectory(dir);

        // tmp 名必须唯一：两个并发写共用同一个 tmp 时，第二个 Move 会抛。
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tmp, content, Utf8NoBom);

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
