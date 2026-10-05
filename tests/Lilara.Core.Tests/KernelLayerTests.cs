namespace Lilara.Core.Tests;

// Kernel/ 是词汇层：只回答「东西是什么」，对 Lilara.Core 的依赖必须为零。
// ⚠️ 这条测试是在替编译器守卫——单程序集下分层只是惯例，没人守就会漂（这个仓库已经漂过一次：
// Protocol / ReasoningPolicy / Claims / SessionSpec 都曾住在别的层）。
public class KernelLayerTests
{
    [Fact]
    public void Kernel_DoesNotDependOnAnyOtherLayer()
    {
        var files = Directory.GetFiles(KernelDir(), "*.cs", SearchOption.AllDirectories);

        // 先钉「找得到东西」：glob 若空了，下面那条空断言会**假绿**。
        Assert.Equal(10, files.Length);

        // 「依赖」= 任何 `using Lilara.Core.X`（X ≠ Kernel）。`using Lilara.Core.Kernel;` 是自己人，放行。
        var offenders = files
            .SelectMany(f => File.ReadLines(f)
                .Select((line, n) => (Text: line.Trim(), Number: n + 1))
                .Where(x => x.Text.StartsWith("using Lilara.Core.", StringComparison.Ordinal))
                .Where(x => x.Text != "using Lilara.Core.Kernel;")
                .Select(x => $"{Path.GetFileName(f)}:{x.Number}  {x.Text}"))
            .ToList();

        Assert.Empty(offenders);
    }

    private static string KernelDir()
    {
        // 从测试程序集往上找仓库根（含 src/Lilara.Core/Kernel）。
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
               && !Directory.Exists(Path.Combine(dir.FullName, "src", "Lilara.Core", "Kernel")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);   // 找不到仓库根 = 测试环境不对，响亮地失败，别静默跳过
        return Path.Combine(dir!.FullName, "src", "Lilara.Core", "Kernel");
    }
}
