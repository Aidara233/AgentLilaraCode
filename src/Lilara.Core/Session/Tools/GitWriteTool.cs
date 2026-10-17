using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Lilara.Core.Kernel;

namespace Lilara.Core.Session.Tools;

public sealed class GitWriteTool : ITool
{
    // ⚠️ 同 `GitReadTool`：这是**保存**上限，投递截断在渲染层。
    private const int MaxOutputBytes = ToolLimits.MaxToolOutputBytes;
    private const int MaxPaths = 100;
    private const int MaxMessageBytes = 16 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly string _gitExecutable;

    public GitWriteTool() : this("git") { }
    internal GitWriteTool(string gitExecutable) => _gitExecutable = gitExecutable;

    public string Name => "git_write";
    public PermissionClass PermissionClass => PermissionClass.Writing;
    public string Description => "执行受限的本地 Git 操作：add、unstage、commit、branch_create、tag_create。标准权限档会询问；不支持远端操作、reset、clean 或任意 Git 参数。add/unstage 必须显式指定仓库内路径，不会默认操作整个仓库。";
    public string ParametersSchemaJson => """
        {
          "type":"object",
          "properties":{
            "operation":{"type":"string","enum":["add","unstage","commit","branch_create","tag_create"]},
            "paths":{"type":"array","items":{"type":"string"},"description":"add 或 unstage 要处理的仓库内相对路径；必填且最多 100 个。"},
            "message":{"type":"string","description":"commit 提交消息，必填，最多 16 KiB UTF-8。"},
            "name":{"type":"string","description":"branch_create 或 tag_create 的新名称。"},
            "revision":{"type":"string","description":"可选起点；默认 HEAD。只接受安全引用或 HEAD~N/HEAD^N 形式。"}
          },
          "required":["operation"]
        }
        """;

