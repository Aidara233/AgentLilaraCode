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

    public static IReadOnlyList<TimelineEvent> ReadAll(string path)
    {
        if (!File.Exists(path)) return [];

        var result = new List<TimelineEvent>();
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            result.Add(JsonSerializer.Deserialize<TimelineEvent>(line, Json.Options)!);
        }
        return result;
    }
}
