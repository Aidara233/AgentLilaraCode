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
[JsonDerivedType(typeof(ReleaseEvent), "release")]
[JsonDerivedType(typeof(ReclaimEvent), "reclaim")]
public abstract record TimelineEvent
{
    /// <summary>
    /// **这条记录在盘上的身份。** 只追加的文件里，它是唯一的指称方式。
    ///
    /// <para>⚠️ **三个消费者共用它，所以它必须是稳定且持久的**（`01` §8.0.2 第七节）：
    /// ① 改历史 / 截断（§14 批次 2）② 头引用某一条 ③ `fetch` 的注记里给出的取回地址。</para>
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
        /// **框架**——工具对自己**那次调用**的结构化描述（`01` §3.7）。
        ///
        /// <para>⚠️ <b>它说的是「那次调用<b>返回了什么</b>」，而那是<b>事实</b> ⇒ 落盘。</b>
        /// 与<b>核心那一半</b>（「这条<b>已回收</b>」）分得开：那一半说的是
        /// 「你**现在还能看到多少**」，随回收状态变 ⇒ **渲染期算、不落盘**（与注记同类）。</para>
        ///
        /// <para>⚠️ <b>为什么要单独一格、而不是拼进 <see cref="Content"/></b>：
        /// 回收今天换的是<b>整个 <c>Content</c></b>。框架若与内容混在同一个块里，
        /// 那一步就会<b>连框架一起吃掉</b>——而它今天没出事，只是因为结果是<b>一对一</b>。
        /// 分成两块之后，<c>Reclaim.Apply</c> 只换 <c>Content</c>，框架一个字不动。</para>
        ///
        /// <para>⚠️ <b>可空 + 不设值 ⇒ 盘上键整个不出现</b>（<c>Json.Options</c> 开了
        /// <c>WhenWritingNull</c>）⇒ <b>老文件逐字节不变、金样不用改</b>。
        /// 这条是「加字段要不要动 <c>FormatVersion</c>」的答案（本仓的 G20），
        /// 与已作废的 <see cref="Ref"/> 用的是同一个手法。</para>
        /// </summary>
        public ToolFraming? Framing { get; init; }

    /// <summary>
    /// **用户改写过的参数**（`01` §3.8：改写后批准）。<c>null</c> = 没改过。
    ///
    /// <para>⚠️ <b>这一格必须有，否则记录在撒谎</b>：`ToolUseBlock` 里躺着的是
    /// **模型原来那段参数**，而用户批准并（可能）改写之后**实际执行的是另一段**。
    /// 不记它，读记录的人（和重放的人）会以为跑的是模型那一段——
    /// 那是 `01` §3.6 ①之二那句「最坏的失败不是少了信息，是**多了一条假信息**」。</para>
    ///
    /// <para>⚠️ 它与 <see cref="Framing"/> 同属「事实」那一半（落盘、只增不减）。</para>
    /// </summary>
    public string? ArgumentsAmended { get; init; }

        /// <summary>
        /// 结果的内容。⚠️ **取回时它是空的**（见 <see cref="Ref"/>）——
        /// 「空内容」在**没有 `Ref`** 时才是「这个工具确实什么都没返回」。
        /// </summary>
        public required IReadOnlyList<Block> Content { get; init; }

        /// <summary>
        /// **⚠️ 已作废：这个字段永远不会被写。** 留着只是为了磁盘格式不动
        /// （它一直是可空 + `WhenWritingNull` ⇒ 老文件逐字节不变，删了反而是一次无谓的格式变更）。
        ///
        /// <para><b>为什么作废</b>（2026-10-06，用户裁定，实测之后）：原设计是
        /// 「取回只留引用、不复制字节」（`01` §8.0.2 六），理由是
        /// <i>「会被取回的东西按定义大，复制回来正好抵消回收省下的东西」</i>。
        /// <b>那个理由在内容寻址下不成立</b>：库里同一个字节只有一份
        /// ⇒ 再写一遍**不多占盘**，差的只是记录里「12 位 id」对「64 位地址」的五十来个字节。</para>
        ///
        /// <para>而它换来三样东西，全是负的：① 一层「找不到」的间接；
        /// ② 一条**零调用方**的空转链（写侧 → 执行侧 → 渲染侧 → 三条适配器守卫）；
        /// ③ 引用型结果的 <see cref="Content"/> 是空的 ⇒ 按「内容自己就够才可回收」
        /// 它**永远收不了**，于是「回收 → 取回 → 永久占位」——正好是 §8.0.2 自己警告过的那个形状。
        /// 带内容的那版反过来还**照常进回收范围**。</para>
        ///
        /// <para>⚠️ 删掉的是**产出方与解引用那一层**（`ToolOutcome.Ref` / `Reference` /
        /// `ToolExecutor` 的分支 / `TimelineRender.DereferenceRef`）。
        /// 字段本身留着，见上。</para>
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

