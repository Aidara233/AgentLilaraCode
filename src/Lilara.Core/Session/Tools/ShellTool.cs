using System.Text;
using Lilara.Core.Kernel;
using Lilara.Core.Shell;

namespace Lilara.Core.Session.Tools;

/// <summary>
/// `shell`——**在工作目录里跑一段 POSIX sh 脚本**（2026-10-23）。
///
/// <para>⚠️ <b>它是一把钥匙，而不是一个帮手</b>：它能在工作目录里执行任意命令，
/// 也能**故意**走到目录外（不这样它就是残缺的）。那正是它与「执行工作目录里的代码」
/// 的两个专用工具（`dotnet_build` / `dotnet_test`，写入类）的分界——
/// 那两个的边界由核心拼死，而这一个的边界**只有权限轴**。</para>
///
/// <para>⚠️ <b>所以它是 <see cref="PermissionClass.Destructive"/>：与 <see cref="DeleteTool"/> 同级</b>
/// （2026-10-23 用户裁定）。落在闸门表上的后果是：`readonly` / `investigate` / `standard`
/// **硬拒**，`authorized` **问一句**，`trusted` 才直接放行。判据是**不可逆**——
/// 一条命令可以 `rm -rf`、可以 `curl … | sh`、可以把服务停掉，而核心<b>事后挽不回来</b>。
/// 反过来（报成写入类）会让一个默认可逆的档位静默执行任意代码。</para>
///
/// <para>⚠️ <b>它不是「一张白名单」，而这一次是刻意的</b>：`git_*` / `dotnet_*` 收得紧
/// 是因为它们各自只做一件事；shell 的价值恰恰是**组合**（`grep … | sort | head`、
/// `make &amp;&amp; ./a.out`）。收窄成白名单会让它退回那批专用工具，而模型仍然做不到
/// 「把一段命令串起来看看」。⇒ 边界换成三件事：权限轴、设备开关（`shell.json`）、超时与输出上限。</para>
///
/// <para>⚠️ <b>为什么只有一种 shell</b>：见 <see cref="ShRunner"/>——所有平台跑同一套 POSIX 语法，
/// 模型写的命令在哪台机器上都成立。Windows 上要 Git for Windows 里的 MSYS sh；
/// 找不到时**工具照常声明**、调用只回一句人话（工具表逐字节稳定，不破前缀）。</para>
///
/// <para>⚠️ <b>不可重演</b>：命令可能改世界、也可能只是读，而核心分不出来
/// ⇒ 不给指纹、不给指称（同 `dotnet_build`）。一次 `ls` 与一次 `rm` 在这种判定下是同一类。</para>
/// </summary>
public sealed class ShellTool : ITool
{
    public const string NameOf = "shell";

    private readonly Func<string?> _environmentExecutable;
    private readonly Func<string, bool> _exists;

    public ShellTool() : this(EnvironmentExecutable, ShRunner.DefaultExists) { }

    /// <summary>
    /// 测试注入口。⚠️ 两个参数**默认值指向生产那条路**，与 `retryDelay` / `networkTransport`
    /// 同一个理由：定位与「找不到 sh」两种行为必须有测试，而测试既不能依赖开发机的 PATH，
    /// 也不该真的去装一个 shell。
    /// </summary>
    /// <param name="environmentExecutable">环境的覆盖值（生产 = 没有；e2e 用它避开 PATH 上的另一份 sh）。</param>
    internal ShellTool(Func<string?> environmentExecutable, Func<string, bool> exists)
    {
        _environmentExecutable = environmentExecutable;
        _exists = exists;
    }

    /// <summary>环境变量的覆盖名（与 `LILARA_HOME` / `LILARA_PORT` 同一族）。</summary>
    public const string ExecutableEnvVar = "LILARA_SH";

