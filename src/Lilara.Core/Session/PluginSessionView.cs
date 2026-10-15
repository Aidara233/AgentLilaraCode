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
            InContext = Axes.InContext(n), Range = Axes.RangeOf(n) }).ToList();
        var bindings = spec is null ? host.Bind() : spec.Plugins ?? [];
        foreach (var binding in bindings)
        {
            var problem = host.Problem(binding);
            foreach (var pair in binding.Defaults.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var found = host.TryGetAxis(pair.Key, out var axis);
                result.Add(new { Name = pair.Key, Label = found ? axis.Label : pair.Key,
                    Values = found ? axis.Values : new[] { pair.Value },
                    InContext = found && axis.InContext, Available = problem is null, Problem = problem, PluginId = binding.Id });
            }
        }
        return result.AsReadOnly();
    }

    public static IReadOnlyList<string> Problems(PluginHost host, SessionSpec spec) =>
        Array.AsReadOnly((spec.Plugins ?? []).Select(host.Problem).OfType<string>().ToArray());
}
