using System.Text.Json;
using Lilara.Core.Canonical;
using Lilara.Core.Persistence;

namespace Lilara.Core.Providers;

/// <summary>提供商定义的落点：&lt;数据根&gt;/providers/&lt;id&gt;.json。</summary>
public sealed class ProviderStore(string dataRoot)
{
    private readonly string _root = Path.Combine(dataRoot, "providers");

    private string PathFor(string id) => Path.Combine(_root, $"{id}.json");

    public void Save(ProviderDefinition def) =>
        AtomicFile.Write(PathFor(def.Id), JsonSerializer.Serialize(def, Json.Options));

    /// <summary>
    /// 读不到就返回 null，不抛——调用方要的是「有没有」，不是异常。
    /// ⚠️ 两种「读不到」都要吞：**文件不存在**，和**文件损坏**。
    /// 只判 File.Exists 是不够的——一个损坏的 provider 文件会抛 JsonException，
    /// 穿透 List 炸掉整个启动，而这正是调用方那个 null 判断想防的事。
    /// 只吞 JsonException：权限/磁盘失败必须暴露，静默「消失」比崩溃更糟。
    /// </summary>
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
            // 损坏的 provider 文件应被当作「没有」，而不是让核心起不来
            return null;
        }
    }

    public IReadOnlyList<ProviderDefinition> List()
    {
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

    /// <summary>
    /// 组装会话持有的模型选择：解析结果的快照 + 指向定义的引用（01 §7.2）。
    /// </summary>
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
