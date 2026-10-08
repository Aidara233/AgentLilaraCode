using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lilara.Core.Kernel;

namespace Lilara.Core.Session.Tools;

/// <summary>
/// 参数解析：把模型给的 <c>ArgumentsJson</c> 原文取出一个**字符串**字段。
///
/// <para>⚠️ <b>它是宽松的，而这是刻意的</b>：模型是**不可信输入源**，随时可能
/// 少个键、给个数字、或干脆给一段不是 JSON 的东西。那些都必须变成**一条说人话的结果**，
/// 而不是一个异常——因为异常会让 `tool_use` / `tool_result` 那一对**只落一半**，
/// 而半个在拿到的不是「少点内容」，是上游 400（`01` §3.6）。</para>
///
/// <para>⚠️ <b>它只读、不重写参数</b>：解析出来的是**值**，而记录里存的仍是原文
/// （逐字那条铁律的地方在记录层，不在执行层）。</para>
/// </summary>
internal static class ToolArgs
{
    /// <summary>取一个字符串字段。缺失 / 不是字符串 / 整段不是 JSON ⇒ <c>null</c>。</summary>
    public static string? String(string argumentsJson, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 取一个**整数**字段。缺失 / 不是整数 / 整段不是 JSON ⇒ <c>null</c>。
    ///
    /// <para>⚠️ <b>「不宽容」是判据，不是洁癖</b>：<c>"42"</c>（字符串）与 <c>1.5</c>（小数）
    /// 都给 <c>null</c>——偏移不是那种量。替模型「帮忙解析」会让一个写错的参数变成
    /// 一次**看着正常、实际错位**的读取，而**它不会报错**。
    /// 宽严同 <see cref="String"/>：只认它说的话，不猜。</para>
    ///
    /// <para>⚠️ 走 <c>TryGetInt64</c> 而**不是** <c>GetDouble</c> 再截断：
    /// 后者会把 <c>1.5</c> 变成 <c>1</c>（静默），而 64 位整数也不都表示得成 <c>double</c>。</para>
    ///
    /// <para>⚠️ <b>这里<b>不</b>判「负数算不算合法」</b>——那是调用方的事（它知道语义：
    /// 偏移取负与非负是两种不同的处理）。本方法只负责「读出它写的那个数」。</para>
    /// </summary>
    public static long? Number(string argumentsJson, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
                   && v.TryGetInt64(out var n)
                ? n
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// 一句话的结果。⚠️ <b>失败也走这里</b>——失败是**内容**，不是异常
/// （理由见 <see cref="ToolOutcome"/> 的注释）。所以本文件里没有「错误」这条独立的路。
/// </summary>
internal static class ToolText
{
    public static ToolOutcome Of(string text) => new() { Content = [new Block.TextBlock(text)] };
}

/// <summary>
/// `ls`——「这**周围**有什么」。与 <see cref="ReadTool"/>（「这**一个**是什么」）是
/// 两个问题，所以是两个工具（`01` §8.0.2 第六节那张「取回 / 重调」表同形）。
///
/// <para>⚠️ <b>它们分开，一半理由是「循环」</b>：`read` 调两次互相独立，证明不了
/// agent 循环接通了；而 `ls` → `read` 逼出「第二次调用**依赖**第一次的结果」——那才是循环。</para>
/// </summary>
public sealed class ListTool : ITool
{
    public string Name => "ls";

    public string Description =>
        "列出一个目录里的条目。路径相对于工作目录；不写就是工作目录本身。"
        + "目录名带尾随斜杠。想看某个文件的内容，用 read。";

    public string ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "相对于工作目录的目录路径。省略 = 工作目录本身。"
            }
          },
          "required": []
        }
        """;

    public Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct)
    {
        // 省略 path 与给空串等价——模型两种都会发。
        var rel = ToolArgs.String(call.ArgumentsJson, "path");
        if (string.IsNullOrWhiteSpace(rel)) rel = ".";

        var full = context.ResolvePath(rel);
        if (full is null)
        {
            return Task.FromResult(ToolText.Of(
                $"路径 '{rel}' 在工作目录之外，拒绝读取。工作目录是 {context.WorkingDirectory}。"));
        }

        if (!Directory.Exists(full))
        {
            // 文件也要认得出来——「你以为它是目录」是最常见的误用，
            // 而只说「找不到」会把模型引向「路径写错了」，它其实要的是 read。
            var hint = File.Exists(full) ? "那是一个文件，用 read 读它。" : "它不存在。";
            return Task.FromResult(ToolText.Of($"'{rel}' 不是一个目录——{hint}"));
        }

        // ⚠️ **排序必须显式**：`Directory.EnumerateFileSystemEntries` 的顺序**由文件系统决定**，
        // 不由我们决定（`SessionStore.ListProjects` 那条同一个教训）。工具输出进上下文，
        // 顺序漂一下就是**每一步都可能不同的前缀**。
        var entries = Directory.EnumerateFileSystemEntries(full)
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        if (entries.Count == 0) return Task.FromResult(ToolText.Of("(空目录)"));

        var sb = new StringBuilder();
        foreach (var name in entries)
        {
            // ⚠️ 尾随斜杠是**目录的唯一标记**：文件名与目录名可以同名，
            // 不给标记模型分不出来——而它下一步该用 read 还是 ls 正是靠这个。
            var isDir = Directory.Exists(Path.Combine(full, name));
            sb.Append(isDir ? name + "/" : name).Append('\n');
        }

        // ⚠️ **没有指纹，这是判定的结果不是遗漏**：目录清单随任何一个文件的增删就变，
        // 无「源」可依赖 ⇒ 它是 `00` §5 的第三类（不可重演），无指纹、永不可优化。
        return Task.FromResult(ToolText.Of(sb.ToString()));
    }
}

/// <summary>
/// `read`——「这**一个**是什么」。**本批唯一真正要证的那条契约**。
///
/// <para>⚠️ <b>它落盘的东西与 `00` §5 三件之间的对应，是这批最容易搞错的地方</b>：</para>
/// <list type="bullet">
///   <item><b>快照</b>（渲染产物）⇒ <see cref="ToolOutcome.Content"/>。⚠️ 对 `read` 来说
///   它今天**恰好**是文件那串字节，但契约上它是「我们喂给模型的那串字节」——别在别处写
///   「快照 = 文件原文」（`00` §5 写得很死）。</item>
///   <item><b>引用</b>（依赖什么）⇒ 相对路径。⚠️ **存相对的**：判据（「还是不是同一个东西」）
///   在**这个会话的工作目录**下成立；工作目录若变了，那条历史本来就该按陈旧处理。</item>
///   <item><b>指纹</b>（当时是什么样）⇒ 见 <see cref="Fingerprint"/>。</item>
/// </list>
/// </summary>
public sealed class ReadTool : ITool
{
    public string Name => "read";

    public string Description =>
        "读一个文本文件，返回全文。路径相对于工作目录。"
        + "只想看目录里有什么，用 ls。";

    public string ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "相对于工作目录的文件路径。"
            }
          },
          "required": ["path"]
        }
        """;

