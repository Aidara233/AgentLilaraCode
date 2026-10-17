using System.Text.Json;
using Lilara.Core.Kernel;

namespace Lilara.Core.Session.Tools;

// ⚠️ **大小写判据只有这一份**：Windows 不敏感、Unix 敏感。`delete` 的 `IsRoot`
// 原先写死 `OrdinalIgnoreCase`，与 `path` 的 `SamePath` 是两份判据（2026-10-17
// 发布检查发现）——两层判据漂开的结果在 Unix 上是「工作目录根」被误判，而那条
// 判据决定要不要拒绝删掉工作目录本身。
internal static class FileOperationPaths
{
    public static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

internal static class FileOperationArgs
{
    public static bool? Boolean(string json, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty(key, out var value)
                   && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>在已存在的父目录中创建一个目录。</summary>
public sealed class MakeDirectoryTool : ITool
{
    public string Name => "mkdir";
    public PermissionClass PermissionClass => PermissionClass.Writing;
    public string Description => "在工作目录内创建一个目录。只创建最后一级，父目录必须已存在；不会覆盖已有文件或目录。";
    public string ParametersSchemaJson => """
        {"type":"object","properties":{"path":{"type":"string","description":"相对于工作目录的新目录路径。"}},"required":["path"]}
        """;

    public Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct)
    {
        var rel = ToolArgs.String(call.ArgumentsJson, "path");
        if (string.IsNullOrWhiteSpace(rel)) return Result("缺少 `path` 参数。要创建哪个目录？");
        var full = context.ResolvePath(rel);
        if (full is null) return Result($"路径 '{rel}' 在工作目录之外，拒绝创建。工作目录是 {context.WorkingDirectory}。");
        if (Directory.Exists(full) || File.Exists(full)) return Result($"'{rel}' 已存在；没有改动。");
        var parent = Path.GetDirectoryName(full);
        if (parent is null || !Directory.Exists(parent)) return Result($"'{rel}' 的父目录不存在；mkdir 只创建一级目录。");

        try
        {
            Directory.CreateDirectory(full);
            return Result($"已创建目录 '{rel}'。", rel);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result($"创建目录 '{rel}' 失败：{ex.Message}");
        }
    }

    internal static Task<ToolOutcome> Result(string text, string? path = null, string toolName = "mkdir") => Task.FromResult(new ToolOutcome
    {
        Content = [new Block.TextBlock(text)],
        Framing = path is null ? null : new ToolFraming(toolName, [new ToolFramingField("路径", path.Replace('\\', '/'))]),
    });
}

/// <summary>删除文件或目录；递归删除必须在参数中显式声明。</summary>
public sealed class DeleteTool : ITool
{
    public string Name => "delete";
    public PermissionClass PermissionClass => PermissionClass.Destructive;
    public string Description => "删除工作目录内的文件或目录。必须显式提供 recursive；目录递归删除不可恢复且可能部分完成。工作目录本身不能删除。";
    public string ParametersSchemaJson => """
        {"type":"object","properties":{"path":{"type":"string","description":"相对于工作目录的目标路径。"},"recursive":{"type":"boolean","description":"必填。true 表示递归删除目录树；false 仅删除空目录。"}},"required":["path","recursive"]}
        """;

