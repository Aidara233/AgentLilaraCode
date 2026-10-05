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

    /// <summary>
    /// 删掉 <paramref name="id"/> 的定义，返回**是否真有一个文件被移除**。
    ///
    /// <para>⚠️ **判据是「那个文件在不在」（文件层），不是 `Load(id) is null`。**
    /// `Load` 把**损坏**的文件也当作「不存在」⇒ 用它会得到一个**永远删不掉的坏配置**，
    /// 而「把一个坏配置删掉」正是用户最想做的事（也是本任务明确不许加闸的那件事）。
    /// 调用方（dispatcher）用这个返回值判「不存在 ⇒ 拒绝」。</para>
    ///
    /// <para>⚠️ **`id` 的合法性不在这里判**：闸是 `CommandDispatcher.IsSafeProviderId`，
    /// 在 dispatcher 里、且必须在**调用本方法之前**（控制台与头共用那一条判据，G16）。
    /// 本方法只做一件事：把 `PathFor(id)` 指到的那个文件移除。</para>
    /// </summary>
    public bool Delete(string id)
    {
        var path = PathFor(id);

        // ⚠️ **先看再删。** `File.Delete` 对不存在的文件是**静默 no-op**（既不抛也不报），
        // 所以它自己交不出「有没有删掉」这个判据——而调用方要的正是它。
        if (!File.Exists(path)) return false;

        File.Delete(path);
        return true;
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
