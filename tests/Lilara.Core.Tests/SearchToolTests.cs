using Lilara.Core.Kernel;
using Lilara.Core.Session.Tools;
using Lilara.Core.Storage;

namespace Lilara.Core.Tests;

/// <summary>
/// `search` 的两条路：内容（`路径:行号: 文本`）与文件名（目录带尾随斜杠）。
/// ⚠️ 这里最要紧的是三条判据：**越界拒绝**、**跳过的事必须说**、**截断必须说**
/// ——它们都来自同一处「宁可明着少，不可暗着假」。
/// </summary>
public class SearchToolTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "lilara-search-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly SessionToolContext _ctx;

    public SearchToolTests()
    {
        _root = Path.Combine(_tmp, "work");
        Directory.CreateDirectory(_root);
        _ctx = new SessionToolContext(_root, new BlobStore(_tmp));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true);
    }

    // ---- content ----

    [Fact]
    public async Task Content_FindsMatchingLines_WithPathAndLineNumber()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "alpha\nBeta\ngamma");
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        await File.WriteAllTextAsync(Path.Combine(_root, "sub", "b.txt"), "beta line");

        var text = await Run("""{"pattern":"beta"}""");

        // 不区分大小写；行号是 1 基；路径统一正斜杠。
        Assert.Contains("a.txt:2: Beta", text);
        Assert.Contains("sub/b.txt:1: beta line", text);
    }

    [Fact]
    public async Task Content_OnAFile_SearchesThatFileOnly()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "needle");
        await File.WriteAllTextAsync(Path.Combine(_root, "b.txt"), "needle");

        var text = await Run("""{"pattern":"needle","path":"a.txt"}""");

        Assert.Contains("a.txt:1: needle", text);
        Assert.DoesNotContain("b.txt", text);
    }

    [Fact]
    public async Task Content_NoMatch_SaysSoRatherThanReturningNothing()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "hello");

        var text = await Run("""{"pattern":"absent"}""");

        Assert.Contains("没有找到", text);
        Assert.Contains("absent", text);
    }

    [Fact]
    public async Task Content_SkipsVersionControlAndBuildDirectories()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "ok.txt"), "needle");
        foreach (var dir in new[] { ".git", "bin", "obj", "node_modules" })
        {
            Directory.CreateDirectory(Path.Combine(_root, dir));
            await File.WriteAllTextAsync(Path.Combine(_root, dir, "noise.txt"), "needle");
        }

        var text = await Run("""{"pattern":"needle"}""");

        Assert.Contains("ok.txt:1: needle", text);
        Assert.DoesNotContain("noise.txt", text);
    }

    [Fact]
    public async Task Content_SkipsBinaryFiles_AndSaysSo()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "ok.txt"), "needle");
        // NUL 是二进制判据（不是扩展名）。
        await File.WriteAllBytesAsync(Path.Combine(_root, "blob.dat"), [0x6E, 0x65, 0x65, 0x64, 0x6C, 0x65, 0x00, 0x00]);

        var text = await Run("""{"pattern":"needle"}""");

        Assert.Contains("ok.txt", text);
        Assert.Contains("二进制", text);
    }

    [Fact]
    public async Task Content_TruncationIsExplicit()
    {
        var lines = string.Join('\n', Enumerable.Repeat("needle", ToolLimits.MaxSearchResults + 5));
        await File.WriteAllTextAsync(Path.Combine(_root, "many.txt"), lines);

        var text = await Run("""{"pattern":"needle"}""");

        Assert.Equal(ToolLimits.MaxSearchResults,
            text.Split('\n').Count(l => l.StartsWith("many.txt:", StringComparison.Ordinal)));
        Assert.Contains("结果已截断", text);
    }

    // ---- filename ----

    [Fact]
    public async Task Filename_FindsFilesAndDirectories_CaseInsensitively()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "Stepper.cs"), "");
        Directory.CreateDirectory(Path.Combine(_root, "StepperDir"));
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        await File.WriteAllTextAsync(Path.Combine(_root, "sub", "stepper2.txt"), "");
        await File.WriteAllTextAsync(Path.Combine(_root, "other.txt"), "");

        var text = await Run("""{"pattern":"stepper","mode":"filename"}""");

        Assert.Contains("Stepper.cs", text);
        Assert.Contains("StepperDir/", text);
        Assert.Contains("sub/stepper2.txt", text);
        Assert.DoesNotContain("other.txt", text);
    }

    [Fact]
    public async Task Filename_NoMatch_SaysSoRatherThanReturningNothing()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "");

        var text = await Run("""{"pattern":"zzz","mode":"filename"}""");

        Assert.Contains("没有找到", text);
    }

    // ---- 闸门与坏参数 ----

    [Fact]
    public async Task EscapingPath_IsRefused()
    {
        var text = await Run("""{"pattern":"x","path":".."}""");

        Assert.Contains("工作目录之外", text);
    }

    [Theory]
    [InlineData("{}")]                                             // 缺 pattern
    [InlineData("""{"pattern":""}""")]                              // 空 pattern
    [InlineData("""{"pattern":123}""")]                             // 类型不对
    [InlineData("""{"pattern":"x","mode":"sideways"}""")]           // mode 不在枚举里
    [InlineData("""{"pattern":"x","path":"nope"}""")]               // 路径不存在
    [InlineData("这不是 JSON")]                                      // 整段不是 JSON
    public async Task BadInput_IsAResultNotAnException(string args)
    {
        var outcome = await new SearchTool().RunAsync(new ToolCall("c1", "search", args), _ctx, default);

        Assert.NotEmpty(Assert.IsType<Block.TextBlock>(Assert.Single(outcome.Content)).Text);
    }

    [Fact]
    public void BuiltIn_IncludesSearch()
    {
        Assert.Contains(BuiltInTools.All, t => t is SearchTool && t.Name == "search");
    }

    // ---- 辅助 ----

    private async Task<string> Run(string args)
    {
        var outcome = await new SearchTool().RunAsync(new ToolCall("c1", "search", args), _ctx, default);
        return Assert.IsType<Block.TextBlock>(Assert.Single(outcome.Content)).Text;
    }
}
