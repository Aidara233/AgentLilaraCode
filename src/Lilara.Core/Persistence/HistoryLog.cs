using System.Text;
using System.Text.Json;
using Lilara.Core.Canonical;
using Lilara.Core.Records;

namespace Lilara.Core.Persistence;

/// <summary>
/// 每步追加的骨架记录（01 §8）。
/// ⚠️ 只追加，永不重写（01 §12.4）——「简化」是加载期解释，不是落盘时改写。
/// </summary>
public static class HistoryLog
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void Append(string path, TimelineEvent evt)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        var line = JsonSerializer.Serialize(evt, Json.Options) + "\n";

        // ⚠️ 显式开 FileStream，**不用 File.AppendAllText**：后者的默认共享模式是
        // FileShare.Read，**只允许别人读**——第二个 append 在开句柄那一刻就抛
        // IOException（Windows：文件正被另一进程使用）。实测过，是**硬失败**不是脏数据。
        // ⚠️ 但 ReadWrite 只解决**句柄冲突**，**不解决字节交错**：两个 append 真并发时
        // 仍可能写出交错的坏行。那靠**每会话逻辑串行**解决（CommandDispatcher 的每会话信号量）。
        // 两个洞各修一个，不可互替。
        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        using var writer = new StreamWriter(fs, Utf8NoBom);
        writer.Write(line);
    }

    /// <summary>
    /// 逐行读回。
    /// ⚠️ 坏行**跳过**，不炸。
    /// 追加不是原子的：断电可能留下半行，而 history.jsonl 只追加、永不重写（01 §12.4），
    /// 那半行会一直在。完好前缀才是值钱的部分——让一条半行把整个会话的历史炸掉
    /// （重启后第一条 `history` 或发消息就吐栈）是本末倒置。
    /// ⚠️ 只吞 <c>JsonException</c>：权限/磁盘失败必须暴露，静默「历史消失」比崩溃更糟。
    /// 与 <see cref="SessionStore.TryReadSpec"/> / <c>ProviderStore.Load</c> 同一判据。
    /// </summary>
    public static IReadOnlyList<TimelineEvent> ReadAll(string path)
    {
        if (!File.Exists(path)) return [];

        var result = new List<TimelineEvent>();

        // ⚠️ 读也**必须** ReadWrite：默认的 FileShare.Read 会**挡住写句柄**——
        // 真实时延：控制台敲 `history` 读历史的同时，头发的消息 append 会抛 IOException
        // （实测过）。与 Append 的共享模式必须**对称**，否则「读挡写」的窗口永远在。
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs, Encoding.UTF8);

        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                var evt = JsonSerializer.Deserialize<TimelineEvent>(line, Json.Options);
                // 反序列化出 null（例如一行字面的 `null`）也不能进列表——
                // 那个 `!` 正是这一轮要根除的东西
                if (evt is not null) result.Add(evt);
            }
            catch (JsonException)
            {
                // 断电留下的半行：跳过，保住前面的前缀
            }
        }
        return result;
    }
}
