namespace Lilara.Core.Kernel;

/// <summary>
/// **工具的权限类别**——闸门按 `档位 × 类别` 查表（`01` §3.5）。
///
/// <para>⚠️ <b>由工具自报，核心不维护"哪些工具危险"的清单</b>：那清单会腐烂，
/// 而**工具自己最清楚**（`00` §1.3「谁的知识谁负责」）。</para>
///
/// <para>⚠️ <b>四档的判据是「不可逆」，不是「写入量」</b>：
/// <see cref="Destructive"/> 是最后一个被自动放行的东西
/// （`01` §3.5：`git push` / 发邮件 / 删库）。</para>
/// </summary>
public enum PermissionClass
{
    /// <summary>只读。⚠️ 注意它**不含**"读网络"——那有副作用，归 <see cref="Consuming"/>。</summary>
    ReadOnly,

    /// <summary>消耗型：读网络 / 花钱 / 花时间。**不改本机状态**，但世界变了。</summary>
    Consuming,

    /// <summary>写本机状态。⚠️ **可逆**是它与下面那档的分界。</summary>
    Writing,

    /// <summary>不可逆（删库 / 推送 / 发邮件）。</summary>
    Destructive,
}

/// <summary>
/// **框架**——工具对自己**那次调用**的结构化描述（`01` §3.7）。
///
/// <para>⚠️ <b>它是「那次调用返回了什么」，是事实 ⇒ 跟着记录落盘</b>。
/// 与**核心那一半**（「这条已回收」）**不是一个东西**：那一半说的是
/// 「你现在还能看到多少」，随回收状态变 ⇒ 渲染期算、不落盘。
/// ⇒ 一条判据管两半、方向相反：**「那次调用发生的事」→ 工具，落盘；
/// 「这一刻的可见性」→ 核心，渲染期算。**</para>
///
/// <para>⚠️ <b>为什么它是<b>结构化</b>的、不是一段拼好的字符串</b>：
/// 核心要能<b>取用</b>其中的事实（比如判「这条结果被截断过」），
/// 而不是去 <c>StartsWith("[read")</c> 解析自己刚拼出来的那句话。
/// 拼法只有一处（<see cref="Render"/>）——两处各拼一遍，那句「定长、位置固定」
/// 就会漂，而**漂了不会报错**。</para>
/// </summary>
/// <param name="Tool">工具名。⚠️ 就是模型回传的那个串（<see cref="ITool.Name"/>）——**别改**。</param>
/// <param name="Fields">
/// 字段。⚠️ <b>顺序由工具定</b>（它知道哪个字段重要），渲染器**不排序**——
/// 排序会漂，而漂了就是「同一步内渲染两次结果不同」（`00` §5 铁律 1）。
/// </param>
public sealed record ToolFraming(string Tool, IReadOnlyList<ToolFramingField> Fields)
{
    /// <summary>
    /// 渲染成**一句定长、位置固定**的文本——它是给模型的**形状**，不是一段散文。
    ///
    /// <para>⚠️ <b>模型认位置与形状，不认语义</b>（`00` §7 判据 5：
    /// 「能进机械层的，绝不靠嘴说」）。⇒ 把它写成一句读得懂的话是在**求**模型配合，
    /// 而下面这个方括号形状是在**给**它一个可辨认的边界。</para>
    ///
    /// <para>⚠️ <b>不转义、不折行</b>：字段值由工具保证（路径 / 数字 / 短词），
    /// 而替换任何字符都会让两个不同的框架渲染成同一句——那是**静默的假信息**。</para>
    /// </summary>
    public string Render() =>
        "[" + Tool + (Fields.Count == 0 ? "" : " " + string.Join(" · ", Fields.Select(f => $"{f.Key} {f.Value}"))) + "]";
}

/// <summary>框架里的一个字段。⚠️ <c>(键, 值)</c> 就够——核心不该懂任何一条工具的字段语义。</summary>
public sealed record ToolFramingField(string Key, string Value);

