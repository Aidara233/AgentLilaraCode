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
        + "要读文件优先用 read / search，要改文件优先用 write / update——它们的结果更省上下文。";

    public string ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "command": {
              "type": "string",
              "description": "要交给 sh -c 的脚本原文。可以多行。"
            }
          },
          "required": ["command"]
        }
        """;

    public async Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct)
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
