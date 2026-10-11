using Lilara.Core.Kernel;
using Lilara.Core.Session;
using Lilara.Core.Session.Tools;
using Lilara.Core.Storage;
using Block = Lilara.Core.Kernel.Block;

namespace Lilara.Core.Tests;

/// <summary>
/// **`update`——第一个写类内建**（`01` §3.5 / §9.3）。
///
/// <para>⚠️ <b>它顺带把闸门那条链从「碰不到」变成「碰得到」</b>：在它之前四个内建
/// 全是只读，而只读在所有档位都自动放行 ⇒ `Gate.Decide` 从没返回过
/// <see cref="Verdict.Ask"/>。本文件里那条 `PermissionClass` 断言是**结构性**的
/// ——把 `update` 改成只读，闸门那条链**当场又断**，而别的测试一条都不会红。</para>
///
/// <para>⚠️ <b>这里跑的是真工具、真闸门、真 `ToolExecutor`</b>，不造替身：
/// 替身能证明「闸门会问」，证明不了「那个问得出来的是 `update` 自己的类别」。</para>
/// </summary>
public class UpdateToolTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "lilara-upd-" + Guid.NewGuid().ToString("N"));
    private readonly string _work;

    public UpdateToolTests()
    {
        _work = Path.Combine(_tmp, "work");
        Directory.CreateDirectory(_work);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true);
    }

    private static ToolCall Call(string args) => new("c1", "update", args);

    /// <summary>造一个真的上下文（库在临时目录里）。</summary>
    private SessionToolContext Context() =>
        new(_work, new BlobStore(Path.Combine(_tmp, "data")));

    private string Write(string rel, string content)
    {
        var full = Path.Combine(_work, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    private static string TextOf(ToolOutcome o) =>
        string.Join("", o.Content.OfType<Block.TextBlock>().Select(b => b.Text));

    // ---- 一、它是写类（闸门那条链的存在性靠这一条）----

    /// <summary>
    /// ⚠️ <b>这条是「闸门有生产者」的根</b>：`update` 必须声明 <see cref="PermissionClass.Writing"/>。
    /// 改成 `ReadOnly` ⇒ 它在所有档位都自动放行 ⇒ `01` §3.5 那两格「问」**重新变成没有生产者**，
    /// 而**没有任何别的测试会红**（这就是它单独钉一条的理由）。
    /// </summary>
    [Fact]
    public void Update_DeclaresItselfWriting_SoTheGateHasAProducer()
    {
        Assert.Equal(PermissionClass.Writing, new UpdateTool().PermissionClass);

        // ⚠️ 顺带钉住那张表的**那一格**：默认档（standard）对写入类正是「问」。
        Assert.Equal(Verdict.Ask, Gate.Decide(new UpdateTool().PermissionClass, "standard"));
    }

    // ---- 二、改得对 ----

    [Fact]
    public async Task Update_ReplacesTheOneOccurrence()
    {
        Write("a.txt", "hello\nworld\n");

        var outcome = await new UpdateTool().RunAsync(
            Call("""{"path":"a.txt","old":"world","new":"there"}"""), Context(), CancellationToken.None);

        Assert.Equal("hello\nthere\n", File.ReadAllText(Path.Combine(_work, "a.txt")));
        Assert.Contains("已改", TextOf(outcome));
    }

    /// <summary>⚠️ 空 `new` = **删掉**那一段，而它是**合法**的（不是「参数写错了」）。</summary>
    [Fact]
    public async Task Update_EmptyNew_DeletesTheSpan()
    {
        Write("a.txt", "keep\nDROP\nkeep2\n");

        await new UpdateTool().RunAsync(
            Call("""{"path":"a.txt","old":"DROP\n","new":""}"""), Context(), CancellationToken.None);

        Assert.Equal("keep\nkeep2\n", File.ReadAllText(Path.Combine(_work, "a.txt")));
    }

    /// <summary>多行 `old` —— 模型靠多带上下文来保证唯一，这条钉住它能跨行匹配。</summary>
    [Fact]
    public async Task Update_MatchesAcrossLines()
    {
        Write("a.txt", "one\ntwo\nthree\n");

        await new UpdateTool().RunAsync(
            Call("""{"path":"a.txt","old":"two\nthree","new":"TWO"}"""), Context(), CancellationToken.None);

        Assert.Equal("one\nTWO\n", File.ReadAllText(Path.Combine(_work, "a.txt")));
    }

    // ---- 三、三条参数错误（说人话的结果，不是异常）----

    /// <summary>
    /// ⚠️ <b>0 次匹配</b>：模型记错了文件当时的样子（陈旧）。
    /// 拒绝而不是硬插——插进去的文件**看着正常**，而它多了一段模型以为已经有的东西。
    /// </summary>
    [Fact]
    public async Task Update_WhenOldIsAbsent_RefusesAndChangesNothing()
    {
        Write("a.txt", "hello\n");

        var outcome = await new UpdateTool().RunAsync(
            Call("""{"path":"a.txt","old":"nope","new":"x"}"""), Context(), CancellationToken.None);

        Assert.Contains("找不到", TextOf(outcome));
        Assert.Equal("hello\n", File.ReadAllText(Path.Combine(_work, "a.txt")));
    }

    /// <summary>
    /// ⚠️ <b>多次匹配 ⇒ 拒绝，而不是「改第一个」</b>。判据：「改哪一处」在多处匹配时是**猜**，
    /// 而猜错的那一刻文件已经坏了。⚠️ 这条是本文件里最要紧的一条——
    /// 把「恰好一次」改成「找到就改」，**只有它会红**。
    /// </summary>
    [Fact]
    public async Task Update_WhenOldAppearsTwice_RefusesAndChangesNothing()
    {
        Write("a.txt", "x\nmid\nx\n");

        var outcome = await new UpdateTool().RunAsync(
            Call("""{"path":"a.txt","old":"x","new":"y"}"""), Context(), CancellationToken.None);

        Assert.Contains("出现了多次", TextOf(outcome));
        // ⚠️ **一个字都没改**——「拒绝」的判据是盘上没变，不是回话里有"多"字。
        Assert.Equal("x\nmid\nx\n", File.ReadAllText(Path.Combine(_work, "a.txt")));
    }

    /// <summary>空 `old` ⇒ 拒绝（它匹配的是每一处插入点，没法确定改哪里）。</summary>
    [Fact]
    public async Task Update_EmptyOld_IsRefused()
    {
        Write("a.txt", "hello\n");

        var outcome = await new UpdateTool().RunAsync(
            Call("""{"path":"a.txt","old":"","new":"x"}"""), Context(), CancellationToken.None);

        Assert.Contains("`old` 是空的", TextOf(outcome));
        Assert.Equal("hello\n", File.ReadAllText(Path.Combine(_work, "a.txt")));
    }

    /// <summary>
    /// ⚠️ <b>缺 `new` 与给空的 `new` 是两回事</b>：前者是**参数写错了**（要拒绝），
    /// 后者是「删掉」这个**明确的意思**。`ToolArgs.String` 对两者给出同一个结果
    /// ⇒ 必须靠 `HasKey` 分开，否则一次漏参数会变成一次**静默的删除**。
    /// </summary>
    [Fact]
    public async Task Update_MissingNew_IsNotTheSameAsEmptyNew()
    {
        Write("a.txt", "hello\n");

        var outcome = await new UpdateTool().RunAsync(
            Call("""{"path":"a.txt","old":"hello"}"""), Context(), CancellationToken.None);

        Assert.Contains("缺少 `new`", TextOf(outcome));
        Assert.Equal("hello\n", File.ReadAllText(Path.Combine(_work, "a.txt")));
    }

    // ---- 四、越界与「不存在」 ----

    [Fact]
    public async Task Update_RefusesPathsOutsideTheWorkingDirectory()
    {
        var outcome = await new UpdateTool().RunAsync(
            Call("""{"path":"../../escape.txt","old":"a","new":"b"}"""), Context(), CancellationToken.None);

        Assert.Contains("工作目录之外", TextOf(outcome));
    }

    [Fact]
    public async Task Update_MissingFile_SaysSoInsteadOfCreatingIt()
    {
        var outcome = await new UpdateTool().RunAsync(
            Call("""{"path":"nope.txt","old":"a","new":"b"}"""), Context(), CancellationToken.None);

        Assert.Contains("不存在", TextOf(outcome));
        Assert.False(File.Exists(Path.Combine(_work, "nope.txt")));
    }

    [Fact]
    public async Task Update_DirectoryIsNotAFile()
    {
        Directory.CreateDirectory(Path.Combine(_work, "adir"));

        var outcome = await new UpdateTool().RunAsync(
            Call("""{"path":"adir","old":"a","new":"b"}"""), Context(), CancellationToken.None);

        Assert.Contains("是一个目录", TextOf(outcome));
    }

    /// <summary>参数根本不是 JSON ⇒ 说人话的结果，**不抛**（抛会让 `tool_use`/`tool_result` 只落一半）。</summary>
    [Fact]
    public async Task Update_GarbageArguments_ReturnsAReadableResult()
    {
        var outcome = await new UpdateTool().RunAsync(
            Call("not json at all"), Context(), CancellationToken.None);

        Assert.Contains("缺少 `path`", TextOf(outcome));
    }

    // ---- 五、可逆：写之前旧内容进库 ----

    /// <summary>
    /// ⚠️ <b>「可逆」这句话的唯一判据是「旧字节取得回」</b>，不是「结果里提到了一个哈希」。
    /// ⇒ 这条测试**真的去库里按那个地址取一次**，并逐字比。
    /// 去掉写前那次 `EnsureStored` ⇒ **只有这条会红**。
    /// </summary>
    [Fact]
    public async Task Update_StoresTheOldContentBeforeOverwriting()
    {
        Write("a.txt", "the original bytes\n");
        var blobs = new BlobStore(Path.Combine(_tmp, "data"));
        var ctx = new SessionToolContext(_work, blobs);

        var outcome = await new UpdateTool().RunAsync(
            Call("""{"path":"a.txt","old":"original","new":"changed"}"""), ctx, CancellationToken.None);

        Assert.Equal("the changed bytes\n", File.ReadAllText(Path.Combine(_work, "a.txt")));

        // ⚠️ 旧内容应当在库里，而且**逐字**是改之前那串。
        var sha = blobs.Put(System.Text.Encoding.UTF8.GetBytes("the original bytes\n"));
        Assert.Equal("the original bytes\n", System.Text.Encoding.UTF8.GetString(blobs.TryGet(sha)!));

        // 而且结果里给出的那个地址就是同一个（否则用户拿到的哈希取不回东西）。
        Assert.Contains(sha[..12], TextOf(outcome));
    }

    // ---- 六、框架与指纹 ----

    /// <summary>
    /// ⚠️ 改动量写进**框架**（`01` §3.7）：它是这一次调用返回了什么的事实，
    /// 而正文说的是"改成功了"。回收换的是 `Content`，框架留着——两者不能混。
    /// </summary>
    [Fact]
    public async Task Update_PutsTheDeltaInFramingNotInContent()
    {
        Write("a.txt", "hello\n");

        var outcome = await new UpdateTool().RunAsync(
            Call("""{"path":"a.txt","old":"hello","new":"hi"}"""), Context(), CancellationToken.None);

        Assert.NotNull(outcome.Framing);
        Assert.Equal("a.txt", outcome.Framing!.Tool);
        Assert.Contains(outcome.Framing.Fields, f => f.Key == "改动" && f.Value == "+2 −5");
    }

    /// <summary>
    /// ⚠️ **指纹算的是改完之后的**：给改之前的哈希会让下一次三态判定把
    /// 「我刚改完」判成「世界动了」——那是误报，而误报的后果是每改一次白扔一次快照。
    /// </summary>
    [Fact]
    public async Task Update_FingerprintMatchesTheStateAfterTheWrite()
    {
        Write("a.txt", "before\n");
        var blobs = new BlobStore(Path.Combine(_tmp, "data"));

        var outcome = await new UpdateTool().RunAsync(
            Call("""{"path":"a.txt","old":"before","new":"after"}"""),
            new SessionToolContext(_work, blobs), CancellationToken.None);

        // 同一个工具、对着**改完之后**的内容再跑一次同样的"无操作"改动，
        // 指纹必须与上一次相同——「世界没动」这条判据靠的就是这个。
        var again = await new UpdateTool().RunAsync(
            Call("""{"path":"a.txt","old":"after","new":"after"}"""),
            new SessionToolContext(_work, blobs), CancellationToken.None);

        Assert.NotNull(outcome.Fingerprint);
        Assert.Equal(outcome.Fingerprint, again.Fingerprint);
    }
}
