using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Lilara.Core.Kernel;
using Lilara.Core.Storage;

namespace Lilara.Core.Session.Tools;

/// <summary>
/// **后台任务的进程表**——<see cref="IBackgroundJobs"/> 的唯一实现（2026-10-24）。
///
/// <para>⚠️ <b>它是进程级的一个对象</b>（一个数据根一份），不是每会话一份：
/// 进程句柄属于这个进程，而任务按 <c>sessionId</c> 归属。⚠️ 这与 `LiveSessions` 同族
/// ——判据是「它描述的是这个进程的现在，而不是某个会话的历史」⇒ **不落盘它的"在跑"状态**
/// （落盘那份只用来在重启之后说「我们丢了什么」）。</para>
///
/// <para>⚠️ <b>它不认识任何一种具体工具</b>：只认「可执行文件 + 参数列表」。
/// 那是 2026-10-24 用户裁定的结果（「其余的可异步指令也一样」）——`shell` 传
/// <c>[-l, -c, 脚本]</c>，`dotnet_build` 传它自己拼好的 argv，两边走同一条起进程的路。
/// ⇒ 加第三个可后台的工具时**这个文件一个字都不用改**。</para>
///
/// <para>⚠️ <b>两处盘上的东西，用途完全不同，别合并</b>：</para>
/// <list type="bullet">
///   <item><c>&lt;jobId&gt;.log</c>：**输出正文**。只追加、有上限（<see cref="BackgroundJobLimits.MaxOutputBytes"/>）。
///   它必须落盘——一个跑了几分钟的任务，输出可能几 MB，而模型只在最后来读一次。</item>
///   <item><c>&lt;jobId&gt;.json</c>：**元数据**（<see cref="BackgroundJobInfo"/>）。原子重写。
///   它存在的**唯一理由**是重启之后还能回答「这里曾经有个任务，结局未知」——
///   进程句柄没了，"在跑"这件事就只剩盘上那一格。</item>
/// </list>
/// </summary>
public sealed class BackgroundJobHost(string dataRoot) : IBackgroundJobs
{
    /// <summary>任务 id → 那一份。⚠️ 结束的任务**留在表里**：模型回头还要问它怎么样了。</summary>
    private readonly ConcurrentDictionary<string, Entry> _jobs = new(StringComparer.Ordinal);

    private readonly string _root = Path.Combine(dataRoot, "jobs");

    /// <summary>
    /// 退出收尾期间**不发终态通知**：那时进程正在关，唤醒一个会话去跑一步模型调用
    /// 是**最坏的一条**（它会被随后的 `Dispose` 打断，而用户已经按了退出）。
    /// </summary>
    private volatile bool _quiet;

    public event Action<BackgroundJobInfo>? Finished;

    /// <summary>
    /// 广播一次终态。⚠️ <b>吞掉订阅方的异常</b>：这里跑在「状态已经写对之后」，
    /// 抛出去只会变成一条没人观测的后台任务异常，而且会**跳过**紧随其后的 `Persist`。
    /// </summary>
    private void PublishFinished(BackgroundJobInfo job)
    {
        if (_quiet) return;

        try { Finished?.Invoke(job); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
        }
    }

    /// <summary>一个任务在本进程里的全部句柄。</summary>
    private sealed class Entry
    {
        public required BackgroundJobInfo Info { get; set; }

        /// <summary>
        /// **进程本体**。⚠️ <b>可空、而且会变</b>：`Start` 必须先把这一项登记进表
        /// （读泵的第一个回调可能比它更早到），那时进程还没起完；重启捡回来的任务是 `null`。
        ///
        /// <para>⚠️ <b>2026-11-07 起它是 <see cref="IRunningProcess"/> 而不是 <c>Process</c></b>：
        /// 前台调用超时被**接管**过来的进程也住这一格（`Adopt`），而它必须是同一套监视 / 杀法
        /// ——两套写法里必有一套在「等退出 + 排空 + 写终态」的顺序上出错，而那个错**不报错**。</para>
        /// </summary>
        public IRunningProcess? Running { get; set; }
        public Task? Completion { get; set; }
        public string? StopReason { get; set; }

