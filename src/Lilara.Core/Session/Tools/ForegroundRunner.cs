using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Lilara.Core.Kernel;

namespace Lilara.Core.Session.Tools;

/// <summary>
/// **一次前台进程调用的结果**（2026-11-07）。
///
/// <para>⚠️ <b>第五格 <see cref="Promoted"/> 是本批新增的那一档</b>：它非 null ⇒ 这次调用
/// **没有结束**，而是被交给了后台宿主继续跑（<see cref="IRunningProcess"/>）。那时
/// <see cref="ExitCode"/> 没有意义、<see cref="Stdout"/> / <see cref="Stderr"/> 只是**交接那一刻**
/// 已经读到的那一段（工具可以把它们附在结果里，也可以不提——日志里是从头开始的那一份）。</para>
/// </summary>
internal sealed record ForegroundResult(
    string Stdout, string Stderr, int ExitCode, bool TimedOut, BackgroundJobInfo? Promoted);

/// <summary>
/// **跑一个前台进程**——`shell` 与 `dotnet_*` 共用这一份（2026-11-07）。
///
/// <para>⚠️ <b>它存在，是因为这两族工具都要做同一件难事</b>：到点之后**要么终止、要么把活着的
/// 进程交给后台**，而判据、读泵、取消、排空、清理的顺序只要有两份实现，就一定有一份是错的
/// ——而错法**不会报错**：输出少一截、进程成了孤儿、或者同一条命令被跑了两遍。</para>
///
/// <para>⚠️ <b>为什么不能「统一先起成后台任务再前台等」</b>：前台的结果按 **stdout / stderr
/// 分段**（`shell` 的「stderr:」那一段），而后台任务的日志是**合并**的一股。
/// 改掉就等于改模型看到的形状——那是「顺手改坏了别的功能」的典型。</para>
/// </summary>
internal static class ForegroundRunner
{
    /// <summary>
    /// 起进程并等它。⚠️ <b>四条出口</b>：
    /// <list type="number">
    ///   <item>它自己结束 ⇒ 交回退出码与两条输出。</item>
    ///   <item>超时且 <paramref name="tryAdopt"/> 成功接管 ⇒ <see cref="ForegroundResult.Promoted"/>
    ///   非 null（进程**归宿主**：这里不杀、不 dispose）。</item>
    ///   <item>超时且接管不了 ⇒ 杀掉整棵树，交回已经读到的输出 + <c>TimedOut</c>。</item>
    ///   <item>外层取消（用户打断）⇒ 杀掉，抛 <see cref="OperationCanceledException"/>。</item>
    /// </list>
    /// </summary>
    /// <param name="start">启动参数。⚠️ 由调用方备好（`sh` 那条还带着 `-l -c` 与 `LC_ALL`）。</param>
    /// <param name="label">起不来时那句人话里点名的东西（可执行文件名）。</param>
    /// <param name="timeout">这一次的时限。到点：能转后台就转，不能就杀。</param>
    /// <param name="requestOf">
    /// **转后台时的元数据**（kind / label / 工作目录）。⚠️ 由工具给：只有它知道自己是 `shell`
    /// 还是 `dotnet_build`。⚠️ <b>`TimeoutSeconds` 必须留空</b>——转后台之后按后台默认（不限时）算。
    /// </param>
    /// <param name="tryAdopt">
    /// **怎么把它交出去**。⚠️ <c>null</c> = 这个工具**没有异步模式** ⇒ 到点就是杀
    /// （用户裁定的两分法）。返回值 <c>null</c> = 交不出去（没归属 / 登记失败）⇒ 也杀。
    /// </param>
    public static async Task<ForegroundResult> RunAsync(
        ProcessStartInfo start,
        string label,
        TimeSpan timeout,
        Func<BackgroundJobRequest>? requestOf,
        Func<IRunningProcess, BackgroundJobInfo?>? tryAdopt,
        CancellationToken ct)
    {
        var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new Win32Exception($"无法启动 {label}");
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            // ⚠️ 起不来的那个 `Process` **必须释放**：它已经拿到了句柄，等 GC 是浪费。
            process.Dispose();
            throw new ShellStartException($"无法启动 `{label}`：{ex.Message}");
        }

