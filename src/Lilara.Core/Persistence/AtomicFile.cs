using System.Text;

namespace Lilara.Core.Persistence;

/// <summary>
/// 原子重写：先写 .tmp，再替换。
/// 崩溃在两步之间只会留下一个孤儿 .tmp，目标文件永远不是半截的。
/// </summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void Write(string path, string content)
    {
        var dir = Path.GetDirectoryName(path)
                  ?? throw new ArgumentException($"路径没有目录部分: {path}", nameof(path));
        Directory.CreateDirectory(dir);

        // ⚠️ tmp 名**必须唯一**：旧实现用固定的 `path + ".tmp"`，两个并发写会写同一个 tmp
        // 再各自 Move，第二个 Move 抛（实测过）。真实时延：`SaveState` 与 HTTP 快照端点
        // 同时碰同一个 state.json。
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tmp, content, Utf8NoBom);

        // ⚠️ Windows 上目标被别的句柄占着时，`File.Move` 抛的**可能是两种异常**：
        //   · `IOException`（sharing violation：对方开了句柄但带了共享）
        //   · `UnauthorizedAccessException`（access denied：对方没带 FILE_SHARE_DELETE）
        // **只接 `IOException` 是不够的**——这条由并发测试实测抓出来（我第一版只接了它）。
        // 两者在这个场景下都是**暂时**的，短重试即可；仍失败就清 tmp 再抛。
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
