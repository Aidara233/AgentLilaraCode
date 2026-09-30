using System.Text;

namespace Lilara.Core.Persistence;

/// <summary>
/// 原子重写：先写 .tmp，再替换。
/// 崩溃在两步之间只会留下一个孤儿 .tmp，目标文件永远不是半截的。
/// </summary>
public static class AtomicFile
{
    public static void Write(string path, string content)
    {
        var dir = Path.GetDirectoryName(path)
                  ?? throw new ArgumentException($"路径没有目录部分: {path}", nameof(path));
        Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tmp, path, overwrite: true);
    }
}
