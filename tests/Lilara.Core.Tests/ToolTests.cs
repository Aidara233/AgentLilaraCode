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

        // ⚠️ 每行是 `名字<TAB>大小`（`01` §9.3 的元数据列）。比较**名字那一格**，
        // 因为大小是另一件事（由 `List_FilesCarryTheirSize_*` 钉）。
        Assert.Equal(["_z.txt", "a.txt", "m/"], lines.Select(l => l.Split('\t')[0]));
    }

    // ⚠️ 尾随斜杠是**目录的唯一标记**——文件名与目录名可以同名，
    // 而模型下一步该用 read 还是 ls 正是靠这个。
    [Fact]
    public async Task List_MarksDirectoriesWithATrailingSlash()
    {
        Directory.CreateDirectory(Path.Combine(_root, "dir"));

        Assert.Equal(["dir/"], (await ListLines("{}")).Select(l => l.Split('\t')[0]));
    }

    [Fact]
    public async Task List_OmittingPath_ListsTheWorkingDirectory()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "");

        Assert.Equal(["a.txt"], (await ListLines("{}")).Select(l => l.Split('\t')[0]));
        Assert.Equal(["a.txt"], (await ListLines("""{"path":"."}""")).Select(l => l.Split('\t')[0]));
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

    // ---- read：offset / limit / 上限（`01` §9.3）--------------------------------
    //
    // ⚠️ **框架在 `outcome.Framing` 上，不在 `Content` 里**（`01` §3.7）：
    // 工具负责**说清这次调用返回了什么**（落盘的事实），而把框架**渲染成一个块**
    // 是 `TimelineRender` 的事（渲染期）。⇒ 这一组断言的是 `outcome.Framing`；
    // 「框架渲染成独立一块、恒定在内容之前」由 `TimelineRenderTests` 钉。

    private static ToolFraming Frame(ToolOutcome o) =>
        Assert.IsType<ToolFraming>(o.Framing);

    private static string Body(ToolOutcome o) =>
        Assert.IsType<Block.TextBlock>(Assert.Single(o.Content)).Text;

    /// <summary>
    /// ⚠️ **单次上限真的生效**，而且它从 `ToolLimits` 读（不是本文件里另一个数）。
    /// </summary>
    [Fact]
    public async Task Read_WithoutLimit_IsCappedAtTheLimit()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "big.txt"), new string('x', ToolLimits.MaxResultBytes + 500));

        var outcome = await new ReadTool().RunAsync(Call("read", """{"path":"big.txt"}"""), _ctx, default);

        Assert.Equal(ToolLimits.MaxResultBytes, Body(outcome).Length);
    }

    /// <summary>
    /// ⚠️ **宁可明着少，不可暗着假**（`01` §9.3）：被截断这件事必须**说清楚**，
    /// 否则模型会把「前一段」当成全文——那是「多了一条假信息」。
    /// 范围由**框架**承载：工具自己的那一半。
    /// </summary>
    [Fact]
    public async Task Read_ReportsItsRangeInTheFrame()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "big.txt"), new string('x', ToolLimits.MaxResultBytes + 500));

        var outcome = await new ReadTool().RunAsync(Call("read", """{"path":"big.txt"}"""), _ctx, default);

        var frame = Frame(outcome).Render();
        Assert.Contains("big.txt", frame);
        Assert.Contains((ToolLimits.MaxResultBytes + 500).ToString(), frame);   // 总长在（模型据此知道还没读完）
    }

    [Fact]
    public async Task Read_WithOffset_ReturnsTheTail()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "0123456789");

        var outcome = await new ReadTool().RunAsync(Call("read", """{"path":"a.txt","offset":4}"""), _ctx, default);

        Assert.Equal("456789", Body(outcome));
    }

    [Fact]
    public async Task Read_WithOffsetAndLimit_ReturnsTheSlice()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "0123456789");

        var outcome = await new ReadTool().RunAsync(
            Call("read", """{"path":"a.txt","offset":2,"limit":3}"""), _ctx, default);

        Assert.Equal("234", Body(outcome));
    }

    /// <summary>读到文件尾之后不是「空结果」——空结果会让模型以为那次调用没有输出。</summary>
    [Fact]
    public async Task Read_WithOffsetPastTheEnd_SaysSo()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "short");

        var outcome = await new ReadTool().RunAsync(Call("read", """{"path":"a.txt","offset":99}"""), _ctx, default);

        var text = Body(outcome);
        Assert.Contains("99", text);
        Assert.Contains("5", text);   // 总长也要给出来
    }

    /// <summary>
    /// ⚠️ **四种用错了必须是说人话的结果，不是异常**——异常会让配对的另一半落不下去
    /// （`01` §3.6 ②：半个在拿到的不是少点内容，是**上游 400**）。
    /// ⚠️ 尤其 `level:"4"`（字符串）那条：**不替模型猜**，否则一次写错的调用
    /// 会变成一次**看着正常**的读取。
    /// </summary>
    [Theory]
    [InlineData("""{"path":"a.txt","offset":-1}""")]
    [InlineData("""{"path":"a.txt","limit":-1}""")]
    [InlineData("""{"path":"a.txt","limit":0}""")]
    [InlineData("""{"path":"a.txt","offset":"4"}""")]
    [InlineData("""{"path":"a.txt","offset":1.5}""")]
    public async Task Read_BadOffsetOrLimit_IsAResultNotAnException(string args)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "0123456789");

        var outcome = await new ReadTool().RunAsync(Call("read", args), _ctx, default);

        Assert.NotEmpty(Body(outcome));
    }

    /// <summary>
    /// ⚠️ **切点不许落在多字节字符中间**（`01` §9.3 的 Review Focus 1）：
    /// 偏移是**字节**，而结果要是**文本**。不对齐的后果不是抛异常
    /// （解码器会把非法序列换成替换字符），而是**首尾多出一个 U+FFFD**——
    /// 那是**看得见的噪声**，模型可能把它当成「文件里真有个怪字符」。
    /// </summary>
    [Fact]
    public async Task Read_SplittingAMultiByteChar_DoesNotEmitAReplacementChar()
    {
        // 「你好世界」每字 3 字节。offset=1 落在第一个字的**中间**。
        await File.WriteAllTextAsync(Path.Combine(_root, "cn.txt"), "你好世界");

        var outcome = await new ReadTool().RunAsync(Call("read", """{"path":"cn.txt","offset":1}"""), _ctx, default);

        var text = Body(outcome);
        Assert.DoesNotContain('\uFFFD', text);
        Assert.Equal("好世界", text);      // 从第一个**完整**的字开始
    }


    /// <summary>
    /// ⚠️ **切点不许落在多字节字符中间**（`01` §9.3 的 Review Focus 1）：
    /// 偏移是**字节**，而结果要是**文本**。不对齐的后果不是抛异常
    /// （解码器会把非法序列换成替换字符），而是**首尾多出一个 U+FFFD**——
    /// 那是**看得见的噪声**，模型可能把它当成「文件里真有个怪字符」。
    ///
    /// <para>⚠️ <b>对齐的方向是「往回切」，不是「往前读」</b>：<c>limit</c> 说的是
    /// **最多**读多少字节 ⇒ 多读一个字符就越过了它许的额度。
    /// 所以 <c>limit=4</c> 得到的是「你」（3 字节），<b>不是</b>「你好」（6 字节）——
    /// 后者会让「最多 4 字节」这句话变成假的。</para>
    /// </summary>
    [Fact]
    public async Task Read_LimitStoppingMidCharacter_CutsBackInsteadOfOverreading()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "cn.txt"), "你好世界");

        // limit=4 落在第二个字（「好」= 字节 3..5）的中间 ⇒ 只留完整的「你」。
        var outcome = await new ReadTool().RunAsync(
            Call("read", """{"path":"cn.txt","limit":4}"""), _ctx, default);

        var text = Body(outcome);
        Assert.DoesNotContain('�', text);
        Assert.Equal("你", text);
    }
    /// <summary>
    /// ⚠️ **上限不能被 `limit` 绕过**——那是这个功能存在的**全部意义**：
    /// 模型（或一次写错的调用）给一个巨大的 `limit`，不该把上下文撑爆。
    /// </summary>
    [Fact]
    public async Task Read_WithAHugeLimit_IsStillCapped()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "big.txt"), new string('x', ToolLimits.MaxResultBytes * 3));

        var outcome = await new ReadTool().RunAsync(
            Call("read", $$"""{"path":"big.txt","limit":{{ToolLimits.MaxResultBytes * 3}}}"""), _ctx, default);

        Assert.Equal(ToolLimits.MaxResultBytes, Body(outcome).Length);
    }

    // ---- ls：元数据（`01` §9.3）------------------------------------------------
    //
    // ⚠️ **行的形状是「tab 分列」**，不是空格对齐：文件名**可以含空格**，
    // 用空格分列会让带空格的名字不可解析——而那是"框架与内容不许共用字面量"
    // 那条判据（G18）在列这一层的同一个形状。
    // ⚠️ 列挂在**内容**上。表头（`[ls src 前 50 条，共 1240]`）将来归**框架**
    // （那一步要等 `ls` 学会产出 `Framing`，本 T 不做）。

    private static string[] Cells(string line) => line.Split('\t');

    /// <summary>文件带大小；**目录不带**（目录没有"大小"这件事，给 0 是在撒谎）。</summary>
    [Fact]
    public async Task List_FilesCarryTheirSize_DirectoriesDoNot()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), new string('x', 12));
        Directory.CreateDirectory(Path.Combine(_root, "dir"));

        var a = (await ListLines("{}")).Single(l => l.Contains("a.txt"));
        var d = (await ListLines("{}")).Single(l => l.Contains("dir"));

        Assert.Equal(["a.txt", "12 B"], Cells(a)[..2]);
        Assert.Equal("dir/", Cells(d)[0]);
        Assert.Equal("", Cells(d)[1]);       // 大小那一格是空的
    }

    /// <summary>
    /// ⚠️ **文件名里的空格不许破坏列**——这正是选 tab 而不是空格的理由。
    /// </summary>
    [Fact]
    public async Task List_AFileNameWithSpaces_DoesNotBreakTheColumns()
    {
        File.WriteAllText(Path.Combine(_root, "my file.txt"), "x");

        var line = (await ListLines("{}")).Single(l => l.Contains("my file"));

        Assert.Equal("my file.txt", Cells(line)[0]);
        Assert.Equal("1 B", Cells(line)[1]);
    }

    /// <summary>大小是**给人/模型读的短写法**，不是字节数原样抖出来。</summary>
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(20 * 1024 * 1024, "20.0 MB")]
    public async Task List_SizeIsAHumanShortForm(int bytes, string expected)
    {
        File.WriteAllText(Path.Combine(_root, "f.bin"), new string('x', bytes));

        var line = (await ListLines("{}")).Single(l => l.Contains("f.bin"));

        Assert.Equal(expected, Cells(line)[1]);
    }

    /// <summary>
    /// ⚠️ **截断必须明说**（`01` §9.3）：静默截断会让模型以为目录只有这些条目——
    /// 那是「多了一条假信息」，同 §3.6 ①之二。
    /// ⚠️ 而且**要说清一共多少条**，否则模型无法判断"还差多少"。
    /// </summary>
    [Fact]
    public async Task List_TruncatesAndSaysHowManyThereAre()
    {
        const int total = ToolLimits.MaxDirectoryEntries + 7;
        for (var i = 0; i < total; i++)
            File.WriteAllText(Path.Combine(_root, $"f{i:D5}.txt"), "");

        var text = await RunText(new ListTool(), Call("ls", "{}"));
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // ⚠️ 判据要钉**结构**，不是钉我恰好选的那句话：
        // 条目行 = `名字<TAB>大小`（两格）；提示行 = `（…）`。
        var entries = lines.Where(l => l.Split('\t').Length == 2).ToList();
        var notices = lines.Where(l => !l.Contains('\t')).ToList();

        Assert.Equal(ToolLimits.MaxDirectoryEntries, entries.Count);

        // 且必须**明说**：总数与显示数都要出现。
        var notice = Assert.Single(notices);
        Assert.Contains(total.ToString(), notice);
        Assert.Contains(ToolLimits.MaxDirectoryEntries.ToString(), notice);
    }
}
