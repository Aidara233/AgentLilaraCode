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
        // ⚠️ **这个数不是判据，是探针**——它抓的是「glob 悄悄少找了文件」（搬走一个 Kernel 文件、
        // 或路径变了）。**加文件时按实测改，别按感觉改**：被机械改过一次的探针就等于没有。
        // 15 → 16（2026-10-06 最小工具批）：加了 `Tool.cs`（工具契约的词汇层）。
        // 16 → 17（2026-10-07 预设批）：加了 `CorePrompts.cs`（核心自带的那份系统提示词模板）。
        // 17 → 18（2026-10-08 询问批）：加了 `Ask.cs`（问用户的词汇层：Kind/Shape/AskRequest）。
        Assert.Equal(18, files.Length);

        var offenders = files
            .SelectMany(f => ExternalUsings(f).Select(u => $"{Path.GetFileName(f)}:{u.Line}  {u.Text}"))
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// 取时间只许走 <see cref="CoreClock"/>。
    ///
    /// <para>⚠️ <b>它挡的不是风格，是一条具体的坑。</b>散落的 <c>DateTimeOffset.UtcNow</c>
    /// 让「新增字段的默认值是此刻」这种规则**无处可立**——反序列化不经过构造器，
    /// 于是缺省到底取不取「现在」根本没有单一落点。先把入口收成一处，条款才谈得上。</para>
    ///
    /// <para>⚠️ <b>它只扫代码行，不扫注释</b>（每行 `//` 之后的部分先切掉）。
    /// 不切的话，别的文件里一句「别用 <c>DateTimeOffset.UtcNow</c>」就会**误报**，
    /// 而误报的守卫最后一定会被改成永远通过。</para>
    ///
    /// <para>⚠️ <b>它拦不住什么</b>：字符串拼出来的调用、<c>DateTime.Today</c>、
    /// 以及 <c>new DateTime(...)</c> 这类构造。它管的是**最常见的那个形状**——
    /// 与本仓其它守卫一样，这是有用的近似，不是证明。</para>
    /// </summary>
    [Fact]
    public void Clock_IsOnlyReadThroughCoreClock()
    {
        var clockFile = Path.Combine("Kernel", "CoreClock.cs");
        var root = Path.Combine(RepoRoot(), "src");

        // 判据是**源码文本**，不是运行时——所以这里不碰任何被测物，不存在「探针≠被测物」的问题。
        var forbidden = new[] { "DateTime.UtcNow", "DateTimeOffset.UtcNow", "DateTime.Now", "DateTimeOffset.Now" };

        var offenders = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(root, f).EndsWith(clockFile, StringComparison.Ordinal))
            .SelectMany(f => CodeLines(f)
                .Where(l => forbidden.Any(t => l.Text.Contains(t, StringComparison.Ordinal)))
                .Select(l => $"{Path.GetRelativePath(root, f)}:{l.Line}  {l.Text.Trim()}"))
            .ToList();

        Assert.Empty(offenders);
    }

    // 行号必须来自**原始文件**（切注释会吃掉 `//` 本身，但不改变行数，所以行号仍然对得上）。
    private static IEnumerable<(int Line, string Text)> CodeLines(string file) =>
        File.ReadLines(file).Select((raw, n) =>
        {
            var cut = raw.IndexOf("//", StringComparison.Ordinal);
            return (Line: n + 1, Text: cut < 0 ? raw : raw[..cut]);
        });

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

    private static string LayerDir(string layer) => Path.Combine(RepoRoot(), "src", "Lilara.Core", layer);

    // 从测试程序集往上找仓库根。
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
               && !Directory.Exists(Path.Combine(dir.FullName, "src", "Lilara.Core", "Kernel")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);   // 找不到仓库根 = 测试环境不对，响亮地失败，别静默跳过
        return dir!.FullName;
    }
}
