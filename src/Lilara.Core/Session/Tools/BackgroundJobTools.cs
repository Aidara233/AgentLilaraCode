using System.Text;
using Lilara.Core.Kernel;

namespace Lilara.Core.Session.Tools;

/// <summary>
/// **等一个后台任务走到终态**——`wait` 与 `job_output` 共用的那一份（2026-11-13）。
///
/// <para>⚠️ <b>为什么必须只有一份</b>：两个工具现在问的是同一个问题（「它结束了没有」），
/// 而判据里有两格容易漂——<b>终态是「不是 Running」，不是「成功」</b>（被杀的、结局未知的
/// 也都不会再动了），以及<b>找不到 = 立刻返回 null</b>（不要空等一个不存在的 id）。
/// 各写一遍的那天，`job_output` 会在一个被杀掉的任务上空转五分钟。</para>
///
/// <para>⚠️ <b>它<b>不</b>订阅 <c>IBackgroundJobs.Finished</c></b>：那个事件是**进程级**的
/// （不按会话/任务过滤），而这里等的是**一条**判据。轮询 100 毫秒的代价是每条 60 微秒不到，
/// 而订阅要自己管退订、并发与「事件在订阅之前就发生了」那个窗口。
/// ⚠️ 这也是 `wait` 一直以来的实现——这里只是把它挪到一处、让第二个调用点不必再抄一遍。</para>
/// </summary>
internal static class JobWait
{
    /// <summary>多久看一次。<c>wait</c> 用的是同一个数（从前散在它的循环里）。</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// 等到任务**不再是 Running**、或到 <paramref name="limit"/>、或调用被取消。
    /// 返回那一刻的任务事实；任务不存在 ⇒ <c>null</c>（调用方各说各的那句话）。
    /// </summary>
    /// <param name="limit">
    /// 最多等多久。<see cref="TimeSpan.MaxValue"/> = 一直等（`wait` 省略 `timeoutSeconds` 那条）。
    /// </param>
    public static async Task<BackgroundJobInfo?> UntilFinishedAsync(
        IBackgroundJobs jobs, string sessionId, string jobId, TimeSpan limit, CancellationToken ct)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var job = jobs.Find(sessionId, jobId);
            if (job is null) return null;

            // ⚠️ 判据是「不是 Running」：Exited / Killed / Lost 三格都不会再动了。
            if (job.State != BackgroundJobState.Running || clock.Elapsed >= limit) return job;

            await Task.Delay(Tick, ct);
        }
    }
}

/// <summary>
/// `jobs`——「我起的那些后台任务，现在怎么样了」（2026-10-24）。
///
/// <para>⚠️ <b>它与 `job_output` 是两个问题，所以是两个工具</b>（同 `ls` / `read` 那条分工）：
/// 这个回答「有哪些、还在不在」，那个回答「它们说了什么」。
/// 合成一个工具的话，「只想知道还在不在」也得取一次输出——而输出是**大的那一半**。</para>
///
/// <para>⚠️ <b>它不叫 `shell_jobs`，是刻意的</b>（用户裁定「其余的可异步指令也一样」）：
/// 能后台跑的不止 `sh`——`dotnet_build` / `dotnet_test` 同样是长任务。
/// ⇒ 列表里每一行带 <c>kind</c>，一眼看得出是哪个工具起的。</para>
/// </summary>
public sealed class JobsTool : ITool
{
    public const string NameOf = "jobs";

    public string Name => NameOf;

    /// <summary>只读：列任务表不改任何东西。</summary>
    public PermissionClass PermissionClass => PermissionClass.ReadOnly;

    public string Description =>
        "列出本会话起的后台任务：任务 id、是哪个工具起的、状态、退出码、已产生的输出字节数、以及（如果结束了）原因。"
        + "状态 Running = 还在跑；Exited = 自己退出了；Killed = 被杀或超时（原因会说清）；"
        + "Lost = 核心重启过，那个任务的结局无从确认。"
        + "读输出用 job_output（⚠️ 任务还在跑时它会等到结束，所以不必靠反复调这个工具来盯）；"
        + "停用 job_kill；起新任务用对应工具的 background: true。"
        + "⚠️ 任务结束时核心会自动通知你，不需要一直守着它。";

