using System.Diagnostics;
using System.Text;
using Lilara.Core.Kernel;

namespace Lilara.Core.Session.Tools;

public sealed class GitReadTool : ITool
{
    // ⚠️ 采集上限 = **保存**上限（`01` §9.3，2026-10-17）：大结果全量入档，
    // 喂给模型的截断由 `TimelineRender.RenderForModel` 一处做。工具这里只防「一次调用撑爆内存 / 盘」。
    private const int MaxOutputBytes = ToolLimits.MaxToolOutputBytes;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);
    private readonly string _gitExecutable;

    public GitReadTool() : this("git") { }
    internal GitReadTool(string gitExecutable) => _gitExecutable = gitExecutable;
    public string Name => "git_read";
    public PermissionClass PermissionClass => PermissionClass.ReadOnly;
    public string Description => "只读查询当前工作目录所属 Git 仓库：status、diff、log、branch、rev-parse、show、blame。命令和选项均受限；不修改仓库、不联网。输出可能截断，可用路径或分页参数缩小范围。";
    public string ParametersSchemaJson => """
        {
          "type":"object",
          "properties":{
            "command":{"type":"string","enum":["status","diff","log","branch","rev-parse","show","blame"]},
            "path":{"type":"string","description":"可选的仓库内相对路径；diff、show、blame 可使用。"},
            "revision":{"type":"string","description":"可选提交或引用；只接受安全引用字符，不能以 - 开头。"},
            "staged":{"type":"boolean","description":"diff 是否查看暂存区；仅 diff 使用。"},
            "all":{"type":"boolean","description":"branch 是否包含远端跟踪引用；只本地读取，不联网。"},
            "limit":{"type":"integer","description":"log 最多条数，1..100；默认 20。"},
            "skip":{"type":"integer","description":"log 跳过条数，0..10000；默认 0。"},
            "unified":{"type":"integer","description":"diff 上下文行数，0..20；默认 3。"},
            "startLine":{"type":"integer","description":"blame 起始行（1 基），需与 endLine 一起给。"},
            "endLine":{"type":"integer","description":"blame 结束行（含），需与 startLine 一起给，范围最多 200 行。"}
          },
          "required":["command"]
        }
        """;

    public async Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct)
    {
        var command = ToolArgs.String(call.ArgumentsJson, "command");
        if (command is null || !Commands.Contains(command, StringComparer.Ordinal))
            return Outcome("`command` 必须是 status、diff、log、branch、rev-parse、show 或 blame。");

        var pathArg = ToolArgs.String(call.ArgumentsJson, "path");
        if (ToolPaths.HasKey(call.ArgumentsJson, "path") && pathArg is null)
            return Outcome("`path` 必须是字符串。");
        var path = ResolveRepoPath(pathArg, context);
        if (pathArg is not null && path is null)
            return Outcome("`path` 必须是工作目录内存在的仓库相对路径；不接受绝对路径或越界路径。");

        var revision = ToolArgs.String(call.ArgumentsJson, "revision");
        if (ToolPaths.HasKey(call.ArgumentsJson, "revision") && revision is null)
            return Outcome("`revision` 必须是字符串。");
        if (revision is not null && !SafeRevision(revision))
            return Outcome("`revision` 格式不安全；只接受分支/标签名称、对象短哈希或 HEAD~N/HEAD^N 形式，且不能以 `-` 开头。");
        var unused = UnusedArguments(command, call.ArgumentsJson);
        if (unused is not null) return Outcome(unused);

        var args = BuildArguments(command, call.ArgumentsJson, path, revision, out var error);
        if (error is not null) return Outcome(error);

        var start = new ProcessStartInfo
        {
            FileName = _gitExecutable,
            WorkingDirectory = context.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["GIT_PAGER"] = "cat";
        start.Environment["PAGER"] = "cat";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["GIT_ATTR_NOSYSTEM"] = "1";
        start.Environment["GIT_LITERAL_PATHSPECS"] = "1";
        start.Environment["LC_ALL"] = "C";
        start.Environment["LANG"] = "C";

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) return Outcome("无法启动 Git；请确认已安装并在 PATH 中。");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return Outcome("无法启动 Git；请确认已安装并在 PATH 中。");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            var stdoutTask = ReadBoundedAsync(process.StandardOutput, MaxOutputBytes, timeout.Token);
            var stderrTask = ReadBoundedAsync(process.StandardError, 4096, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (stdout.Truncated)
                return Outcome(Format(command, stdout.Text, process.ExitCode, stderr.Text, truncated: true));
            if (process.ExitCode != 0)
            {
                var message = stderr.Text.Trim();
                return Outcome(string.IsNullOrEmpty(message)
                    ? $"git {command} 失败（退出码 {process.ExitCode}）。"
                    : $"git {command} 失败（退出码 {process.ExitCode}）：\n{message}");
            }

            return Outcome(Format(command, stdout.Text, process.ExitCode, stderr.Text, truncated: false));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            KillTree(process);
            return Outcome($"git {command} 超过 {Timeout.TotalSeconds:F0} 秒，已终止；可缩小查询范围后重试。");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            KillTree(process);
            throw;
        }
        catch (IOException ex)
        {
            KillTree(process);
            return Outcome($"读取 git {command} 输出失败：{ex.Message}");
        }
        finally
        {
            if (!process.HasExited) KillTree(process);
        }
    }

    private static readonly string[] Commands = ["status", "diff", "log", "branch", "rev-parse", "show", "blame"];

    private static IReadOnlyList<string> BuildArguments(
        string command, string json, string? path, string? revision, out string? error)
    {
        error = null;
        var args = new List<string> { "-c", "core.fsmonitor=false", "-c", "diff.external=", "--no-pager", "--no-optional-locks" };
        switch (command)
        {
            case "status":
                args.AddRange(["status", "--porcelain=v1", "--branch", "--untracked-files=all"]);
                break;
            case "diff":
            {
                var unified = Integer(json, "unified", 3, 0, 20, out error);
                if (error is not null) return [];
                args.AddRange(["diff", "--no-ext-diff", "--no-textconv", "--no-color", $"--unified={unified}"]);
                if (Boolean(json, "staged", false, out error)) args.Add("--cached");
                if (error is not null) return [];
                if (revision is not null) args.Add(revision);
                args.Add("--");
                if (path is not null) args.Add(path);
                break;
            }
            case "log":
            {
                var limit = Integer(json, "limit", 20, 1, 100, out error);
                if (error is not null) return [];
                var skip = Integer(json, "skip", 0, 0, 10000, out error);
                if (error is not null) return [];
                args.AddRange(["log", "--no-color", "--date=iso-strict", "--format=%h%x09%ad%x09%s", $"-n{limit}", $"--skip={skip}"]);
                if (revision is not null) args.Add(revision);
                if (path is not null) args.AddRange(["--", path]);
                break;
            }
            case "branch":
                args.AddRange(["branch", "--list", "--no-color", "--sort=refname"]);
                var includeAll = Boolean(json, "all", false, out error);
                if (error is not null) return [];
                if (includeAll) args.Add("--all");
                break;
            case "rev-parse":
                args.AddRange(["rev-parse", "--verify", "--end-of-options", revision ?? "HEAD"]);
                break;
            case "show":
                args.AddRange(["show", "--no-ext-diff", "--no-textconv", "--no-color", "--format=fuller", "--stat", "--patch", revision ?? "HEAD", "--"]);
                if (path is not null) args.Add(path);
                break;
            case "blame":
            {
                if (path is null) { error = "`blame` 必须提供工作目录内的 `path`。"; return []; }
                var hasStart = ToolPaths.HasKey(json, "startLine");
                var hasEnd = ToolPaths.HasKey(json, "endLine");
                if (hasStart != hasEnd) { error = "`startLine` 与 `endLine` 必须同时提供。"; return []; }
                args.AddRange(["blame", "--date=short"]);
                if (hasStart)
                {
                    var first = Integer(json, "startLine", 0, 1, int.MaxValue, out error);
                    if (error is not null) return [];
                    var last = Integer(json, "endLine", 0, first, int.MaxValue, out error);
                    if (error is not null) return [];
                    if (last - first > 199) { error = "blame 行范围最多 200 行。"; return []; }
                    args.AddRange(["-L", $"{first},{last}"]);
                }
                if (revision is not null) args.Add(revision);
                args.Add("--");
                args.Add(path);
                break;
            }
        }
        return args;
    }

    private static string? UnusedArguments(string command, string json)
    {
        var allowed = command switch
        {
            "status" => Array.Empty<string>(),
            "diff" => new[] { "path", "revision", "staged", "unified" },
            "log" => new[] { "path", "revision", "limit", "skip" },
            "branch" => new[] { "all" },
            "rev-parse" => new[] { "revision" },
            "show" => new[] { "path", "revision" },
            "blame" => new[] { "path", "revision", "startLine", "endLine" },
            _ => Array.Empty<string>(),
        };
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return "参数必须是 JSON 对象。";
            var unknown = doc.RootElement.EnumerateObject()
                .Select(p => p.Name)
                .FirstOrDefault(name => name != "command" && !allowed.Contains(name, StringComparer.Ordinal));
            return unknown is null ? null : $"`{command}` 不支持参数 `{unknown}`。";
        }
        catch (System.Text.Json.JsonException)
        {
            return "Git 工具参数必须是合法 JSON 对象。";
        }
    }

    private static string? ResolveRepoPath(string? path, IToolContext context)
    {
        if (path is null) return null;
        if (string.IsNullOrWhiteSpace(path)
            || Path.IsPathRooted(path)
            || System.Text.RegularExpressions.Regex.IsMatch(path, @"^[A-Za-z]:[\\/]", System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            || path.StartsWith("\\\\", StringComparison.Ordinal)) return null;
        var full = context.ResolvePath(path);
        if (full is null) return null;
        var relative = Path.GetRelativePath(context.WorkingDirectory, full).Replace('\\', '/');
        return relative == "." ? null : relative;
    }

    private static bool SafeRevision(string revision)
    {
        if (revision.Length is 0 or > 256 || revision[0] == '-') return false;
        if (revision.All(Uri.IsHexDigit)) return revision.Length is >= 4 and <= 64;
        return revision.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '/' or '-')
            || System.Text.RegularExpressions.Regex.IsMatch(revision, @"^(HEAD|[A-Za-z0-9_./-]+)([~^][0-9]*)+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    private static int Integer(string json, string key, int defaultValue, int min, int max, out string? error)
    {
        error = null;
        if (!ToolPaths.HasKey(json, key)) return defaultValue;
        var value = ToolArgs.Number(json, key);
        if (value is null || value < min || value > max)
        {
            error = $"`{key}` 必须是 {min} 到 {max} 之间的整数。";
            return defaultValue;
        }
        return (int)value.Value;
    }

    private static bool Boolean(string json, string key, bool defaultValue, out string? error)
    {
        error = null;
        if (!ToolPaths.HasKey(json, key)) return defaultValue;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(key, out var value)
                && value.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                return value.GetBoolean();
        }
        catch (System.Text.Json.JsonException) { }

        error = $"`{key}` 必须是 JSON 布尔值。";
        return defaultValue;
    }

    private static async Task<(string Text, bool Truncated)> ReadBoundedAsync(StreamReader reader, int maxBytes, CancellationToken ct)
    {
        var buffer = new char[1024];
        var output = new StringBuilder();
        var bytes = 0;
        var truncated = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), ct);
            if (count == 0) return (output.ToString(), truncated);
            var chunk = new string(buffer, 0, count);
            var remaining = maxBytes - bytes;
            if (remaining > 0)
            {
                var encoded = Encoding.UTF8.GetBytes(chunk);
                if (encoded.Length <= remaining)
                {
                    output.Append(chunk);
                    bytes += encoded.Length;
                }
                else
                {
                    var chars = Encoding.UTF8.GetCharCount(encoded, 0, remaining);
                    output.Append(chunk.AsSpan(0, Math.Min(chars, chunk.Length)));
                    bytes = maxBytes;
                    truncated = true;
                }
            }
            else truncated = true;
        }
    }

    private static string Format(string command, string stdout, int exitCode, string stderr, bool truncated)
    {
        // ⚠️ 这里的 `truncated` 只可能来自**保存上限**（4 MiB），不再是投递预算：
        // 一般的「输出长」应当**完整入档**，由渲染层投递给模型打开头一段 + `fetch` 接续。
        // 走到这里说明单次调用真的到了保存上限——那是一条**罕见且明确**的失败形态，要说清楚。
        var result = new StringBuilder($"git {command}：\n");
        var marker = truncated
            ? $"\n[输出超过单次保存上限 {ToolLimits.MaxToolOutputBytes} 字节，余下部分未采集；请缩小查询范围重试。]\n"
            : "";
        var bodyLimit = ToolLimits.MaxToolOutputBytes - marker.Length;
        result.Append(stdout.Length <= bodyLimit ? stdout : stdout[..bodyLimit]);
        if (stdout.Length == 0) result.Append("(无输出)\n");
        if (truncated) result.Append(marker);
        if (!string.IsNullOrWhiteSpace(stderr) && result.Length < ToolLimits.MaxToolOutputBytes - 256)
            result.AppendLine($"\nstderr: {stderr.Trim()}");
        if (exitCode != 0 && result.Length < ToolLimits.MaxToolOutputBytes - 64)
            result.AppendLine($"\n退出码：{exitCode}");
        if (result.Length > ToolLimits.MaxToolOutputBytes) result.Length = ToolLimits.MaxToolOutputBytes;
        return result.ToString();
    }

    private static void KillTree(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static ToolOutcome Outcome(string text) => new()
    {
        Content = [new Block.TextBlock(text)],
    };
}