    public Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct)
    {
        var rel = ToolArgs.String(call.ArgumentsJson, "path");
        if (string.IsNullOrWhiteSpace(rel))
        {
            return Task.FromResult(ToolText.Of("缺少 `path` 参数。要读哪个文件？"));
        }

        var full = context.ResolvePath(rel);
        if (full is null)
        {
            return Task.FromResult(ToolText.Of(
                $"路径 '{rel}' 在工作目录之外，拒绝读取。工作目录是 {context.WorkingDirectory}。"));
        }

        if (Directory.Exists(full))
        {
            return Task.FromResult(ToolText.Of($"'{rel}' 是一个目录，不是文件——用 ls 列它。"));
        }

        if (!File.Exists(full)) return Task.FromResult(ToolText.Of($"'{rel}' 不存在。"));

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(ToolText.Of($"读 '{rel}' 失败：{ex.Message}"));
        }

        // ⚠️ **坏编码不是错误，是内容**：一个 GBK 源文件不该让整步失败，更不该让
        // `tool_use` / `tool_result` 只落一半。替换字符至少是**看得见的**失真；
        // 静默丢掉整个文件是看不见的。
        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false)
            .GetString(bytes);

        return Task.FromResult(new ToolOutcome
        {
            Content = [new Block.TextBlock(text)],
            // ⚠️ 分隔符统一成正斜杠：否则同一份历史在 Windows 与别处写出两种「引用」。
            DependsOn = rel.Replace('\\', '/'),
            Fingerprint = FingerprintOf(rel, bytes),
        });
    }

    /// <summary>
    /// 当时的指纹。
    ///
    /// <para>⚠️ <b>它把「路径」也哈希进去，这不是随手加的字段</b>：
    /// 只哈希内容的话，「把 <c>a.txt</c> 删掉、再把另一个文件改名成 <c>a.txt</c>」
    /// **换不出同一个指纹**——于是三态判定会说「世界没动」并沿用旧快照，
    /// 而那个旧快照记录的是**另一个文件**的语义。带路径把这个著名的一致性洞堵住。</para>
    ///
    /// <para>⚠️ <b>长度写在内容前面，也是为了堵洞</b>：不写长度的话，
    /// 「<c>ab</c> + 分隔符 + <c>c</c>」与「<c>a</c> + 分隔符 + <c>bc</c>」拼出来的字节相同
    /// ⇒ 两种不同的（路径, 内容）组合撞成同一个指纹。</para>
    /// </summary>
    private static string FingerprintOf(string rel, byte[] bytes)
    {
        var meta = Encoding.UTF8.GetBytes($"{bytes.Length}\n{rel}\n");
        var buf = new byte[meta.Length + bytes.Length];
        meta.CopyTo(buf, 0);
        bytes.CopyTo(buf, meta.Length);

        return Convert.ToHexStringLower(SHA256.HashData(buf));
    }
}
