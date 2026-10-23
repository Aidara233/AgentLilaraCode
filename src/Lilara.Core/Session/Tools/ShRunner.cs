using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Lilara.Core.Kernel;

namespace Lilara.Core.Session.Tools;

/// <summary>
/// **`sh` 的定位与运行**——`shell` 工具与 `shell.test` 共用这一份。
///
/// <para>⚠️ <b>为什么只有一种 shell：POSIX `sh`</b>（2026-10-23 用户裁定）。模型写出来的
/// 命令必须**在每台机器上都是同一套语法**——引号、变量、管道、`&amp;&amp;` 全按 POSIX 来。
/// 跟随平台（Windows 上换 PowerShell）会让同一段脚本换一台机器就不成立，
/// 而那件事**不会报错**，只会给出不同的结果。⇒ Unix 用系统自带的 <c>sh</c>；
/// Windows 用 Git for Windows 里的 MSYS <c>sh</c>（本机实证：它登录时自己把
/// <c>/c/Windows</c> 拼回 PATH ⇒ <c>git</c> / <c>dotnet</c> 一类只用系统变量就能找到）。</para>
///
/// <para>⚠️ <b>定位顺序是刻意的</b>：配置里点名的路径 → Unix 的 <c>/bin/sh</c> /
/// Windows 的 Git for Windows（先读注册表 <c>HKLM\SOFTWARE\GitForWindows\InstallPath</c>，
/// 再试两个惯例位置）。⚠️ <b>刻意不看核心进程的 PATH</b>：从 IDE / 服务 / 计划任务里起的核心
/// 常常根本没有 Git 的 <c>bin</c>，而在 PATH 上随便捡一个 <c>sh.exe</c> 还可能捡到别的工具链
/// （实测本机 PATH 上就有 Git 的与 STM32 工具链的两份）。</para>
///
/// <para>⚠️ <b>登录 shell（<c>-l</c>）而不是裸 <c>-c</c></b>：Windows 上没有这一步，
/// 核心给子进程的那份最小 PATH（只剩 Windows 目录）会让 <c>git</c> / <c>dotnet</c> 全部找不到。
/// 登录 shell 由 Profile 与 <c>/etc/profile</c> 把系统 PATH 拼回来，而它<b>不读用户自己的
/// <c>~/.bashrc</c></b>（那是 bash 交互式的），所以不会静默执行用户命令。
/// ⚠️ Unix 上登录 shell 会读 <c>/etc/profile</c>、<c>~/.profile</c> —— 这是<b>有意的</b>：
/// 模型看到的 PATH 与用户在终端里看到的一致，否则「它在终端里能跑、在核心这里找不到」会反复发生。</para>
/// </summary>
internal static class ShRunner
{
    /// <summary>
    /// Windows 上 Git for Windows 的**已知安装位置**。
    /// ⚠️ 先问注册表（安装器写的就是真实位置——实测本机装在 <c>D:\Program Files\Git</c>，
    /// 写死 <c>C:\Program Files\Git</c> 会漏掉整类非默认安装），再试两个惯例位置。
    /// </summary>
    /// <remarks>
    /// ⚠️ 两个布局都试：官方安装器有 <c>bin\sh.exe</c>，而「只装 usr\bin」的裁剪版也见过。
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> WindowsCandidates()
    {
        var roots = new List<string>();

        foreach (var key in new[]
                 {
                     @"SOFTWARE\GitForWindows",
                     @"SOFTWARE\WOW6432Node\GitForWindows",
                 })
        {
            try
            {
                using var sub = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key);
                if (sub?.GetValue("InstallPath") is string install && install.Length > 0)
                    roots.Add(install.TrimEnd('\\'));
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // 读不到注册表不是错误：下面还有惯例位置。
            }
        }