    public Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct)
    {
        var rel = ToolArgs.String(call.ArgumentsJson, "path");
        if (string.IsNullOrWhiteSpace(rel)) return MakeDirectoryTool.Result("缺少 `path` 参数。要删除什么？");
        if (!ToolPaths.HasKey(call.ArgumentsJson, "recursive")) return MakeDirectoryTool.Result("缺少 `recursive` 参数；必须明确选择 true 或 false。");
        var recursive = FileOperationArgs.Boolean(call.ArgumentsJson, "recursive");
        if (recursive is null) return MakeDirectoryTool.Result("`recursive` 必须是 JSON 布尔值 true 或 false。");

        var full = context.ResolvePath(rel);
        if (full is null) return MakeDirectoryTool.Result($"路径 '{rel}' 在工作目录之外，拒绝删除。工作目录是 {context.WorkingDirectory}。");
        if (IsRoot(full, context.WorkingDirectory)) return MakeDirectoryTool.Result("不能删除会话工作目录本身。");

        var isDirectory = Directory.Exists(full);
        if (!isDirectory && !File.Exists(full)) return MakeDirectoryTool.Result($"'{rel}' 不存在；没有删除任何内容。");

        try
        {
            if (isDirectory) Directory.Delete(full, recursive.Value);
            else File.Delete(full);
            return Task.FromResult(new ToolOutcome
            {
                Content = [new Block.TextBlock($"已删除{(isDirectory ? "目录" : "文件")} '{rel}'（recursive={recursive.Value.ToString().ToLowerInvariant()}）。")],
                Framing = new ToolFraming("delete", [new ToolFramingField(isDirectory ? "目录" : "文件", rel.Replace('\\', '/')), new ToolFramingField("递归", recursive.Value.ToString().ToLowerInvariant())]),
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return MakeDirectoryTool.Result($"删除 '{rel}' 失败：{ex.Message}。递归删除可能已部分完成；请检查目录状态后再决定是否重试。");
        }
    }

    private static bool IsRoot(string full, string root) =>
        full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Equals(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), FileOperationPaths.Comparison);
}

/// <summary>移动、复制或重命名一个文件；不覆盖目标。</summary>
public sealed class PathTool : ITool
{
    public string Name => "path";
    public PermissionClass PermissionClass => PermissionClass.Writing;
    public string Description => "对工作目录内的单个文件执行 move、copy 或 rename。目录操作不支持；目标必须不存在，父目录必须已存在。rename 只能在同一目录内。";
    public string ParametersSchemaJson => """
        {"type":"object","properties":{"operation":{"type":"string","enum":["move","copy","rename"]},"source":{"type":"string"},"destination":{"type":"string"}},"required":["operation","source","destination"]}
        """;

    public Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct)
    {
        var operation = ToolArgs.String(call.ArgumentsJson, "operation");
        var sourceRel = ToolArgs.String(call.ArgumentsJson, "source");
        var destinationRel = ToolArgs.String(call.ArgumentsJson, "destination");
        if (operation is not ("move" or "copy" or "rename")) return MakeDirectoryTool.Result("`operation` 必须是 move、copy 或 rename。");
        if (string.IsNullOrWhiteSpace(sourceRel) || string.IsNullOrWhiteSpace(destinationRel)) return MakeDirectoryTool.Result("`source` 和 `destination` 都必须是非空路径。");

        var source = context.ResolvePath(sourceRel);
        var destination = context.ResolvePath(destinationRel);
        if (source is null || destination is null) return MakeDirectoryTool.Result("源和目标都必须位于工作目录内。");
        if (IsRoot(source, context.WorkingDirectory) || IsRoot(destination, context.WorkingDirectory)) return MakeDirectoryTool.Result("不能移动、复制或重命名工作目录本身。");
        if (Directory.Exists(source) || Directory.Exists(destination)) return MakeDirectoryTool.Result("`path` 首版只支持文件，不支持目录。");
        if (!File.Exists(source)) return MakeDirectoryTool.Result($"源文件 '{sourceRel}' 不存在。");
        if (SamePath(source, destination)) return MakeDirectoryTool.Result("源和目标是同一路径；没有改动。");
        if (File.Exists(destination) || Directory.Exists(destination)) return MakeDirectoryTool.Result($"目标 '{destinationRel}' 已存在；不会覆盖。");
        var parent = Path.GetDirectoryName(destination);
        if (parent is null || !Directory.Exists(parent)) return MakeDirectoryTool.Result("目标父目录不存在；不会自动创建目录。");
        if (operation == "rename" && !SamePath(Path.GetDirectoryName(source)!, parent)) return MakeDirectoryTool.Result("rename 只能在同一父目录内使用；跨目录请用 move。");
        if (operation == "move" && !SamePath(Path.GetPathRoot(source)!, Path.GetPathRoot(destination)!)) return MakeDirectoryTool.Result("拒绝跨卷 move，避免复制后删除源文件导致失败时数据丢失。");

        try
        {
            switch (operation)
            {
                case "copy": File.Copy(source, destination, overwrite: false); break;
                case "move":
                case "rename": File.Move(source, destination, overwrite: false); break;
            }
            return Task.FromResult(new ToolOutcome
            {
                Content = [new Block.TextBlock($"已{(operation == "copy" ? "复制" : operation == "move" ? "移动" : "重命名")}文件 '{sourceRel}' → '{destinationRel}'。")],
                Framing = new ToolFraming("path", [new ToolFramingField(operation, $"{sourceRel.Replace('\\', '/')} → {destinationRel.Replace('\\', '/')}")]),
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return MakeDirectoryTool.Result($"{operation} '{sourceRel}' 到 '{destinationRel}' 失败：{ex.Message}");
        }
    }

    private static bool SamePath(string a, string b) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
        FileOperationPaths.Comparison);

    private static bool IsRoot(string full, string root) => SamePath(full, root);
}
