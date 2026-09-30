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
    public static void Append(string path, TimelineEvent evt)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        var line = JsonSerializer.Serialize(evt, Json.Options);
        File.AppendAllText(path, line + "\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>
    /// 逐行读回。
    /// ⚠️ 坏行**跳过**，不炸。
    /// <see cref="Append"/> 用的是 <c>File.AppendAllText</c>，不是原子的：
    /// 断电可能留下半行，而 history.jsonl 只追加、永不重写（01 §12.4），那半行会一直在。
    /// 完好前缀才是值钱的部分——让一条半行把整个会话的历史炸掉
    /// （重启后第一条 `history` 或发消息就吐栈）是本末倒置。
    /// ⚠️ 只吞 <c>JsonException</c>：权限/磁盘失败必须暴露，静默「历史消失」比崩溃更糟。
    /// 与 <see cref="SessionStore.TryReadSpec"/> / <c>ProviderStore.Load</c> 同一判据。
    /// </summary>
    public static IReadOnlyList<TimelineEvent> ReadAll(string path)
    {
        if (!File.Exists(path)) return [];

        var result = new List<TimelineEvent>();
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
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
