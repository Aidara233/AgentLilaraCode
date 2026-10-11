using System.Text;
using Lilara.Core.Kernel;
using Lilara.Core.Storage;

namespace Lilara.Core.Session.Tools;

/// <summary>
/// `write`——**整份写一个文件**（没有就新建，有就覆盖）。
///
/// <para>⚠️ <b>它与 <see cref="UpdateTool"/> 是两个工具，判据是「模型该用哪个」</b>：
/// <c>write</c> 要模型**把整个文件写出来**，而它漏掉一行是**静默**的——文件坏了
/// 而结果看着正常。⇒ 改一个已存在的文件时**用 `update`**（只碰它点名的那一段，
/// 而「`old` 找到几处」当场可判）。
/// <c>write</c> 的正当用途是**整份替换**（重写一遍本来就短的文件、或者模型就是要
/// 从头写一个）与**新建**。</para>
///
/// <para>⚠️ <b>为什么仍然要有它</b>：「新建一个文件」没有别的工具做得成——
/// `update` 要求文件已存在（它的语义锚在「当时的原文」上）。没有 `write` 的话，
/// 模型写一个新文件得先想办法造一个空文件，而那既是弯路、又要求它有 `bash` 之类的工具。</para>
///
/// <para>⚠️ <b>覆盖一个已存在的文件是**破坏性**的</b>（内容被整份替换掉），
/// 所以那一格落进可逆性保护：旧内容**写前先存进库**（`EnsureStored`，内容寻址、幂等），
/// 结果里给出旧版本地址。**新建**没有旧内容可存 ⇒ 结构上证明了「新建没有破坏任何东西」。
/// ⚠️ <b>两格都不比 `update` 更高危</b>：`update` 也覆盖（只是覆盖一小段），
/// 两者都靠同一个机制可逆 ⇒ 都是 <see cref="PermissionClass.Writing"/>，
/// 都落在 `standard` 档的**询问线**上。</para>
///
/// <para>⚠️ <b>它与 <see cref="ReadTool"/> / <see cref="UpdateTool"/> 是同一类东西，
/// 不是「核心提供的内容」</b>（`04` §1.3 那句要拆成两半读，见 <see cref="BuiltInTools"/>）：
/// 三个合起来才是「模型能读写自己的工作目录」这件最小的事。</para>
/// </summary>
public sealed class WriteTool : ITool
{
    public string Name => "write";

    /// <summary>⚠️ 写入（可逆：旧内容写前进库）。理由与 <see cref="UpdateTool.PermissionClass"/> 同源。</summary>
    public PermissionClass PermissionClass => PermissionClass.Writing;

    public string Description =>
        "写一个文本文件：**整份**写进去。文件不存在就新建；已存在就**整个覆盖掉**。"
        + "覆盖之前旧内容会先存起来，所以这一步是可逆的——结果里会给出旧版本的地址。"
        + "⚠️ 只想改已存在文件里的一处时，用 update —— 它不会碰到你没点到的地方，"
        + "而整份写要求你把全文都写对，漏一行是看不出来的。"
        + "要新建目录里的文件没关系，但**父目录必须已经存在**（这个工具不建目录）。";

