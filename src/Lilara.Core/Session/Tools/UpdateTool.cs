using System.Text;
using Lilara.Core.Kernel;
using Lilara.Core.Storage;

namespace Lilara.Core.Session.Tools;

/// <summary>
/// `update`——**改一个文件里的一处**。它是**第一个写类工具**（`PermissionClass.Writing`），
/// 因此也是 [`01` §3.5](`Gate` 那张表)`| 标准(3) × 写入 = 问 |` 那一格的**第一个生产者**。
///
/// <para>⚠️ <b>在它之前，闸门那条链在生产里从来没有产出过</b>：四个内建（`ls` / `read` /
/// `fetch` / `ask`）**全是只读**，而只读在所有档位都自动放行 ⇒ `Gate.Decide` 从没返回过
/// <see cref="Verdict.Ask"/>。测试里的写类替身是唯一碰过那条路的东西。</para>
///
/// <para>⚠️ <b>为什么是「定点改动 old→new」而不是「整份替换 content」</b>：
/// 整份替换要求模型**复述整个文件**，而它漏掉一行是**静默**的——文件坏了而结果看着正常。
/// 定点改动只碰它点名的那一段，而「`old` 找到几处」这件事**当场可判**。</para>
///
/// <para>⚠️ <b>`old` 必须恰好出现一次</b>：0 次 ⇒ 它记错了文件当时的样子（陈旧）；多次 ⇒
/// 改哪一处是**猜**。两种都拒。判据同 <c>read</c> 的四个参数错误：
/// **拒绝要说人话**，而不是挑一个改下去。</para>
///
/// <para>⚠️ <b>它与 <see cref="ReadTool"/> 是同一类东西，不是「核心提供的内容」</b>
/// （`04` §1.3「核心自带零个工具」那句要拆成两半读，见 <see cref="BuiltInTools"/>）：
/// `read` 给的是「模型能看见自己的工作目录」，`update` 给的是「模型能**改**它」——
/// 都是那条机制的入口。真正「干活的能力」（跑命令、装包、连数据库）仍然全在插件那一侧。</para>
/// </summary>
public sealed class UpdateTool : ITool
{
    public string Name => "update";

    /// <summary>
    /// ⚠️ <b>写入，不是高危</b>：它改文件内容，而旧内容**在写之前已经进了库**
    /// （见 <see cref="IToolContext.EnsureStored"/>）⇒ 它是**可逆**的。
    /// ⇒ 它会落在 `standard` 档的**询问线**上（默认档，用户点一次头就能跑），
    /// 而不是 `authorized` 档的高危线上。
    /// </summary>
    public PermissionClass PermissionClass => PermissionClass.Writing;

    public string Description =>
        "改一个文本文件里的一处。给 `old`（要替换掉的原文，必须在这份文件里**只出现一次**）"
        + "和 `new`（换成什么）；`new` 给空串就是删掉那一段。"
        + "改之前会先把旧内容存起来，所以这一步是可逆的——结果里会给出旧版本的地址。"
        + "要看文件内容，先用 read；只想看目录里有什么，用 ls。";

