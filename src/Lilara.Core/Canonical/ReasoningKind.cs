namespace Lilara.Core.Canonical;

// 推理块的形态。单一来源——ReasoningBlock.Kind、ReasoningPolicy、适配器都读这里。
// 这些字符串会落盘，改动即数据迁移。
public static class ReasoningKind
{
    // 明文推理，可读、可回传（前提是还有 signature）。
    public const string Text = "text";

    // 加密载荷：原样保留、不解释、不打印。
    public const string Redacted = "redacted";

    public static readonly IReadOnlyList<string> All = [Text, Redacted];
}
