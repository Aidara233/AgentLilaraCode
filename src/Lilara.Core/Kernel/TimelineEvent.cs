using System.Text.Json.Serialization;

namespace Lilara.Core.Kernel;

// 发言者。最小闭环只有这两种。
public enum Speaker
{
    User,
    Model,
}

// 记录 = 事件时间线 + 两个固定槽位（头、尾）。本计划只实现时间线，头尾槽位要等插件系统。
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(MessageEvent), "message")]
[JsonDerivedType(typeof(ToolResultEvent), "toolResult")]
public abstract record TimelineEvent
{
    /// <summary>
    /// **这条记录在盘上的身份。** 只追加的文件里，它是唯一的指称方式。
    ///
    /// <para>⚠️ **三个消费者共用它，所以它必须是稳定且持久的**（`01` §8.0.2 第七节）：
    /// ① `fetch` 的引用（`ToolResultEvent.Ref`）② 批次 2 的改历史 / 截断 ③ 头引用某一条。</para>
    ///
    /// <para>⚠️ **它必须是 `required`，不能靠属性初始化器给默认值。** 那样反序列化一条
    /// **没有 id 的老行**时，初始化器会**当场生成一个新的随机 id** ⇒ 同一个文件读两次得到两组
    /// 不同的 id ⇒ `fetch` 的引用**当场失效**，而且是静默的。
    /// ⇒ 宁可让每个构造点都显式写一次 `Id = Ids.New()`（与 <see cref="At"/> 同一个形状：
    /// 它也是 `required`，因为它同样不能由类型替调用方猜）。</para>
    /// </summary>
    public required string Id { get; init; }

    public required DateTimeOffset At { get; init; }

    // 用户 / 模型的发言。
    public sealed record MessageEvent : TimelineEvent
    {
        public required Speaker From { get; init; }
        public required IReadOnlyList<Block> Blocks { get; init; }
    }

    /// <summary>
    /// **工具结果**——`00` §5「一块的两面」里，与 <see cref="Block.ToolUseBlock"/> 配对的另一半。
    ///
    /// <para>⚠️ <b>它为什么不是块</b>：Claude 的 <c>tool_result</c> 住在 <b>user 角色</b>的消息里，
    /// 塞进共用的 <see cref="Block"/> 会在<b>类型层面与用户文本不可分</b>——而「用户消息是具体载荷」
    /// 是硬约束。⚠️ 更重要的是：<b>它要带「引用 / 快照 / 指纹」三样</b>（`00` §5），
    /// 那是<b>整条记录</b>的属性，不是某一块的属性（`01` §3.6）。</para>
    ///
    /// <para>⚠️ <b><see cref="Content"/> 是一串块，可混排、也可纯图</b>（`01` §3.6）：
    /// 纯文本（`read`）· <b>纯图</b>（截图工具只有图、一个字都没有）· 混排有序。
    /// ⇒ 它甚至不是一个块，是一串。</para>
    /// </summary>
    public sealed record ToolResultEvent : TimelineEvent
    {
        /// <summary>配对到哪一个 <see cref="Block.ToolUseBlock.CallId"/>。**协议 id**，不是本记录的 <see cref="Id"/>。</summary>
        public required string CallId { get; init; }

        /// <summary>
        /// 结果的内容。⚠️ **取回时它是空的**（见 <see cref="Ref"/>）——
        /// 「空内容」在**没有 `Ref`** 时才是「这个工具确实什么都没返回」。
        /// </summary>
        public required IReadOnlyList<Block> Content { get; init; }

        /// <summary>
        /// **`fetch` 专用的那个字段**：引用另一条记录的内容，而不是复制一份。
        ///
        /// <para>⚠️ **为什么是字段不是块**：<b>引用是「结构」，不是「内容」</b>
        /// （与 <see cref="Block.ToolUseBlock"/> 相对的那条推理同源）。做成块的话，
        /// 「一块内容恰好是个引用」与「这条结果是个引用」就混成一个了。</para>
        ///
        /// <para>⚠️ **为什么是引用不是副本**：会被取回的东西按定义**大**（它是被回收过的），
        /// 复制回来正好抵消回收省下的那点东西。渲染时解引用 ⇒ 看到的就是原内容本身。</para>
        ///
        /// <para>⚠️ **解到最底**：`fetch` 一个 `fetch` 的结果时，它指向**原行**、不指向那条
        /// fetch——否则会串成链。</para>
        ///
        /// <para>⚠️ **与 <see cref="Content"/> 恰好一个非空**。两个都空 = 「这个工具什么都没返回」；
        /// 两个都非空 = 不该出现的形状。</para>
        /// </summary>
        public string? Ref { get; init; }
    }

    // 稳定序列化入口。参数类型必须是 TimelineEvent（基类），不能写成泛型 T——
    // 同 Json.StableSerialize 的陷阱：推断成具体子类会丢掉判别式 "kind"，
    // 而且写的时候静默、直到反序列化才炸。事件泵广播它，写错的帧要到解析才发现。
    public static string StableSerialize(TimelineEvent value) =>
        System.Text.Json.JsonSerializer.Serialize(value, Kernel.Json.Options);
}