        // ⚠️ **stdin 必须是一个已关闭的管道**（2026-10-23 实测）：继承核心的控制台会让
        // 某些组合下的 `sh` 挂在等输入上，而症状是「命令卡到超时」。
        // ⚠️ 只在**调用方要了 stdin** 时才关：`dotnet` 那条不重定向 stdin（它不读），
        // 而对着一个没重定向的 `Process` 取 `StandardInput` 会**直接抛**
        // （`StandardIn has not been redirected`——这条是实测踩出来的）。
        if (start.RedirectStandardInput)
        {
            try { process.StandardInput.Close(); }
            catch (IOException) { }
        }

        var half = ToolLimits.MaxToolOutputBytes / 2;
        var stdout = new OutputTap(half);
        var stderr = new OutputTap(half);

        var pump = Task.WhenAll(
            stdout.PumpAsync(process.StandardOutput.BaseStream),
            stderr.PumpAsync(process.StandardError.BaseStream));

        // ⚠️ 排空那条路**必须观察异常**，否则「读管道时 IO 断了」会变成一条没人看的未观测异常。
        _ = pump.ContinueWith(
            t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        var running = new ForegroundProcess(process, pump, stdout, stderr);

        var adopted = false;
        try
        {
            // ⚠️ **读取只跟外层的取消走，不跟超时走**：到点那一下要先把进程树处理掉
            //（转后台或杀掉），再把**已经拿到的输出**如实交回——那常常已经能说明卡在哪。
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutSource.CancelAfter(timeout);

            try
            {
                await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                KillTree(process);
                throw;
            }
            catch (OperationCanceledException)
            {
                // ⚠️ **超时是结果，不是异常**——两种结局由「有没有异步模式」分（用户裁定）。
                if (tryAdopt is not null && requestOf is not null)
                {
                    running.Request = requestOf();
                    if (tryAdopt(running) is { } job)
                    {
                        // ⚠️ 交出去了：**这一格里不能杀、不能 dispose**，进程与读泵都归宿主。
                        adopted = true;
                        return new ForegroundResult(stdout.Text, stderr.Text, -1, false, job);
                    }
                }

                KillTree(process);
                await DrainAsync(stdout, stderr).ConfigureAwait(false);
                return new ForegroundResult(stdout.Text, stderr.Text, -1, true, null);
            }
            catch (IOException ex)
            {
                KillTree(process);
                throw new ShellStartException($"读取 `{label}` 的输出失败：{ex.Message}；进程已终止。");
            }

            // ⚠️ 退出之后还要等读泵：管道里可能还剩最后一段（`echo bye` 的最后一行）。
            // 而它有界（2 秒）——孙进程攥着管道时 EOF 可能永远不来。
            await Task.WhenAny(pump, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
            return new ForegroundResult(stdout.Text, stderr.Text, process.ExitCode, false, null);
        }
        finally
        {
            if (!adopted) process.Dispose();
        }
    }

    /// <summary>
    /// 超时之后把两条读泵的**已完成部分**收回来。⚠️ 有上限（3 秒）：一个孙进程若还攥着管道，
    /// 杀树之后读取可能永远等不到 EOF——而这里是「超时处理」，不能再挂一个新的超时。
    /// </summary>
    private static async Task DrainAsync(OutputTap stdout, OutputTap stderr)
    {
        var all = Task.WhenAll(stdout.Done, stderr.Done);
        await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false);
    }

    /// <summary>⚠️ 整棵树：`sh -c 'make …'` 的孙进程不会随父进程一起走。</summary>
    internal static void KillTree(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }

