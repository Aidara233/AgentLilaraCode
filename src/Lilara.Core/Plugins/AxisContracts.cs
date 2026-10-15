namespace Lilara.Core.Plugins;

/// <summary>插件的稳定身份。插件 id 同时是其轴命名空间。</summary>
public sealed record PluginIdentity(string Id, string Version);

/// <summary>插件声明的一条轴。轴值先统一为规范化字符串。</summary>
public sealed record AxisDefinition
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required IReadOnlyList<string> Values { get; init; }
    public required string DefaultValue { get; init; }
    /// <summary>false：仅供控制面显示和保存，不进入提示词回调的轴视图。</summary>
    public bool InContext { get; init; } = true;

    public bool Accepts(string? value) =>
        value is not null && Values.Any(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase));

    public string Canonical(string value) =>
        Values.First(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase));
}

/// <summary>一步开始时冻结的插件轴视图。</summary>
public sealed record AxisSnapshot(
    IReadOnlyDictionary<string, string> Values,
    DateTimeOffset CapturedAt);

/// <summary>插件可以声明将来需要的能力；本批不执行任意机械回调。</summary>
public enum AxisCapability
{
    None,
    RequestParameter,
    ToolGate,
    Continuation,
    Verification,
}

public sealed record PluginAxisSnapshot(
    PluginIdentity Plugin,
    IReadOnlyList<AxisDefinition> Axes,
    IReadOnlyList<AxisCapability> Capabilities);

/// <summary>受信任的进程内扩展。Prompt 必须纯、可并发调用；本接口不是插件沙箱。</summary>
public interface IAxisPlugin
{
    PluginIdentity Identity { get; }
    IReadOnlyList<AxisDefinition> Axes { get; }
    IReadOnlyList<AxisCapability> Capabilities { get; }
    IReadOnlyList<PromptContribution> Prompt(AxisSnapshot snapshot);
}
