using System.Diagnostics;
using Lilara.Core.Kernel;
using Lilara.Core.Session.Tools;
using Lilara.Core.Storage;
using Block = Lilara.Core.Kernel.Block;

namespace Lilara.Core.Tests;

/// <summary>
/// **一次工具调用的时限**（2026-11-07，用户裁定「加个通用兜底」）。
///
/// <para>三条判据，正好对应那三层的分工：</para>
/// <list type="number">
///   <item><b>兜底</b>（<see cref="ToolExecutor"/>）：到点先发取消，工具收不住就交一条
///   「超时被终止」的结果——**不抛异常**（失败是内容）。⚠️ 而**用户打断**仍然是异常，
///   因为 `RunStepAsync` 靠它区分「中断」与「失败」。</item>
///   <item><b>接管</b>（<see cref="BackgroundJobHost.Adopt"/>）：能转后台的工具把自己的进程
///   交过来，宿主按普通后台任务对待它——**不重跑**，输出从交接那一刻接着写进 `&lt;jobId&gt;.log`。</item>
///   <item><b>分界</b>（`shell`）：用**默认**时限到点 ⇒ 转后台；模型**显式**给了
///   `timeoutSeconds` ⇒ 到点就杀（它点名了截止）。</item>
/// </list>
/// </summary>
public sealed class ToolTimeoutTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-timeout-" + Guid.NewGuid().ToString("N"));

    public ToolTimeoutTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ---- ① 兜底：到点终止 ----

    [Fact]
    public async Task Executor_WhenTheToolOutlastsTheCap_ReturnsATimeoutResultInsteadOfHanging()
    {
        var executor = new ToolExecutor(
            [new StuckTool()], new BlobStore(_root), "trusted",
            // ⚠️ 这里刻意给一个 200 毫秒的上限：被测的是**判定与结果形状**，不是那个 600 秒的默认值。
            callTimeout: TimeSpan.FromMilliseconds(200));

        var clock = Stopwatch.StartNew();
        var batch = await executor.ExecuteAsync(
            [new Block.ToolUseBlock("c1", "stuck", """{"x":1}""")],
            new SessionToolContext(_root, new BlobStore(_root)), CancellationToken.None);

        var result = Assert.Single(batch.Results);
        var text = TextOf(result);

        Assert.Contains("超过", text);
        Assert.Contains("没有结果", text);
        // ⚠️ 框架里那一格是**事实**：模型与清扫都靠它区分「超时」与「失败」。
        Assert.Contains("timeout", result.Framing!.Render());

        // ⚠️ 兜底不许把「等满上限 + 宽限」走成常态：这个替身听取消，所以它应当在宽限里就结束。
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"兜底太慢：{clock.Elapsed}");
    }

    [Fact]
    public async Task Executor_CallerCancellation_ComesBackAsAMarkedBatchInsteadOfThrowing()
    {
        // ⚠️ **这条判据 2026-11-07 反过来了**（用户裁定「工具执行了一半也保留半截输出」）：
        // 从前这里断言「取消**必须抛** `OperationCanceledException`」——理由是
        // `RunStepAsync` 靠那个类型区分「中断」与「失败」。而那条路有一个**代价**：
        // 异常会把**已经跑完的那几条结果**和**半截输出**一起带走，于是记录里那一步
        // 一个字都不留（模型说过的话、真跑过的副作用全部消失）。
        //
        // ⇒ 现在「打断」是一种**有内容的结局**：执行器正常返回一个 `Interrupted` 的批次，
        // 每条调用都有结果（跑完的 / 半截的 / 还没开始的），由 `Stepper` 落盘并返回
        // `StepOutcome.Interrupted`。**「中断 ≠ 失败」这条区分没有丢**，它从「异常类型」
        // 换成了「批次上的一个标记」——而 `Stepper` 才是那个把它翻成结局的地方。
        var executor = new ToolExecutor(
            [new StuckTool()], new BlobStore(_root), "trusted",
            callTimeout: TimeSpan.FromSeconds(30));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var batch = await executor.ExecuteAsync(
            [new Block.ToolUseBlock("c1", "stuck", """{"x":1}""")],
            new SessionToolContext(_root, new BlobStore(_root)), cts.Token);

        Assert.True(batch.Interrupted);
        var result = Assert.Single(batch.Results);
        Assert.Equal("c1", result.CallId);
        // ⚠️ 它**开始跑过**（工具替身真的被调用了）⇒ 说「跑到一半、还没有输出」，
        // 而不是「还没开始」——后者会抹掉「它可能已经动过世界」这件事。
        Assert.Contains("跑到一半", TextOf(result));
        Assert.Contains("interrupted", result.Framing!.Render());
        Assert.Contains("partial", result.Framing!.Render());
    }

    /// <summary>
    /// **同一批里排在后面的那几条**：前一条被打断时它们**一次都没轮到**
    /// ⇒ 各自补一条「还没开始」（2026-11-07）。⚠️ 不补的话记录里会留下一批悬空的
    /// `tool_use`，下一次请求直接非法（`01` §3.6 ①）。
    /// </summary>
    [Fact]
    public async Task Executor_CallerCancellation_FillsTheRestOfTheBatchWithNotStarted()
    {
        var executor = new ToolExecutor(
            [new StuckTool()], new BlobStore(_root), "trusted",
            callTimeout: TimeSpan.FromSeconds(30));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var batch = await executor.ExecuteAsync(
            [
                new Block.ToolUseBlock("c1", "stuck", """{"x":1}"""),
                new Block.ToolUseBlock("c2", "stuck", """{"x":2}"""),
            ],
            new SessionToolContext(_root, new BlobStore(_root)), cts.Token);

        Assert.True(batch.Interrupted);
        Assert.Equal(["c1", "c2"], batch.Results.Select(r => r.CallId));
        Assert.Contains("跑到一半", TextOf(batch.Results[0]));
        Assert.Contains("还没开始", TextOf(batch.Results[1]));
        Assert.Contains("not-started", batch.Results[1].Framing!.Render());
    }

    // ---- ② 接管：活进程被交给后台宿主 ----

    [Fact]
    public async Task Adopt_RegistersTheRunningProcessAsAJob_AndItsOutputStartsWhereTheForegroundLeftOff()
    {
        var jobs = new BackgroundJobHost(_root);
        using var running = new FakeRunningProcess("shell", "sleep 9", head: "开头那一段\n");

        var info = jobs.Adopt("s1", running);

        Assert.NotNull(info);
        Assert.Equal(BackgroundJobState.Running, info!.State);
        // ⚠️ 转后台之后按后台默认算（不限时）：那句「到点转后台」里没有「然后一分钟后再杀」。
        Assert.Null(info.TimeoutSeconds);
        Assert.Single(jobs.List("s1"));

        // 交接之前读到的那一段**必须**已经在日志里（否则 `job_output` 从 0 读起只有半截）。
        Assert.Contains("开头那一段", jobs.Read("s1", info.Id, 0, 4096)!.Text);

        // 交接之后的输出接着往后写，顺序不乱。
        running.Emit("后面的\n");
        Assert.Contains("后面的", jobs.Read("s1", info.Id, 0, 4096)!.Text);

        // 它退出 ⇒ 走的是**同一条** Watch 路径（与宿主自己起的任务没有区别）。
        running.Exit(0);
        await WaitUntil(() => jobs.Find("s1", info.Id)!.State == BackgroundJobState.Exited);

        var done = jobs.Find("s1", info.Id)!;
        Assert.Equal(0, done.ExitCode);
        Assert.Contains("后面的", jobs.Read("s1", info.Id, 0, 4096)!.Text);
    }

    [Fact]
    public async Task Adopt_Killed_EndsAsKilledWithTheReason()
    {
        var jobs = new BackgroundJobHost(_root);
        using var running = new FakeRunningProcess("shell", "sleep 9", head: "");

        var info = jobs.Adopt("s1", running)!;
        var killed = jobs.Kill("s1", info.Id, "你的 job_kill");

        Assert.Equal(BackgroundJobState.Killed, killed!.State);
        Assert.Contains("job_kill", killed.Reason);
        await Task.CompletedTask;
    }

    // ---- ③ 分界：默认时限转后台，显式 timeoutSeconds 就杀 (真 sh) ----

    [Fact]
    public async Task Shell_DefaultTimeout_PromotesToBackground()
    {
        if (!ShRunner.TryLocate(null, ShRunner.DefaultExists, out _, out _)) return;

        var jobs = new BackgroundJobHost(_root);
        var context = new SessionToolContext(
            _root, new BlobStore(Path.Combine(_root, ".blobs")),
            shell: new ShellAccess(new ShellConfig { TimeoutSeconds = 1 }, jobs, "s1"));

        var outcome = await new ShellTool().RunAsync(
            new ToolCall("c", "shell", """{"command":"echo hi; sleep 10"}"""), context, CancellationToken.None);

        var text = TextOf(outcome);

        Assert.Contains("转到后台", text);
        // ⚠️ 它**不是**失败：说失败会诱导模型重跑一遍（而那是第二个进程）。
        Assert.DoesNotContain("命令失败", text);
        Assert.Contains("wait(task=", text);

        var job = Assert.Single(jobs.List("s1"));
        Assert.Equal(BackgroundJobState.Running, job.State);
        Assert.Equal("shell", job.Kind);
        // ⚠️ **没重跑**：只有这一个任务，而且是那个已经在跑的进程。
        // ⚠️ 等一等再断言：**「hi 什么时候到」不由这条判据决定**。整台机器满载时，进程可能
        // 还没跑到 `echo` 就先到点了——那时它已经在日志里、只是还没写进来（读泵没停，
        // 改道之后照样往同一份日志追加）。断言的判据是「日志里有它」，不是「交接那一刻就有」。
        var text2 = string.Empty;
        await WaitUntil(() => (text2 = ReadJob(jobs, job.Id)).Contains("hi"));
        Assert.Contains("hi", text2);

        jobs.Kill("s1", job.Id, "测试收尾");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Shell_ExplicitTimeoutSeconds_StillKills()
    {
        if (!ShRunner.TryLocate(null, ShRunner.DefaultExists, out _, out _)) return;

        var jobs = new BackgroundJobHost(_root);
        var context = new SessionToolContext(
            _root, new BlobStore(Path.Combine(_root, ".blobs")),
            shell: new ShellAccess(new ShellConfig { TimeoutSeconds = 60 }, jobs, "s1"));

        // ⚠️ 模型**点名了截止**（1 秒）⇒ 到点就杀，而不是挪到后台：它要的是那个答案。
        var outcome = await new ShellTool().RunAsync(
            new ToolCall("c", "shell", """{"command":"sleep 5","timeoutSeconds":1}"""),
            context, CancellationToken.None);

        var text = TextOf(outcome);
        Assert.Contains("超过 1 秒", text);
        Assert.DoesNotContain("转到后台", text);
        Assert.Empty(jobs.List("s1"));
    }

    // ---- 替身 ----

    /// <summary>一个**听取消、但永远不自己结束**的工具：兜底那条路的被测对象。</summary>
    private sealed class StuckTool : ITool
    {
        public string Name => "stuck";
        public PermissionClass PermissionClass => PermissionClass.ReadOnly;
        public string Description => "测试替身：跑到被取消为止。";
        public string ParametersSchemaJson => """{"type":"object","properties":{}}""";

        public async Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct)
        {
            // ⚠️ 只响应取消：它自己**永远不会**返回，正是「没有时限、又不听劝」那一类工具的最小形状。
            // ⚠️ 让 `OperationCanceledException` 照常冒出去（别用 `ContinueWith` 把它吞掉）——
            // 吞掉之后这个替身会变成「取消后正常返回」，那两条被测的判据就都测不到了。
            await Task.Delay(Timeout.Infinite, ct);
            return new ToolOutcome { Content = [] };
        }
    }

    /// <summary>
    /// 一个**假进程**：只实现 <see cref="IRunningProcess"/> 需要的那几件事。
    /// ⚠️ 用它而不是真进程，是因为被测的是「交接之后那份输出有没有接上」——
    /// 而真进程没法在测试里精确地在两个时刻吐出两段。
    /// </summary>
    private sealed class FakeRunningProcess(string kind, string label, string head)
        : IRunningProcess
    {
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Func<ReadOnlyMemory<byte>, Task>? _onChunk;

        public BackgroundJobRequest Request { get; } = new()
        {
            Kind = kind,
            Label = label,
            Executable = "sh",
            Arguments = ["-c", label],
            WorkingDirectory = Path.GetTempPath(),
        };

        public bool HasExited { get; private set; }
        public int? ExitCode { get; private set; }
        public int? Pid => 4242;
        public Task Output => Task.CompletedTask;

        public Task WaitForExitAsync(CancellationToken ct = default) => _exit.Task;

        public void Kill()
        {
            Exit(137);
        }

        public async Task<string> AdoptAsync(Func<ReadOnlyMemory<byte>, Task> onChunk)
        {
            _onChunk = onChunk;
            if (head.Length > 0) await onChunk(System.Text.Encoding.UTF8.GetBytes(head));
            return head;
        }

        /// <summary>交出去之后「进程又吐了一段」。</summary>
        public void Emit(string text)
        {
            Assert.NotNull(_onChunk);
            _onChunk(System.Text.Encoding.UTF8.GetBytes(text)).GetAwaiter().GetResult();
        }

        public void Exit(int code)
        {
            ExitCode = code;
            HasExited = true;
            _exit.TrySetResult();
        }

        public void Dispose() { }

    }

    /// <summary>读一份后台日志（会话固定 `s1`）。⚠️ 文件可能还没建出来 ⇒ 空串，不是 null 炸掉。</summary>
    private static string ReadJob(BackgroundJobHost jobs, string id) =>
        jobs.Read("s1", id, 0, 4096)?.Text ?? string.Empty;

    private static string TextOf(TimelineEvent.ToolResultEvent result) =>
        string.Join("\n", result.Content.OfType<Block.TextBlock>().Select(b => b.Text));

    private static string TextOf(ToolOutcome outcome) =>
        string.Join("\n", outcome.Content.OfType<Block.TextBlock>().Select(b => b.Text));

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
    }
}
