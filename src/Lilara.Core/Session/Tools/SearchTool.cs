using System.Text;
using Lilara.Core.Kernel;

namespace Lilara.Core.Session.Tools;

/// <summary>
/// `search`——「这**一片**里哪里有它」。与 <see cref="ListTool"/>（周围有什么）和
/// <see cref="ReadTool"/>（这一个是什么）是三个问题。
///
/// <para>⚠️ <b>一个工具、两种模式</b>：<c>content</c> 搜文件内容（默认），
/// <c>filename</c> 搜文件 / 目录名。合在一张声明里，是因为二者**共享同一个范围闸与遍历**
/// ——拆成两个工具会让「跳过哪些目录」「路径怎么解析」各写一遍，而那两份一定会漂。
/// 参数 <c>mode</c> 是**声明**的一部分，模型从工具描述里读到它，不需要另学一个名字。</para>
///
/// <para>⚠️ <b>结果里必须能看见「截断了」</b>：命中条数与遍历条目数都有上限
/// （<see cref="ToolLimits.MaxSearchResults"/> / <see cref="ToolLimits.MaxSearchEntries"/>），
/// 而静默截断会让模型以为「只有这些」——那是「多了一条假信息」（`01` §9.3）。</para>
/// </summary>
public sealed class SearchTool : ITool
{
    public string Name => "search";

    /// <summary>只读：搜索不改任何东西（`01` §3.5）。</summary>
    public PermissionClass PermissionClass => PermissionClass.ReadOnly;

    public string Description =>
        "在工作目录里按文本搜索。默认搜文件**内容**（不区分大小写的子串，结果形如 `路径:行号: 那一行`）；"
        + "`mode` 给 `filename` 时改成搜**文件 / 目录名**（目录带尾随斜杠）。"
        + "`path` 可以是目录（递归）或单个文件；省略 = 工作目录。"
        + "默认跳过 .git / .hg / .svn / node_modules / bin / obj 这些目录，也不搜二进制文件和超大文件。"
        + "要看某个已知文件的内容，用 read；要看一层目录里有什么，用 ls。";

