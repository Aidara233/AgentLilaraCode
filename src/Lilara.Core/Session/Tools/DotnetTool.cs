using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Lilara.Core.Kernel;

namespace Lilara.Core.Session.Tools;

/// <summary>
/// **受控的 .NET 构建 / 测试执行器**——`dotnet_build` 与 `dotnet_test` 共用这一份。
///
/// <para>⚠️ <b>为什么是「白名单命令 + 结构化参数」，不是 shell</b>：用户裁定过「shell 无防护太危险」，
/// 而 `git_read` / `git_write` 已经立过同一条规矩。这里没有「透传一段命令行」的形状——
/// 模型能给的东西只有 <c>project</c> / <c>noRestore</c> / <c>noIncremental</c> / <c>filter</c>，
/// 其余参数全部由本类拼死。</para>
///
/// <para>⚠️ <b>为什么两个操作是 <see cref="PermissionClass.Writing"/>，而不是 <c>Consuming</c></b>：
/// 表层的理由是「会写 <c>bin/</c> / <c>obj/</c>」，但那是表象。真正的理由是
/// <b>MSBuild 会执行项目里的 target / task，`dotnet test` 更是直接跑工作目录里的任意代码</b>
/// ——模型可以自己写一个 <c>.csproj</c> 再调用这里。⇒ 这两个工具等价于「执行工作目录里的代码」，
/// 必须过写入闸门。报成只读 / 消耗型会让它们在低档位**静默执行任意代码**。</para>
///
/// <para>⚠️ <b>为什么两种操作各是一个工具，而不是一个枚举</b>：参数面、超时、描述都不同；
/// 而工具表在 Claude 协议下位于**前缀最前端**（<c>00</c> §7 判据 5）——同一批加入一次，
/// 改起来不会多破一次前缀。</para>
///
/// <para>⚠️ <b>同目录互斥</b>：同一工作目录的两个会话并发跑 <c>dotnet</c> 会在 <c>obj/</c> /
/// <c>bin/</c> 上撞文件锁，报出来的是一条**与源码无关**的 MSBuild 错误。⇒ 进程内按规范化工作目录
/// 持有一把 <see cref="SemaphoreSlim"/>，覆盖整个子进程生命周期。这是「会话内严格串行」之外
/// <b>唯一</b>一处跨会话同步，而它只保护**工具自己造成的**共享状态（构建中间产物），不是核心状态。</para>
/// </summary>
internal static class DotnetRunner
{
    /// <summary>超时是<b>占位值</b>（同 <see cref="ToolLimits"/> 那条纪律）：没有实测基准，取一个宽松值。</summary>
    internal static readonly TimeSpan BuildTimeout = TimeSpan.FromSeconds(180);
    internal static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(300);

    private const int MaxFilterChars = 512;
    private static readonly string[] ProjectExtensions = [".sln", ".slnx", ".csproj"];

    /// <summary>
    /// 同目录构建互斥。⚠️ <b>只为进程内会话服务</b>：本机唯一核心（<c>README</c> 的硬约束）
    /// ⇒ 不需要跨进程锁；两个 <c>dotnet</c> 进程之间该打架的部分由 MSBuild 自己报错。
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> DirectoryGates =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static Task<ToolOutcome> RunBuildAsync(
        string executable, ToolCall call, IToolContext context, CancellationToken ct) =>
        RunBuildAsync(executable, call, context, BuildTimeout, ct);

    public static async Task<ToolOutcome> RunBuildAsync(
        string executable, ToolCall call, IToolContext context, TimeSpan timeout, CancellationToken ct) =>
        await RunAsync("dotnet_build", "build", executable, call, context, timeout, ct);

    public static Task<ToolOutcome> RunTestAsync(
        string executable, ToolCall call, IToolContext context, CancellationToken ct) =>
        RunTestAsync(executable, call, context, TestTimeout, ct);

    public static async Task<ToolOutcome> RunTestAsync(
        string executable, ToolCall call, IToolContext context, TimeSpan timeout, CancellationToken ct) =>
        await RunAsync("dotnet_test", "test", executable, call, context, timeout, ct);

