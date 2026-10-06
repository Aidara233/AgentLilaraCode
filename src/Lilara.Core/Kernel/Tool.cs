namespace Lilara.Core.Kernel;

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
    /// 给模型看的说明。⚠️ <b>这是唯一教它的地方</b>（`00` §2.2：能进工具描述的，
    /// 绝不进系统提示词）——所以要写清**边界**（能读什么、读不了什么），而不是写清「这是个好东西」。
    /// </summary>
    string Description { get; }

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