/// <summary>
/// **发给 provider 的一份工具定义。** 它是 <see cref="ITool"/> 的投影——`Stepper` 从工具表
/// 造出来交给 <c>ModelRequest</c>，**适配器只认这一层，不认识 <see cref="ITool"/>**。
///
/// <para>⚠️ **为什么要多这一层**：`ITool` 有 <see cref="ITool.RunAsync"/>（行为），
/// 而请求里要的只有**声明**。把 `ITool` 直接放进 `ModelRequest` 会让「发出去的东西」
/// 与「能执行的东西」绑死——而 MCP 工具是**远端**的（`04` §4：本地没有可执行的东西，
/// 只有一个远端名字），它照样要进工具表。</para>
/// </summary>
/// <param name="Name">工具名。</param>
/// <param name="Description">给模型的说明。</param>
/// <param name="ParametersSchemaJson">参数 schema 的**原文**。⚠️ 逐字（同
/// <see cref="Block.ToolUseBlock.Arguments"/> 那条纪律）。</param>
public sealed record ToolSpec(string Name, string Description, string ParametersSchemaJson);

/// <summary>
/// 模型发出的**一次工具调用**。
///
/// <para>⚠️ <b>它是一次调用的<b>全部</b>信息，而这里面没有「会话」「工作目录」</b>——
/// 那些从 <see cref="IToolContext"/> 来。工具因此是**无状态的**：同一个 <see cref="ToolCall"/>
/// 配上同一个上下文，跑几次都一样（`00` §5 铁律 1「渲染必须是纯的」的推论）。</para>
/// </summary>
/// <param name="CallId"><b>协议 id</b>（provider 生成），wire 配对用。⚠️ 不是本记录的稳定 id——
/// 两个 id 的分工见 `01` §8.0.2 第七节。</param>
/// <param name="Name">工具名。⚠️ 核心**用它查表**，不解析它。</param>
/// <param name="ArgumentsJson"><b>原文 JSON</b>，不经解析。⚠️ 逐字保真是铁律（`01` §9）——
/// 它一路从 <see cref="Block.ToolUseBlock.Arguments"/> 原样传到这里，中间没有人重新序列化过。</param>
public sealed record ToolCall(string CallId, string Name, string ArgumentsJson);

/// <summary>
/// 一次工具执行的产物。**结果的三件里，工具负责两件半**：
/// <see cref="Content"/>（快照）· <see cref="DependsOn"/>（依赖什么）· <see cref="Fingerprint"/>（当时的指纹）。
///
/// <para>⚠️ <b>失败也是结果，不是异常</b>：文件不存在 ⇒ 这里放一段说人话的
/// <see cref="Content"/>，而不是抛。理由是协议层面的——工具**配对**（`tool_use` ↔ `tool_result`）
/// 是上游的硬要求，抛异常会让这一对只落一半，而<b>半个在拿到的不是「少点内容」，是上游 400</b>
/// （`01` §3.6）。</para>
///
/// <para>⚠️ <b>工具<b>不</b>决定快照存哪儿</b>：它只交出 <see cref="Content"/>，
/// 「内联进记录」还是「进库 + 留一个 <see cref="Block.StoreBlock"/> 地址」由**核心**按
/// `BlobStore.InlineThresholdBytes` 一处判定。理由有两条：
/// ① 那条阈值是**一条策略**，放核心才只有一份；
/// ② <see cref="IToolContext"/> 因此可以真的是**只读**的（`04` §5.1）——工具根本没有写库的手。
/// ⚠️ 上面那句刻意**不写 cref**：Kernel 不该在文档里指 Storage（这一层的判据是「谁都不认识」），
/// 而分层守卫只扫 `using` 行，**写 cref 它不会红**——正是那种「守卫拦不住、只能靠人」的地方。</para>
/// </summary>
public sealed record ToolOutcome
{
    /// <summary>
    /// 快照——**渲染产物**，不是文件原文（`00` §5）。
    ///
    /// <para>⚠️ 对 `read` 来说这两者今天**恰好**是同一串字节（它就是把文件读出来），
    /// 但契约上不是：别在别处写「快照 = 文件原文」，那会让下一个工具（比如一个把日志
    /// 摘要后再喂的）无从落地。</para>
    ///
    /// <para>⚠️ 空列表 = 「这个工具确实什么都没返回」，是合法形态。
    /// ⚠️ 它**不表示失败**——失败同样是内容（见类型注释）。</para>
    /// </summary>
    public required IReadOnlyList<Block> Content { get; init; }

