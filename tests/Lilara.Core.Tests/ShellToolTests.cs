using System.Diagnostics;
using Lilara.Core.Kernel;
using Lilara.Core.Session.Tools;
using Block = Lilara.Core.Kernel.Block;

namespace Lilara.Core.Tests;

/// <summary>
/// `shell` 的定位、闸门与执行。
///
/// <para>⚠️ <b>「真的执行了」用真 sh 测，但用环境变量把它钉死</b>（`LILARA_SH`）：
/// 开发机 PATH 上可能有别的 <c>sh</c>（实测本机一个是 Git 的、一个是 STM32 工具链的），
/// 而这条测试要证明的是「我们找到的那个 sh 能跑一段 POSIX 脚本」。
/// 找不到 sh 的机器（= 没有任何 POSIX 环境）**跳过**——那不是产品缺陷，
/// 而 `TryLocate` 的「找不到时说人话」由纯逻辑用例覆盖，不依赖本机装了什么。</para>
/// </summary>
public sealed class ShellToolTests : IDisposable
{
    private readonly string _work =
        Path.Combine(Path.GetTempPath(), "lilara-shell-" + Guid.NewGuid().ToString("N"));

    public ShellToolTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
    }

    // ---- 闸门 ----

    [Fact]
    public void Shell_IsDestructive_SameTierAsDelete()
    {
        var tool = new ShellTool();

        Assert.Equal("shell", tool.Name);
        Assert.Equal(PermissionClass.Destructive, tool.PermissionClass);
        Assert.Equal(new DeleteTool().PermissionClass, tool.PermissionClass);

        // 6×4 表里这一行是「高危」那一列：低三档硬拒、授权档问、信任档放。
        Assert.Equal(Verdict.Deny, Gate.Decide(tool.PermissionClass, "off"));
        Assert.Equal(Verdict.Deny, Gate.Decide(tool.PermissionClass, "readonly"));
        Assert.Equal(Verdict.Deny, Gate.Decide(tool.PermissionClass, "investigate"));
        Assert.Equal(Verdict.Deny, Gate.Decide(tool.PermissionClass, "standard"));
        Assert.Equal(Verdict.Ask, Gate.Decide(tool.PermissionClass, "authorized"));
        Assert.Equal(Verdict.Allow, Gate.Decide(tool.PermissionClass, "trusted"));
    }

    [Fact]
    public void Shell_IsRegisteredInTheBuiltInTable()
    {
        var tool = Assert.Single(BuiltInTools.All, t => t.Name == "shell");
        Assert.IsType<ShellTool>(tool);
    }

    // ---- 定位（纯逻辑，不碰文件系统）----

    [Fact]
    public void Locate_ConfiguredPath_WinsAndMustExist()
    {
        // 配置点名了就不回落自动探测：写错的路径必须报出来，而不是「看起来生效了」。
        Assert.False(ShRunner.TryLocate("/nope/sh", _ => false, out _, out var error));
        Assert.Contains("/nope/sh", error);

        Assert.True(ShRunner.TryLocate("/custom/sh", p => p == "/custom/sh", out var path, out _));
        Assert.Equal("/custom/sh", path);
    }

    [Fact]
    public void Locate_WithoutConfig_FindsARealShellOnThisMachine()
    {
        // 本机（Windows = Git for Windows，经注册表找到；Unix = /bin/sh）上都应当找得到。
        // 找不到时这条红是**对的**：它说明这台机器跑不了 shell，而「找不到」的人话由上一条钉。
        Assert.True(ShRunner.TryLocate(null, ShRunner.DefaultExists, out var path, out var error), error);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Locate_OnWindows_PrefersTheWindowsExecutable()
    {
        // ⚠️ 必须是 .exe：`Process` 在 Windows 上起不了 MSYS 的 shell 脚本包装层，
        // 而 PATH 上同时存在 Git 的 sh.exe 与别的工具链的 sh.exe（实测本机就有两份）。
        if (!OperatingSystem.IsWindows()) return;

        Assert.True(ShRunner.TryLocate(null, ShRunner.DefaultExists, out var path, out var error), error);
        Assert.EndsWith(".exe", path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Locate_OnWindows_UsesTheRegistryInstallPath_NotJustTheCProgramFilesDefault()
    {
        if (!OperatingSystem.IsWindows()) return;

        string? install;
        using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\GitForWindows"))
            install = key?.GetValue("InstallPath") as string;
        if (string.IsNullOrWhiteSpace(install)) return;   // 没有 Git for Windows ⇒ 这条不适用

        Assert.True(ShRunner.TryLocate(null, ShRunner.DefaultExists, out var path, out var error), error);
        Assert.StartsWith(install.TrimEnd('\\'), path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Locate_WhenNothingExists_SaysWhatToInstall()
    {
        Assert.False(ShRunner.TryLocate(null, _ => false, out _, out var error));
        Assert.NotNull(error);
        Assert.Contains("sh", error);
        Assert.True(error!.Contains("Git", StringComparison.Ordinal)
                    || error.Contains("/bin/sh", StringComparison.Ordinal));
    }

    // ---- 参数与配置闸（不起进程）----

    [Fact]
    public async Task Shell_WithoutCommand_SaysWhatIsMissing()
    {
        var result = await Run("{}", NewContext(new ShellConfig()));

        Assert.Contains("command", Text(result));
    }

    [Fact]
    public async Task Shell_WhenDisabledOnThisMachine_RefusesBeforeLocating()
    {
        var result = await Run("""{"command":"echo hi"}""",
            NewContext(new ShellConfig { Enabled = false }));

        Assert.Contains("关掉", Text(result));
    }

    [Fact]
    public async Task Shell_WhenConfiguredPathIsMissing_ReportsThePath()
    {
        var result = await Run("""{"command":"echo hi"}""",
            NewContext(new ShellConfig { Executable = Path.Combine(_work, "no-such-sh") }));

        Assert.Contains("no-such-sh", Text(result));
    }

    // ---- 真的执行（经 POSIX sh）----

    [Fact]
    public async Task Shell_RunsARealScript_InTheWorkingDirectory()
    {
        File.WriteAllText(Path.Combine(_work, "hello.txt"), "from-file\n");

        var result = await Run("""{"command":"pwd && cat hello.txt && echo 中文"}""",
            NewContext(new ShellConfig()));

        var text = Text(result);
        Assert.StartsWith("命令成功（退出码 0）。", text);
        // ⚠️ 不断言完整路径的前缀：MSYS 的 `pwd` 印的是它自己的挂载视图（实测 /tmp/…，
        // 而 Windows 侧是 C:\Users\…\Temp\…）。判据是**这个目录的名字**在不在——
        // 而 `cat hello.txt` 成功本身就是工作目录对了的证据。
        Assert.Contains(Path.GetFileName(_work), text);
        Assert.Contains("from-file", text);
        Assert.Contains("中文", text);
    }

    [Fact]
    public async Task Shell_ReportsNonZeroExitAndStderr()
    {
        var result = await Run("""{"command":"echo boom >&2; exit 3"}""", NewContext(new ShellConfig()));

        var text = Text(result);
        Assert.Contains("退出码 3", text);
        Assert.Contains("stderr:", text);
        Assert.Contains("boom", text);
    }

    [Fact]
    public async Task Shell_ReadsStandardInputAsEof_SoCatDoesNotHang()
    {
        // ⚠️ stdin 是一个**已关闭的管道**，不是继承的核心控制台。
        // 实测（2026-10-23）：重定向 stdout/stderr 而让 stdin 继承时，MSYS sh 在某些组合下
        // **连 `echo hi` 都会挂住**——那会让整条工具路径在 e2e 里 10 秒超时而这里全绿。
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var result = await new ShellTool().RunAsync(
            new ToolCall("c", "shell", """{"command":"cat; echo done"}"""),
            NewContext(new ShellConfig()), cts.Token);

        Assert.Contains("done", Text(result));
    }

    [Fact]
    public async Task Shell_WhenItTimesOut_SaysSoAndDoesNotClaimFailure()
    {
        var result = await Run("""{"command":"sleep 5"}""",
            NewContext(new ShellConfig { TimeoutSeconds = 1 }));

        var text = Text(result);
        Assert.Contains("超过 1 秒", text);
        // ⚠️ 超时不说「失败」：命令可能已经做完了大半，说失败会诱导模型重跑一遍。
        Assert.DoesNotContain("命令失败", text);
    }

    [Fact]
    public async Task Shell_HonoursCancellation_InsteadOfWaitingForTheTimeout()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ShellTool().RunAsync(
                new ToolCall("c", "shell", """{"command":"sleep 30"}"""),
                NewContext(new ShellConfig { TimeoutSeconds = 60 }), cts.Token));

        // ⚠️ 用户点了停止就该停：等满 60 秒的超时是另一件事（那是「命令太长」，不是「我不要了」）。
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"取消没有生效：{clock.Elapsed}");
    }

    // ---- 帮手 ----

    private Task<ToolOutcome> Run(string args, IToolContext context) =>
        new ShellTool().RunAsync(new ToolCall("c", "shell", args), context, CancellationToken.None);

    private IToolContext NewContext(ShellConfig config) =>
        new SessionToolContext(_work, new Storage.BlobStore(Path.Combine(_work, ".blobs")), shell: new ShellAccess(config));

    private static string Text(ToolOutcome outcome) =>
        string.Join("\n", outcome.Content.OfType<Block.TextBlock>().Select(b => b.Text));
}