    /// <summary>
    /// 定位用的**环境覆盖**（配置没点名时）。⚠️ `internal`：只有生产组合根与自检该用它，
    /// 工具自己的实例因此能在测试里换掉探测谓词。
    /// </summary>
    internal static string? EnvironmentExecutable() =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ExecutableEnvVar))
            ? null
            : Environment.GetEnvironmentVariable(ExecutableEnvVar);

    /// <summary>
    /// `shell.test` 用的那条定位（配置 → 环境 → 已知位置）。
    /// ⚠️ 与 <see cref="RunAsync"/> **共用 <see cref="ShRunner.TryLocate"/>**——
    /// 各写一份的话，「试通了、真跑却找不到」会出现，而两边看着都对。
    /// </summary>
    internal static bool TryLocateForTest(string? configured, out string path, out string? error) =>
        ShRunner.TryLocate(configured ?? EnvironmentExecutable(), ShRunner.DefaultExists, out path, out error);

    public string Name => NameOf;

    /// <summary>⚠️ 与 `delete` 同级：不可逆（见类型注释）。</summary>
    public PermissionClass PermissionClass => PermissionClass.Destructive;

    public string Description =>
        "在工作目录里运行一段 POSIX sh 命令，返回退出码、stdout 与 stderr。"
        + "这是 POSIX shell（Unix 的 /bin/sh；Windows 上是 Git for Windows 的 sh）——"
        + "用单引号 / 双引号、$VAR、管道、&& 与 ||，不要用 PowerShell 语法。"
        + "命令的工作目录是会话的工作目录；它会真正执行，可以改动文件与系统状态。"
        + "超时会杀掉整棵进程树并如实报告；输出超过上限时只保留开头。"
        + "要读文件优先用 read / search，要改文件优先用 write / update——它们的结果更省上下文。"
        + "⚠️ 这是**前台**执行：它占着这一步，直到命令结束或超时。"
        + "要跑持续很久的东西（起服务、盯日志、长构建），用 background: true 起成后台任务；"
        + "它会立刻返回一个任务 id，之后用 job_output 读进展、jobs 看还在不在、job_kill 停掉。"
        + "⚠️ 后台任务**不随这一步结束，也不会被 session.interrupt 打断**——停它只能靠 job_kill。";

    public string ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "command": {
              "type": "string",
              "description": "要交给 sh -c 的脚本原文。可以多行。"
            },
            "background": {
              "type": "boolean",
              "description": "true = 起成后台任务并立刻返回任务 id（不等它跑完）。省略 = 前台执行。"
            },
            "timeoutSeconds": {
              "type": "integer",
              "description": "超时（秒）。前台：到时杀掉整棵进程树并如实报告。后台：到时自动停掉它。省略 = 用 shell 配置里的值（前台）/ 不限时（后台）。"
            }
          },
          "required": ["command"]
        }
        """;

    /// <summary>
    /// ⚠️ <b>两条路的权限类别是同一个</b>（<see cref="PermissionClass.Destructive"/>）——
    /// 后台化**不降级**：起一个后台进程与前台跑一条命令，对世界的后果一样不可逆。
    /// 判据在 <see cref="PermissionClass"/> 那里已经写过，这里只提醒别为了「后台更轻」而放宽。
    /// </summary>
    public Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct) =>
        ToolArgs.Bool(call.ArgumentsJson, "background")
            ? Task.FromResult(RunBackground(call, context))
            : RunForegroundAsync(call, context, ct);

    /// <summary>
    /// 解析 `timeoutSeconds`。⚠️ <b>越界/不是整数 ⇒ 一句话拒绝</b>，不夹紧：
    /// 模型写了个它以为能生效的时限、而我们悄悄换成另一个值，是最坏的一类静默。
    /// ⚠️ 「不超时」的表达是**省略这个键**，不是给 0——同 `session.create` 的 `askTimeoutMinutes`。
    /// </summary>
    private static string? TryReadTimeout(ToolCall call, int min, int max, out int? seconds)
    {
        seconds = null;

        // ⚠️ 用 `ToolPaths.HasKey` 判断「显式给了」：`ToolArgs.Number` 对「键缺失」与
        // 「值不是数字」都给 null，而那两件事的处置相反（一个用默认值、一个要拒绝）。
        if (!ToolPaths.HasKey(call.ArgumentsJson, "timeoutSeconds")) return null;

        if (ToolArgs.Number(call.ArgumentsJson, "timeoutSeconds") is not { } value)
            return "`timeoutSeconds` 必须是整数秒。";

        if (value < min || value > max)
            return $"`timeoutSeconds` 要在 {min}..{max} 秒之间（给的是 {value}）。不限时就不写这个参数。";

        seconds = (int)value;
        return null;
    }

    /// <summary>
    /// **后台**：起进程、立刻交回任务 id。
    ///
    /// <para>⚠️ 它刻意**不接 <c>CancellationToken</c></b>：那是<b>这一步</b>的令牌，
    /// 而任务的寿命要长过这一步（用户裁定：`interrupt` 只打断当前步）。
    /// ⇒ 传进来再忽略比不传更糟——下一个人会以为它被接上了。</para>
    ///
    /// <para>⚠️ <b>`timeoutSeconds` 在这里是「自动停」而不是「报超时」</b>：
    /// 前台那条超时之后这一步还能看见「命令被终止了」；后台没有人在等，
    /// 所以它只能靠**状态与原因**说清楚（`job.json` 里那句「超过 N 秒的时限，被核心终止了」）。</para>
    /// </summary>
    private ToolOutcome RunBackground(ToolCall call, IToolContext context)
    {
        var command = ToolArgs.String(call.ArgumentsJson, "command");
        if (string.IsNullOrWhiteSpace(command))
            return ToolText.Of("缺少 `command` 参数。要跑什么命令？");

        if (context.Shell is not { } shell)
            return ToolText.Of("这个会话没有 shell 上下文，命令不可用。");

        if (shell.DenialFor() is { } denial)
            return ToolText.Of(denial);

        if (shell.BackgroundDenialFor() is { } backgroundDenial)
            return ToolText.Of(backgroundDenial);

        if (TryReadTimeout(call, BackgroundJobLimits.MinTimeoutSeconds, BackgroundJobLimits.MaxTimeoutSeconds,
                out var timeoutSeconds) is { } timeoutError)
            return ToolText.Of(timeoutError);

        // ⚠️ 与前台那条**同一个定位谓词**（`ShRunner.TryLocate`）：两处各写一份的话，
        // 「前台找得到、后台找不到」会出现，而两边看着都对。
        var configured = shell.Config.Executable ?? _environmentExecutable();
        if (!ShRunner.TryLocate(configured, _exists, out var executable, out var locateError))
            return ToolText.Of(locateError!);

        var started = shell.Jobs!.Start(shell.SessionId!,
            ShRunner.ShellRequest(executable, command, context.WorkingDirectory,
                label: JobsTool.OneLine(command), timeoutSeconds: timeoutSeconds));

        if (started.Job is not { } job)
            return ToolText.Of(started.Error ?? "后台任务没能启动。");

        return new ToolOutcome
        {
            Content =
            [
                new Block.TextBlock(
                    $"已在后台启动（任务 {job.Id}，pid {job.ProcessId}）。它不随这一步结束，也不会被 session.interrupt 打断。"
                    + (job.TimeoutSeconds is { } s ? $"它会在 {s} 秒后被自动停掉（到点写进原因）。\n" : "\n")
                    + "用 job_output 读输出（可反复读，每次只取新的一段）；jobs 看它还在不在；job_kill 停掉它。\n"
                    + $"⏳ 想等它跑完再继续，就调 wait(task=\"{job.Id}\")。\n"
                    + "⚠️ 现在别干等：先去干别的，回头再来看。"),
            ],
            // ⚠️ **不可重演**：再跑一次就是第二个进程。同前台那条判据。
            Fingerprint = null,
            DependsOn = null,
            Framing = new ToolFraming(NameOf,
            [
                new ToolFramingField("job", job.Id),
                new ToolFramingField("state", "running"),
            ]),
        };
    }

    /// <summary>前台：等它跑完（或超时）。这是 2026-10-23 那一版，行为一个字没改。</summary>
    private async Task<ToolOutcome> RunForegroundAsync(
        ToolCall call, IToolContext context, CancellationToken ct)
    {
        var command = ToolArgs.String(call.ArgumentsJson, "command");
        if (string.IsNullOrWhiteSpace(command))
            return ToolText.Of("缺少 `command` 参数。要跑什么命令？");

        if (context.Shell is not { } shell)
            return ToolText.Of("这个会话没有 shell 上下文，命令不可用。");

        if (shell.DenialFor() is { } denial)
            return ToolText.Of(denial);

        // ⚠️ 配置点名优先于环境变量：前者是用户在头里做的一次显式选择，
        // 后者是运维/测试的覆盖（`LILARA_SH`）——两者都给时用户当面的那个算数。
        var configured = shell.Config.Executable ?? _environmentExecutable();
        if (!ShRunner.TryLocate(configured, _exists, out var executable, out var locateError))
            return ToolText.Of(locateError!);

        var timeout = TimeSpan.FromSeconds(shell.Config.TimeoutSeconds);

        // ⚠️ **逐次覆盖**（用户 2026-10-24 裁定「超时时间……整合进来」）：
        // 给了就用给的，没给就用 `shell.json` 里那个。⚠️ 值域用 **ShellLimits**（前景的上限 600 秒），
        // 不是后台那个 3600 ——前台占着一步，跑一小时不是它该做的事。
        if (TryReadTimeout(call, ShellLimits.MinTimeoutSeconds, ShellLimits.MaxTimeoutSeconds,
                out var overrideSeconds) is { } timeoutError)
            return ToolText.Of(timeoutError);

        if (overrideSeconds is { } seconds) timeout = TimeSpan.FromSeconds(seconds);

        int exitCode;
        string stdout, stderr;
        bool timedOut;
        try
        {
            (exitCode, stdout, stderr, timedOut) =
                await ShRunner.RunAsync(executable, command, context.WorkingDirectory, timeout, ct);
        }
        catch (ShellStartException ex)
        {
            return ToolText.Of(ex.Message);
        }

        var result = new StringBuilder();
        // ⚠️ 第一行是**结论**：退出码必须一眼可见。超时**不说**「失败」——命令可能已经做完了大半，
        // 说失败会诱导模型重跑一遍（而重跑一次 `rm` 或一次部署不是没有代价的）。
        result.AppendLine(timedOut
            ? $"命令超过 {timeout.TotalSeconds:F0} 秒，已被终止（整棵进程树）；下面的输出不完整。"
            : exitCode == 0 ? "命令成功（退出码 0）。" : $"命令失败（退出码 {exitCode}）。");
        if (stdout.Length > 0) result.Append(stdout);
        if (stderr.Length > 0)
        {
            if (stdout.Length > 0 && !stdout.EndsWith('\n')) result.AppendLine();
            result.AppendLine("stderr:").Append(stderr);
        }
        if (stdout.Length == 0 && stderr.Length == 0 && !timedOut) result.AppendLine("（没有输出）");

        return new ToolOutcome
        {
            Content = [new Block.TextBlock(result.ToString())],
            // ⚠️ 不可重演（`00` §5）：同一条命令再跑一次可能得到不同结果，而错误的指纹会让
            // 三态判定声称「世界没动过」。⇒ 永不被点杀回收，只靠保护带 / 线 / 显式回收。
            Fingerprint = null,
            DependsOn = null,
            Framing = new ToolFraming(NameOf, [
                new ToolFramingField("exit", timedOut ? "timeout" : exitCode.ToString()),
            ]),
        };
    }
}