    /// <summary>依赖什么。⚠️ 形状**由工具自己定**（`read` 是路径、HTTP 工具是 URL）；
    /// 核心只当它是字符串。契约只有一条：**能重新解析回同一个东西**。见
    /// <see cref="TimelineEvent.ToolResultEvent.DependsOn"/>。</summary>
    public string? DependsOn { get; init; }

    /// <summary>当时那个指纹。⚠️ <c>null</c> 是合法的，且是**最强的一档**——
    /// `00` §5 的不可重演类（执行命令 / 发邮件 / 取时间）**无指纹**，永不可优化。
    /// 别给一个「差不多」的值：假指纹比没有更坏，它会**声称**世界没动过。</summary>
    public string? Fingerprint { get; init; }

    /// <summary>
    /// **框架**——这一次调用返回了什么（`01` §3.7）。⚠️ 它是**事实** ⇒ 跟着记录落盘。
    ///
    /// <para>⚠️ <b>它<b>不是</b> <see cref="Content"/> 的一部分</b>：回收换的是整个
    /// <c>Content</c>，框架若混在里面会被**一起吃掉**。分成两块之后，
    /// 回收只换内容、框架原样——而模型靠「这是**独立的块**」区分二者，
    /// **不靠读一段散文**。</para>
    ///
    /// <para>⚠️ <c>null</c> 是合法的（老记录、和不需要框架的工具都是）。
    /// 不给 ⇒ 盘上键不出现 ⇒ 老文件逐字节不变。</para>
    /// </summary>
    public ToolFraming? Framing { get; init; }

    /// <summary>
    /// **这次执行其实是「问用户」**（`01` §3.8）。非 null ⇒ 本次**没有跑任何东西**，
    /// 核心应当把这一步**中止**、落盘、转 `WaitingForInput`。
    ///
    /// <para>⚠️ <b>只有声明了 <see cref="ITool.AsksBeforeRunning"/> 的工具该填它</b>
    /// ——否则「先判全、再执行」那条不变量破（走在前面的调用可能已经落了副作用）。</para>
    ///
    /// <para>⚠️ <b>它是一个<b>结果</b>，不是一个异常</b>：与 <see cref="Content"/> 那条
    /// 「失败也是内容」同源。而这个结果**不会被落盘**——它转成一次暂停。
    /// 用户答完之后，核心**重新问一次工具**要那批问题（工具因此必须是纯的，
    /// 见 `SessionState.PausedAsk` 的注释）。</para>
    ///
    /// <para>⚠️ <c>null</c> 是默认 —— 老工具、以及所有不声明询问的工具，
    /// 行为**逐字节不变**。</para>
    /// </summary>
    public AskRequest? Ask { get; init; }

    /// <summary>
    /// **这次执行其实是一次派生**（`01` §10，2026-10-24）。非 null ⇒ 工具**没有建会话**
    /// ——它只把「要建什么」说清楚，真正的 `SessionHandle.Create` 在
    /// `CommandDispatcher`（那里才有 `PluginHost` / 事件总线 / `Start`）。
    ///
    /// <para>⚠️ <b>它是 <see cref="Ask"/> 的同族，理由也同源</b>：两者都是「工具够不着、
    /// 但它必须说出来」的事，都走「<b>返回</b>而不是「等」」（`AskRequest` 的类型注释：
    /// 让执行器去 await 一个将来，等于把推进权借出去）。</para>
    ///
    /// <para>⚠️ <b>子会话 id 由工具预分配</b>（`Ids.New()`）：结果正文里那个 id 因此是
    /// **将来真的会存在**的那一个，模型下一步拿它调 `tell_child` 不会指向空气。
    /// 代价如实记：若创建失败（深度到顶、目录没了），那条结果已经说了「已派出」——
    /// 所以**校验必须先于产出**（工具里 `SubagentScope.CanSpawn` 一次判完），
    /// 而创建那一侧只可能因 IO 失败。</para>
    /// </summary>
    public SpawnRequest? Spawn { get; init; }

