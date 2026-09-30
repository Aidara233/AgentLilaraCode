using System.Text.Json.Serialization;

namespace Lilara.Core.Canonical;

/// <summary>
/// 时间线上每个事件的载荷是一串块（01 §3.4）。
/// ⚠️ 最小闭环有文本块与推理块两种。图片 / 工具调用 / 工具结果按同样方式
/// 追加 [JsonDerivedType] 即可。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(TextBlock), "text")]
[JsonDerivedType(typeof(ReasoningBlock), "reasoning")]
public abstract record Block
{
    /// <summary>具体载荷：内容即渲染结果，不可变。</summary>
    public sealed record TextBlock(string Text) : Block;

    /// <summary>
    /// 模型的推理块（00 §5 铁律：**逐字回传，永不可优化**）。
    /// ⚠️ `Signature` 是不透明凭据，上游会校验它；**原样存、原样传，绝不修改或重新生成**。
    /// `Text` 只是可读摘要，可以不显示，但不能因此丢掉整块。
    /// </summary>
    public sealed record ReasoningBlock(string Signature, string Text) : Block;
}