    public string ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {},
          "required": []
        }
        """;

    public Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct)
    {
        if (context.Shell is not { } shell)
            return Task.FromResult(ToolText.Of("这个会话没有后台任务上下文。"));

        if (shell.BackgroundDenialFor() is { } denial)
            return Task.FromResult(ToolText.Of(denial));

        var jobs = shell.Jobs!.List(shell.SessionId!);

        if (jobs.Count == 0)
            return Task.FromResult(ToolText.Of(
                "本会话没有后台任务。要起一个：shell（或 dotnet_build / dotnet_test）加 `background: true`。"));

        var sb = new StringBuilder();
        sb.AppendLine($"本会话共有 {jobs.Count} 个后台任务（新的在前）：");

        foreach (var job in jobs)
        {
            sb.Append("- ").Append(job.Id).Append(' ').Append(job.Kind).Append(' ')
              .Append(Describe(job.State));

            if (job.ExitCode is { } code) sb.Append("（退出码 ").Append(code).Append('）');
            if (job.Reason is { } reason) sb.Append(" —— ").Append(reason);

            sb.Append('\n');
            sb.Append("  ").Append(OneLine(job.Label)).Append('\n');
            sb.Append("  输出 ").Append(job.OutputBytes).Append(" 字节 · 起于 ")
              .Append(job.StartedAt.ToLocalTime().ToString("MM-dd HH:mm:ss"));
            if (job.TimeoutSeconds is { } seconds) sb.Append(" · 时限 ").Append(seconds).Append(" 秒");
            sb.Append('\n');
        }

        return Task.FromResult(new ToolOutcome
        {
            Content = [new Block.TextBlock(sb.ToString().TrimEnd())],
            // ⚠️ **没有指纹**：任务状态随时在变（一个 Running 的下一秒就可能是 Exited），
            // 给它一个指纹等于声称「世界没动过」。不可重演那一类。
            Fingerprint = null,
            DependsOn = null,
            Framing = new ToolFraming(NameOf,
            [
                new ToolFramingField("任务", jobs.Count.ToString()),
                new ToolFramingField("在跑", jobs.Count(j => j.State == BackgroundJobState.Running).ToString()),
            ]),
        });
    }

    /// <summary>状态 → 一句人能念的短词。⚠️ 判据是 <see cref="BackgroundJobState"/> 那四个常量，别另拼字面量。</summary>
    internal static string Describe(string state) => state switch
    {
        BackgroundJobState.Running => "在跑",
        BackgroundJobState.Exited => "已退出",
        BackgroundJobState.Killed => "已停止",
        BackgroundJobState.Lost => "结局未知",
        _ => state,
    };

    /// <summary>
    /// 命令压成一行。⚠️ 换行在列表里会**毁掉可读性**（每条占三行已经够了），
    /// 而截断必须**看得出来**（省略号）。
    /// </summary>
    internal static string OneLine(string command)
    {
        var flat = command.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
        while (flat.Contains("  ", StringComparison.Ordinal))
            flat = flat.Replace("  ", " ", StringComparison.Ordinal);

        const int max = 160;
        return flat.Length <= max ? flat : flat[..max] + "…";
    }
}

/// <summary>
/// `job_output`——读一个后台任务的输出（2026-10-24）。
///
/// <para>⚠️ <b>它按字节窗口读，而不是「给我全部」</b>：一个跑了一小时的日志可能有几 MB，
/// 而模型每次只需要**新的一段**。⇒ 给 <c>offset</c>，回来的那份带
/// <c>nextOffset</c>——「再来一次该从哪读」由**核心**算，不让模型自己数字节
/// （它数错的方式是静默重复或静默跳过）。</para>
///
/// <para>⚠️ <b>省略 <c>offset</c> = 从头读</b>，<b>不是</b>「从上次读到的地方接着读」：
/// 后者需要一个「读到哪了」的游标，而那是**会话状态**——本仓没有那个东西，
/// 加一个就要回答「重启之后它还在吗」「两个会话各读一次算谁的」。</para>
///
/// <para>⚠️ <b>2026-11-13 起它<b>顺带就是一次等待</b></b>（用户裁定：「模型主动调用
/// `job_output` 的时候视为 `wait`，防止模型反复轮询」）：任务还在跑时**先等它走到终态**
/// （最多 <see cref="BackgroundJobLimits.MaxWaitSeconds"/> 秒）再读。
/// 理由是实测出来的形状——一个等构建的模型会「`jobs` → 还在跑 → `job_output` → 还是没有 →
/// 再 `jobs`」，每一次都是一整步模型调用，而它想知道的只有「完了没有」。</para>
///
/// <para>⚠️ <b>要<b>看此刻的进度</b>就传 <c>peek: true</c></b>（比如一个 dev server 的启动日志：
/// 它可能几小时都不退出）。⚠️ 而「等」这件事**本来就不该由模型来催**：任务结束时核心会
/// 自动叫醒会话（`IBackgroundJobs.Finished` → 一对合成调用 + 结果），所以等不到也不该轮询。</para>
/// </summary>
public sealed class JobOutputTool : ITool
{
    public const string NameOf = "job_output";

    public string Name => NameOf;

    /// <summary>只读：读日志不改任何东西。</summary>
    public PermissionClass PermissionClass => PermissionClass.ReadOnly;

    public string Description =>
        "读一个后台任务的输出（stdout 与 stderr 按到达顺序合在一起）。"
        + "任务还在跑时它会先等它结束（最多 " + BackgroundJobLimits.MaxWaitSeconds
        + " 秒）——所以不要用 jobs / job_output 反复轮询：调一次就是一次等待。"
        + "想看一个还没结束的任务此刻的进度（比如 dev server 的启动日志），传 peek: true（不等）。"
        + "用 offset 接着读：不写 offset = 从头读；结果里会给 nextOffset，把它填进下一次的 offset 就能只拿新的一段。"
        + "输出可能比一次能给的更多——那时结果末尾会明说还有多少字节。"
        + "任务结束时核心会自动通知你，不需要一直守着。";

    public string ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "job": {
              "type": "string",
              "description": "任务 id（jobs 或各工具的 background: true 给出的那个）。"
            },
            "offset": {
              "type": "integer",
              "description": "从第几个字节开始读。省略 = 0（从头）。用上一次结果里的 nextOffset 接着读。"
            },
            "peek": {
              "type": "boolean",
              "description": "true = 不等它结束，只读此刻已有的输出（看进度用）。省略 = 还在跑就先等它结束。"
            }
          },
          "required": ["job"]
        }
        """;

    public async Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct)
    {
        var jobId = ToolArgs.String(call.ArgumentsJson, "job");
        if (string.IsNullOrWhiteSpace(jobId))
            return ToolText.Of("缺少 `job` 参数。要看哪个任务？用 jobs 列出它们。");

        if (context.Shell is not { } shell)
            return ToolText.Of("这个会话没有后台任务上下文。");

        if (shell.BackgroundDenialFor() is { } denial)
            return ToolText.Of(denial);

        var offset = ToolArgs.Number(call.ArgumentsJson, "offset") ?? 0;
        if (offset < 0)
            return ToolText.Of(
                $"`offset` 不能是负数（给的是 {offset}）。从头读就省略它。");

        var peek = ToolArgs.Bool(call.ArgumentsJson, "peek");
        var jobs = shell.Jobs!;
        var sessionId = shell.SessionId!;

        var before = jobs.Find(sessionId, jobId);
        if (before is null)
            return ToolText.Of(
                $"本会话没有任务 '{jobId}'。用 jobs 看有哪些（只列本会话的）。");

        // ⚠️ **「还在跑」⇒ 这就是一次等待**（2026-11-13，用户裁定）。⚠️ 等多久由
        // `BackgroundJobLimits.MaxWaitSeconds` 定，而它**必须**小于通用工具时限
        // （603 那条注释）：到通用上限会得到一句「超时被终止、这一条没有结果」，
        // 而这里等的是**别人的进程**——那句话是假信息。
        var waited = TimeSpan.Zero;
        if (!peek && before.State == BackgroundJobState.Running)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await JobWait.UntilFinishedAsync(jobs, sessionId, jobId,
                TimeSpan.FromSeconds(BackgroundJobLimits.MaxWaitSeconds), ct);
            waited = clock.Elapsed;

            // 等的时候会话被删了 / 任务被清理了：读不到就如实说，别假装读到了空。
            if (jobs.Find(sessionId, jobId) is null)
                return ToolText.Of($"任务 '{jobId}' 在等待期间不见了（会话或任务被清理？）。");
        }

        var read = jobs.Read(sessionId, jobId, offset, BackgroundJobLimits.MaxReadBytes);
        if (read is null)
            return ToolText.Of(
                $"本会话没有任务 '{jobId}'。用 jobs 看有哪些（只列本会话的）。");

        var job = jobs.Find(sessionId, jobId)!;

        var sb = new StringBuilder();
        // ⚠️ 第一行是**结论**（同 `shell` 前台那条）：状态与总量一眼可见。
        sb.Append(DescribeHeader(job, read));
        sb.Append('\n');

        if (read.Text.Length > 0) sb.Append(read.Text);
        else if (read.Offset >= read.TotalBytes)
            sb.Append(read.TotalBytes == 0 ? "（还没有任何输出）" : $"（{read.Offset} 字节处已经是末尾，没有新内容）");

        if (read.NextOffset < read.TotalBytes)
            sb.Append("\n\n[还有更多] 这条日志共 ").Append(read.TotalBytes)
              .Append(" 字节；这次给到 ").Append(read.NextOffset)
              .Append("。接着读就再调一次 job_output，offset=").Append(read.NextOffset).Append("。");

        // ⚠️ 被丢弃的那一段必须**说出来**：`MaxOutputBytes` 之后我们不再保留，
        // 静默少一段会让模型以为「日志就这些」。
        if (job.OutputBytes >= BackgroundJobLimits.MaxOutputBytes)
            sb.Append("\n\n[已截断] 这个任务的输出超过上限（").Append(BackgroundJobLimits.MaxOutputBytes)
              .Append(" 字节），更早之后的部分没有保存。进程本身没有被停。");

        // ⚠️ **等没等到都要说出来**（2026-11-13）：这句话是「不要轮询」那条纪律的落点——
        // 模型读到的要么是「等到了」，要么是「还在跑，而它结束时会自动通知你」。
        if (waited > TimeSpan.Zero)
        {
            var seconds = Math.Max(1, (int)Math.Round(waited.TotalSeconds));
            sb.Append(job.State == BackgroundJobState.Running
                ? $"\n\n[还在跑] 这次等了它 {seconds} 秒，它还没结束（最多等 "
                  + $"{BackgroundJobLimits.MaxWaitSeconds} 秒）。⚠️ 不要反复轮询：它结束时核心会自动通知你；"
                  + "想看此刻的进度就传 peek: true。"
                : $"\n\n[等到了] 这次调用的前 {seconds} 秒就是在等它跑完（同 wait 的效果）。");
        }
        else if (peek && job.State == BackgroundJobState.Running)
        {
            // ⚠️ 只有 peek 才可能走到这里（默认那条一定等过）⇒ 提醒一句「默认是会等的」。
            sb.Append("\n\n[此刻的快照] 你传了 peek: true，所以没有等它（输出会继续增长）。"
                + "不传 peek 时这个工具会一直等到它结束。");
        }

        return new ToolOutcome
        {
            Content = [new Block.TextBlock(sb.ToString().TrimEnd())],
            // ⚠️ 没有指纹：它是「此刻的日志窗口」，下一毫秒就可能变长。
            Fingerprint = null,
            DependsOn = null,
            Framing = new ToolFraming(NameOf,
            [
                new ToolFramingField("job", jobId),
                new ToolFramingField("state", job.State),
                new ToolFramingField("offset", $"{read.Offset}–{read.NextOffset}/{read.TotalBytes}"),
                // ⚠️ 只在实际等过时出现：老形状（不等）逐字不变。
                .. waited > TimeSpan.Zero
                    ? [new ToolFramingField("waited", Math.Max(1, (int)Math.Round(waited.TotalSeconds)).ToString())]
                    : Array.Empty<ToolFramingField>(),
            ]),
        };
    }

    /// <summary>
    /// 抬头那行。⚠️ 三件事必须都在：**任务是谁**、**它现在什么状态**、**这段窗口在整个日志的哪里**。
    /// 少第三样，模型会把一段中段当成开头。
    /// </summary>
    private static string DescribeHeader(BackgroundJobInfo job, BackgroundJobRead read)
    {
        var sb = new StringBuilder();
        sb.Append("任务 ").Append(job.Id).Append(' ').Append(job.Kind).Append(' ')
          .Append(JobsTool.Describe(job.State));

        if (job.ExitCode is { } code) sb.Append("（退出码 ").Append(code).Append('）');
        if (job.Reason is { } reason) sb.Append(" —— ").Append(reason);

        sb.Append("：第 ").Append(read.Offset).Append('–').Append(read.NextOffset)
          .Append(" 字节，共 ").Append(read.TotalBytes).Append(" 字节");

        if (read.Running) sb.Append("（还在跑，输出会继续增长）");

        return sb.ToString();
    }
}

