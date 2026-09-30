using System.Text.Json.Serialization;

namespace Lilara.Core.Canonical;

/// <summary>
/// 时间线上每个事件的载荷是一串块（01 §3.4）。
/// ⚠️ 最小闭环只有文本块——因为核心不带任何工具、不带任何提示词（04 §1.3）。
/// 图片 / 工具调用 / 工具结果 / 推理块按同样方式追加 [JsonDerivedType] 即可。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(TextBlock), "text")]
public abstract record Block
{
    /// <summary>具体载荷：内容即渲染结果，不可变。</summary>
    public sealed record TextBlock(string Text) : Block;
}
