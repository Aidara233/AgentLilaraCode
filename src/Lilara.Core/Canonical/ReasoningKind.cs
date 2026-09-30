namespace Lilara.Core.Canonical;

/// <summary>
/// 推理块的形态。⚠️ 单一来源——`ReasoningBlock.Kind`、`ReasoningPolicy`、
/// 适配器都读这里，不各写一份字面量。
/// ⚠️ 这些字符串**会落盘**，改动即数据迁移。
/// </summary>
public static class ReasoningKind
{
    /// <summary>明文推理，可读、可回传（前提是还有 signature）。</summary>
    public const string Text = "text";

    /// <summary>加密载荷：原样保留、不解释、不打印（ApiPlan spec §10.2）。</summary>
    public const string Redacted = "redacted";

    public static readonly IReadOnlyList<string> All = [Text, Redacted];
}
