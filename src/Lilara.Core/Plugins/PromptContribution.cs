namespace Lilara.Core.Plugins;

public enum PromptSlot
{
    System,
    Primary,
    Floating,
}

/// <summary>
/// 一个结构化提示词字段。插件只贡献字段，宿主负责排序和拼接，避免自由文本互相覆盖。
/// </summary>
public sealed record PromptContribution
{
    public required string PluginId { get; init; }
    public required string Key { get; init; }
    public required PromptSlot Slot { get; init; }
    public required string Text { get; init; }
    public bool Stable { get; init; }

    internal string SortKey => $"{PluginId}{Key}";
}

public sealed record PromptContributionSnapshot(
    IReadOnlyList<PromptContribution> System,
    IReadOnlyList<PromptContribution> Primary,
    IReadOnlyList<PromptContribution> Floating)
{
    public string? Text(PromptSlot slot) =>
        slot switch
        {
            PromptSlot.System => Join(System),
            PromptSlot.Primary => Join(Primary),
            PromptSlot.Floating => Join(Floating),
            _ => throw new ArgumentOutOfRangeException(nameof(slot)),
        };

    private static string? Join(IReadOnlyList<PromptContribution> fields) =>
        fields.Count == 0 ? null : string.Join("\n", fields.Select(f => $"[{f.PluginId}/{f.Key}]\n{f.Text}"));
}
