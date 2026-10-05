using System.Collections.Concurrent;
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

    public static void Append(string path, TimelineEvent evt)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        var line = JsonSerializer.Serialize(evt, Json.Options) + "\n";

        // 不用 File.AppendAllText：它默认 FileShare.Read，第二个 append 开句柄就抛 IOException。
        lock (PathLocks.GetOrAdd(Path.GetFullPath(path), _ => new Lock()))
        {
            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(fs, Utf8NoBom);
            writer.Write(line);
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
                // 断电留下的半行：跳过，保住前面的前缀
            }
        }
        return result;
    }
}
