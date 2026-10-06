using System.Text.Json.Serialization;

namespace Lilara.Core.Kernel;

// 时间线上每个事件的载荷是一串块。今天有三种「内容」块与两种「结构」块：
//
//   内容：text（文本）· reasoning（推理）· image（图片，字节在库里）
//   结构：toolUse（模型的调用）· store（引用库对象）
//
// ⚠️ **工具结果不是块**——它是它自己的 `TimelineEvent` 派生类型。
// 理由：Claude 的 tool_result 住在 **user 角色**的消息里，塞进共用的 `Block` 会
// 在类型层面与用户文本不可分，而「用户消息是具体载荷」是硬约束（`01` §3.6）。
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(TextBlock), "text")]
[JsonDerivedType(typeof(ReasoningBlock), "reasoning")]
[JsonDerivedType(typeof(ImageBlock), "image")]
[JsonDerivedType(typeof(ToolUseBlock), "toolUse")]
[JsonDerivedType(typeof(StoreBlock), "store")]
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

    /// <summary>
    /// **模型发出的工具调用**——它是模型输出的一部分，所以是块。
    ///
    /// <para>⚠️ <b>这与「工具结果」不对称，而那个不对称是协议给的</b>：
    /// `tool_use` 住在**模型**的消息里（模型的输出），`tool_result` 住在**用户**角色的
    /// 消息里。所以一个是块、一个是它自己的事件类型（`01` §3.6）。</para>
    ///
    /// <para>⚠️ <b><see cref="Arguments"/> 是<b>原文 JSON 文本</b>，不是对象</b>——
    /// 用法见 <see cref="JsonRawConverter"/>。参数**必须逐字回传**（`01` §9 铁律），
    /// 而「解析成对象再拼回去」正是要防的：键序、空白、数字字面量都会变。</para>
    ///
    /// <para>⚠️ <b>id 有两个，别合并</b>（`01` §8.0.2 第七节）：<see cref="CallId"/> 是
    /// **协议 id**（provider 生成，wire 配对用）；`TimelineEvent.Id` 是**我们的稳定 id**
    /// （盘上那一行的身份，取回 / 改历史 / 头引用共用）。</para>
    /// </summary>
    public sealed record ToolUseBlock(
        string CallId,
        string Name,
        [property: JsonConverter(typeof(JsonRawConverter))] string Arguments) : Block;

    /// <summary>
    /// 一张**图片**。字节住库里（`store/&lt;sha256&gt;`），这里只存地址与媒体类型。
    ///
    /// <para>⚠️ **图片永远不许折进文本**（`01` §3.6）：协议把「图文」表示成数组 ⇒
    /// 文本与图片是**兄弟块**。折了之后图没了而描述还在 ⇒ 多一条**假信息**
    /// （图上写着 42，图已经不在了）。</para>
    /// </summary>
    public sealed record ImageBlock(string Sha256, string MediaType) : Block;

    /// <summary>
    /// 一个**库对象**的引用（不限于图片：PDF 等原生模态内容同理）。
    ///
    /// <para>⚠️ 与 <see cref="ImageBlock"/> 分开是刻意的：那个是**内容本身**
    /// （协议原生图片块），这个是**一个地址**。混成一个会让「这段内容是什么模态」
    /// 与「它的字节在哪」两件事绑死。</para>
    /// </summary>
    public sealed record StoreBlock(string Sha256, string MediaType) : Block;
}

/// <summary>
/// 一串块的**规范序列化**——写入侧（`ToolExecutor` 把混排结果进库）与判定侧
/// （`Reclaim` 算它的地址）**共用这一个**。
///
/// <para>⚠️ <b>为什么必须是共用的一个函数</b>：判定算地址、写入算地址，各写一遍的后果是
/// <b>静默的</b>——键序、转义、忽略 null 任何一处漂了，算出来的地址就永远不在库里
/// ⇒ <b>一条都回收不了且不报错</b>（本仓栽过的「两层判据必须共用一个谓词」）。</para>
///
/// <para>⚠️ <b>参数类型必须是 <see cref="Block"/> 的集合，不能是泛型</b>——同
/// <see cref="Json.StableSerialize"/> 的陷阱：推断成具体子类会丢掉判别式 <c>kind</c>，
/// 于是同一串块写出两种字节、进而两个地址。</para>
/// </summary>
public static class BlockList
{
    /// <summary>
    /// 库对象的媒体类型：内容是**一串块**（<see cref="ToJson"/> 的字节），不是原始文本。
    ///
    /// <para>⚠️ <b>它与 <c>text/plain</c> 是两条不同的路，读取方必须分得开</b>：
    /// 单文本块的快照存的是**文件原文**，混排 / 多块的存的是**块数组 JSON**。
    /// 混着读会让 `read` 的库对象吐出一段 JSON 来（而它本该是原文）。</para>
    /// </summary>
    public const string MediaType = "application/vnd.lilara.blocks+json";

    public static string ToJson(IReadOnlyList<Block> blocks) =>
        System.Text.Json.JsonSerializer.Serialize(blocks, Json.Options);
}
