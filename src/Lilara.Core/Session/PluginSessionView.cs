using System.Collections.ObjectModel;
using Lilara.Core.Kernel;
using Lilara.Core.Plugins;

namespace Lilara.Core.Session;

/// <summary>控制面、控制台与推进器共用的插件轴投影。</summary>
public static class PluginSessionView
{
    public static IReadOnlyDictionary<string, string> Values(SessionState state)
    {
        var values = new Dictionary<string, string>(state.Axes ?? Axes.Defaults(), StringComparer.Ordinal);
        if (state.PluginAxes is not null)
            foreach (var pair in state.PluginAxes)
                if (!Axes.IsKnown(pair.Key)) values[pair.Key] = pair.Value;
        return new ReadOnlyDictionary<string, string>(values);
    }

    public static IReadOnlyList<object> Definitions(PluginHost host, SessionSpec? spec = null)
    {
        var result = Axes.Names.Select(n => (object)new
        { Name = n, Label = Axes.LabelOf(n), Values = Axes.ValuesOf(n),
            InContext = Axes.InContext(n), Range = Axes.RangeOf(n),
            // ⚠️ **等级解释也归核心**（2026-11-03）：头要把「这一档停下来靠什么」显示给人看
            // （`iterate` 没有模型侧出口那条），而头不许手抄轴语义——它只能从这一格拿。
            // ⚠️ 永远是对象（可能是 `{}`），不是 null：漏字段与「没有解释」在头上必须分得开。
            Descriptions = Axes.DescriptionsOf(n) }).ToList();
        var bindings = spec is null ? host.Bind() : spec.Plugins ?? [];
        foreach (var binding in bindings)
        {
            var problem = host.Problem(binding);
            foreach (var pair in binding.Defaults.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var found = host.TryGetAxis(pair.Key, out var axis);
                result.Add(new { Name = pair.Key, Label = found ? axis.Label : pair.Key,
                    Values = found ? axis.Values : new[] { pair.Value },
                    InContext = found && axis.InContext, Available = problem is null, Problem = problem, PluginId = binding.Id,
                    // 插件轴的解释在注册时就被复制冻结（`AxisDefinition.LevelDescriptions`）。
                    Descriptions = found ? axis.LevelDescriptions : Axes.DescriptionsOf(null) });
            }
        }
        return result.AsReadOnly();
    }

    public static IReadOnlyList<string> Problems(PluginHost host, SessionSpec spec) =>
        Array.AsReadOnly((spec.Plugins ?? []).Select(host.Problem).OfType<string>().ToArray());
}