    public string ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "相对于工作目录的文件路径。父目录必须已存在。"
            },
            "content": {
              "type": "string",
              "description": "文件的完整内容。给空串 = 一个空文件。"
            }
          },
          "required": ["path", "content"]
        }
        """;

    public Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct)
    {
        var rel = ToolArgs.String(call.ArgumentsJson, "path");
        if (string.IsNullOrWhiteSpace(rel))
            return Task.FromResult(ToolText.Of("缺少 `path` 参数。要写哪个文件？"));

        // ⚠️ **`content` 单独判「键在不在」**：空串是**合法**的（= 一个空文件），
        // 而 `ToolArgs.String` 对「键缺失」与「值就是空串」给出同一个结果
        // ⇒ 不分开的话，「忘了给 content」会变成一次**静默的清空**。
        // （同 `update` 的 `new` 那条：两处用的是同一个 `ToolPaths.HasKey`。）
        if (!ToolPaths.HasKey(call.ArgumentsJson, "content"))
            return Task.FromResult(ToolText.Of("缺少 `content` 参数。要写进去什么？（给空串 = 空文件。）"));

        var content = ToolArgs.String(call.ArgumentsJson, "content") ?? "";
        var bytes = Encoding.UTF8.GetBytes(content);

        if (bytes.Length > ToolLimits.MaxUpdateBytes)
        {
            return Task.FromResult(ToolText.Of(
                $"要写的内容有 {bytes.Length} 字节，超过一次能写的上限（{ToolLimits.MaxUpdateBytes} 字节）。"
                + "分几次写：先 write 一个开头，再用 update 接着补。"));
        }

        // ⚠️ **越界闸只有一处**（`ResolvePath`）；写盘那只手在下面我们自己身上。
        var full = context.ResolvePath(rel);
        if (full is null)
        {
            return Task.FromResult(ToolText.Of(
                $"路径 '{rel}' 在工作目录之外，拒绝写入。工作目录是 {context.WorkingDirectory}。"));
        }

        if (Directory.Exists(full))
            return Task.FromResult(ToolText.Of($"'{rel}' 是一个目录，不是文件——write 只能写文件。"));

        var existed = File.Exists(full);

        // ⚠️ **父目录不存在 ⇒ 拒绝，不替你建**（同 `session.create` 不建目录那条：
        // 建目录是策略，`00` §1.3）。⚠️ 判据是**父目录**，不是路径本身——
        // 「在已有目录里新建文件」是正常用法，而 `Directory.CreateDirectory` 连中间层级
        // 一起造出来（一次笔误就长出一棵树），那才是要拒的。
        var parent = Path.GetDirectoryName(full);
        if (parent is not null && !Directory.Exists(parent))
        {
            return Task.FromResult(ToolText.Of(
                $"'{rel}' 的父目录不存在（{Path.GetFileName(parent)}）——这个工具不建目录。"
                + "先确认路径写对了，或者让用户先建。"));

            // ⚠️ 一个我**没有**去堵的洞（如实记着）：父目录可以是**指向工作目录之外**
            // 的符号链接 / 目录联接，而 `ResolvePath` 是**词法**判定、不解析链接目标
            // （`01` 那条已认下的边界）。⇒ 这个工具让那条洞**从只读变成可写**。
            // 今天接受：Windows 上创建符号链接要特权，不是模型随手能做到的；
            // 而真堵它要把 `ResolvePath` 改成解析到最终目标再判一次。
        }

        // ---- 覆盖：写前把旧内容存起来（可逆性） ----
        //
        // ⚠️ **顺序不能反**：先写盘再存旧内容的话，中间崩一次就**永久丢了**旧版本
        //（写盘是覆盖，旧字节那时已经没了）。
        string? oldSha = null;
        if (existed)
        {
            byte[] original;
            try
            {
                original = File.ReadAllBytes(full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Task.FromResult(ToolText.Of($"写 '{rel}' 失败（读旧内容时）：{ex.Message}"));
            }

            oldSha = context.EnsureStored(original);
        }

        try
        {
            // ⚠️ **走 `AtomicFile`**：崩在写的中途会留下**半截文件**，
            // 而那是这条链上唯一「会毁掉用户数据」的失败形态（同 `UpdateTool`）。
            AtomicFile.WriteBytes(full, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(ToolText.Of($"写 '{rel}' 失败：{ex.Message}"));
        }

        // ⚠️ **框架写「新建 / 覆盖」与字节数**（`01` §3.7）：那是这一次调用返回了什么的事实，
        // 而正文只说「写成了」。⚠️ **它比 `update` 那格更要紧**：模型下一步的判断
        // 取决于「我刚才是建了一个文件、还是盖掉了一个」——而两者在正文里长得一样。
        var framing = new ToolFraming(rel.Replace('\\', '/'),
        [
            new ToolFramingField(existed ? "覆盖" : "新建", $"{bytes.Length} B"),
            // ⚠️ 只在**真的覆盖过**时给旧版地址——新建没有旧版，
            // 编一个空哈希会让模型以为「有个版本可以还原」。
            .. oldSha is null ? [] : new[] { new ToolFramingField("旧版", oldSha[..12]) },
        ]);

        return Task.FromResult(new ToolOutcome
        {
            Content =
            [
                new Block.TextBlock(existed
                    ? $"已覆盖 '{rel}'（{bytes.Length} 字节）。写之前的内容存在 {oldSha}（要还原就照它写回去）。"
                    : $"已新建 '{rel}'（{bytes.Length} 字节）。"),
            ],
            Framing = framing,
            // ⚠️ 分隔符统一成正斜杠，同 `read` / `update`。
            DependsOn = rel.Replace('\\', '/'),
            // ⚠️ **指纹算的是写完之后**——同 `update` 那条：给写之前的会让下一次三态判定
            // 把「我刚写完」判成「世界动了」（误报，方向安全但白扔一次快照）。
            Fingerprint = ToolPaths.FingerprintOf(rel.Replace('\\', '/'), bytes),
        });
    }
}
