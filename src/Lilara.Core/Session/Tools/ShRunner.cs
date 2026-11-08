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
    /// 跑一段脚本。⚠️ **参数经 <c>ArgumentList</c> 原样到达**（不拼命令行、不经第二层解析）：
    /// 脚本是本进程看到的**字节**，<c>sh -lc &lt;script&gt;</c> 把它当一个字符串参数收下。
    /// </summary>
    /// <param name="executable">已定位的 <c>sh</c>。</param>
    /// <param name="script">模型给的命令原文。</param>
    /// <param name="workingDirectory">会话的工作目录（脚本的相对路径从这里算）。</param>
    /// <param name="timeout">这一次的时限。⚠️ 到点之后的两种结局见 <paramref name="tryAdopt"/>。</param>
    /// <param name="tryAdopt">
    /// **到点时能不能转后台**（2026-11-07）。<c>null</c> = 不转（杀掉）。
    /// ⚠️ `shell` 只在「用默认时限」时传它：模型显式给了 `timeoutSeconds` ⇒ 它点名了截止，
    /// 那时到点就该杀，而不是把它挪到后台。
    /// </param>
    public static Task<ForegroundResult> RunAsync(
        string executable, string script, string workingDirectory, TimeSpan timeout,
        CancellationToken ct, Func<IRunningProcess, BackgroundJobInfo?>? tryAdopt = null) =>
        ForegroundRunner.RunAsync(
            ScriptStartInfo(executable, script, workingDirectory), executable, timeout,
            // ⚠️ 元数据由**这里**给：`sh` 怎么拼 argv 只有 `ShellRequest` 知道。
            () => ShellRequest(executable, script, workingDirectory, label: script),
            tryAdopt, ct);

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
    /// **起一个后台进程**（2026-10-24 通用版）：只认「可执行文件 + 参数列表」，
    /// 不认识任何一种具体工具。
    ///
    /// <para>⚠️ <b>与 <see cref="Start"/> 的分界</b>：那个是「跑一段 sh 脚本」（前台那条路
    /// 与 `shell` 的后台都走它），这个是**任意 argv**——`dotnet_build` 自己拼好了参数，
    /// 硬塞回一段 shell 文本会引入引号问题（而引号出错的症状是**静默跑错命令**）。</para>
    ///
    /// <para>⚠️ <b>读泵必须立刻起</b>：不读的话，子进程写满管道（Windows 上约 64 KiB）
    /// 就会**自己阻塞**，而那看起来像「命令卡住了」，完全不像「输出太多」。</para>
    ///
    /// <para>⚠️ <b>它<b>不</b>链接任何 <c>CancellationToken</c></b>：后台任务的寿命与「这一步」
    /// 无关（`session.interrupt` 只打断当前步）。停止由 <see cref="IBackgroundJobs.Kill"/>
    /// 或超时看守发起。</para>
    /// </summary>
    /// <param name="request">纯数据的启动描述（可执行文件 / argv / 环境覆盖 / 工作目录）。</param>
    /// <param name="onChunk">读到一段就调一次（字节，不解码）。⚠️ 回调抛异常不影响读泵继续排空。</param>
    /// <exception cref="ShellStartException">起不来（找不到可执行文件 / 没有权限）。</exception>
    public static (Process Process, Task Pump) StartProcess(
        BackgroundJobRequest request, Func<ReadOnlyMemory<byte>, Task> onChunk)
    {
        var start = new ProcessStartInfo
        {
            FileName = request.Executable,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            // ⚠️ stdin 与前台同一条实测结论：重定向之后**立刻关掉**，
            // 否则某些组合下进程会挂在等输入上（而 `RunAsync` 会等到超时才回来）。
            RedirectStandardInput = true,
        };

        foreach (var argument in request.Arguments) start.ArgumentList.Add(argument);

        // ⚠️ **值为 null = 删掉**：`dotnet_build` 靠它清掉继承的 `DOTNET_*` / `MSBUILD*` /
        // `NUGET_*`（那些是模型看不见的输入，能改变构建行为）。
        foreach (var (key, value) in request.Environment)
        {
            if (value is null) start.Environment.Remove(key);
            else start.Environment[key] = value;
        }

        return StartCore(start, request.Executable, onChunk);
    }

    /// <summary>
    /// 真正起进程的那一段（两条入口共用）。⚠️ 失败时**释放进程对象**再抛：
    /// 不释放的话，一个起不来的 `Process` 会攥着它已经拿到的句柄直到 GC。
    /// </summary>
    private static (Process Process, Task Pump) StartCore(
        ProcessStartInfo start, string executable, Func<ReadOnlyMemory<byte>, Task> onChunk)
    {
        var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new Win32Exception($"无法启动 {executable}");
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            process.Dispose();
            throw new ShellStartException($"无法启动 `{executable}`：{ex.Message}");
        }

        // ⚠️ 同 `RunAsync` 那条实测：stdin 必须是一个**已关闭的管道**，不是继承的控制台。
        try { process.StandardInput.Close(); }
        catch (IOException) { }

        var pump = PumpBothAsync(process, onChunk);
        return (process, pump);
    }

    /// <summary>
    /// **备好一份"跑这段脚本"的启动参数**（`shell` 前台与后台共用这一处）。
    ///
    /// <para>⚠️ <b>抽出来是判据，不是整洁</b>：`sh` 的参数顺序（`-l` 然后 `-c` 然后脚本）、
    /// 三条重定向、`LC_ALL`/`LANG`、stdin 那个「必须重定向再立刻关掉」的实测结论——
    /// 任何一条在第二处抄漏，症状都**不会报错**：`-c` 写成裸参数会静默退化成「跑一个文件」，
    /// 少了 `LC_ALL` 只是输出没人读得懂，少了 stdin 重定向会让命令挂到超时。
    /// 这正是本仓那条「两层判据必须共用一个谓词」。</para>
    /// </summary>
    /// <remarks>
    /// ⚠️ **调用方负责 <c>ArgumentList</c> 的收尾与进程的整个生命周期**：
    /// 本方法只造参数对象，起不起、读不读、什么时候关 stdin 都是调用方的事。
    /// </remarks>
    public static ProcessStartInfo ScriptStartInfo(string executable, string script, string workingDirectory)
    {
        var start = CreateStartInfo(executable, workingDirectory);
        start.ArgumentList.Add("-l");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        return start;
    }

    /// <summary>
    /// **`shell` 那一族后台启动要的那份请求**——`sh -lc &lt;脚本&gt;` 写成纯数据。
    ///
    /// <para>⚠️ <b>它存在，是为了让「sh 在哪、脚本怎么传」只有一处</b>：
    /// `shell` 工具与测试都从这里拿，两处各拼一遍 `["-l","-c",…]` 就会漂
    /// （而漂的后果是**静默跑错东西**）。</para>
    /// </summary>
    public static BackgroundJobRequest ShellRequest(
        string executable, string script, string workingDirectory, string label, int? timeoutSeconds = null) =>
        new()
        {
            Kind = "shell",
            Label = label,
            Executable = executable,
            Arguments = ["-l", "-c", script],
            WorkingDirectory = workingDirectory,
            Environment = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["LC_ALL"] = "C",
                ["LANG"] = "C",
            },
            TimeoutSeconds = timeoutSeconds,
        };

    /// <summary>
    /// **起一个进程，交回它 + 两个持续排空的读泵**——`shell` 的后台那条路。
    ///
    /// <para>⚠️ 它现在是 <see cref="StartProcess"/> 的一层壳：`sh` 专用那部分只在
    /// <see cref="ShellRequest"/> 里，起进程的判据只有一处。⚠️ 保留它是因为
    /// `ShellStartException` 的措辞要说清「是哪个可执行文件起不来」，而那需要 executable 参数。</para>
    /// </summary>
    public static (Process Process, Task Pump) Start(
        string executable, string script, string workingDirectory,
        Func<ReadOnlyMemory<byte>, Task> onChunk) =>
        StartProcess(ShellRequest(executable, script, workingDirectory, label: script), onChunk);

    /// <summary>
    /// 两条流 → 一个回调。⚠️ 用 <c>Task.WhenAll</c> 而非 await 串联（理由见 <see cref="Start"/>）。
    ///
    /// <para>⚠️ <b>顺手把回调的异常吞掉</b>：回调是调用方的东西（写盘 / 计数），
    /// 它抛了之后如果我们跟着停掉读泵，子进程会在写满管道时**卡死**——那时现场是
    /// 「后台任务不输出、也不结束」，而真正的原因早就被另一个异常盖住了。
    /// ⇒ 宁可丢掉后续输出，也不能让子进程卡死。</para>
    /// </summary>
    private static async Task PumpBothAsync(Process process, Func<ReadOnlyMemory<byte>, Task> onChunk)
    {
        var stdout = PumpAsync(process.StandardOutput.BaseStream, onChunk);
        var stderr = PumpAsync(process.StandardError.BaseStream, onChunk);
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
    }

    private static async Task PumpAsync(Stream stream, Func<ReadOnlyMemory<byte>, Task> onChunk)
    {
        var buffer = new byte[8192];
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

            try
            {
                await onChunk(buffer.AsMemory(0, read)).ConfigureAwait(false);
            }
            catch
            {
                // ⚠️ 见 `PumpBothAsync` 的注释：回调的失败不许让子进程卡死。
            }
        }
    }

    // ⚠️ **读取与杀树那两份已经搬到 `ForegroundRunner`**（2026-11-07，前台/后台共用）：
    // 「只保留开头、到上限后继续排空管道」与「整棵进程树」现在各只有**一处**实现。
    // 别把它们加回来——两份实现的漂移**不会报错**，只会让保存上限在一路输出上悄悄变成两倍，
    // 或者让孙进程活下来。
}

/// <summary>`sh` 起不来 / 输出读不了——工具把它转成一条说人话的结果（失败是内容）。</summary>
internal sealed class ShellStartException(string message) : Exception(message);