    public async Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct)
    {
        if (!TryParse(call.ArgumentsJson, out var root, out var parseError)) return Outcome(parseError!);
        using (root)
        {
            var json = root!.RootElement;
            if (json.ValueKind != JsonValueKind.Object) return Outcome("参数必须是 JSON 对象。");
            var operation = String(json, "operation");
            if (operation is not ("add" or "unstage" or "commit" or "branch_create" or "tag_create"))
                return Outcome("`operation` 必须是 add、unstage、commit、branch_create 或 tag_create。");

            var allowed = operation switch
            {
                "add" or "unstage" => new[] { "paths" },
                "commit" => new[] { "message" },
                "branch_create" or "tag_create" => new[] { "name", "revision" },
                _ => Array.Empty<string>(),
            };
            var unknown = json.EnumerateObject().Select(p => p.Name)
                .FirstOrDefault(name => name != "operation" && !allowed.Contains(name, StringComparer.Ordinal));
            if (unknown is not null) return Outcome($"`{operation}` 不支持参数 `{unknown}`。");

            var args = new List<string>();
            string summary;
            switch (operation)
            {
                case "add":
                case "unstage":
                {
                    if (!json.TryGetProperty("paths", out var pathsElement) || pathsElement.ValueKind != JsonValueKind.Array)
                        return Outcome("`paths` 必须是非空字符串数组。");
                    var paths = new List<string>();
                    foreach (var item in pathsElement.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String) return Outcome("`paths` 中每一项都必须是字符串。");
                        var input = item.GetString();
                        var path = ResolveRepoPath(input, context);
                        if (path is null) return Outcome($"路径 '{input}' 无效；必须是工作目录内的非根目录相对路径。");
                        if (!paths.Contains(path, StringComparer.Ordinal)) paths.Add(path);
                    }
                    if (paths.Count == 0 || paths.Count > MaxPaths) return Outcome($"`paths` 必须包含 1 到 {MaxPaths} 个有效路径。");
                    args.AddRange(operation == "add" ? ["add", "--"] : ["restore", "--staged", "--"]);
                    args.AddRange(paths);
                    summary = operation == "add"
                        ? $"已将 {paths.Count} 个指定路径加入暂存区。"
                        : $"已将 {paths.Count} 个指定路径从暂存区取消暂存；工作区内容未被还原。";
                    break;
                }
                case "commit":
                {
                    var message = String(json, "message");
                    if (message is null || string.IsNullOrWhiteSpace(message)) return Outcome("`message` 必须是非空提交消息。");
                    if (Encoding.UTF8.GetByteCount(message) > MaxMessageBytes) return Outcome($"`message` 超过 {MaxMessageBytes} 字节上限。");
                    if (message.Any(char.IsControl) && message.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
                        return Outcome("`message` 不能包含控制字符（换行和制表符除外）。");
                    args.AddRange(["commit", "--file", "-"]);
                    summary = "已创建本地提交。";
                    break;
                }
                default:
                {
                    var name = String(json, "name");
                    if (name is null || !SafeRefName(name)) return Outcome("`name` 不是安全的 Git 分支或标签名称。");
                    var revision = String(json, "revision") ?? "HEAD";
                    if (!SafeRevision(revision)) return Outcome("`revision` 格式不安全。");
                    args.AddRange(operation == "branch_create"
                        ? ["branch", "--", name, revision]
                        : ["tag", "--", name, revision]);
                    summary = operation == "branch_create"
                        ? $"已创建本地分支 `{name}`，起点为 `{revision}`。"
                        : $"已创建本地轻量标签 `{name}`，指向 `{revision}`。";
                    break;
                }
            }

            var start = CreateStartInfo(_gitExecutable, context, args);
        if (operation == "commit") start.RedirectStandardInput = true;
        using var process = new Process { StartInfo = start };
            try
            {
                if (!process.Start()) return Outcome("无法启动 Git；请确认已安装并在 PATH 中。");
            }
            catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
            {
                return Outcome("无法启动 Git；请确认已安装并在 PATH 中。");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            try
            {
                Task? inputTask = null;
                if (operation == "commit")
                {
                    var message = String(json, "message")!;
                    inputTask = WriteInputAsync(process, message, timeout.Token);
                }
                var stdoutTask = ReadBoundedAsync(process.StandardOutput, MaxOutputBytes, timeout.Token);
                var stderrTask = ReadBoundedAsync(process.StandardError, 4096, timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                if (inputTask is not null) await inputTask;
                var stdout = await stdoutTask;
                var stderr = await stderrTask;
                if (process.ExitCode != 0)
                {
                    var error = stderr.Text.Trim();
                    return Outcome(error.Length == 0
                        ? $"git {operation} 失败（退出码 {process.ExitCode}）。"
                        : $"git {operation} 失败（退出码 {process.ExitCode}）：\n{error}");
                }
                var output = new StringBuilder(summary);
                if (!string.IsNullOrWhiteSpace(stdout.Text)) output.AppendLine().Append(stdout.Text.TrimEnd());
                if (stdout.Truncated)
                    output.AppendLine().Append($"[输出超过单次保存上限 {ToolLimits.MaxToolOutputBytes} 字节，余下部分未采集。]");
                if (output.Length > ToolLimits.MaxToolOutputBytes) output.Length = ToolLimits.MaxToolOutputBytes;
                return Outcome(output.ToString());
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                KillTree(process);
                return Outcome($"git {operation} 超过 {Timeout.TotalSeconds:F0} 秒，已终止；请检查仓库状态后再决定是否重试。");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                KillTree(process);
                throw;
            }
            catch (IOException ex)
            {
                KillTree(process);
                return Outcome($"git {operation} 执行或读取输出失败：{ex.Message}；请检查仓库状态后再决定是否重试。");
            }
            finally
            {
                if (!process.HasExited) KillTree(process);
            }
        }
    }

    private static ProcessStartInfo CreateStartInfo(string gitExecutable, IToolContext context, IEnumerable<string> args)
    {
        var start = new ProcessStartInfo
        {
            FileName = gitExecutable,
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
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["GIT_LITERAL_PATHSPECS"] = "1";
        start.Environment["LC_ALL"] = "C";
        start.Environment["LANG"] = "C";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        start.Environment["GIT_ATTR_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_COUNT"] = "3";
        start.Environment["GIT_CONFIG_KEY_0"] = "core.fsmonitor";
        start.Environment["GIT_CONFIG_VALUE_0"] = "false";
        start.Environment["GIT_CONFIG_KEY_1"] = "diff.external";
        start.Environment["GIT_CONFIG_VALUE_1"] = "";
        start.Environment["GIT_CONFIG_KEY_2"] = "core.hooksPath";
        start.Environment["GIT_CONFIG_VALUE_2"] = Path.Combine(Path.GetTempPath(), "lilara-no-hooks");
        return start;
    }

    private static async Task WriteInputAsync(Process process, string message, CancellationToken ct)
    {
        await process.StandardInput.WriteAsync(message.AsMemory(), ct);
        await process.StandardInput.FlushAsync(ct);
        process.StandardInput.Close();
    }

    private static string? ResolveRepoPath(string? path, IToolContext context)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)
            || System.Text.RegularExpressions.Regex.IsMatch(path, @"^[A-Za-z]:[\\/]", System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            || path.StartsWith("\\\\", StringComparison.Ordinal)) return null;
        var full = context.ResolvePath(path);
        if (full is null) return null;
        var relative = Path.GetRelativePath(context.WorkingDirectory, full).Replace('\\', '/');
        return relative == "." || relative.StartsWith("../", StringComparison.Ordinal) ? null : relative;
    }

    private static bool SafeRevision(string value) => value.Length is > 0 and <= 256
        && value[0] != '-'
        && (value.All(Uri.IsHexDigit) && value.Length is >= 4 and <= 64
            || value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '/' or '-')
            || System.Text.RegularExpressions.Regex.IsMatch(value, @"^(HEAD|[A-Za-z0-9_./-]+)([~^][0-9]*)+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant));

    private static bool SafeRefName(string value)
    {
        if (value.Length is 0 or > 200 || value[0] == '-' || value[0] == '.' || value[^1] is '.' or '/') return false;
        if (value.Contains("..", StringComparison.Ordinal) || value.Contains("@{", StringComparison.Ordinal)
            || value.Contains("//", StringComparison.Ordinal) || value.Contains('\\')
            || value.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) return false;
        return value.Split('/').All(segment => segment.Length > 0 && segment[0] != '.' && segment.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'));
    }

    private static string? String(JsonElement json, string key) =>
        json.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool TryParse(string json, out JsonDocument? document, out string? error)
    {
        try { document = JsonDocument.Parse(json); error = null; return true; }
        catch (JsonException) { document = null; error = "Git 工具参数必须是合法 JSON 对象。"; return false; }
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
            var encoded = Encoding.UTF8.GetBytes(chunk);
            var remaining = maxBytes - bytes;
            if (remaining <= 0) { truncated = true; continue; }
            if (encoded.Length <= remaining) { output.Append(chunk); bytes += encoded.Length; continue; }
            var chars = Encoding.UTF8.GetCharCount(encoded, 0, remaining);
            output.Append(chunk.AsSpan(0, Math.Min(chars, chunk.Length)));
            bytes = maxBytes;
            truncated = true;
        }
    }

    private static void KillTree(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }

    private static ToolOutcome Outcome(string text) => new() { Content = [new Block.TextBlock(text)] };
}