        public required string LogPath { get; init; }
        public required string MetaPath { get; init; }
        public required Lock Gate { get; init; }

        /// <summary>已经写进日志的字节数。⚠️ 到上限之后**不再增长**（但进程继续跑）。</summary>
        public long Written { get; set; }

        /// <summary>因为到上限而被丢弃的字节数。</summary>
        public long Dropped { get; set; }
    }

    // ---- 启动 ----

    public BackgroundJobStart Start(string sessionId, BackgroundJobRequest request)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return new BackgroundJobStart(null, "缺少会话归属，后台执行不可用。");

        // ⚠️ **并发不限**（用户 2026-10-27 裁定）：不检查运行中数量。
        // 不设置数量配额——
        // **别把这段删掉当成"少了个校验"**：那道闸是有意去掉的。

        // ⚠️ **带前缀的 id**（`job-…`，见 `Ids.NewJob`）：`wait` 靠它把「后台进程」与
        // 「子会话」这两类目标分开——两者的裸 id 都是 12 位 hex，混起来就分不清了。
        var id = Ids.NewJob();
        var dir = Path.Combine(_root, sessionId);
        var logPath = Path.Combine(dir, id + ".log");
        var metaPath = Path.Combine(dir, id + ".json");

        Directory.CreateDirectory(dir);

        // ⚠️ **先登记、再起进程**：读泵的第一个回调可能在 `StartProcess` 返回**之前**就到
        // （进程一起来就可能吐输出）。⇒ `Entry` 先造好放进去，进程句柄稍后再填。
        var entry = new Entry
        {
            Info = new BackgroundJobInfo
            {
                Id = id,
                SessionId = sessionId,
                Kind = request.Kind,
                Label = request.Label,
                WorkingDirectory = request.WorkingDirectory,
                StartedAt = CoreClock.Now(),
                State = BackgroundJobState.Running,
                TimeoutSeconds = request.TimeoutSeconds,
            },
            LogPath = logPath,
            MetaPath = metaPath,
            Gate = new Lock(),
        };

        _jobs[id] = entry;

        Process process;
        Task pump;
        try
        {
            (process, pump) = ShRunner.StartProcess(request, chunk =>
            {
                Append(entry, chunk);
                return Task.CompletedTask;
            });
        }
        catch (ShellStartException ex)
        {
            // ⚠️ 起不来的那个占位项**必须撤掉**：留着它会让 `jobs` 显示一个永远 Running
            // 的幽灵，而它占着并发名额 ⇒ 几次失败之后这个会话就再也起不了后台任务了。
            _jobs.TryRemove(id, out _);
            return new BackgroundJobStart(null, ex.Message);
        }

        // ⚠️ 包装成 `IRunningProcess`：与「接管」那条路**共用**同一套监视 / 杀法（见 `Entry.Running`）。
        entry.Running = new PumpedProcess(process, request, pump);
        lock (entry.Gate)
            entry.Info = entry.Info with { ProcessId = entry.Running.Pid };

        Watch(entry, request.TimeoutSeconds);