    private static async Task<ToolOutcome> RunAsync(
        string toolName, string verb, string executable, ToolCall call, IToolContext context,
        TimeSpan timeout, CancellationToken ct)
    {
        if (!TryParseArguments(toolName, verb, call.ArgumentsJson, context,
                out var project, out var noRestore, out var noIncremental, out var filter, out var error))
            return Decision(toolName, error!);

        var args = BuildArguments(verb, project, noRestore, noIncremental, filter);

        var start = CreateStartInfo(executable, context, args);
        using var process = new Process { StartInfo = start };

        var gate = DirectoryGates.GetOrAdd(context.WorkingDirectory, _ => new SemaphoreSlim(1, 1));
        try
        {
            // ⚠️ 等闸也要响应取消：一个长测试在跑时，另一个会话不能「等完才看见自己已经被打断」。
            await gate.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }

        try
        {
            try
            {
                if (!process.Start())
                    return Decision(toolName, $"无法启动 `dotnet {verb}`；请确认已安装 .NET SDK 并在 PATH 中。");
            }
            catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
            {
                return Decision(toolName, $"无法启动 `dotnet {verb}`；请确认已安装 .NET SDK 并在 PATH 中。");
            }

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutSource.CancelAfter(timeout);
            try
            {
                // ⚠️ **工具侧只防炸盘**：完整输出入档，投递截断由
                // `TimelineRender.RenderForModel` 一处做。
                // ⚠️ stdout / stderr **共用**保存上限的一半：否则两路各自可以顶到上限，
                // 拼起来就是一倍——保存上限从「上限」变成「建议」。
                var half = ToolLimits.MaxToolOutputBytes / 2;
                var stdoutTask = ReadBoundedAsync(process.StandardOutput, half, timeoutSource.Token);
                var stderrTask = ReadBoundedAsync(process.StandardError, half, timeoutSource.Token);
                await process.WaitForExitAsync(timeoutSource.Token);
                var stdout = await stdoutTask;
                var stderr = await stderrTask;
                return Format(toolName, verb, project, process.ExitCode, stdout, stderr, timedOut: false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                KillTree(process);
                return Decision(toolName,
                    $"`dotnet {verb}` 超过 {timeout.TotalSeconds:F0} 秒被终止，输出不完整；"
                    + "可以缩小范围（指定更小的 `project`，测试用 `filter`）后重试。");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                KillTree(process);
                throw;
            }
            catch (IOException ex)
            {
                KillTree(process);
                return Decision(toolName, $"读取 `dotnet {verb}` 输出失败：{ex.Message}；构建进程已终止。");
            }
            finally
            {
                if (!process.HasExited) KillTree(process);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 解析并校验全部参数（`--` 之前、启动进程之前）。⚠️ <b>先判全、再执行</b>与
    /// <see cref="ToolExecutor"/> 那条同形：任何一项不合法都**不启动任何进程**。
    /// </summary>
    private static bool TryParseArguments(
        string toolName, string verb, string json, IToolContext context,
        out string project, out bool noRestore, out bool noIncremental, out string? filter, out string? error)
    {
        project = "";
        noRestore = false;
        noIncremental = false;
        filter = null;
        error = null;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            error = "参数必须是 JSON 对象。";
            return false;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "参数必须是 JSON 对象。";
                return false;
            }

            var allowed = verb == "build"
                ? new[] { "project", "noRestore", "noIncremental" }
                : new[] { "project", "noRestore", "filter" };
            var unknown = root.EnumerateObject().Select(p => p.Name)
                .FirstOrDefault(name => !allowed.Contains(name, StringComparer.Ordinal));
            if (unknown is not null)
            {
                error = $"`{toolName}` 不支持参数 `{unknown}`。";
                return false;
            }

            if (root.TryGetProperty("noRestore", out var restoreValue))
            {
                // ⚠️ 只认 JSON 布尔：`"true"` 字符串要被拒绝，而不是替模型解析——
                // 同 `ToolArgs.Number` 那条「只认它说的话，不猜」。
                if (restoreValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    error = "`noRestore` 必须是 JSON 布尔值。";
                    return false;
                }
                noRestore = restoreValue.GetBoolean();
            }

            if (verb == "build" && root.TryGetProperty("noIncremental", out var incrementalValue))
            {
                if (incrementalValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    error = "`noIncremental` 必须是 JSON 布尔值。";
                    return false;
                }
                noIncremental = incrementalValue.GetBoolean();
            }

            if (verb == "test" && root.TryGetProperty("filter", out var filterValue))
            {
                if (filterValue.ValueKind != JsonValueKind.String || !SafeFilter(filterValue.GetString()))
                {
                    error = "`filter` 必须是一个合法的测试筛选表达式（例如 FullyQualifiedName~StepperTests），"
                        + $"最多 {MaxFilterChars} 个字符，且不能以 `-` 开头。";
                    return false;
                }
                filter = filterValue.GetString();
            }

            var projectArg = root.TryGetProperty("project", out var projectValue) ? projectValue : default;
            if (root.TryGetProperty("project", out _))
            {
                if (projectValue.ValueKind != JsonValueKind.String)
                {
                    error = "`project` 必须是字符串。";
                    return false;
                }
                var raw = projectValue.GetString();
                if (!TryResolveProject(raw, context, out project, out error)) return false;
            }
            else
            {
                if (!TryDiscoverProject(context, out project, out error)) return false;
            }

            return true;
        }
    }

    /// <summary>
    /// 把模型给的路径解析成**工作目录内**的项目文件。
    /// ⚠️ 越界闸与 `git_*` 共用 <see cref="IToolContext.ResolvePath"/>，不另写一份。
    /// </summary>
    private static bool TryResolveProject(string? raw, IToolContext context, out string project, out string? error)
    {
        project = "";
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "`project` 不能是空串；省略它表示使用工作目录顶层的解决方案 / 项目文件。";
            return false;
        }
        if (raw[0] == '-')
        {
            error = "`project` 不能以 `-` 开头。";
            return false;
        }

        var full = context.ResolvePath(raw);
        if (full is null || !File.Exists(full))
        {
            error = $"`project` 必须是工作目录内存在的解决方案或项目文件：'{raw}'。";
            return false;
        }
        if (!ProjectExtensions.Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase))
        {
            error = "`project` 只接受 `.sln` / `.slnx` / `.csproj` 文件。";
            return false;
        }

        var relative = Path.GetRelativePath(context.WorkingDirectory, full).Replace('\\', '/');
        if (relative.StartsWith("../", StringComparison.Ordinal) || relative == "..")
        {
            error = $"`project` 必须是工作目录内的文件：'{raw}'。";
            return false;
        }
        project = relative;
        return true;
    }

