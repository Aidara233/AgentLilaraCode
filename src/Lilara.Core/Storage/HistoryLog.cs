using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lilara.Core.Kernel;

namespace Lilara.Core.Storage;

// 每步追加的骨架记录——只追加，永不重写（「简化」是加载期解释，不是落盘时改写）。
public static class HistoryLog
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // 每个文件一把锁，防字节交错——与 FileShare.ReadWrite 是两件事，缺一不可：
    // 共享模式解决「句柄冲突」，这把锁解决两个 append 各写一半拼成的坏行。
    // history.jsonl 只追加永不重写，坏行会一直在，只在下次读时才炸。
    private static readonly ConcurrentDictionary<string, Lock> PathLocks = new();

    public static void Append(string path, TimelineEvent evt) => AppendMany(path, [evt]);

    /// <summary>
    /// **一条步的多个事件，一次落盘。** 步是持久化的原子单位（`01` §4），
    /// 而有工具之后一步要落「模型消息（含 `tool_use`）+ N 条结果」——
    /// 分 N 次 <see cref="Append"/> 就等于把原子单位拆成 N 个崩溃窗口。
    ///
    /// <para>⚠️ <b>为什么必须是「一次 `AppendMany`」而不是「循环调 `Append`」</b>：
    /// 后者能拿到同一把锁 <b>N 次</b>，中间任何一次别的写入都能插进来
    /// ⇒ 「这一步的记录是连续的」这句话<b>当场变成假的</b>。真正的原子性从<b>一把锁 + 一次
    /// open/close</b> 来，不是从「调用得够快」来。</para>
    ///
    /// <para>⚠️ <b>它没解决什么，别读成「现在原子了」</b>：一次 `Write` 仍可能被部分落盘
    /// （断电），而 <see cref="ReadAll"/> 只跳<b>语法</b>坏行、管不了<b>语义</b>不完整的步。
    /// ⇒ 尾部悬空仍在可能范围内，只是窗口从「N 次 append」缩到「一次 append 内部」。
    /// 要彻底钉住得靠<b>加载时的结构配对校验</b>（`01` §3.6 ③ 的另一个选项）——</para>
    ///
    /// <para>⚠️ 那个校验今天**不存在**，而它缺失的后果是具体的：一条**悬空的 `tool_use`**
    /// 发回上游就是 400（缺一条结果只是内容少，缺配对是请求非法）。
    /// 见 <c>ToolResultEvent</c> 的注释与 `01` §3.6。</para>
    /// </summary>
    public static void AppendMany(string path, IReadOnlyList<TimelineEvent> events)
    {
        // 空批次是**合法**输入，而不是「调用方写错了」：一步没有工具调用时就只有一条模型消息，
        // 而「拒答且没工具」那种一步**一条事件都没有**（`Stepper` 那里已经这么处理了）。
        // ⇒ 不抛，直接什么都不写——抛的话会逼调用方在每处先判空，那是把这里的形状泄漏出去。
        if (events.Count == 0) return;

        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        // ⚠️ 序列化在**锁外**：抛 JSON 异常时不该占着文件锁，
        // 而且这一步是纯 CPU、没有共享状态。**写入必须在锁内**（下面那一段）。
        var lines = new StringBuilder();
        foreach (var evt in events) lines.Append(JsonSerializer.Serialize(evt, Json.Options)).Append('\n');

        // 不用 File.AppendAllText：它默认 FileShare.Read，第二个 append 开句柄就抛 IOException。
        lock (PathLocks.GetOrAdd(Path.GetFullPath(path), _ => new Lock()))
        {
            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(fs, Utf8NoBom);
            writer.Write(lines.ToString());
        }
    }

    // 逐行读回。坏行跳过、不炸——追加不是原子的，断电可能留下半行，
    // 而 history.jsonl 只追加永不重写，那半行会一直在；完好前缀才是值钱的部分。
    // 只吞 JsonException：权限/磁盘失败必须暴露，静默「历史消失」比崩溃更糟。
    public static IReadOnlyList<TimelineEvent> ReadAll(string path)
    {
        if (!File.Exists(path)) return [];

        var result = new List<TimelineEvent>();

        // 读也必须 FileShare.ReadWrite：默认的 FileShare.Read 会挡住写句柄，与 Append 必须对称。
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs, Encoding.UTF8);

        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                var evt = JsonSerializer.Deserialize<TimelineEvent>(line, Json.Options);
                // 反序列化可能是 null（例如一行字面的 null），不能进列表。
                if (evt is not null) result.Add(evt);
            }
            catch (JsonException)
            {
                // ⚠️ **两种「读不出来」在这里合流，而它们的处置完全不同**（2026-10-06 补）：
                //   ① **断电留下的半行** ⇒ 真该跳过（前缀才是值钱的部分）。
                //   ② **缺 `id` 的老行**（2026-10-06 之前的记录没有这个字段）⇒ **绝不能跳过**。
                // 原先只有 ①，而 `TimelineEvent.Id` 是 `required` ⇒ ② 也走进这个 catch
                // ⇒ **所有 10-06 之前的会话历史读出来是空的**（实测：盘上 3 行、API 交 `[]`）。
                // ⚠️ 当时注释写着「缺 id ⇒ 当场抛，抛是响亮失败」——**那句是错的**：
                // 抛出发生在反序列化，而**这个 catch 的本职就是为断电容错**，于是它被吞了。
                // ⇒ 补救：老行**按缺省值再读一次**（`id` 是唯一必填，别处都已有）——见 `ReadLegacy`。
                if (ReadLegacy(line) is { } legacy) result.Add(legacy);
            }
        }
        return result;
    }

    /// <summary>
    /// 读一条**没有 `id` 的老行**：补一个 id 再反序列化。
    ///
    /// <para>⚠️ <b>补的 id 必须<b>可复现</b>，不能是 `Ids.New()`（随机）或时钟</b>：
    /// 那样同一个文件读两次得到两组 id，而 id 是 `fetch` / 改历史 / 头引用三处共用的指称
    /// （`01` §8.0.2 第七节）⇒ 引用会**静默失效**。这恰恰是 `0b` 给 `Id` 设 `required`
    /// 时想防的那件事——只是当时防错了地方（拦住了读，而不是拦住随手编一个）。
    /// ⇒ 用**行内容**派生：同一行永远得到同一个 id，且不同行几乎不可能撞（12 位 hex）。</para>
    ///
    /// <para>⚠️ <b>不改盘</b>：只追加的文件里，补 id 只发生在**读的时候**。
    /// 要改成盘的话得重写整份（那正是「截断」那条不可逆的路，不在这里）。</para>
    /// </summary>
    private static TimelineEvent? ReadLegacy(string line)
    {
        // 先确认它**确实**只是缺 id：带 id 的行进不来这里（上面已经读成功了）。
        var patched = InsertMissingId(line);

        try
        {
            return JsonSerializer.Deserialize<TimelineEvent>(patched, Json.Options);
        }
        catch (JsonException)
        {
            // 加了 id 还读不出来 ⇒ 它是真的坏行（半行 / 手改坏），按老规矩跳过。
            return null;
        }
    }

    /// <summary>
    /// 给一行 JSON **对象**插一个（或换掉那个为 null 的）`id`。
    /// ⚠️ 用 <see cref="System.Text.Json.Nodes.JsonNode"/> 而不是字符串拼接：
    /// 拼接要自己处理转义与「插在哪一层」，而这里恰好有个现成的解析器。
    /// </summary>
    private static string InsertMissingId(string line)
    {
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(line) is not System.Text.Json.Nodes.JsonObject obj)
                return line;

            // 已经有非空 id ⇒ 什么都不做（这条其实不该走到这里）。
            if (obj["id"] is System.Text.Json.Nodes.JsonValue existing
                && existing.TryGetValue<string>(out var id) && !string.IsNullOrEmpty(id))
                return line;

            obj["id"] = Convert.ToHexStringLower(SHA256.HashData(Utf8NoBom.GetBytes(line)))[..12];
            return obj.ToJsonString(Json.Options);
        }
        catch (JsonException)
        {
            return line;   // 连解析都做不到 ⇒ 交给上层按坏行跳过
        }
    }
}
