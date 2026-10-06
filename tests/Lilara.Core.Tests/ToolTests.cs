using Lilara.Core.Kernel;
using Lilara.Core.Session.Tools;
using Lilara.Core.Storage;

namespace Lilara.Core.Tests;

/// <summary>
/// 工具与句柄。⚠️ 这里最要紧的一组是**逃逸拒绝**——它是安全守卫，不是格式检查：
/// 模型给的路径是**不可信输入**，而 `../../../../etc/passwd` 会让工具读出工作目录之外。
/// </summary>
public class ToolTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "lilara-tool-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly SessionToolContext _ctx;

    public ToolTests()
    {
        _root = Path.Combine(_tmp, "work");
        Directory.CreateDirectory(_root);
        _ctx = new SessionToolContext(_root, new BlobStore(_tmp));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true);
    }

    private static ToolCall Call(string name, string args) => new("c1", name, args);

    // ---- 范围：逃逸拒绝（安全守卫）----

    /// <summary>
    /// ⚠️ **这一组钉的是判据的形状**：先 <c>GetFullPath</c> 把 <c>..</c> 解析掉**再判**，
    /// 而不是去数输入里有几个 <c>..</c>。变体越多越能证明它是**形状判定**而不是字符串匹配。
    /// </summary>
    [Theory]
    [InlineData("..")]                              // 上跳一层
    [InlineData("../")]                             // 上跳（尾分隔符）
    [InlineData("../outside.txt")]                  // 平级
    [InlineData("../../../../etc/passwd")]          // 多级
    [InlineData("a/../../outside.txt")]             // 中途上跳（不是前缀）
    [InlineData("a/b/../../../outside.txt")]        // 更绕
    [InlineData("..\\outside.txt")]                 // Windows 分隔符
    [InlineData("./../outside.txt")]                // 当前 + 上跳
    [InlineData("//server/share")]                  // UNC
    [InlineData("C:\\Windows\\System32")]           // 绝对路径
    [InlineData("/etc/passwd")]                     // 根相对
    [InlineData("")]                                // 空
    [InlineData("   ")]                             // 空白
    public void ResolvePath_RejectsEscapesOutOfTheWorkingDirectory(string bad)
    {
        Assert.Null(_ctx.ResolvePath(bad));
    }

    // ⚠️ 这一条是**判据里那个尾分隔符**的证据：少了它，`<root>-evil` 会被判成在 `<root>` 之下。
    // 这是「前缀匹配」最经典的那个洞，而它在这里是**安全**后果：工作目录的**兄弟目录**可读。
    [Fact]
    public void ResolvePath_RejectsASiblingWhoseNameStartsWithTheRoot()
    {
        var sibling = Path.Combine(_tmp, "work-evil");
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "secret.txt"), "不该读到");

        Assert.Null(_ctx.ResolvePath("../work-evil/secret.txt"));
    }

    [Theory]
    [InlineData("a.txt")]
    [InlineData("./a.txt")]
    [InlineData("sub/a.txt")]
    [InlineData("sub/deep/a.txt")]
    [InlineData("a/../a.txt")]                      // 上跳又回来 ⇒ 仍在范围内，合法
    public void ResolvePath_AcceptsPathsThatStayInside(string good)
    {
        var full = _ctx.ResolvePath(good);

        Assert.NotNull(full);
        Assert.StartsWith(_ctx.WorkingDirectory, full, StringComparison.OrdinalIgnoreCase);
    }

    // 工作目录**本身**是合法的（`ls` 省略 path 走的就是它）。
    [Fact]
    public void ResolvePath_AcceptsTheWorkingDirectoryItself()
    {
        Assert.Equal(_ctx.WorkingDirectory, _ctx.ResolvePath("."));
    }

    // ---- ls ----

    // ⚠️ **排序必须是显式的**：`Directory.EnumerateFileSystemEntries` 的顺序由文件系统决定
    // ⇒ 不排的话，工具的输出顺序会漂，而工具输出**进上下文**（每一步都可能不同的前缀）。
    // 这条用「创建顺序与序数序相反」来证伪纯偶然的巧合：`_z` 先建，但它必须排在 `a` 之后。
    [Fact]
    public async Task List_IsSortedOrdinally_NotInCreationOrder()
    {
        File.WriteAllText(Path.Combine(_root, "_z.txt"), "");
        File.WriteAllText(Path.Combine(_root, "a.txt"), "");
        Directory.CreateDirectory(Path.Combine(_root, "m"));

        var lines = await ListLines("{}");

        Assert.Equal(["_z.txt", "a.txt", "m/"], lines);
    }

    // ⚠️ 尾随斜杠是**目录的唯一标记**——文件名与目录名可以同名，
    // 而模型下一步该用 read 还是 ls 正是靠这个。
    [Fact]
    public async Task List_MarksDirectoriesWithATrailingSlash()
    {
        Directory.CreateDirectory(Path.Combine(_root, "dir"));

        Assert.Equal(["dir/"], await ListLines("{}"));
    }

    [Fact]
    public async Task List_OmittingPath_ListsTheWorkingDirectory()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "");

        Assert.Equal(["a.txt"], await ListLines("{}"));
        Assert.Equal(["a.txt"], await ListLines("""{"path":"."}"""));
    }

    [Fact]
    public async Task List_EmptyDirectory_SaysSoRatherThanReturningNothing()
    {
        Assert.Equal(["(空目录)"], await ListLines("{}"));
    }

    // 「你以为它是目录」是最常见的误用 ⇒ 结果里必须点名正确的那条路（read），
    // 只说「找不到」会把模型引向「路径写错了」。
    [Fact]
    public async Task List_OnAFile_PointsAtRead()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "x");

        var text = await RunText(new ListTool(), Call("ls", """{"path":"a.txt"}"""));

        Assert.Contains("read", text);
    }

    [Fact]
    public async Task List_EscapingPath_IsRefused_NotSilentlyEmpty()
    {
        var text = await RunText(new ListTool(), Call("ls", """{"path":".."}"""));

        Assert.Contains("工作目录之外", text);
    }

    // ---- read ----

    [Fact]
    public async Task Read_ReturnsTheFileText()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "你好\nworld");

        var outcome = await new ReadTool().RunAsync(Call("read", """{"path":"a.txt"}"""), _ctx, default);

        Assert.Equal("你好\nworld", Assert.IsType<Block.TextBlock>(Assert.Single(outcome.Content)).Text);
    }

    // 引用与指纹都落（`00` §5 三件里除快照外的两件）——它们是三态判定的输入。
    [Fact]
    public async Task Read_CarriesADependencyAndAFingerprint()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "x");

        var outcome = await new ReadTool().RunAsync(Call("read", """{"path":"a.txt"}"""), _ctx, default);

        Assert.Equal("a.txt", outcome.DependsOn);
        Assert.NotNull(outcome.Fingerprint);
    }

    /// <summary>
    /// ⚠️ **指纹里带路径，是为了堵那个著名的一致性洞**：只哈希内容的话，
    /// 「把 <c>a.txt</c> 删掉、再把另一个文件改名成 <c>a.txt</c>」会换出**同一个指纹**
    /// ⇒ 三态判定说「世界没动」⇒ 沿用旧快照，而旧快照记录的是**另一个文件**的语义。
    /// </summary>
    [Fact]
    public async Task Read_Fingerprint_DistinguishesSameContentAtDifferentPaths()
    {
        const string same = "一样的内容";
        File.WriteAllText(Path.Combine(_root, "a.txt"), same);
        File.WriteAllText(Path.Combine(_root, "b.txt"), same);

        var a = await new ReadTool().RunAsync(Call("read", """{"path":"a.txt"}"""), _ctx, default);
        var b = await new ReadTool().RunAsync(Call("read", """{"path":"b.txt"}"""), _ctx, default);

        Assert.NotEqual(a.Fingerprint, b.Fingerprint);
    }

    // 同路径同内容 ⇒ 同一个指纹（否则三态判定永远「不一致」，每一步都白重读）。
    [Fact]
    public async Task Read_Fingerprint_IsStableForUnchangedContent()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "x");

        var first = await new ReadTool().RunAsync(Call("read", """{"path":"a.txt"}"""), _ctx, default);
        var second = await new ReadTool().RunAsync(Call("read", """{"path":"a.txt"}"""), _ctx, default);

        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public async Task Read_Fingerprint_ChangesWithContent()
    {
        var f1 = await ReadFingerprint("v1");
        var f2 = await ReadFingerprint("v2");

        Assert.NotEqual(f1, f2);
    }

    // ⚠️ **坏编码不是错误，是内容**：GBK 源文件不该让整步失败，
    // 更不该让 `tool_use` / `tool_result` 只落一半（半个在拿到的不是少点内容，是上游 400）。
    [Fact]
    public async Task Read_WithInvalidUtf8_ReturnsReplacementCharactersInsteadOfThrowing()
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "gbk.txt"), [0xC4, 0xE3, 0xBA, 0xC3]);   // GBK 的「你好」

        var outcome = await new ReadTool().RunAsync(Call("read", """{"path":"gbk.txt"}"""), _ctx, default);

        // 不抛、且**有内容**（替换字符是看得见的失真）。
        Assert.NotEmpty(Assert.IsType<Block.TextBlock>(Assert.Single(outcome.Content)).Text);
    }

    // 四种「用错了」都必须是**说人话的结果**，而不是异常——异常会让配对的另一半落不下去。
    [Theory]
    [InlineData("{}")]                                  // 缺 path
    [InlineData("""{"path":""}""")]                     // 空 path
    [InlineData("""{"path":"../x"}""")]                 // 逃逸
    [InlineData("""{"path":"nope.txt"}""")]             // 不存在
    [InlineData("这不是 JSON")]                          // 参数根本不是 JSON
    [InlineData("""{"path":123}""")]                     // 类型不对
    public async Task Read_BadInput_IsAResultNotAnException(string args)
    {
        var outcome = await new ReadTool().RunAsync(Call("read", args), _ctx, default);

        Assert.NotEmpty(outcome.Content);
        Assert.Null(outcome.Fingerprint);   // 没读到东西 ⇒ 没有指纹（别编一个）
    }

    [Fact]
    public async Task Read_OnADirectory_PointsAtLs()
    {
        Directory.CreateDirectory(Path.Combine(_root, "d"));

        var text = await RunText(new ReadTool(), Call("read", """{"path":"d"}"""));

        Assert.Contains("ls", text);
    }

    // 两个工具的自述必须是**互指**的：`00` §2.2 说工具描述是唯一教模型的地方。
    [Fact]
    public void BothTools_PointAtEachOther()
    {
        Assert.Contains("read", new ListTool().Description);
        Assert.Contains("ls", new ReadTool().Description);
    }

    // ---- 辅助 ----

    private async Task<string[]> ListLines(string args) =>
        (await RunText(new ListTool(), Call("ls", args))).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private async Task<string> RunText(ITool tool, ToolCall call)
    {
        var outcome = await tool.RunAsync(call, _ctx, default);
        return Assert.IsType<Block.TextBlock>(Assert.Single(outcome.Content)).Text;
    }

    private async Task<string?> ReadFingerprint(string content)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), content);
        var outcome = await new ReadTool().RunAsync(Call("read", """{"path":"a.txt"}"""), _ctx, default);
        return outcome.Fingerprint;
    }
}
