using System.Diagnostics;
using Lilara.Core.Kernel;

namespace Lilara.Core.Session.Tools;

/// <summary>Wait on an owned process or child session without spending model calls on polling.</summary>
public sealed class WaitTool : ITool
{
    public const string NameOf = "wait";
    public string Name => NameOf;
    public PermissionClass PermissionClass => PermissionClass.ReadOnly;
    public string Description =>
        "等待后台任务结束。task 接受后台进程的 job-… id 或 spawn 返回的子会话 id。"
        + "后台进程若是后续工作的前置条件，启动后应立即调用本工具并省略 timeoutSeconds，核心会自动等待结束，不需要 jobs/job_output 轮询。"
        + "只有任务本身会长期运行，或需要并行推进独立工作时才提供 timeoutSeconds 或暂缓等待。"
        + "子会话需要用户回答时立即返回阻塞状态。中断当前步只取消等待，后台任务继续运行。";
    public string ParametersSchemaJson => """
        {"type":"object","properties":{
          "task":{"type":"string","description":"启动工具返回的任务 id（job-… 或子会话 id）。"},
          "timeoutSeconds":{"type":"integer","minimum":1,"description":"最多等待的秒数；省略表示等待直到结束。到时返回状态，任务继续运行。"}
        },"required":["task"]}
        """;

    public async Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct)
    {
        var id = ToolArgs.String(call.ArgumentsJson, "task");
        if (string.IsNullOrWhiteSpace(id)) return ToolText.Of("缺少 task 参数，请填写启动工具返回的 id。");
        long? seconds = null;
        if (ToolPaths.HasKey(call.ArgumentsJson, "timeoutSeconds"))
        {
            seconds = ToolArgs.Number(call.ArgumentsJson, "timeoutSeconds");
            if (seconds is null or < 1 or > 86400)
                return ToolText.Of("timeoutSeconds 必须是 1..86400 的整数；省略就等待直到结束。");
        }
        var clock = Stopwatch.StartNew();
        var limit = seconds is { } n ? TimeSpan.FromSeconds(n) : TimeSpan.MaxValue;
        if (id.StartsWith(Injected.JobPrefix, StringComparison.Ordinal))
        {
            if (context.Shell is not { Jobs: { } jobs, SessionId: { } sid })
                return ToolText.Of("这个会话没有后台任务宿主。");

            // ⚠️ **循环只有一份**（`JobWait`，2026-11-13）：`job_output` 现在也要等同一个问题，
            // 各写一遍的那天，两处对「终态是哪个」与「找不到怎么办」的判断会漂。
            var job = await JobWait.UntilFinishedAsync(jobs, sid, id, limit, ct);
            if (job is null) return ToolText.Of($"本会话没有任务 {id}。");
            return Result(id, job.State, job.State != BackgroundJobState.Running,
                $"{job.Reason}\n" + (job.ExitCode is { } exit ? $"退出码 {exit}。\n" : "")
                + $"输出用 job_output(job=\"{id}\") 读取。");
        }

        if (context.Subagents is not { } scope || !scope.Children.Any(c => c.SessionId == id))
            return ToolText.Of($"{id} 不是本会话可见的子会话。");
        if (context is not SessionToolContext { SubagentStatus: { } probe })
            return ToolText.Of("缺少子会话实时状态通道，无法确认任务是否完成。");

        // ⚠️ **「等」的时候要说清在等什么**（2026-11-19）：状态只有一个词，而父能做的事
        // 对两种询问完全不同（提问它能答，授权它只看自己档位批不批得了）。
        // ⚠️ 询问的细节取的是**步开始那份快照**（`scope`）：实况探针只给状态。
        // 拿不到（那条询问是这一步跑起来之后才出现的）就只说状态，**不编一个种类**。
        var waiting = scope.Children.FirstOrDefault(c => c.SessionId == id)?.Waiting;

        // Drivers briefly expose Idle between steps. Require a quiet observation interval too.
        TimeSpan? quietSince = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var state = probe(id);
            if (state is null) return ToolText.Of($"子会话 {id} 已不存在，完成情况未知。");
            if (state == SessionStatus.WaitingForInput)
                return Result(id, state, false, WaitingNote(waiting));
            if (state == SessionStatus.Idle)
            {
                quietSince ??= clock.Elapsed;
                if (clock.Elapsed - quietSince.Value >= TimeSpan.FromMilliseconds(500))
                    return Result(id, state, true, "子会话当前这轮工作已停下；请查看 subagent_report 回报。后续仍可 tell_child 继续交流。");
            }
            else quietSince = null;
            if (clock.Elapsed >= limit)
                return Result(id, state, false, "等待时限已到；子会话未被停止，可以稍后继续等待。");
            await Task.Delay(100, ct);
        }
    }

    /// <summary>
    /// **「它在等人」那一句该怎么写**（2026-11-19）。判据只有询问的 <see cref="AskKind"/>——
    /// 一个词分两档，因为父能做的事分两档：
    /// 提问 ⇒ `tell_child` 那句话就是答复；授权 ⇒ 只有「在自己档位之内」才批得了
    /// （超出那一格就只能等人，`approve_child` 会如实拒绝）。⚠️ 拿不到询问细节时只说状态，
    /// **不猜种类**（猜错会让父去点一个点了没用的动词）。
    /// </summary>
    private static string WaitingNote(Kernel.WaitingAsk? waiting) => waiting switch
    {
        null => "子会话需要回答或审批；请查看问题后处理。等待已返回，任务未被停止。",
        { Kind: AskKind.Question } w =>
            $"子会话挂着一条提问（ask {w.AskId}）：用 tell_child 把回答给它，那句话会落进那条提问的结果里。"
            + "人也可以在界面上直接答——先到的那个生效。等待已返回，任务未被停止。",
        { Kind: AskKind.Permission } w =>
            $"子会话在等人批准 `{w.ToolName}`（ask {w.AskId}）。"
            + "那条调用在你自己的档位之内就用 approve_child 放行；超出那一格就只有人能批"
            + "（说给用户，或者用 tell_child 让它改道）。等待已返回，任务未被停止。",
        _ => "子会话需要回答或审批；请查看问题后处理。等待已返回，任务未被停止。",
    };

    private static ToolOutcome Result(string id, string state, bool done, string detail) => new()
    {
        Content = [new Block.TextBlock((done ? "等到了" : "尚未完成") + $"：任务 {id}，状态 {state}。\n" + detail)],
        Framing = new ToolFraming(NameOf, [new("task", id), new("state", state)]),
    };
}
