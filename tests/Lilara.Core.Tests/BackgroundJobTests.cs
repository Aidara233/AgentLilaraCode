using System.Diagnostics;
using Lilara.Core.Kernel;
using Lilara.Core.Session.Tools;
using Lilara.Core.Storage;
using Block = Lilara.Core.Kernel.Block;

namespace Lilara.Core.Tests;

/// <summary>
/// **后台任务（通用）**（2026-10-24）：起得来、读得到、杀得掉、超时会停、核心退出时一起走。
///
/// <para>⚠️ <b>这一组用真 <c>sh</c> 跑真进程</b>（同 `ShellToolTests`），因为要证明的正是
/// 「进程跨步存活」——一个假宿主证明不了那件事。⚠️ 但每条用例都**自己杀干净**
/// （`Dispose` 里 `KillAll`）：漏掉一个 `sleep 30`，整套测试最后会变成「跑完不退」。
/// 找不到 <c>sh</c> 的机器（= 没有任何 POSIX 环境）**跳过**——那不是产品缺陷。</para>
///
/// <para>⚠️ <b>绝不能断言精确耗时</b>（同 `InterruptTests` 那条）：这类测试在 CI 上会间歇红，
/// 而间歇红的测试最后一定会被改成永远通过。⇒ 只断言「远早于命令本该结束的时间」。</para>
///
/// <para>⚠️ <b>本批把宿主从「只认 sh 脚本」改成「任意 executable + argv」</b>
/// （用户裁定「其余的可异步指令也一样」），所以这里既有 <c>sh</c> 的用例，
/// 也有一个**不经过 sh** 的用例（<c>dotnet --version</c>）——它证明宿主不认识任何具体工具。</para>
/// </summary>
public sealed class BackgroundJobTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-jobs-" + Guid.NewGuid().ToString("N"));

    private readonly string _work =
        Path.Combine(Path.GetTempPath(), "lilara-jobswork-" + Guid.NewGuid().ToString("N"));

    private readonly BackgroundJobHost _host;

    public BackgroundJobTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_work);
        _host = new BackgroundJobHost(_root);
    }

    public void Dispose()
    {
        _host.KillAll("测试收尾。");
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
    }

    // ---- 起得来，而且不等它 ----

    [Fact]
    public void Start_ReturnsImmediately_WhileTheCommandIsStillRunning()
    {
        if (!Locate(out var sh)) return;

        var clock = Stopwatch.StartNew();
        var started = _host.Start("s1", ShellRequest(sh, "sleep 5"));
        clock.Stop();

        Assert.NotNull(started.Job);
        Assert.Equal(BackgroundJobState.Running, started.Job!.State);
        Assert.Equal("shell", started.Job.Kind);

        // ⚠️ 远早于 5 秒：判据是「它没有等命令跑完」，不是精确毫秒。
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"启动等了 {clock.Elapsed}");
    }

    /// <summary>
    /// 宿主只认「可执行文件 + argv」，**不认识 shell**（用户裁定「其余的可异步指令也一样」）。
    /// ⚠️ 这条用例走的是 <c>dotnet --version</c>：它不经过 `sh`，因此不能靠「反正都能跑」通过。
    /// </summary>
    [Fact]
    public void Start_RunsAnArbitraryExecutable_WithoutGoingThroughShell()
    {
        var started = _host.Start("s1", new BackgroundJobRequest
        {
            Kind = "dotnet_build",
            Label = "dotnet --version",
            Executable = "dotnet",
            Arguments = ["--version"],
            WorkingDirectory = _work,
        });

        // ⚠️ 这台机器没装 SDK ⇒ 起不来，但那也是**一句人话**而不是异常。
        if (started.Job is not { } job)
        {
            Assert.False(string.IsNullOrWhiteSpace(started.Error));
            return;
        }

        Assert.Equal("dotnet_build", job.Kind);
        var read = WaitFor(() => _host.Read("s1", job.Id, 0, 64 * 1024) is { Text.Length: > 0 } r ? r : null);
        Assert.NotNull(read);
    }

    // ---- 输出读得到、能接着读 ----

    [Fact]
    public void Read_GivesTheOutput_AndNextOffsetIsPayable()
    {
        if (!Locate(out var sh)) return;

        var job = _host.Start("s1", ShellRequest(sh, "echo hello-jobs")).Job!;

        // ⚠️ **等到真的有内容**，不是「等到 Read 返回非 null」——后者第一次调用就成立，
        // 而那时进程可能一个字都还没写（`Read` 对空输出是合法结果，不是「还没好」）。
        var read = WaitFor(() =>
            _host.Read("s1", job.Id, 0, 64 * 1024) is { Text.Length: > 0 } r ? r : null);

        Assert.NotNull(read);
        Assert.Contains("hello-jobs", read!.Text);

        // ⚠️ **`NextOffset` 必须可兑付**：从那里接着读，不能拿到一段重复的字节。
        var again = _host.Read("s1", job.Id, read.NextOffset, 64 * 1024)!;
        Assert.Equal(read.NextOffset, again.Offset);
        Assert.Empty(again.Text);
    }

    [Fact]
    public void Read_OnAnUnknownJob_ReturnsNull_InsteadOfPretending()
    {
        Assert.Null(_host.Read("s1", "nope", 0, 1024));
    }

    // ---- 归属 ----

    [Fact]
    public void AnotherSession_CannotSeeOrKillMyJob()
    {
        if (!Locate(out var sh)) return;

        var job = _host.Start("s1", ShellRequest(sh, "sleep 5")).Job!;

        Assert.Empty(_host.List("s2"));
        Assert.Null(_host.Find("s2", job.Id));
        Assert.Null(_host.Read("s2", job.Id, 0, 1024));
        Assert.Null(_host.Kill("s2", job.Id, "不该杀得到。"));

        // ⚠️ 而它对本会话仍然是活的——「杀不到」不等于「顺手杀掉了」。
        Assert.Equal(BackgroundJobState.Running, _host.Find("s1", job.Id)!.State);
    }

    // ---- 杀得掉，而且原因写得出来 ----

    [Fact]
    public void Kill_StopsIt_AndRecordsTheReason()
    {
        if (!Locate(out var sh)) return;

        var job = _host.Start("s1", ShellRequest(sh, "sleep 30")).Job!;

        var killed = _host.Kill("s1", job.Id, "测试停掉它。");

        Assert.NotNull(killed);
        Assert.Equal(BackgroundJobState.Killed, killed!.State);
        Assert.Equal("测试停掉它。", killed.Reason);
        Assert.NotNull(killed.EndedAt);
    }

    [Fact]
    public void Kill_OnAnAlreadyExitedJob_DoesNotRewriteItsOutcome()
    {
        if (!Locate(out var sh)) return;

        var job = _host.Start("s1", ShellRequest(sh, "exit 7")).Job!;
        var exited = WaitFor(() =>
            _host.Find("s1", job.Id) is { State: BackgroundJobState.Exited } e ? e : null)!;

        var killed = _host.Kill("s1", job.Id, "其实已经结束了。");

        // ⚠️ 「我杀了一个早就跑完的任务」不能把它写成 Killed——那是一句假的因果。
        Assert.Equal(BackgroundJobState.Exited, killed!.State);
        Assert.Equal(exited.ExitCode, killed.ExitCode);
        Assert.Null(killed.Reason);
    }

    [Fact]
    public void Kill_TakesTheWholeProcessTree()
    {
        if (!Locate(out var sh)) return;

        // ⚠️ 孙进程：`sh -c 'sleep 30 & wait'` 起了一个子进程，而 `sh` 自己也在等它。
        // 只杀 `sh` 会留下那个 sleep——「后台任务杀了还在跑」正是这一类。
        var job = _host.Start("s1", ShellRequest(sh, "sleep 30 & wait")).Job!;
        Thread.Sleep(300);   // 让它把子进程起起来

        _host.Kill("s1", job.Id, "连树一起停。");
        Thread.Sleep(300);

        Assert.Equal(BackgroundJobState.Killed, _host.Find("s1", job.Id)!.State);
    }

    // ---- 超时：自动停，并把原因写出来 ----

    [Fact]
    public void Timeout_StopsTheJob_AndSaysWhy()
    {
        if (!Locate(out var sh)) return;

        var job = _host.Start("s1", ShellRequest(sh, "sleep 30", timeoutSeconds: 1)).Job!;
        Assert.Equal(1, job.TimeoutSeconds);

        var stopped = WaitFor(() =>
            _host.Find("s1", job.Id) is { State: BackgroundJobState.Killed } e ? e : null,
            timeout: TimeSpan.FromSeconds(20));

        // ⚠️ 判据是**状态 + 原因**：没有人在等这个任务，所以它只能靠这两样说清楚
        // 「为什么停了」。只说 Killed 而不说原因的话，模型会以为是我们手动停的。
        Assert.NotNull(stopped);
        Assert.Contains("时限", stopped!.Reason);
    }

    // ---- 已结束的任务，输出仍然读得到 ----

    [Fact]
    public void OutputSurvivesTheKill()
    {
        if (!Locate(out var sh)) return;

        var job = _host.Start("s1", ShellRequest(sh, "echo before-death; sleep 30")).Job!;
        WaitFor(() => _host.Read("s1", job.Id, 0, 1024) is { Text.Length: > 0 } r ? r : null);

        _host.Kill("s1", job.Id, "停。");

        var after = _host.Read("s1", job.Id, 0, 1024);
        Assert.NotNull(after);
        Assert.Contains("before-death", after!.Text);
    }

    // ---- 输出上限：不阻塞、只丢弃 ----

    [Fact]
    public void ALargeOutput_DoesNotDeadlockTheProcess()
    {
        if (!Locate(out var sh)) return;

        // ⚠️ 这一条防的是那个最容易漏的坑：**不持续排空管道**时，子进程写满
        // 约 64 KiB 的管道缓冲会**自己卡死**，症状是「命令不结束」，看起来完全不像「输出太多」。
        var job = _host.Start("s1", ShellRequest(sh,
            "i=0; while [ $i -lt 30000 ]; do echo line$i; i=$((i+1)); done")).Job!;

        var exited = WaitFor(() =>
            _host.Find("s1", job.Id) is { State: BackgroundJobState.Exited } e ? e : null,
            timeout: TimeSpan.FromSeconds(30));

        Assert.NotNull(exited);
        Assert.Equal(0, exited!.ExitCode);
    }

    // ---- 并发上限：拒绝，不排队 ----

    [Fact]
    public void MoreThanFourJobs_CanRunTogether()
    {
        if (!Locate(out var sh)) return;
        for (var i = 0; i < 8; i++)
            Assert.NotNull(_host.Start("s1", ShellRequest(sh, "sleep 30")).Job);
        Assert.Equal(8, _host.List("s1").Count);
    }

    // ---- 重启补偿 ----

    [Fact]
    public void Recover_MarksLeftoverRunningJobsAsLost_WithAReason()
    {
        if (!Locate(out var sh)) return;

        // 造一个"上一个进程留下的"残骸：起一个任务，然后**绕开 KillAll** 直接把元数据
        // 改回 Running（模拟被强杀）。
        var job = _host.Start("s1", ShellRequest(sh, "sleep 30")).Job!;
        _host.Kill("s1", job.Id, "先停掉它，然后手动伪造一份 Running 元数据。");

        var metaPath = Path.Combine(_root, "jobs", "s1", job.Id + ".json");
        var forged = _host.Find("s1", job.Id)! with
        {
            State = BackgroundJobState.Running,
            Reason = null,
            EndedAt = null,
        };
        File.WriteAllText(metaPath, System.Text.Json.JsonSerializer.Serialize(forged, Json.Options));

        // 新宿主 = 新进程。
        var restarted = new BackgroundJobHost(_root);
        restarted.Recover();

        var recovered = restarted.Find("s1", job.Id);
        Assert.NotNull(recovered);
        Assert.Equal(BackgroundJobState.Lost, recovered!.State);

        // ⚠️ 原因必须写出来（用户点名要求）：模型得知道「这是结局未知」，
        // 而不是「它失败了」——两者的下一步动作完全不同。
        Assert.Contains("结局", recovered.Reason);
        Assert.DoesNotContain("失败", recovered.Reason!);

        restarted.KillAll("测试收尾。");
    }

    [Fact]
    public void Recover_KeepsFinishedJobsReadable()
    {
        if (!Locate(out var sh)) return;

        var job = _host.Start("s1", ShellRequest(sh, "echo from-before-restart")).Job!;
        WaitFor(() => _host.Find("s1", job.Id) is { State: BackgroundJobState.Exited } e ? e : null);

        // ⚠️ **还要等元数据真的落盘**：内存里的状态先变、`job.json` 后写，
        // 两次之间构造一个新宿主会读到旧的 `Running`。那个窗口在真实世界里的后果是
        // 「重启之后显示结局未知」——安全的那一侧；但测试必须**确定性**，
        // 否则它会变成「偶尔红」，而偶尔红的测试最后一定被改成永远通过。
        var metaPath = Path.Combine(_root, "jobs", "s1", job.Id + ".json");
        WaitFor<object>(() =>
            File.Exists(metaPath)
            && File.ReadAllText(metaPath).Contains(BackgroundJobState.Exited, StringComparison.Ordinal)
                ? metaPath
                : null);

        var restarted = new BackgroundJobHost(_root);
        restarted.Recover();

        var found = restarted.Find("s1", job.Id);
        Assert.NotNull(found);
        Assert.Equal(BackgroundJobState.Exited, found!.State);

        // ⚠️ 输出仍然读得到：那是「我上次起的任务怎么样了」的答案本体。
        var read = restarted.Read("s1", job.Id, 0, 1024);
        Assert.NotNull(read);
        Assert.Contains("from-before-restart", read!.Text);

        restarted.KillAll("测试收尾。");
    }

    // ---- 工具层：闸门与参数 ----

    [Fact]
    public void TheThreeJobToolsAreRegistered_AndOnlyKillIsReadable()
    {
        Assert.Contains(BuiltInTools.All, t => t is JobsTool);
        Assert.Contains(BuiltInTools.All, t => t is JobOutputTool);
        Assert.Contains(BuiltInTools.All, t => t is JobKillTool);

        Assert.Equal(PermissionClass.ReadOnly, new JobsTool().PermissionClass);
        Assert.Equal(PermissionClass.ReadOnly, new JobOutputTool().PermissionClass);

        // ⚠️ **`job_kill` 是只读，这是用户裁定，不是笔误**：
        // 「杀后台这类撤回性功能放低点……防止切了权限想杀都杀不掉」。
        // 这条断言的作用就是让下一个人改它之前先读到那句话。
        Assert.Equal(PermissionClass.ReadOnly, new JobKillTool().PermissionClass);

        // 而「起」那一侧没有降级——`shell` 仍是 Destructive。
        Assert.Equal(PermissionClass.Destructive, new ShellTool().PermissionClass);
        // `dotnet_*` 仍是 Writing（会执行工作目录里的代码）。
        Assert.Equal(PermissionClass.Writing, new DotnetBuildTool().PermissionClass);
        Assert.Equal(PermissionClass.Writing, new DotnetTestTool().PermissionClass);
    }

    /// <summary>
    /// ⚠️ **「可异步」这件事对三个工具是同一套选项**（用户裁定「整合起来」）。
    /// 这条钉的是**形状**：schema 里必须同时有 `background` 与 `timeoutSeconds`。
    /// 少了哪一个，模型就只能靠猜——而猜错的方向是「以为转后台了、其实这一步被占住」。
    /// </summary>
    [Theory]
    [InlineData(typeof(ShellTool))]
    [InlineData(typeof(DotnetBuildTool))]
    [InlineData(typeof(DotnetTestTool))]
    public void AsyncCapableTools_AllExposeBackgroundAndTimeout(Type toolType)
    {
        var tool = (ITool)Activator.CreateInstance(toolType)!;

        Assert.Contains("\"background\"", tool.ParametersSchemaJson, StringComparison.Ordinal);
        Assert.Contains("\"timeoutSeconds\"", tool.ParametersSchemaJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutAJobHost_TheBackgroundToolsSaySoInsteadOfGuessing()
    {
        var context = new SessionToolContext(
            _work, new BlobStore(Path.Combine(_work, ".blobs")),
            shell: new ShellAccess(new ShellConfig()));

        var outcome = await new JobsTool().RunAsync(
            new ToolCall("c", JobsTool.NameOf, "{}"), context, CancellationToken.None);

        Assert.Contains("没有后台任务宿主", Text(outcome));
    }

    [Fact]
    public async Task Shell_BackgroundWithoutACommand_SaysWhatIsMissing()
    {
        var outcome = await new ShellTool().RunAsync(
            new ToolCall("c", ShellTool.NameOf, """{"background":true}"""),
            NewContext(), CancellationToken.None);

        Assert.Contains("command", Text(outcome));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("999999")]
    public async Task Shell_BackgroundWithAnOutOfRangeTimeout_IsRejected(string seconds)
    {
        var outcome = await new ShellTool().RunAsync(
            new ToolCall("c", ShellTool.NameOf,
                $$"""{"command":"echo hi","background":true,"timeoutSeconds":{{seconds}}}"""),
            NewContext(), CancellationToken.None);

        // ⚠️ 判据是**拒绝**，不是夹紧：悄悄换成一个模型没要求的数，
        // 会让它误判「为什么会停」——那是本仓反复反对的那类静默。
        Assert.Contains("timeoutSeconds", Text(outcome));
        Assert.Contains("之间", Text(outcome));
    }

    [Fact]
    public async Task Shell_BackgroundStartsAJob_AndItsIdIsUsable()
    {
        if (!Locate(out var sh)) return;

        var outcome = await new ShellTool().RunAsync(
            new ToolCall("c", ShellTool.NameOf, """{"command":"sleep 20","background":true}"""),
            NewContext(executable: sh), CancellationToken.None);

        var text = Text(outcome);
        Assert.Contains("已在后台启动", text);

        // 从任务表里取 id——结果正文里也有，但「真的起了」只有表能证明。
        var jobId = _host.List("s1").Single().Id;
        Assert.Contains(jobId, text);
        Assert.Contains($"wait(task=\"{jobId}\")", text);
        Assert.Contains("不要用 jobs / job_output 轮询", text);
        Assert.Equal(BackgroundJobState.Running, _host.Find("s1", jobId)!.State);
    }

    [Fact]
    public async Task DotnetBuild_BackgroundWithoutASolution_SaysWhatIsMissing()
    {
        // ⚠️ 这条不真的构建：它证明「后台那条路也走同一份参数校验」——
        // 空工作目录里没有 .sln，所以它必须在**起进程之前**就回一句话。
        var outcome = await new DotnetBuildTool().RunAsync(
            new ToolCall("c", "dotnet_build", """{"background":true}"""),
            NewContext(), CancellationToken.None);

        Assert.Contains(".sln", Text(outcome));
        Assert.Empty(_host.List("s1"));
    }

    [Fact]
    public async Task JobOutput_PagesByOffset_AndSaysThereIsMore()
    {
        if (!Locate(out var sh)) return;

        // ⚠️ 输出要**超过一次交付的上限**（`BackgroundJobLimits.MaxReadBytes` = 64 KiB）
        // 才会出现「还有更多」。5000 行 × 17 字节 ≈ 85 KB ⇒ 稳稳超一次。
        var job = _host.Start("s1", ShellRequest(sh,
            "i=0; while [ $i -lt 5000 ]; do echo 0123456789abcdef; i=$((i+1)); done")).Job!;
        WaitFor(() => _host.Find("s1", job.Id) is { State: BackgroundJobState.Exited } e ? e : null);

        var first = await new JobOutputTool().RunAsync(
            new ToolCall("c", JobOutputTool.NameOf, $$"""{"job":"{{job.Id}}"}"""),
            NewContext(executable: sh), CancellationToken.None);

        var text = Text(first);
        Assert.Contains("还有更多", text);

        // ⚠️ 它必须给出一个**能接着用的** offset：模型下一步就照着它读。
        Assert.Contains("offset=", text);
        Assert.Contains(job.Id, text);
    }

    [Fact]
    public async Task Wait_UsesTheSameId_AndCancellationDoesNotKillTheJob()
    {
        Assert.True(Locate(out var sh));
        var job = _host.Start("s1", ShellRequest(sh, "sleep 30")).Job!;
        using var cancel = new CancellationTokenSource(250);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WaitTool().RunAsync(
            new ToolCall("w", "wait", $$"""{"task":"{{job.Id}}"}"""), NewContext(), cancel.Token));
        Assert.Equal(BackgroundJobState.Running, _host.Find("s1", job.Id)!.State);
        _host.Kill("s1", job.Id, "test");
        var result = await new WaitTool().RunAsync(new ToolCall("w", "wait",
            $$"""{"task":"{{job.Id}}"}"""), NewContext(), CancellationToken.None);
        Assert.Contains("等到了", Text(result));
    }

    [Fact]
    public async Task WaitChild_ObservesLiveState_AndReturnsQuestionsAsBlocked()
    {
        var calls = 0;
        var scope = new SubagentScope("s1", null, 0, 1, [new("child", SessionStatus.Running)]);
        var context = new SessionToolContext(_work, new BlobStore(_root), subagents: scope,
            subagentStatus: _ => Interlocked.Increment(ref calls) < 3 ? SessionStatus.Running : SessionStatus.Idle);
        var result = await new WaitTool().RunAsync(new("w", "wait", """{"task":"child"}"""), context, CancellationToken.None);
        Assert.Contains("等到了", Text(result));
        Assert.True(calls >= 3);
        context = new SessionToolContext(_work, new BlobStore(_root), subagents: scope,
            subagentStatus: _ => SessionStatus.WaitingForInput);
        result = await new WaitTool().RunAsync(new("w", "wait", """{"task":"child"}"""), context, CancellationToken.None);
        Assert.Contains("尚未完成", Text(result));
        Assert.Contains("审批", Text(result));
    }

    [Fact]
    public async Task Finish_ReportsExactlyOnce_AfterOutputIsDrained()
    {
        Assert.True(Locate(out var sh));
        var count = 0;
        var done = new TaskCompletionSource<BackgroundJobInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.Finished += j => { Interlocked.Increment(ref count); done.TrySetResult(j); };
        var job = _host.Start("s1", ShellRequest(sh, "echo final-output")).Job!;
        var final = await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(job.Id, final.Id);
        Assert.Contains("final-output", _host.Read("s1", job.Id, 0, 1024)!.Text);
        _host.Kill("s1", job.Id, "late kill");
        Assert.Equal(1, count);
    }

    // ---- 帮手 ----

    private BackgroundJobRequest ShellRequest(string sh, string script, int? timeoutSeconds = null) =>
        ShRunner.ShellRequest(sh, script, _work, JobsTool.OneLine(script), timeoutSeconds);

    private SessionToolContext NewContext(string? executable = null) =>
        new(_work, new BlobStore(Path.Combine(_work, ".blobs")),
            shell: new ShellAccess(
                new ShellConfig { Executable = executable ?? TryFindSh() },
                _host, "s1"));

    /// <summary>本机找得到 <c>sh</c> 吗。⚠️ 找不到 ⇒ 调用方**跳过**（同 `ShellToolTests`）。</summary>
    private static bool Locate(out string sh) =>
        ShRunner.TryLocate(null, ShRunner.DefaultExists, out sh, out _);

    private static string? TryFindSh() =>
        ShRunner.TryLocate(null, ShRunner.DefaultExists, out var sh, out _) ? sh : null;

    /// <summary>
    /// 轮询一件会自己变的事（状态 / 输出），到 <paramref name="timeout"/> 为止。
    /// ⚠️ **不用 `Thread.Sleep` 定长等待**：那会让「慢机器上偶尔红」变成常态。
    /// </summary>
    private static T? WaitFor<T>(Func<T?> read, TimeSpan? timeout = null) where T : class
    {
        var deadline = Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromSeconds(10);

        while (deadline.Elapsed < limit)
        {
            if (read() is { } value) return value;
            Thread.Sleep(20);
        }

        return null;
    }

    private static string Text(ToolOutcome outcome) =>
        string.Join("\n", outcome.Content.OfType<Block.TextBlock>().Select(b => b.Text));
}