    /// <summary>
    /// **画一条线**——`01` §12.4 那个「线」的落点，也是回收与压缩共用的那个位置标记。
    ///
    /// <para>⚠️ <b>它是一个<b>标记</b>，不是一次对话</b>：`release` 不返回内容——
    /// 它只落一条「已释放。」的确认（非空，见 `ReleaseTool` 的注释），不渲染成任何块。
    /// §11.2 给压缩写过同一句：「工具结果为空……它的意义在于<b>标记</b>」。</para>
    ///
    /// <para>⚠️ <b>为什么它必须落盘，而「哪条被回收了」不落盘</b>（`01` §8.0.2 那张表）：
    /// <b>注入边界是事实，逐条的判定是派生的</b>。</para>
    /// <list type="bullet">
    ///   <item><b>这条线本身</b>⇒ 模型**声明过**「这一批资源我用完了」——那是一件发生过的事，
    ///   <b>算不回来</b>（要靠它才能倒查到 <c>R</c>）。⇒ 落盘。</item>
    ///   <item><b>线以下哪几条该换注记</b>⇒ <c>线 + 判据 ⇒ 结论</c>，纯派生。⇒ **不落盘**。</item>
    /// </list>
    /// <para>⇒ 判据 8（能派生的就不要持久化）在这里**两半都成立**：派生的那半不落盘，
    /// 而**位置不是派生值**——它是这件事的原始事实。</para>
    ///
    /// <para>⚠️⚠️ <b>它是一条<b>只追加</b>的记录里的一个新判别式 ⇒ 加它是一次单向门。</b>
    /// 磁盘上一次写过之后，<b>回滚这个类型 = 那个会话永久读不出来</b>（不是读错，是抛）。
    /// ⇒ 与 `Reclaim.NoteText` 那条纪律同源：**落盘字节的形状，写下去之前要想清楚**。
    /// 今天它是安全的：没有会话会写它——那个工具是这一批才加的。</para>
    ///
    /// <para>⚠️ <b>它不进「现在要用的那串事件」</b>（`TimelineRender.Render` 会把它过滤掉）：
    /// 它是**给我们自己**看的锚（倒查、线以下的解释），
    /// 而模型的上下文里已经有那次 `tool_use` 了——再喂一条「我释放了 src/」是多余的字节。
    /// ⇒ 两个消费者各取所需：**判定**读它（要从**记录**读，不是从渲染读），
    /// **模型**看不见它。</para>
    /// </summary>
    /// <param name="Scope">
    /// 释放的声明范围（模型报的路径，**原样**——与 `ClaimAcquire` 的 `Scope` 同形）。
    /// ⚠️ <b>这份副本是判据的输入</b>：`Reclaim.LineCovers` 读它（不回头去翻那条
    /// `tool_use` 的参数）⇒ 一条记录就答得出「这次释放覆盖了什么」，
    /// 不必跨事件反查。⚠️ 而模型看见的那串事件里没有这条记录（`TimelineRender`
    /// 会把它挡在外面）——**判定读记录，模型读渲染**。
    /// </param>
    /// <param name="Summary">
    /// 可空：§12.4 的 `release(scope, summary)` 里那个 `summary`。
    /// `null` = 这次释放不给摘要（今天的工具不填它）。
    /// ⚠️ 它**不是**压缩记录——线下放**摘要**是另一条产物（§11.1 那张产物表）。
    /// </param>
    public sealed record ReleaseEvent : TimelineEvent
    {
        public required IReadOnlyList<string> Scope { get; init; }

        public string? Summary { get; init; }
    }

    // 稳定序列化入口。参数类型必须是 TimelineEvent（基类），不能写成泛型 T——
    // 同 Json.StableSerialize 的陷阱：推断成具体子类会丢掉判别式 "kind"，
    // 而且写的时候静默、直到反序列化才炸。事件泵广播它，写错的帧要到解析才发现。
    public static string StableSerialize(TimelineEvent value) =>
        System.Text.Json.JsonSerializer.Serialize(value, Kernel.Json.Options);
}