    /// <summary>
    /// **「到点转后台」那条结果怎么写**（2026-11-07）。`shell` 与 `dotnet_*` **共用这一份**：
    /// 同一个结局在两处各写一遍，措辞迟早会漂——而模型是**照着措辞做事**的
    /// （`wait(task=…)` 那个 id 出现在哪儿、要不要重试，全靠这几句）。
    /// </summary>
    /// <param name="keep">调用方自己的框架字段（`shell` 给 `exit`，`dotnet` 给 `project`）——
    /// 本方法在后面补上 `job` / `state`。</param>
    /// <param name="head">交接那一刻已经读到的输出。⚠️ 日志里有**从头开始**的完整一份，
    /// 所以这里明确说「这是转后台之前的那一段」，免得模型把它当成全部。</param>
    internal static ToolOutcome PromotedOutcome(
        string toolName, BackgroundJobInfo job, TimeSpan waited, string head,
        params ToolFramingField[] keep)
    {
        var text = new StringBuilder();
        text.AppendLine(
            $"超过 {waited.TotalSeconds:F0} 秒还没结束，已**转到后台继续**（任务 {job.Id}，pid {job.ProcessId}）"
            + "——它不是失败，也**没有重跑**，进程一直在原地跑。");
        text.AppendLine(
            $"⏳ 后续工作依赖它的结果时，下一步直接调 wait(task=\"{job.Id}\")（会自动等到结束，别用 jobs / job_output 轮询）；"
            + "要看日志用 job_output，要停掉用 job_kill。");
        text.AppendLine("⚠️ 转后台之后它不再受这一步的时限约束，session.interrupt 也打不到它了。");

        if (head.Length > 0)
        {
            text.AppendLine();
            text.AppendLine("转后台之前已经输出的那一段（完整的一份在日志里、从头开始）：");
            text.Append(head);
        }

        return new ToolOutcome
        {
            Content = [new Block.TextBlock(text.ToString())],
            // ⚠️ **不可重演**：再跑一次就是第二个进程。
            Fingerprint = null,
            DependsOn = null,
            Framing = new ToolFraming(toolName,
            [
                .. keep,
                new ToolFramingField("job", job.Id),
                new ToolFramingField("state", "running"),
            ]),
        };
    }

    /// <summary>
    /// **前台这一侧的 <see cref="IRunningProcess"/>**：进程 + 两条读泵 + 可改道的输出。
    /// ⚠️ 只有它实现 <see cref="IRunningProcess.AdoptAsync"/>（宿主自己起的那些不走这条路：
    /// 它们的输出从一开始就直接进日志）。
    /// </summary>
    private sealed class ForegroundProcess(
        Process process, Task pump, OutputTap stdout, OutputTap stderr) : IRunningProcess
    {
        /// <summary>
        /// 交给宿主的元数据。⚠️ 在 <see cref="AdoptAsync"/> 之前由运行器填好
        /// （工具通过 <c>requestOf</c> 提供）。
        /// </summary>
        public BackgroundJobRequest Request { get; set; } = null!;

        public bool HasExited
        {
            get { try { return process.HasExited; } catch (InvalidOperationException) { return true; } }
        }

        public int? ExitCode
        {
            get
            {
                try { return process.HasExited ? process.ExitCode : null; }
                catch (InvalidOperationException) { return null; }
            }
        }

        public int? Pid
        {
            get { try { return process.Id; } catch (InvalidOperationException) { return null; } }
        }

        public Task WaitForExitAsync(CancellationToken ct = default) => process.WaitForExitAsync(ct);

        public Task Output => pump;

        public void Kill() => KillTree(process);

        public async Task<string> AdoptAsync(Func<ReadOnlyMemory<byte>, Task> onChunk)
        {
            // ⚠️ **两条流分别改道，但都指向同一个日志**——于是日志里是 stdout / stderr
            // 按到达次序交错的一股（与后台任务那条路**同一个形状**，`job_output` 读它）。
            var head = await stdout.AdoptAsync(onChunk).ConfigureAwait(false);
            var err = await stderr.AdoptAsync(onChunk).ConfigureAwait(false);
            return head + err;
        }

        public void Dispose() => process.Dispose();
    }
}

