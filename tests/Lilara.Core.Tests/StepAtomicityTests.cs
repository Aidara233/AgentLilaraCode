using Lilara.Core.Kernel;
using Lilara.Core.Session.Tools;
using Lilara.Core.Storage;

namespace Lilara.Core.Tests;

/// <summary>
/// 步原子性（`01` §4 / §3.6 ③④）。⚠️ 这里最要紧的一条是**并发下的批次连续性**：
/// 「一次落盘 = 一个原子」如果只是「调用得够快」，这个测试会红。
/// </summary>
public class StepAtomicityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lilara-batch-" + Guid.NewGuid().ToString("N"));
    private string HistoryPath => Path.Combine(_root, "history.jsonl");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static TimelineEvent Event(string id, string text) => new TimelineEvent.MessageEvent
    {
        Id = id, At = DateTimeOffset.UnixEpoch, From = Speaker.Model,
        Blocks = [new Block.TextBlock(text)],
    };

    [Fact]
    public void AppendMany_WritesEveryEventInOrder()
    {
        HistoryLog.AppendMany(HistoryPath, [Event("a", "1"), Event("b", "2"), Event("c", "3")]);

        Assert.Equal(["a", "b", "c"], HistoryLog.ReadAll(HistoryPath).Select(e => e.Id));
    }

    // 空批次是**合法输入**（拒答且没有工具的那一步就是空的）⇒ 不抛、也不创建文件。
    [Fact]
    public void AppendMany_WithNoEvents_IsANoOp()
    {
        HistoryLog.AppendMany(HistoryPath, []);

        Assert.False(File.Exists(HistoryPath));
    }

    /// <summary>
    /// ⚠️ **这条是「原子性」真正的证据**，也是它唯一能被证伪的地方。
    ///
    /// <para>8 个线程各写若干批，每批 3 条（id 里编着批号）。断言：<b>每一批的 3 条在文件里
    /// 必须连续</b>——若有别的批插进来，说明那个批次不是一次写入的。</para>
    ///
    /// <para>⚠️ **它证伪的是什么**：把 <c>AppendMany</c> 写成「循环调 <c>Append</c>」
    /// 也会通过**单线程**的其它所有测试，只会在这里红——因为那样每批要拿 N 次锁，
    /// 中间必然放别人进来。这正是「一把锁 + 一次 open/close」与「调用得快」的区别。</para>
    /// </summary>
    [Fact]
    public void AppendMany_KeepsEachBatchContiguous_UnderConcurrentWriters()
    {
        const int writers = 8, batchesPerWriter = 25, batchSize = 3;

        Parallel.For(0, writers, w =>
        {
            for (var b = 0; b < batchesPerWriter; b++)
            {
                var batch = Enumerable.Range(0, batchSize)
                    .Select(i => Event($"w{w}b{b}i{i}", $"{w}-{b}-{i}"))
                    .ToList();
                HistoryLog.AppendMany(HistoryPath, batch);
            }
        });

        var ids = HistoryLog.ReadAll(HistoryPath).Select(e => e.Id).ToList();
        Assert.Equal(writers * batchesPerWriter * batchSize, ids.Count);

        // 每一批：它的 3 条必须挨着。用「同批成员的位置必须连续」来判——
        // 只比首尾不够（`a b x y c` 那种首尾也可能对）。
        for (var w = 0; w < writers; w++)
        {
            for (var b = 0; b < batchesPerWriter; b++)
            {
                var at = ids.IndexOf($"w{w}b{b}i0");
                Assert.True(at >= 0, $"w{w}b{b} 的整批不见了");
                Assert.Equal(
                    Enumerable.Range(0, batchSize).Select(i => $"w{w}b{b}i{i}"),
                    ids.Skip(at).Take(batchSize));
            }
        }
    }
}