    /// <summary>
    /// **这次执行其实是一次注入**——父对子说一句，或子对父说一句（`01` §10.1）。
    ///
    /// <para>⚠️ 与 <see cref="Spawn"/> 同一个形状、同一条理由：工具只声明，
    /// 追加记录 + 入队 + 启动都在 `CommandDispatcher`（`Stepper` 刻意不碰事件总线）。</para>
    ///
    /// <para>⚠️ <b>父 → 子那一半要能被 Stepper 认出来</b>（好让
    /// <see cref="TellRequest.TargetSessionId"/> 走一次「是不是我的孩子」的校验），
    /// 所以它也走这条结构化通道，而不是让工具自己闷头写。</para>
    /// </summary>
    public TellRequest? Tell { get; init; }

    /// <summary>
    /// **这次执行其实是一次归档**——父会话把某个孩子从视野里收起（2026-10-26）。
    ///
    /// <para>⚠️ 与 <see cref="Spawn"/> / <see cref="Tell"/> 同一个形状、同一条理由：
    /// 工具没有改 spec 的笔，也没打算有。⇒ 它只声明「收起哪一个」，
    /// 写 <see cref="SessionSpec.ArchivedAt"/> 与发 `session.archived` 都在
    /// `CommandDispatcher`（同 `release` 的线）。</para>
    ///
    /// <para>⚠️ <b>它是一种<b>可逆</b>标记</b>：不删记录、不搬目录，取消归档就把字段写回 <c>null</c>。
    /// ⇒ 工具的 <see cref="ITool.PermissionClass"/> 是 <see cref="PermissionClass.ReadOnly"/>
    /// ——与 `release` 同一条判据：它做的唯一一件事是**说出一句话**。</para>
    /// </summary>
    public ArchiveRequest? Archive { get; init; }
}

/// <summary>
/// 工具能碰到的东西——**句柄**（`04` §5.1）。
///
/// <para>⚠️ <b>它刻意只有读取方法。</b>「只读」在这里是**结构性**的，不是纪律性的：
/// 工具类里根本没有写盘的手，所以「工具偷偷改了东西」不是一个需要靠评审去抓的问题。
/// 与 <see cref="ITool"/> 相对的是核心自己——**改加载边界（`compact`）、回收、改历史
/// 都在核心**，从句柄上拿不到。</para>
///
/// <para>⚠️ <b>今天只有两个成员，而这是刻意的</b>：`04` §5.1 说句柄是「只读**数据根** +
/// blob 读取器」，但数据根那一半要等 `fetch`（今天的工具都不需要跨会话定位）。
/// ⇒ **别为了「留位」先加一个没人用的成员**：那是死代码，而这个仓库刚清过一轮。</para>
/// </summary>
public interface IToolContext
{
    /// <summary>会话的工作目录（`spec.WorkingDirectory`）。工具的相对路径都从这里算。</summary>
    string WorkingDirectory { get; }

    /// <summary>
    /// 把**相对工作目录**的路径解析成绝对路径；**逃逸就是 <c>null</c>**。
    ///
    /// <para>⚠️ <b>这个闸不是可选的</b>：模型给的路径是**不可信输入**，
    /// 而 <c>../../../../etc/passwd</c> 会让工具读出工作目录之外的东西。
    /// 判据与 <see cref="Storage.BlobStore"/> 那条同源——**形状校验**（解析出来的绝对路径
    /// 必须仍在工作目录之下），不是「看着像不像恶意路径」。</para>
    ///
    /// <para>⚠️ <b>它拦不住符号链接 / 目录联接</b>：一个落在工作目录里的链接可以指向外面，
    /// 而这里做的是**词法**判定、不解析链接目标。今天**接受**这个洞，理由两条：
    /// ① Windows 上创建符号链接需要特权，不是模型随手能做到的；
    /// ② 要真堵住得解析到最终目标再判一次，而那是「范围」那条线自己的事（用户已说
    /// 工作目录 / 鉴权 / 默认范围要**逐渐提上日程**）。⇒ 写在这里，别当成没人知道。</para>
    /// </summary>
    string? ResolvePath(string relativePath);