    public string ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "相对于工作目录的文件路径。"
            },
            "old": {
              "type": "string",
              "description": "要被替换掉的原文。必须在这份文件里**恰好出现一次**——出现 0 次或多次都会被拒绝。多给几行上下文能保证唯一。"
            },
            "new": {
              "type": "string",
              "description": "换成什么。给空串 = 删掉 `old` 那一段。"
            }
          },
          "required": ["path", "old", "new"]
        }
        """;

    public Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct)
    {
        var rel = ToolArgs.String(call.ArgumentsJson, "path");
        if (string.IsNullOrWhiteSpace(rel))
            return Task.FromResult(ToolText.Of("缺少 `path` 参数。要改哪个文件？"));

        var old = ToolArgs.String(call.ArgumentsJson, "old");
        if (old is null)
            return Task.FromResult(ToolText.Of("缺少 `old` 参数。要被替换掉的原文是什么？"));

        // ⚠️ **`new` 单独判「键在不在」**：空串是**合法**的（= 删掉那一段），
        // 而 `ToolArgs.String` 对「键缺失」与「值就是空串」给出的是**同一个结果**。
        // ⇒ 用 `HasKey` 把两件事分开，否则「没给 new」会被当成「删掉」——一次静默的删除。
        // （同 `read` 的 `limit` 那条：显式给了但取不出数，要**说清**，不能悄悄当成"没给"。）
        if (!HasKey(call.ArgumentsJson, "new"))
            return Task.FromResult(ToolText.Of("缺少 `new` 参数。换成什么？（给空串表示删掉 `old`。）"));

        var @new = ToolArgs.String(call.ArgumentsJson, "new") ?? "";

        if (old.Length == 0)
        {
            return Task.FromResult(ToolText.Of(
                "`old` 是空的——那样匹配到的是每一处插入点，没法确定改哪里。"
                + "给一段具体的原文（多带几行上下文）。"));
        }

        // ⚠️ **越界闸只有一处**（`ResolvePath`）：它是**只读**的，写盘那只手在下面我们自己身上。
        var full = context.ResolvePath(rel);
        if (full is null)
        {
            return Task.FromResult(ToolText.Of(
                $"路径 '{rel}' 在工作目录之外，拒绝修改。工作目录是 {context.WorkingDirectory}。"));
        }

        if (Directory.Exists(full))
            return Task.FromResult(ToolText.Of($"'{rel}' 是一个目录，不是文件——update 只能改文件。"));

        if (!File.Exists(full)) return Task.FromResult(ToolText.Of($"'{rel}' 不存在。"));

        // ⚠️ **长度先判、再读**：上限防的是「一次全量读进内存 + 一次全量重写」，
        // 所以不能先 `ReadAllText` 再量——那样上限就白设了。
        long length;
        try
        {
            length = new FileInfo(full).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(ToolText.Of($"改 '{rel}' 失败：{ex.Message}"));
        }

        if (length > ToolLimits.MaxUpdateBytes)
        {
            return Task.FromResult(ToolText.Of(
                $"'{rel}' 有 {length} 字节，超过一次能改的上限（{ToolLimits.MaxUpdateBytes} 字节）。"
                + "这个工具会把整份文件读进来再写回去，所以大文件一次改不了。"));
        }

        string original;
        try
        {
            original = File.ReadAllText(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return Task.FromResult(ToolText.Of($"改 '{rel}' 失败：{ex.Message}"));
        }

        // ⚠️ **数出现几次，而不是 `IndexOf` 找到第一个就改**：
        // 「改哪一处」在多处匹配时是**猜**，而猜错的那一刻文件已经坏了。
        // ⇒ 判据是「恰好一次」，两个方向都拒、且**各自说清是哪个方向**。
        var first = original.IndexOf(old, StringComparison.Ordinal);
        if (first < 0)
        {
            return Task.FromResult(ToolText.Of(
                $"'{rel}' 里找不到 `old` 那段原文。它可能已经被改过了——用 read 看一眼现在的样子。"));
        }

        var second = original.IndexOf(old, first + 1, StringComparison.Ordinal);
        if (second >= 0)
        {
            return Task.FromResult(ToolText.Of(
                $"`old` 在 '{rel}' 里出现了多次（至少 2 处，第 2 处从第 {second} 个字符开始），"
                + "没法确定你要改哪一处。多带几行**上下文**再试，让它只出现一次。"));
        }

        var updated = string.Concat(original.AsSpan(0, first), @new, original.AsSpan(first + old.Length));

        // ⚠️ **写之前先把旧内容存进库**（可逆性）：`EnsureStored` 走**内容寻址**，
        // 所以换个文件名、或同一份内容存两次，拿到的是同一条地址——它**不会**把库撑大。
        // ⚠️ 顺序不能反：先写盘再存旧内容的话，中间崩一次就**永久丢了**旧版本
        //（写盘是覆盖，旧字节那时已经没了）。
        var oldSha = context.EnsureStored(Encoding.UTF8.GetBytes(original));

        try
        {
            // ⚠️ **走 `AtomicFile` 而不是 `File.WriteAllText`**：崩在写的中途会留下
            // **半截文件**，而那是这一批里唯一「会毁掉用户数据」的失败形态。
            // 同 `ApprovalStore.Save` / `ProviderStore.Save`。
            AtomicFile.Write(full, updated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(ToolText.Of($"写 '{rel}' 失败：{ex.Message}"));
        }

        // ⚠️ **框架写「改了多少」**（`01` §3.7）：改动量是**这一次调用返回了什么**的事实，
        // 而它不在正文里——正文说的是"改成功了"。同 `read` 把范围写进框架那条。
        var framing = new ToolFraming(rel.Replace('\\', '/'),
        [
            new ToolFramingField("改动", $"+{@new.Length} −{old.Length}"),
            new ToolFramingField("旧版", oldSha[..12]),
        ]);

        return Task.FromResult(new ToolOutcome
        {
            Content =
            [
                new Block.TextBlock(
                    $"已改 '{rel}'：把\n---\n{old}\n---\n换成了\n---\n{@new}\n---\n"
                    + $"改之前的完整内容存在 {oldSha}（要还原就照它写回去）。"),
            ],
            Framing = framing,
            // ⚠️ 分隔符统一成正斜杠，同 `read`：否则同一份历史在 Windows 与别处写出两种指称。
            DependsOn = rel.Replace('\\', '/'),
            // ⚠️ **指纹算的是改完之后的**——三态判定判的是「这个源现在是什么样」，
            // 而记录这一刻的世界就是改完的样子。给改之前的哈希会让下一次判定
            // 把「我刚改完」判成「世界动了」（那是**误报**，方向是安全的那一侧，
            // 但它让每一次 update 都白扔一次快照）。
            Fingerprint = FingerprintOf(rel.Replace('\\', '/'), updated),
        });
    }

    /// <summary>参数里**显式出现了**这个键吗（哪怕值是个空串）。同 `ReadTool.HasKey`。</summary>
    private static bool HasKey(string argumentsJson, string key)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(argumentsJson);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && doc.RootElement.TryGetProperty(key, out _);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 当时的指纹。⚠️ **与 <see cref="ReadTool"/> 那份是同一个形状，而这是刻意的复制**：
    /// 两者算的都是「路径 + 内容」的哈希，且都带着那两条堵洞的规则
    /// （长度写在内容前、路径进哈希）。
    ///
    /// <para>⚠️ <b>为什么不抽成一处共享</b>：抽出去就得选一个住址，而
    /// `read` 那一份住在 <c>ListTool.cs</c>（`ReadTool` 与 `ToolArgs` 同文件），
    /// 那是个**跟它没关系**的地方。相比「多一个 30 行的函数」，
    /// 「让 `update` 的指纹规则与 `read` 的**漂开**」才是真代价——
    /// 而漂开的后果是**同一个文件被两条记录用两个指纹指称**，
    /// 三态判定随即错乱。⇒ 两份实现各自带着一条指向对方的注释，
    /// 改一处时另一处**在 diff 里看得见**。</para>
    /// </summary>
    private static string FingerprintOf(string rel, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var meta = Encoding.UTF8.GetBytes($"{bytes.Length}\n{rel}\n");
        var buf = new byte[meta.Length + bytes.Length];
        meta.CopyTo(buf, 0);
        bytes.CopyTo(buf, meta.Length);

        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(buf));
    }
}
