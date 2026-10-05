using System.Text.Json.Serialization;

namespace Lilara.Core.Kernel;

// 时间线上每个事件的载荷是一串块。最小闭环有文本块与推理块两种；
// 图片 / 工具调用 / 工具结果按同样方式追加 [JsonDerivedType] 即可。
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(TextBlock), "text")]
[JsonDerivedType(typeof(ReasoningBlock), "reasoning")]
public abstract record Block
{
    // 具体载荷：内容即渲染结果，不可变。
    public sealed record TextBlock(string Text) : Block;

    // 模型的推理块。
    // 铁律：能回传的协议必须逐字回传，不能回传的（Chat / Responses）必须不伪造。
    // Signature：不透明凭据，原样存原样传、绝不修改；可以为 null——本机网关的
    //   deepseek-flash 返回的 thinking 块就没有 signature。
    // Text：可读推理文本；Kind = Redacted 时是加密载荷的 base64。
    // Kind：Text（明文推理，可回传）或 Redacted（加密载荷，原样保留不解释）。
    // [JsonPropertyName("reasoningKind")] 是硬约束：判别式已占用 "kind"，camelCase 会把
    //   Kind 也写成 "kind"，两者相撞 STJ 直接抛 InvalidOperationException。改这个键名
    //   = history.jsonl 的形状迁移，只追加的历史里旧行会读不回来。
    public sealed record ReasoningBlock(
        string? Signature,
        string Text,
        string Kind = ReasoningKind.Text) : Block
    {
        // 归一必须写在 init 访问器里，不能只写在属性初始化器上——属性初始化器只在
        // 主构造器路径上跑，而 record 的 with { Kind = ... } 走 init setter，会绕过它。
        private readonly string _kind = Normalize(Kind);

        // 块形态。读取与写入都经过 Normalize，所以「Kind 为 null」在任何路径上
        // 都不可能：主构造器、with、以及 STJ 反序列化。
        [JsonPropertyName("reasoningKind")]
        public string Kind
        {
            get => _kind;
            init => _kind = Normalize(value);
        }

        // 参数必须是 string?：STJ 会把 "reasoningKind": null 绑进来。
        private static string Normalize(string? kind) => kind ?? ReasoningKind.Text;
    }
}