    /// <summary>读一个库对象。不存在（或名字形状不对）返回 <c>null</c>，不抛。</summary>
    byte[]? ReadBlob(string sha256);

    /// <summary>
    /// **把一串字节放进库，交回它的地址**（内容寻址）。⚠️ <b>它<b>不是</b>「写库的手」</b>——
    /// 见下。
    ///
    /// <para>⚠️ <b>它存在，是因为「写盘的工具要能保住旧版本」</b>（`update` 那条：
    /// 写之前把旧内容存起来 ⇒ 这一步可逆）。而<b>工具仍然看不见 <c>BlobStore</c></b>——
    /// 它拿到的是一个地址字符串，不是那个能读能写、还能算大小的对象。
    /// ⇒ 本接口那条「<b>工具没有写库的手</b>」的设计**一个字没动**：
    /// 交出去的是一支**专用笔**（存进去、拿到地址），不是库本身。</para>
    ///
    /// <para>⚠️ <b>为什么不干脆给工具一个 <c>BlobStore</c></b>：那样工具就能
    /// 读任意地址、写任意字节，而「谁决定快照存哪儿」那条分工
    /// （<see cref="ToolOutcome"/>：**核心**按 `InlineThresholdBytes` 一处判定）
    /// 当场变成「工具自己看着办」。⇒ 宽度是刻意压到「一个方向、一个动作」的。</para>
    ///
    /// <para>⚠️ <b>内容寻址 = 幂等</b>：同一串字节存两次拿到同一个地址、且不占第二份空间。
    /// ⇒ 这个能力**撑不爆库**，工具可以放心用它做「留个底」这种事。</para>
    /// </summary>
    string EnsureStored(byte[] content);

    /// <summary>
    /// 按**记录的稳定 id** 找一条工具结果的快照——交回的是**当时那串块**（可能不止一块）。
    /// 找不到 / 那条结果没有可寻址的形状 ⇒ <c>null</c>，不抛。
    ///
    /// <para>⚠️ <b>返回「串」而不是「一块」，是因为混排</b>（`01` §3.6 ①之一）：
    /// 一条结果可以是「这是那张图」+ 图那样一串块，也可以是纯图、一个字都没有。
    /// ⇒ `fetch` 把当时那串**原样**交回，不折成一段文本（折了就是多一条假信息）。</para>
    ///
    /// <para>⚠️ <b>它只读，而且这件事由类型保证</b>：句柄上没有任何写盘方法。
    /// 实现里按**记录**算地址（不是渲染后的形态）——回收只换渲染、记录不动，
    /// 拿渲染后的内容去算会算出**那段注记**的哈希 ⇒ 当场永久取不回（`01` §8.0.2 一 / 八）。</para>
    ///
    /// <para>⚠️ <b>「只能取回本会话」这条限制写在<b>句柄</b>上，不写进工具的语义</b>
    /// ——`04` §5.1 明文要求这么做。跨会话取回出现时**只改本接口的实现**，
    /// 工具一个字不动。（§5.1 说句柄「应该是数据根」；今天实现的是**本会话**那一格，
    /// 差额如实记在文档里，别读成已经给了根。）</para>
    /// </summary>
    IReadOnlyList<Block>? SnapshotOf(string stableId);

    /// <summary>
    /// 按**记录的稳定 id** 取回一条结果的**原始字节**（只对单文本块的结果有值）。
    /// 找不到 / 形状不是单文本块 ⇒ <c>null</c>，不抛。
    ///
    /// <para>⚠️ <b>它存在，是因为「投递截断」需要一条退路</b>（2026-10-17）：
    /// 大结果在渲染时只喂给模型开头一段，注记里给出稳定 id 与下一个偏移；
    /// `fetch` 用这个成员按字节窗口取回原文——**不必重跑那个工具**
    /// （重跑要花时间、还不可复现）。</para>
    ///
    /// <para>⚠️ <b>它仍然是只读的</b>，与 <see cref="ReadBlob"/> / <see cref="SnapshotOf"/> 同一条：
    /// 句柄上没有写盘的手。</para>
    /// </summary>
    byte[]? RawBytesOf(string stableId);

