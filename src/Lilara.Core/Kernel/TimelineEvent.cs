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
        ///
        /// <para>⚠️ <b>它与 <see cref="DependsOn"/> / <see cref="Fingerprint"/> 是**三件不同的东西**，别读混</b>
        /// （三个词挨在一起，这是最容易混的地方）：<b>那两件是结果**自己**的属性</b>
        /// （从哪儿来、世界动没动），<b>这一件指**另一条记录**</b>（`fetch` 的取回源）。
        /// ⇒ 普通工具的结果只填 <see cref="Content"/> + 后两件，<b>不填这一件</b>。</para>
        /// </summary>
        public string? Ref { get; init; }

        /// <summary>
        /// **依赖什么**——`00` §5 的「结果 = 引用 + 快照 + 指纹」里的第一件。
        ///
        /// <para>⚠️ <b>它必须落盘，而理由不是「有用」，是「算不回来」</b>：
        /// 三态判定判的是「**世界动没动**」，做法是**重新解析这个指称**再比指纹
        /// ⇒ 没有它，判定无从开始。（它**可能**能从紧邻的 <see cref="Block.ToolUseBlock.Arguments"/>
        /// 重算出来，但那是**碰巧**——比如工具用了默认值、或参数在展开之后才成为路径。）</para>
        ///
        /// <para>⚠️ **它的形状由工具自己定**（`read` 是路径、HTTP 工具是 URL、DB 工具是连接串 + 查询）。
        /// 这里只是一个字符串，因为**核心不该懂任何一条工具的参数语义**。
        /// 契约只有一条：**它是能重新解析回同一个东西的指称**。</para>
        ///
        /// <para>⚠️ **可以不填**——见 <see cref="Fingerprint"/>。</para>
        /// </summary>
        public string? DependsOn { get; init; }

        /// <summary>
        /// **当时那个指纹**——`00` §5 三态判定要比的就是它。
        ///
        /// <para>⚠️ <b>这一件是「为什么结果要落盘三件」的真正原因</b>：判定要拿
        /// **现在的**指纹与**当时的**比，而「当时的」**只能从记录里读**
        /// ——它是**最不可派生**的一个（要重算它，得先把那个源原文找回来）。
        /// `00` §2.2 那张表写的正是这个：「**骨架（引用 + 指纹）✅ 永久**；快照 ❌ 可回收」。</para>
        ///
        /// <para>⚠️ <b><c>null</c> 是合法的，而且是最强的一档</b>——`00` §5：
        /// **不可重演类（执行命令 / 发邮件 / 取时间）「无指纹」，永不可优化**。
        /// ⇒ 别把它写成 <c>required</c>：那会逼着「没有指纹」的工具编一个假的出来，
        /// 而一个假指纹比 <c>null</c> 坏得多（它会**声称**世界没动过）。</para>
        ///
        /// <para>⚠️ **它与 <see cref="DependsOn"/> 是两个独立的哈希，别合并成一个**
        /// （`01` §3.6）：一个说「依赖什么」，一个说「它当时是什么样」。
        /// 合并之后，「同一个对象被两条记录依赖」这件事就表达不出来了。</para>
        /// </summary>
        public string? Fingerprint { get; init; }
    }

    // 稳定序列化入口。参数类型必须是 TimelineEvent（基类），不能写成泛型 T——
    // 同 Json.StableSerialize 的陷阱：推断成具体子类会丢掉判别式 "kind"，
    // 而且写的时候静默、直到反序列化才炸。事件泵广播它，写错的帧要到解析才发现。
    public static string StableSerialize(TimelineEvent value) =>
        System.Text.Json.JsonSerializer.Serialize(value, Kernel.Json.Options);
}