/// <summary>
/// `job_kill`——停掉一个后台任务（2026-10-24）。
///
/// <para>⚠️ <b>它是 <see cref="PermissionClass.ReadOnly"/>，而这<b>不是</b>笔误</b>
/// （用户裁定：<b>「杀后台这类撤回性功能放低点……防止切了权限想杀都杀不掉」</b>）。
/// 判据不是「它是否只读」，而是：</para>
/// <list type="number">
///   <item><b>它的效果是「撤回」</b>——把一个我们自己起的进程停掉，方向是**减少**世界上的副作用。
///   一个降了权限的会话最该保住的恰恰是这种能力。</item>
///   <item><b>它的作用域被宿主按 <c>sessionId</c> 锁死</b>：杀不到别人的任务、杀不到系统进程、
///   杀不到任何「不是本会话起的」东西。⇒ 越权面是**空的**。</item>
///   <item><b>真正的闸在「起」那一侧</b>：`shell` 是 <see cref="PermissionClass.Destructive"/>、
///   `dotnet_*` 是 <see cref="PermissionClass.Writing"/>，标准档起不了它们。
///   ⇒ 「能杀」不等于「能造成」。</item>
/// </list>
///
/// <para>⚠️ <b>如实记下的代价：`off` 档仍然杀不掉</b>（`Gate` 里 `Allowed["off"] = []`，
/// 那是最严的一档，连 `read` 都不放行）。⇒ 从高档起了任务、之后拨到 `off` 的会话，
/// 模型侧停不下来。逃生口是**用户侧**（控制台 / 头的任务面板）——那条路不受权限轴管辖，
/// 正如 `session.interrupt` 不受它管辖。⚠️ **那条用户侧入口还没做**（见 `01` §14 #22）。</para>
/// </summary>
public sealed class JobKillTool : ITool
{
    public const string NameOf = "job_kill";