    /// <summary>
    /// **这一刻的尾块文本**——`floating_context` 读它（`01` §3.2，2026-10-17）。
    ///
    /// <para>⚠️ <b>它是一份<b>快照</b>，不是「让工具现算一遍」</b>：推进器在步开始
    /// （答复路径上则是执行那一刻）调一次 <c>SystemBlock.Render</c>，把尾块文本冻进句柄。
    /// ⇒ 模型的合成注入与工具返回的**是同一串字节**——各算一份的话，
    /// 「核心注入的那份」与「模型自己要来的那份」会在同一步里分叉，而那是最难发现的一类假信息。</para>
    ///
    /// <para>⚠️ <b>空串 = 这一刻没有浮动块</b>（没有轴进上下文、也没有插件贡献浮动字段）。
    /// 它不是错误：尾块「连包边一起不产出」是 `SystemBlock.Wrap` 的判据。</para>
    ///
    /// <para>⚠️ 它**不是回看历史**的入口：浮动是状态、活在「此刻」，从不落盘——
    /// 想要某一步当时发出去的那份，不存在，也不需要存在。</para>
    /// </summary>
    string FloatingContext { get; }

    /// <summary>
    /// **用户下发过、而 `compact` 还没消费的压缩指令**（2026-10-22）。
    ///
    /// <para>⚠️ <b>它是那份<b>持久化</b>指令在工具这一侧的唯一入口</b>（`SessionState.PendingCompact`）。
    /// `compact` 据此判「这次调用算不算数」——没指令时它是<b>一句人话 + 什么都不做</b>。</para>
    ///
    /// <para>⚠️ <b>它不是「留位」用的</b>（那条「别为了留位先加没人用的成员」）：消费者今天就有两个
    /// ——`CompactTool`（判生效）与尾块指令（`Stepper` 每步注入的那句「用户要求压缩」）。
    /// 两份都读<b>同一个</b>判据，而判据本身只有 `SessionState.PendingCompact` 一处。</para>
    /// </summary>
    bool CompactRequested { get; }

    /// <summary>
    /// 这个会话**这一刻的联网能力快照**（`network.json` + `network` 轴值），2026-10-22。
    ///
    /// <para>⚠️ <b>`null` = 没有联网上下文</b>（纯单测手工构造的句柄）。联网工具据此回一句
    /// 说人话的「这里没有联网配置」，而不是猜一个「开着」——与 `FloatingContext` 的空串同向：
    /// **不知道就说不**。</para>
    ///
    /// <para>⚠️ <b>它是一份快照，不是「让工具现读配置」</b>：一步之内两条调用必须按同一份
    /// 配置与同一个轴值判定（同权限档位那条）。</para>
    /// </summary>
    NetworkAccess? Network { get; }

    /// <summary>
    /// 这个会话**这一刻的 shell 能力快照**（`shell.json`），2026-10-23。
    ///
    /// <para>⚠️ <b>`null` = 没有 shell 上下文</b>（纯单测手工构造的句柄）——工具据此回一句
    /// 说人话的「这里没有 shell 配置」，而不是猜一个「开着」。
    /// ⚠️ 它在**权限闸之后**才被读：能不能跑由 `permission` 轴判（`shell` 是
    /// <see cref="PermissionClass.Destructive"/>），这份配置只回答「这台机器跑不跑得了」。</para>
    /// </summary>
    ShellAccess? Shell { get; }

    /// <summary>
    /// 这个会话此刻是否处于 goal 模式（主动性轴 = `goal`）。
    ///
    /// <para>⚠️ <b>只给 `finish_goal` 用</b>：不在 goal 模式下调用它应当是**一句说明 + 什么都不做**，
    /// 而不是假装收尾。真正的「把轴拨回 standard」在推进器那一侧做（工具没有改状态的手）。</para>
    /// </summary>
    bool GoalActive { get; }