    /// <summary>
    /// 省略 `project` 时找工作目录**顶层**的唯一解决方案 / 项目文件。
    /// ⚠️ 不递归、不猜：零个或多个都报错并列出候选。
    /// </summary>
    private static bool TryDiscoverProject(IToolContext context, out string project, out string? error)
    {
        project = "";
        error = null;
        List<string> candidates;
        try
        {
            candidates = Directory.EnumerateFiles(context.WorkingDirectory)
                .Where(f => ProjectExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .Select(Path.GetFileName)
                .Where(n => n is not null && n[0] != '.')
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"列不出工作目录里的项目文件：{ex.Message}。";
            return false;
        }

        if (candidates.Count == 1)
        {
            project = candidates[0];
            return true;
        }
        error = candidates.Count == 0
            ? "工作目录顶层没有 `.sln` / `.slnx` / `.csproj` 文件；请用 `project` 指定一个。"
            : "工作目录顶层有多个候选，请用 `project` 指定一个：" + string.Join("、", candidates);
        return false;
    }

    /// <summary>
    /// `test --filter` 的表达式校验。⚠️ 它是**一个表达式**，不是任意参数——但仍是模型输入，
    /// 所以按不可信从紧：字符白名单 + 长度上限 + 拒绝 `-` 开头。
    /// 允许 `FullyQualifiedName` 表达式的常见语法；不允许 shell 元字符（这里没有 shell，但也不给它们机会）。
    /// </summary>
    private static bool SafeFilter(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= MaxFilterChars
        && value[0] != '-'
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '~' or '=' or '!' or '&' or '|'
            or '(' or ')' or ' ' or '"' or '\'' or '-' or ':' or ',');

    /// <summary>
    /// 命令行的**唯一**拼法。⚠️ 它是 <see cref="TryParseArguments"/> 与测试**共用的同一份**——
    /// 测试里手抄一份「期望的参数」再和它比，等于让测试去校验自己抄的那份，而不是校验产品。
    /// </summary>
    internal static IReadOnlyList<string> BuildArguments(
        string verb, string project, bool noRestore, bool noIncremental, string? filter)
    {
        var args = new List<string>
        {
            verb, project,
            "--nologo", "--verbosity", "minimal", "--disable-build-servers",
        };
        if (noRestore) args.Add("--no-restore");
        if (verb == "build" && noIncremental) args.Add("--no-incremental");
        if (verb == "test" && filter is not null) args.AddRange(["--filter", filter]);
        return args;
    }

    private static ProcessStartInfo CreateStartInfo(string executable, IToolContext context, IEnumerable<string> args)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = context.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);

