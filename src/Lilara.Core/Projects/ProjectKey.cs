using System.Security.Cryptography;
using System.Text;

namespace Lilara.Core.Projects;

/// <summary>
/// projectKey = 工作目录规范化后的指纹（01 §8）。
/// Project 是涌现的、不是声明的：一个文件夹天然就是一个 project（01 §8.1）。
/// </summary>
public static class ProjectKey
{
    public static string FromPath(string workingDirectory)
    {
        var normalized = Path.GetFullPath(workingDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToLowerInvariant(); // Windows 下大小写不敏感（01 §12.1）

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(hash)[..16];
    }
}