/// <summary>
/// **一条管的读泵 + 可改道的输出**（2026-11-07）。
///
/// <para>⚠️ <b>为什么不用 `StreamReader` 那一套</b>：交接给后台宿主时，后续的字节**必须原样**
/// 写进 `job.log`，而 `StreamReader` 已经把字节解成了 char（还自带一层缓冲区）——改道时
/// 那一层里剩下的东西**拿不回来**，症状就是日志缺一段开头或中间，而它看起来完全正常。</para>
///
/// <para>⚠️ <b>两件事共用一把闸 <c>_gate</c></b>：追加正文、以及「改道并先把旧的交出去」。
/// 改道必须在闸内把旧的那一份交完，否则一个并发到达的字节块会**插到开头那一段前面**
/// ——日志的次序就错了，而 `job_output` 的 offset 是按字节算的，错序之后没法察觉。</para>
/// </summary>
internal sealed class OutputTap(int maxBytes)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
    private readonly StringBuilder _text = new();
    private readonly char[] _chars = new char[8192];
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Func<ReadOnlyMemory<byte>, Task>? _tee;
    private int _bytes;

    /// <summary>读泵跑完（管道 EOF）。⚠️ 交接之后它仍然有效（读泵没停，只是目的地变了）。</summary>
    public Task Done => _done.Task;

    /// <summary>
    /// 已经读到的正文（**保留头部**，最多 <c>maxBytes</c> 字节）。
    /// ⚠️ 改道之后它不再增长——那时正文在日志里。
    /// ⚠️ 读它要拿闸：改道那条路在闸内 `await` 写盘，这里等一等就行（不会死锁：
    /// 没有线程亲和，闸只护住一段短暂的临界区）。
    /// </summary>
    public string Text
    {
        get
        {
            _gate.Wait();
            try { return _text.ToString(); }
            finally { _gate.Release(); }
        }
    }

    public async Task PumpAsync(Stream stream)
    {
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    // 流被关掉 = 进程退出了。这不是错误，是**结束**。
                    return;
                }

                if (read == 0) return;

                Func<ReadOnlyMemory<byte>, Task>? tee;
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    tee = _tee;
                    if (tee is null) AppendHead(buffer.AsSpan(0, read));
                }
                finally
                {
                    _gate.Release();
                }

                // ⚠️ **在闸外、且在下一次读之前 `await`**：`buffer` 会被下一轮复用，
                // 而写盘那一刻它还是这一块的内容（宿主的 `Append` 是同步的，返回 `Task.CompletedTask`）。
                if (tee is not null) await tee(buffer.AsMemory(0, read)).ConfigureAwait(false);
            }
        }
        finally
        {
            _done.TrySetResult();
        }
    }

    /// <summary>
    /// **改道给 <paramref name="tee"/>，并先把已经攒下的那一份交出去**。
    /// 返回那一份的文本（工具拿它写进结果）。⚠️ 见类型注释那条次序判据。
    /// </summary>
    public async Task<string> AdoptAsync(Func<ReadOnlyMemory<byte>, Task> tee)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var head = _text.ToString();
            _text.Clear();
            _bytes = 0;
            _tee = tee;
            if (head.Length > 0)
                await tee(Encoding.UTF8.GetBytes(head)).ConfigureAwait(false);
            return head;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 解码并**保留到上限为止**。⚠️ 上限按解出来的 UTF-8 字节算（与原先
    /// `ShRunner.ReadBoundedAsync` 那条判据逐字相同：`ToolLimits.MaxToolOutputBytes` 的一半）。
    /// ⚠️ 过了上限**继续排空管道**（不排空的话，子进程写满管道缓冲会自己卡死），
    /// 只是不再保留——那个症状看起来完全不像「输出太多」。
    /// </summary>
    private void AppendHead(ReadOnlySpan<byte> chunk)
    {
        if (_bytes >= maxBytes) return;

        var count = _decoder.GetChars(chunk, _chars, flush: false);
        if (count == 0) return;

        var text = new string(_chars, 0, count);
        var bytes = Encoding.UTF8.GetByteCount(text);
        if (_bytes + bytes <= maxBytes)
        {
            _text.Append(text);
            _bytes += bytes;
            return;
        }

        // 到上限：按**字符边界**截到剩下的空间，之后不再保留。
        var remaining = maxBytes - _bytes;
        var take = text.Length;
        while (take > 0 && Encoding.UTF8.GetByteCount(text.AsSpan(0, take)) > remaining) take--;
        _text.Append(text, 0, take);
        _bytes = maxBytes;
    }
}