/// <summary>
/// 执行相位（`01` §4：一步 = 一次模型调用 + 其工具执行）。
/// </summary>
public class ToolExecutorTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "lilara-exec-" + Guid.NewGuid().ToString("N"));
    private readonly string _work;
    private readonly BlobStore _blobs;
    private readonly SessionToolContext _ctx;
    private readonly IReadOnlyList<ITool> _tools = [new ListTool(), new ReadTool()];

    public ToolExecutorTests()
    {
        _work = Path.Combine(_tmp, "work");
        Directory.CreateDirectory(_work);
        _blobs = new BlobStore(_tmp);
        _ctx = new SessionToolContext(_work, _blobs);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true);
    }

    private static Block.ToolUseBlock Use(string callId, string name, string args) => new(callId, name, args);

    private ToolExecutor Subject() => new(_tools, _blobs);

    /// <summary>
    /// ⚠️ **顺序 = 调用序，不是完成序**（`01` §4 的明文规定）。
    /// 这条用「乱序的入参 + 输出必须与入参同序」来钉——它是**结构**保证的
    /// （结果按 <c>callId</c> 归位），所以换成并发执行时不用改。
    /// </summary>
    [Fact]
    public async Task Execute_KeepsCallOrder_NotCompletionOrder()
    {
        File.WriteAllText(Path.Combine(_work, "a.txt"), "A");
        File.WriteAllText(Path.Combine(_work, "b.txt"), "B");

        // 刻意让「文件系统上先存在」与「调用序」相反：b 先建、但排在 a 后面调用。
        var calls = new List<Block.ToolUseBlock>
        {
            Use("c1", "read", """{"path":"b.txt"}"""),
            Use("c2", "read", """{"path":"a.txt"}"""),
        };

        var results = await Subject().ExecuteAsync(calls, _ctx, default);

        Assert.Equal(["c1", "c2"], results.Select(r => r.CallId));
        Assert.Equal("B", ((Block.TextBlock)results[0].Content[0]).Text);
        Assert.Equal("A", ((Block.TextBlock)results[1].Content[0]).Text);
    }

    // 每条结果都带引用与指纹（`00` §5：三件里除快照外的两件）。
    [Fact]
    public async Task Execute_CarriesDependencyAndFingerprintThrough()
    {
        File.WriteAllText(Path.Combine(_work, "a.txt"), "x");

        var results = await Subject().ExecuteAsync(
            [Use("c1", "read", """{"path":"a.txt"}""")], _ctx, default);

        var r = Assert.Single(results);
        Assert.Equal("a.txt", r.DependsOn);
        Assert.NotNull(r.Fingerprint);
        Assert.Null(r.Ref);   // `Ref` 是 fetch 专用——普通工具不填
    }

    /// <summary>
    /// ⚠️ **快照进库的判据是「体积」，不是「文本 vs 二进制」**（`BlobStore.InlineThresholdBytes`）。
    /// 一段超大的**文本**同样该进库，而记录里只留一个地址。
    /// </summary>
    [Fact]
    public async Task Execute_LargeSnapshot_GoesToTheBlobStore_LeavingAnAddress()
    {
        var big = new string('x', BlobStore.InlineThresholdBytes + 1);
        File.WriteAllText(Path.Combine(_work, "big.txt"), big);

        var results = await Subject().ExecuteAsync(
            [Use("c1", "read", """{"path":"big.txt"}""")], _ctx, default);

        var store = Assert.IsType<Block.StoreBlock>(Assert.Single(Assert.Single(results).Content));
        Assert.Equal("text/plain", store.MediaType);
        Assert.Equal(big, System.Text.Encoding.UTF8.GetString(_blobs.TryGet(store.Sha256)!));
    }

    // 小快照内联（不进库）——阈值以下散进两处只会让记录更难读。
    [Fact]
    public async Task Execute_SmallSnapshot_StaysInline()
    {
        File.WriteAllText(Path.Combine(_work, "a.txt"), "小");

        var results = await Subject().ExecuteAsync(
            [Use("c1", "read", """{"path":"a.txt"}""")], _ctx, default);

        Assert.IsType<Block.TextBlock>(Assert.Single(Assert.Single(results).Content));
        // ⚠️ 断言写成「目录不存在**或**为空」：内联时不写库 ⇒ 库目录压根不会被创建，
        // 而 `Directory.GetFiles` 对不存在的目录**会抛**（不是返回空）。
        // 直接 `Assert.Empty(Directory.GetFiles(...))` 会让这条测试在正确的行为上失败。
        Assert.True(!Directory.Exists(_blobs.Root) || Directory.GetFiles(_blobs.Root).Length == 0);
    }

    /// <summary>
    /// ⚠️ **名字查不到也是一条结果，不是异常。** 异常会让配对的 `tool_result` 落不下去，
    /// 而那在协议层面是**上游 400**（`01` §3.6）——「模型编了个工具名」是常态，不是故障。
    /// </summary>
    [Fact]
    public async Task Execute_UnknownToolName_IsAResult()
    {
        var results = await Subject().ExecuteAsync(
            [Use("c1", "nonexistent", "{}")], _ctx, default);

        var r = Assert.Single(results);
        Assert.Equal("c1", r.CallId);
        Assert.Contains("nonexistent", ((Block.TextBlock)r.Content[0]).Text);
    }

    // 一条工具抛了，不该带走同一步里别的结果（本批的两个工具不抛，但契约要立住）。
    [Fact]
    public async Task Execute_OneToolFailing_DoesNotDropTheOthers()
    {
        File.WriteAllText(Path.Combine(_work, "ok.txt"), "fine");

        var results = await Subject().ExecuteAsync(
        [
            Use("c1", "read", """{"path":"../../escape"}"""),   // 被拒（一条结果）
            Use("c2", "read", """{"path":"ok.txt"}"""),         // 正常
        ], _ctx, default);

        Assert.Equal(2, results.Count);
        Assert.Equal("fine", ((Block.TextBlock)results[1].Content[0]).Text);
    }
}