    public string Name => NameOf;

    /// <summary>⚠️ 见类型注释：撤回类，**刻意**声明成只读（用户裁定）。</summary>
    public PermissionClass PermissionClass => PermissionClass.ReadOnly;

    public string Description =>
        "停掉一个后台任务（整棵进程树）。已经结束的任务再停一次不算失败，也不会改写它的结局。"
        + "⚠️ 它只作用于本会话起的后台任务。要停的是「这一步」而不是后台任务时，用别的方式中断这一步。"
        + "停掉之后输出仍然读得到（已经写下来的那些不会消失）。";

    public string ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "job": {
              "type": "string",
              "description": "要停的任务 id（jobs 或各工具的 background: true 给出的那个）。"
            }
          },
          "required": ["job"]
        }
        """;

    public Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct)
    {
        var jobId = ToolArgs.String(call.ArgumentsJson, "job");
        if (string.IsNullOrWhiteSpace(jobId))
            return Task.FromResult(ToolText.Of("缺少 `job` 参数。要停哪个任务？用 jobs 列出它们。"));

        if (context.Shell is not { } shell)
            return Task.FromResult(ToolText.Of("这个会话没有后台任务上下文。"));

        if (shell.BackgroundDenialFor() is { } denial)
            return Task.FromResult(ToolText.Of(denial));

        var before = shell.Jobs!.Find(shell.SessionId!, jobId);
        if (before is null)
            return Task.FromResult(ToolText.Of(
                $"本会话没有任务 '{jobId}'。用 jobs 看有哪些（只列本会话的）。"));

        var after = shell.Jobs.Kill(shell.SessionId!, jobId, "模型调用 job_kill 停掉了它。");

        if (after is null)
            return Task.FromResult(ToolText.Of($"本会话没有任务 '{jobId}'。"));

        // ⚠️ **判据是「杀之前它还在跑吗」，不是「杀之后是什么状态」**：
        // 对一个早就退出的任务再杀一次不算失败，但**不能说**「已停止」——
        // 那会让人以为是我们停掉了它（它自己退的）。两句话分开写。
        var text = before.State == BackgroundJobState.Running
            ? $"已停止任务 {jobId}（{before.Kind}，整棵进程树）。"
              + $"已经写下的输出仍然读得到：job_output job={jobId}。"
            : $"任务 {jobId} 本来就已经结束了（{JobsTool.Describe(before.State)}"
              + (before.ExitCode is { } code ? $"，退出码 {code}" : "")
              + "），没有需要停止的东西。输出仍然读得到：job_output job=" + jobId + "。";

        return Task.FromResult(new ToolOutcome
        {
            Content = [new Block.TextBlock(text)],
            // ⚠️ 没有指纹：停止是一次不可重演的动作（再调一次已经没有东西可停）。
            Fingerprint = null,
            DependsOn = null,
            Framing = new ToolFraming(NameOf,
            [
                new ToolFramingField("job", jobId),
                new ToolFramingField("state", after.State),
            ]),
        });
    }
}