        roots.Add(@"C:\Program Files\Git");
        roots.Add(@"C:\Program Files (x86)\Git");

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return Path.Combine(root, "bin", "sh.exe");
            yield return Path.Combine(root, "usr", "bin", "sh.exe");
        }
    }

    /// <summary>
    /// 找 <c>sh</c>。⚠️ <b>找不到是<b>结果</b>，不是异常</b>（失败也是内容）——
    /// 交回一句能照着做的人话：Windows 上装 Git、或配置里点名路径。
    /// </summary>
    /// <param name="configured">配置里点名的路径（<c>null</c> = 自动探测）。</param>
    /// <param name="fileExists">探测用的谓词；生产传 <see cref="File.Exists"/>，测试可替身。</param>
    public static bool TryLocate(string? configured, Func<string, bool> fileExists, out string path, out string? error)
    {
        path = "";
        error = null;

        if (!string.IsNullOrWhiteSpace(configured))
        {
            var trimmed = configured.Trim();
            // ⚠️ 点名了就必须用它：配置里写错路径时**回落自动探测**会让用户以为配置生效了。
            if (!fileExists(trimmed))
            {
                error = $"配置里指定的 sh 不存在：`{trimmed}`。改 `shell set --executable <路径>`，或清空以自动探测。";
                return false;
            }
            path = trimmed;
            return true;
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var candidate in WindowsCandidates())
            {
                if (fileExists(candidate)) { path = candidate; return true; }
            }

            error = "找不到 sh。Windows 上需要 Git for Windows（它自带 MSYS sh）："
                + "装好之后会自动找到；装在别处时用 `shell set --executable \"<…>\\bin\\sh.exe\"` 点名。";
            return false;
        }

        // Unix：/bin/sh 一定在（POSIX）；不走 PATH 是为了不依赖核心进程的环境。
        path = "/bin/sh";
        if (fileExists(path)) return true;

        error = "找不到 /bin/sh。这个系统缺少 POSIX shell，shell 工具不可用。";
        return false;
    }

    /// <summary>生产用的探测谓词：真实文件系统。</summary>
    public static bool DefaultExists(string path) => File.Exists(path);

    /// <summary>
    /// 跑一段脚本。⚠️ <b>参数经 <c>ArgumentList</c> 原样到达</b>（不拼命令行、不经第二层解析）：
    /// 脚本是本进程看到的**字节**，<c>sh -lc &lt;script&gt;</c> 把它当一个字符串参数收下。
    /// </summary>
    /// <param name="executable">已定位的 <c>sh</c>。</param>
    /// <param name="script">模型给的命令原文。</param>
    /// <param name="workingDirectory">会话的工作目录（脚本的相对路径从这里算）。</param>
    public static async Task<(int ExitCode, string Stdout, string Stderr, bool TimedOut)> RunAsync(
        string executable, string script, string workingDirectory, TimeSpan timeout, CancellationToken ct)
    {
        var start = CreateStartInfo(executable, workingDirectory);
        start.ArgumentList.Add("-l");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new Win32Exception("无法启动 sh");
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            throw new ShellStartException($"无法启动 `{executable}`：{ex.Message}");
        }

        // ⚠️ **立刻关掉 stdin**（2026-10-23 实测修）：`RedirectStandardInput` 让子进程拿到一个**空管道**
        // 而不是继承核心的控制台 —— 后者会在某些组合下让 sh 起不来（实测：核心的 stdout 是管道、
        // 控制台还是另一个句柄时，连 `sh -lc "echo hi"` 都会挂到超时，而同一台机器上换成管道 stdin 就正常）。
        // 关掉写端 ⇒ 下一条命令的 `read` / `cat` 立刻看到 EOF，而不是挂到超时。
        try { process.StandardInput.Close(); }
        catch (IOException) { }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(timeout);

        // ⚠️ **读取只跟外层的取消走，不跟超时走**：超时那一下要先把进程树杀掉、
        // 再把**已经拿到的输出**如实交回——那常常已经能说明卡在哪（一条等输入的命令、一个坏掉的 profile）。
        // 若读取也挂在超时令牌上，那一份现场会被取消一起扔掉。
        var half = ToolLimits.MaxToolOutputBytes / 2;
        var stdoutTask = ReadBoundedAsync(process.StandardOutput, half, ct);
        var stderrTask = ReadBoundedAsync(process.StandardError, half, ct);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            KillTree(process);
            throw;
        }
        catch (OperationCanceledException)
        {
            // ⚠️ 超时是**结果**，不是异常：杀掉整棵进程树，带回已经拿到的输出。
            KillTree(process);
            var (partialOut, partialErr) = await DrainAsync(stdoutTask, stderrTask);
            return (-1, partialOut, partialErr, true);
        }
        catch (IOException ex)
        {
            KillTree(process);
            throw new ShellStartException($"读取 sh 输出失败：{ex.Message}；进程已终止。");
        }

        try
        {
            return (process.ExitCode, await stdoutTask, await stderrTask, false);
        }
        finally
        {
            if (!process.HasExited) KillTree(process);
        }
    }

    /// <summary>
    /// 超时之后把两条读取的**已完成部分**收回来。⚠️ 有上限（3 秒）：
    /// 一个孙进程若还攥着管道，杀树之后读取可能永远等不到 EOF——而这里是「超时处理」，
    /// 不能再挂一个新的超时。
    /// </summary>
    private static async Task<(string Stdout, string Stderr)> DrainAsync(Task<string> stdout, Task<string> stderr)
    {
        var all = Task.WhenAll(stdout, stderr);
        await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(3)));

        return (
            stdout.IsCompletedSuccessfully ? stdout.Result : "",
            stderr.IsCompletedSuccessfully ? stderr.Result : "");
    }

    private static ProcessStartInfo CreateStartInfo(string executable, string workingDirectory)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            // ⚠️ **stdin 重定向后立刻关掉**（见 `RunAsync`）：等输入的命令看到的是 EOF，不是核心的控制台。
            RedirectStandardInput = true,
        };

        // ⚠️ 输出语言固定成英文——同 `DotnetRunner`：同一份失败不该按区域设置渲染成不同文本。
        start.Environment["LC_ALL"] = "C";
        start.Environment["LANG"] = "C";

        return start;
    }

    /// <summary>
    /// 有界读取：只保留开头，最多 <paramref name="maxBytes"/> 字节；到上限后**继续排空管道**但不保留。
    /// ⚠️ 与 `DotnetRunner.ReadBoundedAsync` 同一个形状、同一个理由：工具不替核心做投递截断。
    /// </summary>
    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maxBytes, CancellationToken ct)
    {
        var output = new StringBuilder();
        var bytes = 0;
        var buffer = new char[4096];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), ct);
            if (count == 0) break;
            if (bytes >= maxBytes) continue;

            var chunk = new string(buffer, 0, count);
            var chunkBytes = Encoding.UTF8.GetByteCount(chunk);
            if (bytes + chunkBytes <= maxBytes)
            {
                output.Append(chunk);
                bytes += chunkBytes;
                continue;
            }

            var remaining = maxBytes - bytes;
            var take = chunk.Length;
            while (take > 0 && Encoding.UTF8.GetByteCount(chunk.AsSpan(0, take)) > remaining) take--;
            output.Append(chunk, 0, take);
            bytes = maxBytes;
        }
        return output.ToString();
    }

    /// <summary>⚠️ 整棵树：`sh -c 'make …'` 的孙进程不会随父进程一起走。</summary>
    private static void KillTree(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }
}

/// <summary>`sh` 起不来 / 输出读不了——工具把它转成一条说人话的结果（失败是内容）。</summary>
internal sealed class ShellStartException(string message) : Exception(message);
