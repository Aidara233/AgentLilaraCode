using System.Text;
using Lilara.Core.Kernel;
using Lilara.Core.Session;
using Lilara.Core.Session.Tools;
using Lilara.Core.Storage;
using Block = Lilara.Core.Kernel.Block;

namespace Lilara.Core.Tests;

/// <summary>
/// **`write`——整份写一个文件**（没有就新建，有就覆盖）。
///
/// <para>⚠️ <b>它与 `UpdateToolTests` 是姊妹文件，而分开的理由是判据不同</b>：
/// `update` 那组钉的是「`old` 恰好一次」；这一组钉的是**新建与覆盖是两条不同的路**
/// （覆盖要保住旧内容、新建要证明没有破坏任何东西）。</para>
/// </summary>
public class WriteToolTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "lilara-wr-" + Guid.NewGuid().ToString("N"));
    private readonly string _work;

    public WriteToolTests()
    {
        _work = Path.Combine(_tmp, "work");
        Directory.CreateDirectory(_work);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true);
    }

    private static ToolCall Call(string args) => new("c1", "write", args);

    private SessionToolContext Context() => new(_work, new BlobStore(Path.Combine(_tmp, "data")));

    private string Full(string rel) => Path.Combine(_work, rel);

    private static string TextOf(ToolOutcome o) =>
        string.Join("", o.Content.OfType<Block.TextBlock>().Select(b => b.Text));

    // ---- 一、它是写类 ----

    /// <summary>
    /// ⚠️ 与 `update` 同一条判据：写类 ⇒ 默认档（standard）那一格是**问**。
    /// 改成只读会让闸门那条链重新断掉，而**别的测试一条都不会红**。
    /// </summary>
    [Fact]
    public void Write_DeclaresItselfWriting()
    {
        Assert.Equal(PermissionClass.Writing, new WriteTool().PermissionClass);
        Assert.Equal(Verdict.Ask, Gate.Decide(new WriteTool().PermissionClass, "standard"));
    }

    // ---- 二、新建 ----

    [Fact]
    public async Task Write_CreatesAFileThatDidNotExist()
    {
        var outcome = await new WriteTool().RunAsync(
            Call("""{"path":"new.txt","content":"hello\n"}"""), Context(), CancellationToken.None);

        Assert.Equal("hello\n", File.ReadAllText(Full("new.txt")));
        Assert.Contains("已新建", TextOf(outcome));
    }

    /// <summary>
    /// ⚠️ **新建不该有「旧版地址」**——那格编一个值会让模型以为「有个版本可以还原」。
    /// 判据是**框架里那一格不出现**（不是「值为空」）。
    /// </summary>
    [Fact]
    public async Task Write_CreateReportsNoOldVersion()
    {
        var outcome = await new WriteTool().RunAsync(
            Call("""{"path":"new.txt","content":"x"}"""), Context(), CancellationToken.None);

        Assert.NotNull(outcome.Framing);
        Assert.Equal("新建", outcome.Framing!.Fields[0].Key);
        Assert.DoesNotContain(outcome.Framing.Fields, f => f.Key == "旧版");
    }

    /// <summary>空 `content` = **一个空文件**，是合法的（不是「参数写错了」）。</summary>
    [Fact]
    public async Task Write_EmptyContent_MakesAnEmptyFile()
    {
        await new WriteTool().RunAsync(
            Call("""{"path":"empty.txt","content":""}"""), Context(), CancellationToken.None);

        Assert.True(File.Exists(Full("empty.txt")));
        Assert.Equal("", File.ReadAllText(Full("empty.txt")));
    }

    /// <summary>在**已存在**的目录里新建是正常用法（那正是它存在的理由之一）。</summary>
    [Fact]
    public async Task Write_CreatesInsideAnExistingSubdirectory()
    {
        Directory.CreateDirectory(Full("sub"));

        await new WriteTool().RunAsync(
            Call("""{"path":"sub/a.txt","content":"hi"}"""), Context(), CancellationToken.None);

        Assert.Equal("hi", File.ReadAllText(Full("sub/a.txt")));
    }

    // ---- 三、覆盖 ----

    [Fact]
    public async Task Write_OverwritesAnExistingFile()
    {
        File.WriteAllText(Full("a.txt"), "old content\n");

        var outcome = await new WriteTool().RunAsync(
            Call("""{"path":"a.txt","content":"new\n"}"""), Context(), CancellationToken.None);

        Assert.Equal("new\n", File.ReadAllText(Full("a.txt")));
        Assert.Contains("已覆盖", TextOf(outcome));
        Assert.Equal("覆盖", outcome.Framing!.Fields[0].Key);
    }

    /// <summary>
    /// ⚠️ <b>「可逆」的唯一判据是「旧字节取得回」</b>，不是「结果里提了个哈希」。
    /// ⇒ 这条真的去库里按那个地址取一次，并逐字比。
    /// 去掉写前那次 `EnsureStored` ⇒ **只有这条会红**。
    /// </summary>
    [Fact]
    public async Task Write_StoresTheOldContentBeforeOverwriting()
    {
        File.WriteAllText(Full("a.txt"), "the original line\n");
        var blobs = new BlobStore(Path.Combine(_tmp, "data"));
        var ctx = new SessionToolContext(_work, blobs);

        var outcome = await new WriteTool().RunAsync(
            Call("""{"path":"a.txt","content":"shorter"}"""), ctx, CancellationToken.None);

        Assert.Equal("shorter", File.ReadAllText(Full("a.txt")));

        var sha = blobs.Put(Encoding.UTF8.GetBytes("the original line\n"));
        Assert.Equal("the original line\n", Encoding.UTF8.GetString(blobs.TryGet(sha)!));
        Assert.Contains(sha[..12], TextOf(outcome));
    }

    /// <summary>
    /// ⚠️ **覆盖成空文件也要保住旧内容**——那是最容易漏的一格
    /// （「反正结果是空的，没什么好存的」是错的：被清掉的东西才最需要留底）。
    /// </summary>
    [Fact]
    public async Task Write_ClearingAFileStillKeepsTheOldContent()
    {
        File.WriteAllText(Full("a.txt"), "important\n");
        var blobs = new BlobStore(Path.Combine(_tmp, "data"));

        var outcome = await new WriteTool().RunAsync(
            Call("""{"path":"a.txt","content":""}"""),
            new SessionToolContext(_work, blobs), CancellationToken.None);

        Assert.Equal("", File.ReadAllText(Full("a.txt")));

        var sha = blobs.Put(Encoding.UTF8.GetBytes("important\n"));
        Assert.Equal("important\n", Encoding.UTF8.GetString(blobs.TryGet(sha)!));
        Assert.Contains(sha[..12], TextOf(outcome));
    }

    // ---- 四、参数与越界 ----

    /// <summary>
    /// ⚠️ **缺 `content` 与给空的 `content` 是两回事**：前者是参数写错了（要拒），
    /// 后者是「一个空文件」这个明确的意思。
    /// ⚠️ **这条是本文件里最要紧的一条**——不分开的话，一次漏参数会变成一次**静默的清空**。
    /// </summary>
    [Fact]
    public async Task Write_MissingContent_IsNotTheSameAsEmptyContent()
    {
        File.WriteAllText(Full("a.txt"), "keep me\n");

        var outcome = await new WriteTool().RunAsync(
            Call("""{"path":"a.txt"}"""), Context(), CancellationToken.None);

        Assert.Contains("缺少 `content`", TextOf(outcome));
        // ⚠️ **一个字都没动**——判据是盘上没变，不是回话里有"缺少"两个字。
        Assert.Equal("keep me\n", File.ReadAllText(Full("a.txt")));
    }

    [Fact]
    public async Task Write_MissingPath_IsRefused()
    {
        var outcome = await new WriteTool().RunAsync(
            Call("""{"content":"x"}"""), Context(), CancellationToken.None);

        Assert.Contains("缺少 `path`", TextOf(outcome));
    }

    [Fact]
    public async Task Write_RefusesPathsOutsideTheWorkingDirectory()
    {
        var outcome = await new WriteTool().RunAsync(
            Call("""{"path":"../../escape.txt","content":"x"}"""), Context(), CancellationToken.None);

        Assert.Contains("工作目录之外", TextOf(outcome));
    }

    /// <summary>
    /// ⚠️ <b>父目录不存在 ⇒ 拒绝，不替你建</b>（同 `session.create` 那条：建目录是策略）。
    /// ⚠️ 判据是**父目录**：`Directory.CreateDirectory` 会把中间层级一起造出来，
    /// 一次笔误就长出一棵树——那正是这条要挡的。
    /// </summary>
    [Fact]
    public async Task Write_DoesNotCreateMissingParentDirectories()
    {
        var outcome = await new WriteTool().RunAsync(
            Call("""{"path":"nope/deep/a.txt","content":"x"}"""), Context(), CancellationToken.None);

        Assert.Contains("父目录不存在", TextOf(outcome));
        Assert.False(Directory.Exists(Full("nope")));      // ⚠️ 连第一层都没造
    }

    [Fact]
    public async Task Write_DirectoryIsNotAFile()
    {
        Directory.CreateDirectory(Full("adir"));

        var outcome = await new WriteTool().RunAsync(
            Call("""{"path":"adir","content":"x"}"""), Context(), CancellationToken.None);

        Assert.Contains("是一个目录", TextOf(outcome));
    }

    /// <summary>参数根本不是 JSON ⇒ 说人话的结果，**不抛**（抛会让 `tool_use`/`tool_result` 只落一半）。</summary>
    [Fact]
    public async Task Write_GarbageArguments_ReturnsAReadableResult()
    {
        var outcome = await new WriteTool().RunAsync(
            Call("not json"), Context(), CancellationToken.None);

        Assert.Contains("缺少 `path`", TextOf(outcome));
    }

    // ---- 五、指纹 ----

    /// <summary>
    /// ⚠️ 指纹算的是**写完之后**：给写之前的会让下一次三态判定把「我刚写完」
    /// 判成「世界动了」（误报）。⇒ 同内容再写一次，指纹必须相同。
    /// </summary>
    [Fact]
    public async Task Write_FingerprintMatchesTheStateAfterTheWrite()
    {
        var one = await new WriteTool().RunAsync(
            Call("""{"path":"a.txt","content":"same"}"""), Context(), CancellationToken.None);
        var two = await new WriteTool().RunAsync(
            Call("""{"path":"a.txt","content":"same"}"""), Context(), CancellationToken.None);

        Assert.NotNull(one.Fingerprint);
        Assert.Equal(one.Fingerprint, two.Fingerprint);
    }

    /// <summary>
    /// ⚠️ **`write` 与 `update` / `read` 用的是同一个指纹函数**（`ToolPaths.FingerprintOf`）：
    /// 漂开的话，同一个文件会被三条记录用三种指纹指称，三态判定随即错乱。
    /// ⇒ 判据是「同样的（路径, 内容）⇒ 同样的指纹」，跨工具成立。
    /// </summary>
    [Fact]
    public async Task Write_SharesItsFingerprintWithUpdate()
    {
        var w = await new WriteTool().RunAsync(
            Call("""{"path":"a.txt","content":"payload"}"""), Context(), CancellationToken.None);

        File.WriteAllText(Full("b.txt"), "payload");
        var u = await new UpdateTool().RunAsync(
            Call("""{"path":"b.txt","old":"payload","new":"payload"}"""), Context(), CancellationToken.None);

        // ⚠️ 路径不同 ⇒ 指纹**本来就该不同**（路径进哈希是刻意的，见 `ToolPaths`）。
        Assert.NotEqual(w.Fingerprint, u.Fingerprint);

        // ⇒ 同路径同内容时才比得出「是同一个函数」：把 b.txt 删掉、用 write 写同名同内容。
        File.Delete(Full("b.txt"));
        var w2 = await new WriteTool().RunAsync(
            Call("""{"path":"b.txt","content":"payload"}"""), Context(), CancellationToken.None);

        Assert.Equal(u.Fingerprint, w2.Fingerprint);
    }
}