        Persist(entry);
        return new BackgroundJobStart(Snapshot(entry), null);
    }

    /// <summary>
    /// **接管一个已经在跑的进程**（2026-11-07）：前台工具调用到点、而它有异步模式时走这里。
    ///
    /// <para>⚠️ <b>它<b>不</b>重起任何东西</b>：进程、读泵、已经吐出来的输出全都在原地，
    /// 变的只是「谁负责收尾」——从这里起归宿主（`Watch` 同一条路，`job_kill` 杀得掉）。</para>
    ///
    /// <para>⚠️ <b>先登记、再改道</b>：改道之后工具那边就不再攒正文了，若登记失败，
    /// 那份输出就没人在管（而进程还在跑）。⇒ 顺序是「登记 → 改道 → 写元数据 → 开工监视」，
    /// 任何一步之前的失败都让调用方把进程杀掉（它拿得到 <c>null</c>）。</para>
    /// </summary>
    public BackgroundJobInfo? Adopt(string sessionId, IRunningProcess running)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;

        var id = Ids.NewJob();
        var dir = Path.Combine(_root, sessionId);
        var logPath = Path.Combine(dir, id + ".log");
        var metaPath = Path.Combine(dir, id + ".json");

        Directory.CreateDirectory(dir);

        var entry = new Entry
        {
            Info = new BackgroundJobInfo
            {
                Id = id,
                SessionId = sessionId,
                Kind = running.Request.Kind,
                Label = running.Request.Label,
                WorkingDirectory = running.Request.WorkingDirectory,
                StartedAt = CoreClock.Now(),
                State = BackgroundJobState.Running,
                // ⚠️ 转后台之后按**后台默认**算（不限时）：用户看到的那句「到点转后台」里
                // 没有「然后一分钟后再杀」这半句（同 `shell background`）。
                TimeoutSeconds = running.Request.TimeoutSeconds,
            },
            LogPath = logPath,
            MetaPath = metaPath,
            Gate = new Lock(),
        };

        _jobs[id] = entry;
        entry.Running = running;

        try
        {
            // ⚠️ **改道 + 把已经读到的那一份先写进日志**（同步等它：这一步必须在
            // 「返回给工具」之前完成，否则工具会以为交接成功了、而开头那段还在内存里）。
            running.AdoptAsync(chunk =>
            {
                Append(entry, chunk);
                return Task.CompletedTask;
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // ⚠️ 登记撤掉、把进程交回调用方杀掉——**不能**既没接管、又留着跑。
            _jobs.TryRemove(id, out _);
            return null;
        }

        lock (entry.Gate)
            entry.Info = entry.Info with { ProcessId = running.Pid, OutputBytes = entry.Written };

        Watch(entry, running.Request.TimeoutSeconds);

        Persist(entry);
        return Snapshot(entry);
    }

    /// <summary>
    /// **宿主自己起的那个进程**的 <see cref="IRunningProcess"/> 形状。
    /// ⚠️ <see cref="AdoptAsync"/> 在这里是**不支持**的：它的输出从一开始就直奔日志，
    /// 没有「攒在内存里的开头那一份」需要交出去。
    /// </summary>
    private sealed class PumpedProcess(Process process, BackgroundJobRequest request, Task pump)
        : IRunningProcess
    {
        public BackgroundJobRequest Request => request;

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

        public void Kill() => TryKillTree(process);

        public Task<string> AdoptAsync(Func<ReadOnlyMemory<byte>, Task> onChunk) =>
            throw new InvalidOperationException("后台任务的输出从一开始就写在日志里；没有需要改道的东西。");

        public void Dispose() => process.Dispose();
    }

    /// <summary>
    /// **看着它退出**（每个任务一个）。⚠️ 这是状态从 Running 走到 Exited/Killed 的唯一路径。
    ///
    /// <para>⚠️ <b>`WaitForExitAsync` 之后还要等读泵</b>：进程退出时管道里可能还剩最后一段
    /// （比如 `sh -c 'echo bye'` 的最后一行）。不等它就把状态改成 Exited，模型读到的输出
    /// 会**少一截尾部**——而那看起来完全正常。</para>
    ///
    /// <para>⚠️ <b>读泵用有界等待（2 秒）</b>：孙进程若还攥着管道，管道永远不会 EOF
    /// （`RunAsync` 里那条同源经验）。这里不能再挂一个新的无限等待。</para>
    ///
    /// <para>⚠️ <b>超时在这条路上兑现</b>：到点之后再走**同一条** Kill 路径，
    /// 于是「超时被杀」与「被 `job_kill` 杀掉」共用一套状态写入——两处各写一份的话，
    /// 「谁先到谁算数」这条判据会漂。</para>
    ///
    /// <para>⚠️ <b>2026-11-07 起它对「接管来的进程」也适用</b>（`Adopt`）：两条路都只认
    /// <see cref="IRunningProcess"/>，因为「等退出 + 排空 + 写终态」的那套次序只有一份。</para>
    /// </summary>
    private void Watch(Entry entry, int? timeoutSeconds)
    {
        entry.Completion = Task.Run(async () =>
        {
            try
            {
                var running = entry.Running!;
                var exit = running.WaitForExitAsync();
                if (timeoutSeconds is { } seconds)
                {
                    using var timer = new CancellationTokenSource();
                    var delay = Task.Delay(TimeSpan.FromSeconds(seconds), timer.Token);
                    if (await Task.WhenAny(exit, delay).ConfigureAwait(false) == delay)
                    {
                        lock (entry.Gate)
                        {
                            if (entry.Info.State == BackgroundJobState.Running && !running.HasExited)
                            {
                                entry.StopReason ??= $"超过 {seconds} 秒的时限，被核心终止（整棵进程树）。";
                                running.Kill();
                            }
                        }
                    }
                    await timer.CancelAsync();
                }
                await exit.ConfigureAwait(false);
                await Task.WhenAny(running.Output, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
                lock (entry.Gate)
                {
                    var stopped = entry.StopReason is not null;
                    entry.Info = entry.Info with
                    {
                        State = stopped ? BackgroundJobState.Killed : BackgroundJobState.Exited,
                        Reason = entry.StopReason,
                        ExitCode = stopped ? null : running.ExitCode,
                        EndedAt = CoreClock.Now(),
                        OutputBytes = entry.Written,
                    };
                    Persist(entry);
                }
                PublishFinished(Snapshot(entry));
            }
            catch (Exception ex)
            {
                lock (entry.Gate)
                {
                    entry.Info = entry.Info with
                    {
                        State = BackgroundJobState.Lost,
                        Reason = $"任务监控中断：{ex.Message}",
                        EndedAt = CoreClock.Now(),
                        OutputBytes = entry.Written,
                    };
                    Persist(entry);
                }
                PublishFinished(Snapshot(entry));
            }
            finally { entry.Running?.Dispose(); }
        }, CancellationToken.None);
    }

    // ---- 读 ----

    public IReadOnlyList<BackgroundJobInfo> List(string sessionId) =>
        [.. _jobs.Values
            .Where(e => string.Equals(e.Info.SessionId, sessionId, StringComparison.Ordinal))
            .OrderByDescending(e => e.Info.StartedAt)
            .Select(Snapshot)];

    public BackgroundJobInfo? Find(string sessionId, string jobId) =>
        Owned(sessionId, jobId) is { } entry ? Snapshot(entry) : null;

    public BackgroundJobRead? Read(string sessionId, string jobId, long offset, int maxBytes)
    {
        if (Owned(sessionId, jobId) is not { } entry) return null;

        maxBytes = Math.Clamp(maxBytes, 1, BackgroundJobLimits.MaxReadBytes);
        offset = Math.Max(0, offset);

        lock (entry.Gate)
        {
            var total = entry.Written;
            var running = entry.Info.State == BackgroundJobState.Running;

            if (offset >= total)
                return new BackgroundJobRead("", offset, offset, total, running);

            var want = (int)Math.Min(maxBytes, total - offset);
            var buffer = new byte[want];

            try
            {
                using var stream = new FileStream(
                    entry.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                stream.Seek(offset, SeekOrigin.Begin);

                var read = 0;
                while (read < want)
                {
                    var n = stream.Read(buffer, read, want - read);
                    if (n == 0) break;
                    read += n;
                }

                // ⚠️ 回退到 UTF-8 字符边界：切在半个字符中间会交付一个替换符，
                // 而那个字节**再也不会被交付**（`NextOffset` 已经越过它了）。
                // 判据与 `TimelineRender.DeliveryPrefixBytes` 同一条。
                var usable = read;
                if (read > 0)
                {
                    // A byte window may end inside a multibyte character, including at buffer.Length.
                    var lead = read - 1;
                    while (lead >= 0 && (buffer[lead] & 0xC0) == 0x80) lead--;
                    if (lead >= 0)
                    {
                        var value = buffer[lead];
                        var width = value < 0x80 ? 1 : value < 0xE0 ? 2 : value < 0xF0 ? 3 : 4;
                        if (lead + width > read) usable = lead;
                    }
                }
                var text = new UTF8Encoding(false, throwOnInvalidBytes: false).GetString(buffer, 0, usable);

                return new BackgroundJobRead(text, offset, offset + usable, total, running);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 文件没了（被清理 / 盘出问题）⇒ 说一句人话，而不是抛：
                // 抛会让这一步整个失败，而「读不到日志」不该连带丢掉模型的其他工作。
                return new BackgroundJobRead(
                    $"（这个任务的日志读不出来：{ex.Message}）", offset, offset, total, running);
            }
        }
    }

    /// <summary>
    /// 把 <paramref name="length"/> 回退到一个不切在**续字节**上的位置。
    /// ⚠️ 只回退，不前进：前进会跳过一个我们其实拿得到的字符。
    /// </summary>
    private static int TrimToCharBoundary(byte[] buffer, int length)
    {
        if (length == 0 || length == buffer.Length) return length;

        var cut = length;
        while (cut > 0 && (buffer[cut] & 0xC0) == 0x80) cut--;
        return cut;
    }

    // ---- 停 ----

    /// <summary>
    /// 停一个任务。⚠️ <b>它<b>不</b>广播终态</b>（2026-10-27 定的）：调用它的只有
    /// 「模型 `job_kill`」与「核心退出」两条路，而两者都已经知道这件事了
    /// ——唤醒一个会话去读「你刚杀了一个任务」只是噪音（模型手上就有那条结果）。
    /// ⚠️ 真正需要唤醒的是**没人看着的那一次结束**（自己退出 / 超时），那条在 `Watch` 里。
    /// </summary>
    public BackgroundJobInfo? Kill(string sessionId, string jobId, string reason)
    {
        if (Owned(sessionId, jobId) is not { } entry) return null;
        lock (entry.Gate)
        {
            if (entry.Info.State != BackgroundJobState.Running) return SnapshotCore(entry);
            if (entry.Running is { HasExited: false } running)
            {
                running.Kill();
                entry.StopReason ??= reason;
            }
        }
        // The watcher drains output and commits the final state before reporting it.
        entry.Completion?.GetAwaiter().GetResult();
        return Snapshot(entry);
    }

    public void KillAll(string reason)
    {
        _quiet = true;
        foreach (var entry in _jobs.Values)
        {
            try { Kill(entry.Info.SessionId, entry.Info.Id, reason); }
            catch (Exception) { /* Continue stopping other owned processes. */ }
        }
    }

    private static void TryKillTree(Process? process)
    {
        try { process?.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 进程已经没了 / 杀不动。状态已经写对了（它确实不再跑了）。
        }
    }

    // ---- 重启补偿 ----

    /// <summary>
    /// **把上一次进程留下的 <see cref="BackgroundJobState.Running"/> 标成
    /// <see cref="BackgroundJobState.Lost"/>**，并清掉过期的残骸。
    ///
    /// <para>⚠️ <b>这是强杀 / 断电唯一的补偿点</b>：正常退出走 <see cref="KillAll"/>，
    /// 而 <c>kill -9</c> 走不到那里 ⇒ 盘上会留一格 Running。不修的话 `jobs`
    /// 会一直显示「在跑」，而那是一个**假的活口**——模型会等一个永远不会来的结果。</para>
    ///
    /// <para>⚠️ <b>它只改状态、不杀任何东西</b>：那些进程可能还活着，但**我们手上没有句柄**，
    /// 而按 pid 去杀是另一件事（pid 会被复用，杀错是灾难）。⇒ 如实写「结局未知」。</para>
    /// </summary>
    public void Recover()
    {
        if (!Directory.Exists(_root)) return;

        var now = CoreClock.Now();

        foreach (var sidDir in Directory.GetDirectories(_root))
        {
            foreach (var metaPath in Directory.GetFiles(sidDir, "*.json"))
            {
                BackgroundJobInfo? info;
                try
                {
                    info = JsonSerializer.Deserialize<BackgroundJobInfo>(
                        FileFormat.ReadChecked(metaPath), Json.Options);
                }
                catch (Exception ex) when (ex is JsonException or FileFormatException or IOException)
                {
                    // 读不出来 ⇒ 跳过。⚠️ 不删：一份坏文件可能正是排查线索。
                    continue;
                }

                if (info is null) continue;

                if (info.State == BackgroundJobState.Running)
                {
                    var timedOut = info.TimeoutSeconds is { } s
                        ? $"（它设了 {s} 秒的时限，而那个时限的看守也随着进程没了。）"
                        : "";

                    var lost = info with
                    {
                        State = BackgroundJobState.Lost,
                        Reason = "核心在上一次运行时退出了（或被强杀）；这个任务的结局无从确认"
                               + "——它可能已经跑完，也可能还在后台。输出文件里是它被抓到的全部内容。"
                               + timedOut,
                        EndedAt = now,
                        OutputBytes = LengthOf(Path.ChangeExtension(metaPath, ".log")),
                    };

                    try
                    {
                        AtomicFile.Write(metaPath, JsonSerializer.Serialize(
                            lost with { Version = Json.FormatVersion }, Json.Options));
                    }
                    catch (IOException) { }

                    _jobs[lost.Id] = new Entry
                    {
                        Info = lost,
                        Running = null,
                        LogPath = Path.ChangeExtension(metaPath, ".log"),
                        MetaPath = metaPath,
                        Gate = new Lock(),
                        Written = lost.OutputBytes,
                    };

                    continue;
                }

                if (info.EndedAt is { } ended && now - ended > BackgroundJobLimits.Retention)
                {
                    // 过期的残骸：元数据与日志一起删。⚠️ 只删**已结束**的。
                    try
                    {
                        File.Delete(metaPath);
                        File.Delete(Path.ChangeExtension(metaPath, ".log"));
                    }
                    catch (IOException) { }
                    continue;
                }

                // 保留：让「我上次起的那个任务怎么样了」还有答案。
                _jobs[info.Id] = new Entry
                {
                    Info = info,
                    Running = null,
                    LogPath = Path.ChangeExtension(metaPath, ".log"),
                    MetaPath = metaPath,
                    Gate = new Lock(),
                    Written = info.OutputBytes,
                };
            }
        }
    }

    private static long LengthOf(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch (IOException) { return 0; }
    }

    // ---- 内部 ----

    /// <summary>按归属取一个任务。⚠️ 归属不匹配 === 不存在（**不**泄露「那是别人的」）。</summary>
    private Entry? Owned(string sessionId, string jobId) =>
        _jobs.TryGetValue(jobId, out var entry)
            && string.Equals(entry.Info.SessionId, sessionId, StringComparison.Ordinal)
            ? entry
            : null;

    private static BackgroundJobInfo Snapshot(Entry entry)
    {
        lock (entry.Gate) return SnapshotCore(entry);
    }

    private static BackgroundJobInfo SnapshotCore(Entry entry) =>
        entry.Info with { OutputBytes = entry.Written };

    /// <summary>
    /// 追加一段输出。⚠️ <b>超过上限之后仍然把管道排空</b>（只是不保留）——
    /// 不排空的话，子进程会在写满管道缓冲（Windows 上约 64 KiB）时**自己卡死**，
    /// 而那个症状看起来像「命令卡住了」，完全不像「输出太多」。
    /// </summary>
    private void Append(Entry entry, ReadOnlyMemory<byte> chunk)
    {
        lock (entry.Gate)
        {
            var room = BackgroundJobLimits.MaxOutputBytes - entry.Written;

            if (room <= 0)
            {
                entry.Dropped += chunk.Length;
                return;
            }

            var take = (int)Math.Min(chunk.Length, room);

            try
            {
                using var stream = new FileStream(
                    entry.LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                stream.Write(chunk.Span[..take]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 写不进去（盘满 / 文件被删）⇒ 当作丢弃。⚠️ 不抛：读泵抛了就停，
                // 停了子进程就卡死在管道上（见上）。
                entry.Dropped += chunk.Length;
                return;
            }

            entry.Written += take;
            if (take < chunk.Length) entry.Dropped += chunk.Length - take;
        }
    }

    /// <summary>
    /// 元数据落盘。⚠️ <b>失败只吞不抛</b>：它是**诊断**用的那份，而一次「状态没写进去」
    /// 不该让 `job_kill` 变成一个失败调用（进程确实已经杀了）。代价如实认：
    /// 那份 json 可能落后于内存，而重启之后我们会据此说「结局未知」——那是**安全的那一侧**。
    /// </summary>
    private static void Persist(Entry entry)
    {
        try
        {
            lock (entry.Gate)
            {
                var info = SnapshotCore(entry);
                AtomicFile.Write(entry.MetaPath, JsonSerializer.Serialize(
                    info with { Version = Json.FormatVersion }, Json.Options));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