    /// <summary>
    /// 这一刻的**派生关系快照**（`01` §10，2026-10-24）。`null` = 这里没有派生上下文
    /// （手工构造的句柄、纯单测）——`spawn` / `tell_child` 据此**如实说**「这里不能派生」，
    /// 而不是猜「大概是顶层会话吧」。
    ///
    /// <para>⚠️ <b>它是一份快照，不是「让工具现查」</b>：与 `Network` / `Shell` 同一条理由
    /// ——工具在**不碰 store** 的前提下要能回答「我还能不能派生」「我有哪些孩子」，
    /// 而这两件事都要读盘。冻结在建设点，步内一致。</para>
    ///
    /// <para>⚠️ <b>`Children` 里的状态是<b>盘上那份</b></b>：`Running` 只写内存
    /// （`SessionStatus` 的类型注释），而句柄这里够不着 `CoreHost`。⇒ 子会话正在跑时
    /// 这里读到的是 `Idle`。**别在这一层假装知道它在跑**——那需要给句柄开一个
    /// 「内存状态」的口子，而那是另一件事（且它会让句柄不再是纯数据的投影）。</para>
    /// </summary>
    SubagentScope? Subagents { get; }
}

/// <summary>
/// 一条工具。
///
/// <para>⚠️ <b>接口上<b>没有</b>注册、没有生命周期、没有优先级</b>——因为**工具不是独立实体**，
/// 工具表是**派生**的（`04` §1.3）。加进来一套注册与卸载，正是那张设计要省掉的东西。</para>
///
/// <para>⚠️ <b>「内建」是一条<b>属性</b>，不是第三种来源</b>：来源只有三种
/// （内建 / 插件 / MCP），而「内建」说的是**不可卸载**——判据同 `04` §1.2 给协议适配器的那条
/// （「做成可卸载的插件等于把『能跑』变成可选项」）。⇒ 别在接口上加 <c>IsBuiltIn</c>：
/// 那会把「谁提供」与「能不能拿掉」两件事混成一个布尔。</para>
///
/// <para>⚠️ <b>本批不做钩子相位、权限类别、<c>tool.setEnabled</c></b>——
/// `00` §5 那四个钩子里，本批只落「执行」；「渲染」是 <see cref="RunAsync"/> 的直接产物，
/// 「优化」「取消」要等回收与中断那两块。</para>
/// </summary>
public interface ITool
{
    /// <summary>工具名。⚠️ 它就是模型在 <see cref="Block.ToolUseBlock.Name"/> 里回传的那个串，
    /// 也是查表的键——**别改**，改了等于让历史里已有的调用变成死链。</summary>
    string Name { get; }

    /// <summary>
    /// **权限类别**——闸门按 `档位 × 类别` 查表（`01` §3.5）。
    ///
    /// <para>⚠️ <b>它挂在 <see cref="ITool"/> 上，<b>绝不</b>挂 <c>ToolSpec</c></b>：
    /// `ToolSpec` 是**进请求的**（那是最前端），放那儿 = 把权限写进前缀
    /// ⇒ 改一次档位就破一次全库缓存，而 `01` §9.2 那条「改闸门零前缀代价」**当场作废**。
    /// `04` §7.2 那张表写得很明确：**权限类别 ❌ 不破前缀**——因为**它不在请求里**。</para>
    ///
    /// <para>⚠️ <b>它没有默认实现，而这是刻意的</b>：编译器逼**每个工具作者表态**。
    /// 一个安静的默认值会让作者**永远不填**，而默认值恰恰是"标准档问一句"那种
    /// ——**看起来没坏，实际全错**。（`04` §5 给 MCP 定的默认 `Writing` 是给
    /// **声明不了自己的那一侧**的，它属于**适配层**，不属于这个接口。）</para>
    ///
    /// <para>⚠️ <b>判据是「不可逆」</b>（`01` §3.5）：`Destructive` 是最后一个被自动放行的东西。</para>
    /// </summary>
    PermissionClass PermissionClass { get; }

