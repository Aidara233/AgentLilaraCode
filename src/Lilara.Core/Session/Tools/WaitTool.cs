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
        + "省略 timeoutSeconds 就一直等到结束；提供时限则到点返回当前状态，不会停止任务。"
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
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var job = jobs.Find(sid, id);
                if (job is null) return ToolText.Of($"本会话没有任务 {id}。");
                if (job.State != BackgroundJobState.Running || clock.Elapsed >= limit)
                    return Result(id, job.State, job.State != BackgroundJobState.Running,
                        $"{job.Reason}\n" + (job.ExitCode is { } exit ? $"退出码 {exit}。\n" : "")
                        + $"输出用 job_output(job=\"{id}\") 读取。");
                await Task.Delay(100, ct);
            }
        }

        if (context.Subagents is not { } scope || !scope.Children.Any(c => c.SessionId == id))
            return ToolText.Of($"{id} 不是本会话可见的子会话。");
        if (context is not SessionToolContext { SubagentStatus: { } probe })
            return ToolText.Of("缺少子会话实时状态通道，无法确认任务是否完成。");

        // Drivers briefly expose Idle between steps. Require a quiet observation interval too.
        TimeSpan? quietSince = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var state = probe(id);
            if (state is null) return ToolText.Of($"子会话 {id} 已不存在，完成情况未知。");
            if (state == SessionStatus.WaitingForInput)
                return Result(id, state, false, "子会话需要回答或审批；请查看问题后处理。等待已返回，任务未被停止。");
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

    private static ToolOutcome Result(string id, string state, bool done, string detail) => new()
    {
        Content = [new Block.TextBlock((done ? "等到了" : "尚未完成") + $"：任务 {id}，状态 {state}。\n" + detail)],
        Framing = new ToolFraming(NameOf, [new("task", id), new("state", state)]),
    };
}
