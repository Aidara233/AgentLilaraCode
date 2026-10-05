namespace Lilara.Core.Tests;

// 分层守卫：单程序集下分层只是惯例，没人守就会漂
// （这个仓库漂过一次——Protocol / ReasoningPolicy / Claims / SessionSpec 都曾住错层）。
//
// 两层断言：
//   ① Kernel/ 对 Lilara.Core 的依赖为零（词汇层不依赖任何人）
//   ② 任何一层的 `using Lilara.Core.X` 都必须指向**它下面**的层，不许回头依赖上层
public class ArchitectureTests
{
    // 底 → 顶。数值越大越靠上；一条 using 的目标层必须**严格低于**发出它的那一层。
    private static readonly string[] LayerOrder =
        ["Kernel", "Storage", "Providers", "Protocol", "Session", "Hosting", "Control", "Console"];

    [Fact]
    public void Kernel_DoesNotDependOnAnyOtherLayer()
    {
        var files = Directory.GetFiles(LayerDir("Kernel"), "*.cs", SearchOption.AllDirectories);

        // 先钉「找得到东西」：glob 若空了，下面的空断言会**假绿**。
        Assert.Equal(12, files.Length);

        var offenders = files
            .SelectMany(f => ExternalUsings(f).Select(u => $"{Path.GetFileName(f)}:{u.Line}  {u.Text}"))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void NoLayer_DependsOnALayerAboveIt()
    {
        var offenders = new List<string>();

        foreach (var layer in LayerOrder)
        {
            foreach (var file in Directory.GetFiles(LayerDir(layer), "*.cs", SearchOption.AllDirectories))
            {
                foreach (var (line, text) in ExternalUsings(file))
                {
                    var target = TargetLayer(text);
                    // 允许「严格低于本层」与「同层」（同层含子命名空间，如 Adapters 引 Adapters.Claude）。
                    // 只有**严格高于**本层才算回头依赖。
                    if (target is null || LayerIndex(target) <= LayerIndex(layer)) continue;

                    offenders.Add($"{layer}/{Path.GetFileName(file)}:{line}  {text}"
                        + $"   ← 依赖了平级/上层的 {target}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    private static int LayerIndex(string name) => Array.IndexOf(LayerOrder, name);

    // `using Lilara.Core.Hosting.Services;` → "Hosting"
    private static string? TargetLayer(string usingLine)
    {
        var rest = usingLine["using Lilara.Core.".Length..].TrimEnd(';');
        var head = rest.Split('.')[0];
        return LayerOrder.Contains(head) ? head : null;   // 不是层名（如 Tests）⇒ 不管
    }

    // 该文件里所有 `using Lilara.Core.X`——`using Lilara.Core.Kernel;`（自己人）由调用方按层判
    private static IEnumerable<(int Line, string Text)> ExternalUsings(string file) =>
        File.ReadLines(file)
            .Select((raw, n) => (Text: raw.Trim(), Line: n + 1))
            .Where(x => x.Text.StartsWith("using Lilara.Core.", StringComparison.Ordinal))
            .Where(x => x.Text != "using Lilara.Core.Kernel;")   // Kernel 是所有人的底，放行
            .Select(x => (x.Line, x.Text));

    private static string LayerDir(string layer)
    {
        // 从测试程序集往上找仓库根。
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
               && !Directory.Exists(Path.Combine(dir.FullName, "src", "Lilara.Core", "Kernel")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);   // 找不到仓库根 = 测试环境不对，响亮地失败，别静默跳过
        return Path.Combine(dir!.FullName, "src", "Lilara.Core", layer);
    }
}
