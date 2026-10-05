using System.Text.Json;
using Lilara.Core.Kernel;
using Lilara.Core.Persistence;

namespace Lilara.Core.Providers;

// 提供商定义的落点：<数据根>/providers/<id>.json。
public sealed class ProviderStore(string dataRoot)
{
    private readonly string _root = Path.Combine(dataRoot, "providers");

    private string PathFor(string id) => Path.Combine(_root, $"{id}.json");

    public void Save(ProviderDefinition def) =>
        AtomicFile.Write(PathFor(def.Id), JsonSerializer.Serialize(def, Json.Options));

    // 读不到返回 null，不抛。两种「读不到」都吞：文件不存在，和文件损坏——
    // 只判 File.Exists 不够，损坏的文件会抛 JsonException 炸掉整个启动。
    // 只吞 JsonException：权限/磁盘失败必须暴露，静默「消失」比崩溃更糟。
    public ProviderDefinition? Load(string id)
    {
        var path = PathFor(id);
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<ProviderDefinition>(File.ReadAllText(path), Json.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // 删掉定义，返回是否真有文件被移除。判据是「文件在不在」，不是 Load(id) is null——
    // Load 把损坏的文件也当作不存在，那样就永远删不掉一个坏配置。
    // id 合法性不在这里判：闸是 CommandDispatcher.IsSafeProviderId，在本方法之前。
    // 删要像写一样重试：Windows 上文件被别的句柄读着时（FileShare.Read 不含
    // FILE_SHARE_DELETE）File.Delete 抛 IOException，而读是本项目的常态。
    // 失败必须重抛，不许返回 false——false 会被读成「没有这个提供商」，对暂时占用是假话。
    public bool Delete(string id)
    {
        var path = PathFor(id);

        // File.Delete 对不存在的文件是静默 no-op，所以必须先看再删。
        if (!File.Exists(path)) return false;

        // 与 AtomicFile.Write 同款重试：IOException（sharing violation）与
        // UnauthorizedAccessException（没带 FILE_SHARE_DELETE）都接，仍失败则照抛。
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Delete(path);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 3)
            {
                Thread.Sleep(10 * (attempt + 1));
            }
        }
    }

    public IReadOnlyList<ProviderDefinition> List()    {
        if (!Directory.Exists(_root)) return [];

        return Directory.GetFiles(_root, "*.json")
            .Select(f => TryRead(f))
            .Where(d => d is not null)
            .Select(d => d!)
            .OrderBy(d => d.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static ProviderDefinition? TryRead(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<ProviderDefinition>(File.ReadAllText(path), Json.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // 组装会话持有的模型选择：解析结果的快照 + 指向定义的引用。
    public ModelSelection ToSelection(
        ProviderDefinition def, string? modelOverride, string effort, int maxTokens) => new()
    {
        ProviderId = def.Id,
        ModelId = modelOverride ?? def.DefaultModel,
        Protocol = def.Protocol,
        Effort = effort,
        MaxTokens = maxTokens,
    };
}
