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
    /// 模型的推理块。
    /// ⚠️ `00` §5 铁律：**能回传的协议必须逐字回传**；不能回传的（Chat / Responses）
    /// **必须不伪造**——见 ApiPlan spec §10.2。
    /// </summary>
    /// <param name="Signature">
    /// 不透明凭据，上游校验用。**原样存、原样传，绝不修改**。
    /// ⚠️ **可以为 null**：实测本机网关的 deepseek-flash 返回的 thinking 块没有 signature。
    /// </param>
    /// <param name="Text">可读推理文本；`Kind = "redacted"` 时是加密载荷的 base64。</param>
    /// <param name="Kind">
    /// `"text"`（明文推理，可回传）或 `"redacted"`（加密载荷，原样保留不解释）。
    /// </param>
    /// ⚠️ `[JsonPropertyName("reasoningKind")]` **不是审美选择，是硬约束**：
    /// 判别式（TypeDiscriminatorPropertyName）已经占用了 `"kind"`，而 camelCase 会把
    /// `Kind` 也写成 `"kind"`——两者相撞时 `System.Text.Json` 直接抛
    /// `InvalidOperationException: contains property 'kind' that conflicts with an existing
    /// metadata property name`（实测：整个类型连序列化都做不了，不只是这个属性）。
    /// 所以**线上键名必须是别的**，取 `"reasoningKind"`：自解释，且与判别式的 `"kind"` 不会混。
    /// ⚠️ 改这个键名 = `history.jsonl` 的形状迁移，只追加的历史里旧行会读不回来。
    public sealed record ReasoningBlock(
        string? Signature,
        string Text,
        [property: JsonPropertyName("reasoningKind")] string Kind = "text") : Block;
}