    public string ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "pattern": {
              "type": "string",
              "description": "要搜索的子串，不区分大小写。不能为空。"
            },
            "path": {
              "type": "string",
              "description": "相对于工作目录的起点：目录（递归搜索）或单个文件。省略 = 工作目录。"
            },
            "mode": {
              "type": "string",
              "enum": ["content", "filename"],
              "description": "content = 搜文件内容（默认）；filename = 搜文件 / 目录名。"
            }
          },
          "required": ["pattern"]
        }
        """;

    /// <summary>
    /// 递归时整棵跳过的目录名。⚠️ 判据是「里面不是**这个会话要读的**东西」：
    /// 版本库内部、依赖树、构建产物——把它们算进来，一次搜索就能把上下文撑满，
    /// 而命中几乎全是噪声。
    /// ⚠️ 只对**子目录**生效：把 `path` 直接指向 `bin` 时仍然会搜它（那是模型明确的请求）。
    /// </summary>
    private static readonly string[] SkippedDirectories =
        [".git", ".hg", ".svn", "node_modules", "bin", "obj"];

    public Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct)
    {
        // 模型是**不可信输入源**：四种用错都必须是说人话的结果，不是异常
        // （异常会让 tool_use / tool_result 只落一半，见 `01` §3.6）。
        var pattern = ToolArgs.String(call.ArgumentsJson, "pattern");
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return Task.FromResult(ToolText.Of("缺少 `pattern` 参数。要搜什么？"));
        }

        var rel = ToolArgs.String(call.ArgumentsJson, "path");
        if (string.IsNullOrWhiteSpace(rel)) rel = ".";

        var mode = ToolArgs.String(call.ArgumentsJson, "mode");
        if (string.IsNullOrWhiteSpace(mode)) mode = "content";
        var byName = string.Equals(mode, "filename", StringComparison.OrdinalIgnoreCase);
        if (!byName && !string.Equals(mode, "content", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(ToolText.Of("`mode` 只能是 `content` 或 `filename`。"));
        }

        var full = context.ResolvePath(rel);
        if (full is null)
        {
            return Task.FromResult(ToolText.Of(
                $"路径 '{rel}' 在工作目录之外，拒绝搜索。工作目录是 {context.WorkingDirectory}。"));
        }

        if (!Directory.Exists(full) && !File.Exists(full))
        {
            return Task.FromResult(ToolText.Of($"'{rel}' 不存在。"));
        }

        return Task.FromResult(byName
            ? SearchNames(pattern, full, context)
            : SearchContent(pattern, full, context));
    }

    // ---------------- filename ----------------

    private static ToolOutcome SearchNames(string pattern, string full, IToolContext context)
    {
        var hits = new List<string>();
        var capped = false;
        var scanned = false;

        if (File.Exists(full))
        {
            if (Path.GetFileName(full).Contains(pattern, StringComparison.OrdinalIgnoreCase))
                hits.Add(Display(full, context));
        }
        else
        {
            var walked = Walk(full, includeDirectories: true);
            scanned = walked.Capped;
            foreach (var dir in walked.Directories)
            {
                if (dir.Length > 0 && Path.GetFileName(dir).Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    hits.Add(Display(dir, context) + "/");
            }
            foreach (var file in walked.Files)
            {
                if (Path.GetFileName(file).Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    hits.Add(Display(file, context));
            }
            // 遍历顺序是确定的（每层按名字序数排序），但文件与目录混在两条表里；
            // 排一次让输出只取决于「有什么」，不取决于「怎么走到的」。
            hits.Sort(StringComparer.Ordinal);
        }

        if (hits.Count > ToolLimits.MaxSearchResults)
        {
            hits.RemoveRange(ToolLimits.MaxSearchResults, hits.Count - ToolLimits.MaxSearchResults);
            capped = true;
        }

        var sb = new StringBuilder();
        if (hits.Count == 0)
        {
            sb.Append($"没有找到名字包含 \"{pattern}\" 的文件或目录。");
        }
        else
        {
            sb.Append(string.Join('\n', hits));
        }

        if (capped) AppendResultCap(sb);
        if (scanned) AppendScanCap(sb);

        return ToolText.Of(sb.ToString());
    }

    // ---------------- content ----------------

    private static ToolOutcome SearchContent(string pattern, string full, IToolContext context)
    {
        var files = new List<string>();
        var scanCapped = false;

        if (File.Exists(full))
        {
            files.Add(full);
        }
        else
        {
            var walked = Walk(full, includeDirectories: false);
            files.AddRange(walked.Files);
            scanCapped = walked.Capped;
        }

        var hits = new List<string>();
        var binaries = 0;
        var tooBig = 0;
        var unreadable = 0;
        var capped = false;

        foreach (var file in files)
        {
            long size;
            try
            {
                size = new FileInfo(file).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unreadable++;
                continue;
            }

            if (size > ToolLimits.MaxSearchFileBytes)
            {
                tooBig++;
                continue;
            }

            // ⚠️ 二进制判据是**内容里有 NUL**，不是扩展名：扩展名清单会腐，
            // 而「读出来是一堆替换字符」对模型没用（同 `read` 那条「坏编码不抛」的判据）。
            var binary = LooksBinary(file);
            if (binary is null)
            {
                unreadable++;
                continue;
            }
            if (binary.Value)
            {
                binaries++;
                continue;
            }

            var shown = Display(file, context);
            try
            {
                var lineNo = 0;
                foreach (var line in File.ReadLines(file))
                {
                    lineNo++;
                    if (!line.Contains(pattern, StringComparison.OrdinalIgnoreCase)) continue;

                    hits.Add($"{shown}:{lineNo}: {Clip(line)}");
                    if (hits.Count >= ToolLimits.MaxSearchResults)
                    {
                        capped = true;
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unreadable++;
            }

            if (capped) break;
        }

        var sb = new StringBuilder();
        if (hits.Count == 0)
        {
            sb.Append($"没有找到包含 \"{pattern}\" 的文本。");
        }
        else
        {
            sb.Append(string.Join('\n', hits));
        }

        var skipped = SkippedNote(binaries, tooBig, unreadable);
        if (skipped.Length > 0) sb.Append('\n').Append(skipped);
        if (capped) AppendResultCap(sb);
        if (scanCapped) AppendScanCap(sb);

        return ToolText.Of(sb.ToString());
    }

    // ---------------- shared ----------------

    private sealed class Walked
    {
        public List<string> Files { get; } = [];
        public List<string> Directories { get; } = [];

        /// <summary>遍历在到达上限时提前停了——这件事**必须**传出去（见类注释）。</summary>
        public bool Capped { get; set; }
    }

    private static Walked Walk(string root, bool includeDirectories)
    {
        var walked = new Walked();
        WalkDirectory(root, includeDirectories, walked);
        return walked;
    }

    private static void WalkDirectory(string dir, bool includeDirectories, Walked walked)
    {
        if (walked.Capped) return;

        FileSystemInfo[] entries;
        try
        {
            entries = new DirectoryInfo(dir).GetFileSystemInfos();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;     // 这一个目录读不了；别让整次搜索失败
        }

        // ⚠️ 排序必须显式：`GetFileSystemInfos` 的顺序由文件系统决定（同 `ListTool`）。
        Array.Sort(entries, static (a, b) => string.CompareOrdinal(a.Name, b.Name));

        foreach (var entry in entries)
        {
            if (walked.Files.Count + walked.Directories.Count >= ToolLimits.MaxSearchEntries)
            {
                walked.Capped = true;
                return;
            }

            if (entry is DirectoryInfo d)
            {
                if (SkipDirectory(d) || IsReparsePoint(d)) continue;
                if (includeDirectories) walked.Directories.Add(d.FullName);
                WalkDirectory(d.FullName, includeDirectories, walked);
            }
            else
            {
                walked.Files.Add(entry.FullName);
            }
        }
    }

    private static bool SkipDirectory(DirectoryInfo dir) =>
        SkippedDirectories.Contains(dir.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 目录联接 / 符号链接：递归进去可能**绕圈**，也可能走到工作目录之外。
    /// 词法闸（<see cref="IToolContext.ResolvePath"/>）拦不住它们（已知边界），
    /// 这里能做的就是不跟。
    /// </summary>
    private static bool IsReparsePoint(DirectoryInfo dir)
    {
        try
        {
            return (dir.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>内容看起来是不是二进制。读不了 ⇒ <c>null</c>（由调用方记「读不了」，别把它算成二进制）。</summary>
    private static bool? LooksBinary(string full)
    {
        try
        {
            using var stream = File.OpenRead(full);
            var buf = new byte[8192];
            var read = stream.Read(buf, 0, buf.Length);
            return buf.AsSpan(0, read).IndexOf((byte)0) >= 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>统一成正斜杠：同一份历史在 Windows 与别处写出两种路径是漂。</summary>
    private static string Display(string full, IToolContext context) =>
        Path.GetRelativePath(context.WorkingDirectory, full).Replace('\\', '/');

    /// <summary>
    /// 一行只留开头一段。⚠️ 超长行（压缩过的 JS、日志）会把一次结果撑满；
    /// 截断用可见的省略号**明说**，不静默切掉。
    /// </summary>
    private static string Clip(string line)
    {
        var text = line.TrimEnd();
        return text.Length <= ToolLimits.MaxSearchLineChars
            ? text
            : text[..ToolLimits.MaxSearchLineChars] + "…";
    }

    private static string SkippedNote(int binaries, int tooBig, int unreadable)
    {
        var parts = new List<string>(3);
        if (binaries > 0) parts.Add($"{binaries} 个二进制文件");
        if (tooBig > 0) parts.Add($"{tooBig} 个大于 {ToolLimits.MaxSearchFileBytes / (1024 * 1024)} MB 的文件");
        if (unreadable > 0) parts.Add($"{unreadable} 个读不了的文件");
        return parts.Count == 0 ? "" : $"（跳过 {string.Join(" / ", parts)}）";
    }

    private static void AppendResultCap(StringBuilder sb) =>
        sb.Append('\n')
          .Append("（结果已截断：只列出前 ").Append(ToolLimits.MaxSearchResults)
          .Append(" 条匹配。把 path 缩小一些，或换一个更具体的 pattern。）");

    private static void AppendScanCap(StringBuilder sb) =>
        sb.Append('\n')
          .Append("（只遍历了前 ").Append(ToolLimits.MaxSearchEntries)
          .Append(" 个目录项，已达到上限——把 path 指向更窄的目录。）");
}
