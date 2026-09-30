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
    /// <param name="Text">可读推理文本；`Kind = ReasoningKind.Redacted` 时是加密载荷的 base64。</param>
    /// <param name="Kind">
    /// <see cref="ReasoningKind.Text"/>（明文推理，可回传）或
    /// <see cref="ReasoningKind.Redacted"/>（加密载荷，原样保留不解释）。
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
        string Kind = ReasoningKind.Text) : Block
    {
        // ⚠️ 归一必须写在 **init 访问器**里，不能只写在属性初始化器上。
        // 属性初始化器**只在主构造器路径**上跑；而 record 的 `with { Kind = ... }`
        // 走的是 init setter，会绕过它。实测过：`a with { Kind = null! }`
        // 归一前是 null → 字段被 WhenWritingNull 丢掉 → 往返后不等于自己。
        // 落盘格式已冻结，这里漏一次就是一次数据迁移。
        private readonly string _kind = Normalize(Kind);

        /// <summary>
        /// 块形态。⚠️ 读取与写入**都**经过 <see cref="Normalize"/>——
        /// 所以「Kind 为 null 的块」在任何路径上都不可能出现：主构造器、`with`、
        /// 以及 STJ 反序列化（它可能把字面 `null` 绑到参数上）。
        /// </summary>
        [JsonPropertyName("reasoningKind")]
        public string Kind
        {
            get => _kind;
            init => _kind = Normalize(value);
        }

        // ⚠️ 参数必须是 `string?`：非空参数会让 `??` 被判为冗余，
        // 而 STJ 真的会把 `"reasoningKind": null` 绑进来。
        private static string Normalize(string? kind) => kind ?? ReasoningKind.Text;
    }
}