    /// <summary>
    /// **这个工具要不要过权限闸门**（2026-10-22）。
    ///
    /// <para>⚠️ <b>默认 <c>true</c>，而默认值的方向是「受管」</b>——与 <see cref="AsksBeforeRunning"/>
    /// 同一条理由：一个忘了表态的工具被多拦一次（可见），好过它静默地绕过闸门（不可见）。</para>
    ///
    /// <para>⚠️ <b>今天只有 `compact` 声明 `false`</b>，判据是「它不改变世界」：
    /// 它只动<b>加载边界</b>（哪些记录还给模型看），不碰工作目录、不联网、不进库；
    /// 而它<b>本来就只在用户下发的指令期内生效</b>（`IToolContext.CompactRequested`）。
    /// ⇒ 指令就是授权。再拿一个档位去拦它，只会造出「`off` 档下压缩压不了」——
    /// 而 `off` 档的语义是「别动我的东西」，不是「不许整理你自己的上下文」；
    /// 真压不了时连这次会话本身都会撑爆。</para>
    ///
    /// <para>⚠️ <b>别把它当成「这个类别是安全的」的快捷方式</b>：闸门按<b>工具</b>放行，
    /// 而不是往 <see cref="PermissionClass"/> 里加一档——加一档会让 `Gate` 那两张表的
    /// 每一行都移动边界（`Gate` 的类型注释写明了那是静默错位的来源）。</para>
    /// </summary>
    bool GatedByPermission => true;

    /// <summary>
    /// 给模型看的说明。⚠️ <b>这是唯一教它的地方</b>（`00` §2.2：能进工具描述的，
    /// 绝不进系统提示词）——所以要写清**边界**（能读什么、读不了什么），而不是写清「这是个好东西」。
    /// </summary>
    string Description { get; }

    /// <summary>
    /// **这个工具会不会问用户**（`01` §3.8）。
    ///
    /// <para>⚠️ <b>它存在的唯一理由是「先判全、再执行任何人都任何东西」</b>：
    /// 闸门的 <c>Ask</c> 是**静态可知**的（查表），而工具自己发现的问
    /// （`ask` 工具）只有跑起来才知道——那时副作用**已经发生了**。
    /// ⇒ 与 <see cref="PermissionClass"/> 同形：让**声明**先于**执行**。</para>
    ///
    /// <para>⚠️ <b>它是<b>静态声明</b>，不是「这一次会不会问」</b>：
    /// 声明 <c>true</c> 的工具**永远**不执行、只产出那一批问题（它本来就没有副作用，
    /// 否则它就不该声明这个）；声明 <c>false</c> 的工具**永远不问**。
    /// ⇒ 判据是「**它在执行前就知道要问什么吗**」，不是「它想不想问」。</para>
    ///
    /// <para>⚠️ <b>有默认值 <c>false</c>，而这是刻意与本仓「不给沉默的默认」那条相反的例外</b>：
    /// 绝大多数的工具**不问**，而「不问」在这里是**安全**的那一侧——
    /// 一个忘了声明的询问工具，后果是它照常执行（= 今天的行为），不是静默漏掉一次授权。
    /// 对比 <see cref="PermissionClass"/> 那个<b>不给默认值</b>的：那里两个方向都危险。</para>
    /// </summary>
    bool AsksBeforeRunning => false;

    /// <summary>
    /// 参数的 JSON Schema，**原文**。⚠️ 与 <see cref="Block.ToolUseBlock.Arguments"/> 同一条纪律
    /// （逐字，不经解析再序列化）——它是要**发给 provider** 的字节，规范化一下就可能不再合法。
    /// </summary>
    string ParametersSchemaJson { get; }

    /// <summary>
    /// 执行。
    ///
    /// <para>⚠️ <b>它应该是异步的，因为工具会做 I/O</b>；`ls` / `read` 今天是同步的，
    /// 返回一个已完成的 <see cref="Task"/> 即可（<c>Task.FromResult</c>）。这里不取巧写成同步 API：
    /// 签名是**插件契约**的一部分，等第一个真异步工具出现再改，等于让所有已写的工具跟着改一遍。</para>
    ///
    /// <para>⚠️ <b>失败不抛</b>——见 <see cref="ToolOutcome"/>。</para>
    /// </summary>
    Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct);
}