        // ⚠️ 清掉继承的构建环境：外部的 `DOTNET_*` / `MSBUILD*` / `NUGET_*` 能改变构建行为
        // （目标框架、还原源、诊断开关），而模型不该被这些**它看不见的输入**影响；
        // 同 `GitWriteTool` 清 `GIT_*` 的判据。
        foreach (var key in start.Environment.Keys
                     .Where(k => k.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase)
                         || k.StartsWith("MSBUILD", StringComparison.OrdinalIgnoreCase)
                         || k.StartsWith("NUGET_", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
            start.Environment.Remove(key);

        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        start.Environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "0";
        // 构建输出的语言固定成英文，避免同一份失败在不同区域设置下渲染成不同的文本。
        start.Environment["LC_ALL"] = "C";
        start.Environment["LANG"] = "C";
        // ⚠️ Windows 上 `dotnet` 会把中文摘要按控制台代码页（默认 936/GBK）写出来，
        // 而这里按 UTF-8 解码 ⇒ 不解码失败也只会得到乱码，且**不报错**。
        start.Environment["VSLANG"] = "1033";
        return start;
    }

    /// <summary>
    /// 有界读取：**只保留开头**，最多 <see cref="ToolLimits.MaxToolOutputBytes"/> 字节。
    ///
    /// <para>⚠️ <b>工具不再替核心做投递截断</b>（2026-10-17）：完整的构建 / 测试输出
    /// 必须**入档**，否则 `fetch` 取不回被砍掉的部分。喂给模型时只显示开头一段 +
    /// `fetch(id, offset)` 注记——那由 `TimelineRender.RenderForModel` 一处做。
    /// ⇒ 这里只防「一次调用把内存 / 盘撑爆」，读到上限后**继续排空管道**但不保留。</para>
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

            // 最后一段按字节裁到上限（字符同样按边界回退，避免半个字符）。
            var remaining = maxBytes - bytes;
            var take = chunk.Length;
            while (take > 0 && Encoding.UTF8.GetByteCount(chunk.AsSpan(0, take)) > remaining) take--;
            output.Append(chunk, 0, take);
            bytes = maxBytes;
        }
        return output.ToString();
    }

    private static ToolOutcome Format(
        string toolName, string verb, string project, int exitCode, string stdout, string stderr, bool timedOut)
    {
        var ok = exitCode == 0 && !timedOut;
        var outcomeText = verb == "build" ? "构建" : "测试";
        var result = new StringBuilder();

        // ⚠️ 第一行是**结论**：成功 / 失败必须一眼可见，且失败时**不**用「已完成」这种中性词。
        result.AppendLine(ok ? $"{outcomeText}成功。" : $"{outcomeText}失败（退出码 {exitCode}）。");
        if (timedOut) result.AppendLine("⚠️ 进程因超时被终止，下面的输出不完整。");

        result.Append(stdout);
        if (stderr.Length > 0) result.AppendLine("stderr:").Append(stderr);

        return new ToolOutcome
        {
            Content = [new Block.TextBlock(result.ToString())],
            // ⚠️ 编译 / 测试属于「不可重演」（`00` §5）：同一个命令再跑一次可能得到不同结果
            // （时间、并发、外部状态），而错误的指纹会让三态判定**声称世界没动过**。
            // ⇒ 不给指纹、不给指称：它们永不被点杀回收，只靠保护带 / 线 / 显式回收。
            Fingerprint = null,
            DependsOn = null,
            Framing = new ToolFraming(toolName, [
                new ToolFramingField("project", project),
                new ToolFramingField("exit", exitCode.ToString()),
            ]),
        };
    }

    private static void KillTree(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }

    private static ToolOutcome Decision(string toolName, string text) => new()
    {
        Content = [new Block.TextBlock(text)],
        Framing = new ToolFraming(toolName, [new ToolFramingField("结果", "未执行")]),
    };
}

