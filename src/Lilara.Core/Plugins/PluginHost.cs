using System.Collections.ObjectModel;
using System.Text;
using Lilara.Core.Kernel;

namespace Lilara.Core.Plugins;

/// <summary>轴注册与贡献收集的唯一入口。插件注册后不可覆盖彼此的声明。</summary>
public sealed class PluginHost
{
    private readonly Dictionary<string, IAxisPlugin> _plugins = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string PluginId, AxisDefinition Definition)> _axes =
        new(StringComparer.Ordinal);
    private bool _sealed;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PluginAxisSnapshot> _schemas = new(StringComparer.Ordinal);
    public const int MaxFieldsPerPlugin = 64;
    public const int MaxFieldBytes = 16 * 1024;
    public const int MaxPromptBytes = 64 * 1024;

    public IReadOnlyList<PluginAxisSnapshot> Plugins
    {
        get { lock (_gate) return Array.AsReadOnly(_schemas.Values.OrderBy(p => p.Plugin.Id, StringComparer.Ordinal).ToArray()); }
    }

    public void Register(IAxisPlugin plugin)
    {
        lock (_gate) RegisterCore(plugin);
    }

    private void RegisterCore(IAxisPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        if (_sealed) throw new InvalidOperationException("Plugin registration is closed.");

        var id = plugin.Identity.Id;
        if (!IsPluginId(id)) throw new ArgumentException($"Invalid plugin id '{id}'.", nameof(plugin));
        if (string.IsNullOrWhiteSpace(plugin.Identity.Version))
            throw new ArgumentException("Plugin version is required.", nameof(plugin));
        if (_plugins.ContainsKey(id)) throw new InvalidOperationException($"Plugin '{id}' is already registered.");

        var candidate = new List<(string PluginId, AxisDefinition Definition)>();
        foreach (var axis in plugin.Axes)
        {
            if (axis is null || !IsAxisId(axis.Id) ||
                !axis.Id.StartsWith(id + ".", StringComparison.Ordinal))
                throw new ArgumentException($"Plugin '{id}' may only register axes in its own namespace.", nameof(plugin));
            if (string.IsNullOrWhiteSpace(axis.Label) || axis.Values.Count == 0 ||
                axis.Values.Any(string.IsNullOrWhiteSpace) ||
                axis.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != axis.Values.Count ||
                !axis.Accepts(axis.DefaultValue))
                throw new ArgumentException($"Axis '{axis.Id}' has an invalid schema or default.", nameof(plugin));
            if (_axes.ContainsKey(axis.Id) || candidate.Any(x => x.Definition.Id == axis.Id))
                throw new InvalidOperationException($"Axis '{axis.Id}' is already registered.");
            candidate.Add((id, axis with { Values = Array.AsReadOnly(axis.Values.ToArray()),
                DefaultValue = axis.Canonical(axis.DefaultValue) }));
        }

        var capabilities = plugin.Capabilities.Distinct().Order().ToArray();
        if (capabilities.Any(c => c != AxisCapability.None))
            throw new NotSupportedException($"Plugin '{id}' requests unsupported mechanical capabilities: {string.Join(", ", capabilities)}.");
        _schemas.Add(id, new PluginAxisSnapshot(plugin.Identity,
            Array.AsReadOnly(candidate.Select(c => c.Definition).OrderBy(a => a.Id, StringComparer.Ordinal).ToArray()),
            Array.AsReadOnly(capabilities)));
        _plugins.Add(id, plugin);
        foreach (var entry in candidate) _axes.Add(entry.Definition.Id, entry);
    }

    public void Seal() { lock (_gate) _sealed = true; }

    public bool TryGetAxis(string? id, out AxisDefinition definition)
    {
        Seal();
        if (id is not null && _axes.TryGetValue(id, out var entry))
        {
            definition = entry.Definition;
            return true;
        }
        definition = null!;
        return false;
    }

    public bool TryCanonicalize(string? id, string value, out string canonical)
    {
        canonical = "";
        if (!TryGetAxis(id, out var definition) || !definition.Accepts(value)) return false;
        canonical = definition.Canonical(value);
        return true;
    }

    public AxisSnapshot Freeze(IReadOnlyDictionary<string, string> values, DateTimeOffset capturedAt,
        IReadOnlyList<SessionPluginBinding>? bindings = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        Seal();
        var copy = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in values) copy.Add(pair.Key, pair.Value);
        foreach (var binding in bindings ?? Bind())
        {
            if (Problem(binding) is { } problem) throw new InvalidOperationException(problem);
            foreach (var pair in binding.Defaults)
            {
                if (!TryCanonicalize(pair.Key, copy.GetValueOrDefault(pair.Key, pair.Value), out var value))
                    throw new InvalidOperationException($"Invalid saved value for '{pair.Key}'.");
                copy[pair.Key] = value;
            }
        }
        return new AxisSnapshot(new ReadOnlyDictionary<string, string>(copy), capturedAt);
    }

    public PromptContributionSnapshot Collect(AxisSnapshot snapshot, IReadOnlyList<SessionPluginBinding>? bindings = null)
    {
        Seal();
        var all = new List<PromptContribution>();
        var bytes = 0;
        foreach (var binding in (bindings ?? Bind()).OrderBy(b => b.Id, StringComparer.Ordinal))
        {
            if (Problem(binding) is { } problem) throw new InvalidOperationException(problem);
            var plugin = _plugins[binding.Id];
            var schema = _schemas[binding.Id];
            // 提示词回调仅看可见核心轴和本插件可见轴，不投影机械轴或其他插件的轴。
            var visible = snapshot.Values.Where(pair => Axes.IsKnown(pair.Key) ? Axes.InContext(pair.Key) :
                schema.Axes.Any(a => a.Id == pair.Key && a.InContext))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            IReadOnlyList<PromptContribution> contributions;
            try
            {
                contributions = plugin.Prompt(new AxisSnapshot(
                    new ReadOnlyDictionary<string, string>(visible), snapshot.CapturedAt)) ??
                    throw new InvalidOperationException("Null contributions.");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Plugin '{binding.Id}' prompt collection failed: {ex.Message}", ex);
            }
            if (contributions.Count > MaxFieldsPerPlugin) throw new InvalidOperationException("Too many plugin prompt fields.");
            foreach (var contribution in contributions)
            {
                if (contribution is null || contribution.PluginId != binding.Id ||
                    !IsFieldKey(contribution.Key) || string.IsNullOrWhiteSpace(contribution.Text) ||
                    !Enum.IsDefined(contribution.Slot))
                    throw new InvalidOperationException($"Plugin '{binding.Id}' returned an invalid prompt contribution.");
                if (contribution.Slot == PromptSlot.Floating && contribution.Stable)
                    throw new InvalidOperationException("Floating contributions cannot be stable prefix content.");
                if (contribution.Slot != PromptSlot.Floating && !contribution.Stable)
                    throw new InvalidOperationException("System and primary contributions must be stable.");
                var size = Encoding.UTF8.GetByteCount(contribution.Text);
                bytes += size + Encoding.UTF8.GetByteCount(contribution.PluginId) + Encoding.UTF8.GetByteCount(contribution.Key) + 8;
                if (size > MaxFieldBytes || bytes > MaxPromptBytes)
                    throw new InvalidOperationException("Plugin prompt byte limit exceeded.");
                all.Add(contribution);
            }
        }

        if (all.GroupBy(c => (c.Slot, c.PluginId, c.Key)).Any(g => g.Count() > 1))
            throw new InvalidOperationException("Prompt contribution keys must be unique within a slot and plugin.");

        IReadOnlyList<PromptContribution> Ordered(PromptSlot slot) => Array.AsReadOnly(all
            .Where(c => c.Slot == slot)
            .OrderBy(c => c.PluginId, StringComparer.Ordinal)
            .ThenBy(c => c.Key, StringComparer.Ordinal)
            .ToArray());

        return new PromptContributionSnapshot(
            Ordered(PromptSlot.System), Ordered(PromptSlot.Primary), Ordered(PromptSlot.Floating));
    }

    public IReadOnlyList<SessionPluginBinding> Bind()
    {
        Seal();
        return Array.AsReadOnly(Plugins.Select(p => new SessionPluginBinding(p.Plugin.Id, p.Plugin.Version,
            new ReadOnlyDictionary<string, string>(p.Axes.ToDictionary(a => a.Id, a => a.DefaultValue, StringComparer.Ordinal))))
            .ToArray());
    }

    public string? Problem(SessionPluginBinding binding)
    {
        Seal();
        if (!_schemas.TryGetValue(binding.Id, out var schema))
            return $"Plugin '{binding.Id}' is missing; saved configuration is preserved.";
        if (schema.Plugin.Version != binding.Version)
            return $"Plugin '{binding.Id}' requires version '{binding.Version}', installed '{schema.Plugin.Version}'.";
        if (schema.Axes.Count != binding.Defaults.Count || schema.Axes.Any(a =>
            !binding.Defaults.TryGetValue(a.Id, out var value) || !a.Accepts(value)))
            return $"Plugin '{binding.Id}' schema differs from its session binding.";
        return null;
    }

    private static bool IsFieldKey(string value) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 && value.Split('.').All(IsPluginId);

    private static bool IsPluginId(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 64 &&
        value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    private static bool IsAxisId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128) return false;
        var parts = value.Split('.');
        return parts.Length == 2 && parts.All(part =>
            part.Length > 0 && part.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'));
    }
}
