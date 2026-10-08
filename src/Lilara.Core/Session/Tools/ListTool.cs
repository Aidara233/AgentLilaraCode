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

    /// <summary>只读：列目录不改任何东西（`01` §3.5）。</summary>
    public PermissionClass PermissionClass => PermissionClass.ReadOnly;


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
        //
        // ⚠️ **用 `EnumerateFileSystemInfos` 而不是 `EnumerateFileSystemEntries` + 逐个
        // `Directory.Exists`**：后者**每个条目一次系统调用**（大目录是 O(n) 次 IO），
        // 而 `FileSystemInfo` 已经把类型/大小/时间都带回来了。
        var entries = new DirectoryInfo(full)
            .EnumerateFileSystemInfos()
            .OrderBy(i => i.Name, StringComparer.Ordinal)
            .ToList();

        if (entries.Count == 0) return Task.FromResult(ToolText.Of("(空目录)"));

        // ⚠️ **截断在这里，而且它从 `ToolLimits` 读**（本仓的 G17：上限只能有一个源）。
        // ⚠️ **必须明说截了**：静默截断会让模型以为目录只有这些条目——
        // 那是「多了一条假信息」（`01` §3.6 ①之二 / §9.3）。而且**总数也要给**，
        // 否则模型无法判断"还差多少"。
        var shown = entries.Count > ToolLimits.MaxDirectoryEntries
            ? entries.Take(ToolLimits.MaxDirectoryEntries).ToList()
            : entries;

        var sb = new StringBuilder();
        foreach (var info in shown)
        {
            // ⚠️ 尾随斜杠是**目录的唯一标记**：文件名与目录名可以同名，
            // 不给标记模型分不出来——而它下一步该用 read 还是 ls 正是靠这个。
            var isDir = info is DirectoryInfo;
            // ⚠️ **tab 分列，不是空格对齐**：文件名**可以含空格**，用空格分列会让
            // 带空格的名字不可解析（同 §3.7 那条「框架与内容不许共用字面量」）。
            // ⚠️ 目录的"大小"那一格**留空**：目录没有"大小"这件事，给 0 是在**撒谎**。
            sb.Append(isDir ? info.Name + "/" : info.Name)
              .Append('\t')
              .Append(isDir ? "" : Size(info))
              .Append('\n');
        }

        if (shown.Count < entries.Count)
        {
            sb.Append($"（只列了前 {shown.Count} 条，这个目录一共 {entries.Count} 条）").Append('\n');
        }

        // ⚠️ **没有指纹，这是判定的结果不是遗漏**：目录清单随任何一个文件的增删就变，
        // 无「源」可依赖 ⇒ 它是 `00` §5 的第三类（不可重演），无指纹、永不可优化。
        return Task.FromResult(ToolText.Of(sb.ToString()));
    }

    /// <summary>
    /// 大小 → **给人/模型读的短写法**（`1023 B` / `1.5 KB` / `20.0 MB`）。
    ///
    /// <para>⚠️ <b>它是给人看的，所以按常用的 1024 进制，一位小数</b>——
    /// `20971520` 这种原样抖出来对模型**没有帮助**（它要判的是"这个该不该 read"，
    /// 而 `20.0 MB` 一眼就能判）。</para>
    ///
    /// <para>⚠️ <b>不列清单（"B/KB/MB/GB/TB" 那种表），改用两个数字</b>——
    /// 表会腐，而这里只需要跨三个量级。</para>
    /// </summary>
    private static string Size(FileSystemInfo info)
    {
        long bytes;
        try
        {
            bytes = (info as FileInfo)?.Length ?? 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "?";     // 问不出来就说"不知道"，**别编一个 0**（那是撒谎）
        }

        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
            _ => $"{bytes / (1024.0 * 1024 * 1024):F1} GB",
        };
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

    /// <summary>只读：读文件不改任何东西（`01` §3.5）。</summary>
    public PermissionClass PermissionClass => PermissionClass.ReadOnly;


    public string Description =>
        "读一个文本文件。路径相对于工作目录。"
        + "默认返回全文；文件很大时只返回**开头一段**，并在结果第一行写明这一段是整个文件的哪一部分"
        + "——那行的**总长度**告诉你还有多少没看到，接着读就加 `offset`。"
        + "只想看目录里有什么，用 ls。";

    public string ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "相对于工作目录的文件路径。"
            },
            "offset": {
              "type": "integer",
              "description": "从这个字节开始读（0 基）。接着读上一段时，填上一次第一行给出的下一个位置。省略 = 0。"
            },
            "limit": {
              "type": "integer",
              "description": "最多读多少字节（正整数）。省略 = 读到文件尾（但单次仍有上限）。"
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

        // ⚠️ **四个参数错误都是"说人话的结果"，不是异常**（`01` §3.6 ②）：
        // 异常会让 `tool_use` / `tool_result` 那一对**只落一半** ⇒ 上游 400。
        var offset = ToolArgs.Number(call.ArgumentsJson, "offset") ?? 0;
        if (offset < 0) return Task.FromResult(ToolText.Of($"`offset` 不能是负数（给的是 {offset}）。"));

        // ⚠️ **显式给了 limit 但取不出数**（字符串 / 小数 / 给了个非数字）要**说清**，
        // 不能悄悄当成"没给"——那会让一次写错的调用变成一次**看着正常**的读取。
        long? limit = null;
        if (HasKey(call.ArgumentsJson, "limit"))
        {
            limit = ToolArgs.Number(call.ArgumentsJson, "limit");
            if (limit is not { } l || l <= 0)
                return Task.FromResult(ToolText.Of("`limit` 要是正整数（最多读多少字节）。"));
        }

        long length;
        try
        {
            length = new FileInfo(full).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(ToolText.Of($"读 '{rel}' 失败：{ex.Message}"));
        }

        if (offset >= length)
        {
            // ⚠️ **不是空结果**：空结果会让模型以为「那次调用没有输出」，
            // 而它其实只是**读过头了**（同 `fetch` 取不回时那条判据）。
            return Task.FromResult(ToolText.Of(
                $"'{rel}' 一共 {length} 字节，从第 {offset} 字节之后就没有内容了。"));
        }

        // ⚠️ **上限封顶在这里，而且它从 `ToolLimits` 读**（本仓的 G17：
        // 上限只能有一个源。`ls` 里那句「这个文件太大」读的是同一个数）。
        //
        // ⚠️ **两层 `Min` 看着冗余，其实不是**——而且删掉外层的那次变异**差点被放过去**：
        //   · 内层 `limit ?? Max` 管的是**没给 limit** 那一半
        //   · 外层那个 `Min(…, Max)` 管的是**给了 limit** 那一半
        // ⇒ 只留内层时，`{"limit": 786432}` 就**原样读 786432 字节**——
        //   **上限被模型自己的参数绕过去了**，而那正是这个功能存在的全部意义。
        // ⚠️ 判据：`limit` 是**请求**，`MaxResultBytes` 是**约束**。请求可以小于约束，不能大于。
        var count = Math.Min(Math.Min(limit ?? ToolLimits.MaxResultBytes, ToolLimits.MaxResultBytes), length - offset);

        byte[] bytes;
        try
        {
            bytes = ReadRange(full, offset, count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(ToolText.Of($"读 '{rel}' 失败：{ex.Message}"));
        }

        // ⚠️ **切点不许落在多字节字符中间**：偏差只有几个字节，而代价是
        // **看得见的噪声**（首尾各一个 U+FFFD），模型可能把它当成「文件里真有个怪字符」。
        // ⇒ 往前/往后对齐到字符边界（下面的 `Decode`）。
        var text = Decode(bytes, offset > 0, offset + count < length);

        // ⚠️ **范围写进框架**（`01` §3.7）：被截断这件事必须**明说**，
        // 否则模型会把「前一段」当成全文——那是「多了一条假信息」（§9.3）。
        var framing = new ToolFraming(rel.Replace('\\', '/'),
        [
            new ToolFramingField("第", $"{offset}–{offset + count}"),
            new ToolFramingField("共", length.ToString()),
        ]);

        return Task.FromResult(new ToolOutcome
        {
            Content = [new Block.TextBlock(text)],
            Framing = framing,
            // ⚠️ 分隔符统一成正斜杠：否则同一份历史在 Windows 与别处写出两种「引用」。
            DependsOn = rel.Replace('\\', '/'),
            // ⚠️ **指纹仍然算整份文件的**（不是这一段的）：三态判定判的是
            // 「那个源动没动」，而它是一整个文件。只哈希这一段会让"文件后半被改了"
            // 被判成「世界没动」——静默的陈旧。
            Fingerprint = FingerprintOf(rel.Replace('\\', '/'), ReadWholeFile(full)),
        });
    }

    /// <summary>参数里**显式出现了**这个键吗（哪怕值取不出来）。</summary>
    private static bool HasKey(string argumentsJson, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(key, out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 只读 <paramref name="offset"/> 起的 <paramref name="count"/> 个字节。
    ///
    /// <para>⚠️ **不 `ReadAllBytes` 再切**：那样在一个 20 MB 的文件上仍然分配 20 MB
    /// ——上限防的是**上下文**不是内存，但白花的那份没有必要。</para>
    /// </summary>
    private static byte[] ReadRange(string full, long offset, long count)
    {
        using var stream = File.OpenRead(full);
        stream.Seek(offset, SeekOrigin.Begin);

        var buf = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = stream.Read(buf, read, (int)(count - read));
            if (n == 0) break;      // 文件在两次读之间变短了 —— 拿到多少算多少
            read += n;
        }

        return read == buf.Length ? buf : buf[..read];
    }

    private static byte[] ReadWholeFile(string full)
    {
        try
        {
            return File.ReadAllBytes(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// 把一段**可能是半截字符**的字节解成文本，必要时往两侧对齐到字符边界。
    ///
    /// <para>⚠️ <b>对齐的做法是「剥掉首尾不完整的序列」，不是「换成 U+FFFD」</b>：
    /// 替换字符是**看得见的噪声**，模型会以为文件里真有个怪字符。
    /// 而边界处的半个字符**根本不是内容**——它是我们切出来的。</para>
    ///
    /// <para>⚠️ <b>它只处理边界，中间的不完整序列照旧交给解码器**
    /// （替换字符在那儿是**正确**的：那说明文件本身就是坏编码，
    /// 而 `Read_WithInvalidUtf8_ReturnsReplacementCharactersInsteadOfThrowing` 钉着那条）。</para>
    /// </summary>
    private static string Decode(byte[] bytes, bool trimHead, bool trimTail)
    {
        var start = 0;
        var end = bytes.Length;

        if (trimHead)
        {
            // 向前跳到第一个**引导字节**（不是 10xxxxxx 续字节）。
            while (start < end && (bytes[start] & 0xC0) == 0x80) start++;
        }

        if (trimTail)
        {
            // 从尾往前找**最后一个完整序列**的起点：一个引导字节后面跟足它声明的续字节。
            var i = end - 1;
            while (i >= start && (bytes[i] & 0xC0) == 0x80) i--;
            if (i >= start)
            {
                var need = bytes[i] switch
                {
                    >= 0xF0 => 4,
                    >= 0xE0 => 3,
                    >= 0xC0 => 2,
                    _ => 1,
                };
                if (i + need > end) end = i;   // 末尾那个序列不完整 ⇒ 切掉它
            }
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false)
            .GetString(bytes, start, end - start);
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