/// <summary>
/// `dotnet_build`——**编译**当前工作目录里的解决方案 / 项目（`01` §3.5 的「自检」）。
///
/// <para>⚠️ <b>它是 `verify` 轴的第一条落点</b>：在它之前，「改完自己跑编译」这句话
/// 对模型是**做不到的事**——核心没有 shell，也没有构建工具。⇒ 那条轴只能约束模型「说老实话」，
/// 产不出验证。</para>
///
/// <para>⚠️ <b>权限是写入，理由是「执行工作目录里的代码」</b>（见 <see cref="DotnetRunner"/> 头注释）：
/// `dotnet build` 会跑 MSBuild target / task，而项目文件是模型自己能写的。
/// 把它当只读会在 `readonly` 档放行任意代码执行。</para>
/// </summary>
public sealed class DotnetBuildTool : ITool
{
    private readonly string _executable;

    public DotnetBuildTool() : this("dotnet") { }
    internal DotnetBuildTool(string executable) => _executable = executable;

    public string Name => "dotnet_build";
    public PermissionClass PermissionClass => PermissionClass.Writing;

    public string Description =>
        "编译工作目录里的 .NET 解决方案或项目，返回构建结果与错误输出。"
        + "不给 `project` 时使用工作目录顶层的唯一 .sln / .slnx / .csproj；多个候选会报错并列出它们。"
        + "会先还原依赖（可能联网）；`noRestore` 跳过还原。"
        + "`noIncremental` 强制重新编译——改完代码想确认「真的能编过」时用它。"
        + "只编译，不跑测试；不开放其他 dotnet 参数。";

    public string ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "project": {
              "type": "string",
              "description": "相对于工作目录的 .sln / .slnx / .csproj 路径。省略 = 使用工作目录顶层的唯一候选。"
            },
            "noRestore": {
              "type": "boolean",
              "description": "跳过还原（确定依赖已还原时才用）。"
            },
            "noIncremental": {
              "type": "boolean",
              "description": "忽略增量构建结果，强制完整重新编译。"
            }
          }
        }
        """;

    public Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct) =>
        DotnetRunner.RunBuildAsync(_executable, call, context, ct);
}

/// <summary>
/// `dotnet_test`——**跑测试**（`01` §3.5 的「相关测试」那一半）。
///
/// <para>⚠️ <b>它比 <see cref="DotnetBuildTool"/> 更危险，而权限类别是同一档</b>：
/// 测试体是任意代码（可以删目录、起进程、写盘），但它是**可逆**的（过程产物在
/// <c>bin/</c> / <c>obj/</c> / <c>TestResults/</c>），所以落在写入档而不是高危档。
/// ⚠️ 如果将来出现「测试写到了工作目录之外」的用法，那不是这个工具该兜的——
/// 它是「执行任意代码」的既有代价，与进程内插件那条「装一个插件 = 把核心交给它」同源。</para>
/// </summary>
public sealed class DotnetTestTool : ITool
{
    private readonly string _executable;

    public DotnetTestTool() : this("dotnet") { }
    internal DotnetTestTool(string executable) => _executable = executable;

    public string Name => "dotnet_test";
    public PermissionClass PermissionClass => PermissionClass.Writing;

    public string Description =>
        "运行工作目录里的 .NET 项目的测试，返回通过 / 失败摘要与失败输出。"
        + "不给 `project` 时使用工作目录顶层的唯一 .sln / .slnx / .csproj；多个候选会报错并列出它们。"
        + "会先还原依赖（可能联网）；`noRestore` 跳过还原。"
        + "`filter` 只跑匹配的测试，例如 FullyQualifiedName~StepperTests。"
        + "不开放其他 dotnet 参数。";

    public string ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "project": {
              "type": "string",
              "description": "相对于工作目录的 .sln / .slnx / .csproj 路径。省略 = 使用工作目录顶层的唯一候选。"
            },
            "noRestore": {
              "type": "boolean",
              "description": "跳过还原（确定依赖已还原时才用）。"
            },
            "filter": {
              "type": "string",
              "description": "测试筛选表达式，例如 FullyQualifiedName~StepperTests；最多 512 字符。"
            }
          }
        }
        """;

    public Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct) =>
        DotnetRunner.RunTestAsync(_executable, call, context, ct);
}
