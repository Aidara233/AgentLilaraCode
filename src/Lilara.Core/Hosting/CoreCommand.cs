using Lilara.Core.Protocol;
using Lilara.Core.Kernel;

namespace Lilara.Core.Hosting;

/// <summary>
/// 一条命令。**这是「头能做什么」的类型化形状**，与文本无关——控制台把一行字解析成它，
/// 控制面把一帧 JSON 反序列化成它。两个前端走**同一个命令集**。
/// </summary>
public abstract record CoreCommand
{
    /// <summary>
    /// 配置提供商。除 <paramref name="Id"/> 外**都可空**——
    /// null 表示「本次没提到」，落盘时要**保留旧值**（整文件覆盖会抹掉手写配置）。
    ///
    /// <paramref name="ApiKey"/> 也是可空的，语义是**省略 = 保留已存的那个 key**；
    /// 而**新** provider 省略 key 必须被拒（存一个发不出去的配置比拒绝更糟）——
    /// 那条语义在 dispatcher，**别照此在别处放行**。
    /// </summary>
    public sealed record ProviderUpsert(
        string Id, string? ApiKey, string? Model, string? Protocol, string? Endpoint, string? Auth)
        : CoreCommand
    {
        /// <summary>
        /// 可选的**模型清单**（头把会话的模型画成下拉，选项从这里来）。
        ///
        /// <para>⚠️ 它是**加法式的 init 属性、不是位置参数**：`null` = 本次没提到 = **保留旧清单**，
        /// 与其余五个字段同一条语义。做成位置参数会逼着每一个构造点（含十来条既有测试）
        /// 都写一个它并不关心的 `null`，而那种「为了编译而写的参数」正是最容易写错的地方。</para>
        /// </summary>
        public IReadOnlyList<string>? Models { get; init; }
    }

    /// <summary>
    /// 删掉一个提供商定义（移除 `<数据根>/providers/&lt;id&gt;.json`）。
    /// `Id` 与 <see cref="ProviderUpsert.Id"/> 走的是同一个闸（`CommandDispatcher.IsSafeProviderId`），
    /// 而这条上更要紧：**删是不可逆的**——坏 id（`../escaped`）会让它在 `providers/` **之外**动手，
    /// 而那种删除**挽不回来**。**不存在 ⇒ 拒绝，不是幂等成功**（判据在 dispatcher，两个前端共用）。
    /// </summary>
    public sealed record ProviderDelete(string Id) : CoreCommand;

    /// <summary>
    /// 用该提供商的配置发一次**最小请求**，回答「通不通 + 为什么」。
    ///
    /// ⚠️ **它不是命令不合法那一类**（`Ack.Rejected`）——「provider 不存在 / id 不合法」才是；
    /// 「连不上 / 401 / 上游 5xx」是**业务结果**，走 `Ack.Accepted` 的 `Note`（`03` §3 的硬约束：
    /// `rejected` 只用于命令本身不合法）。否则一次网络抖动会被说成「你的命令写错了」。
    ///
    /// ⚠️ 它**不改任何状态**、也不删东西——与 <see cref="ProviderDelete"/> 只共用一个 id 闸。
    /// </summary>
    public sealed record ProviderTest(string Id) : CoreCommand;

    /// <summary>
    /// 写一份预设（`&lt;数据根&gt;/presets/&lt;id&gt;.md`）。<b>内容就是那段系统提示词</b>。
    ///
    /// <para>⚠️ <b>空 <paramref name="Content"/> 是合法且有意义的值</b>（= 纯聊天），
    /// 别把它当「没给」——它与「没提到这个字段」完全不同，而这条命令**没有别的字段**，
    /// 所以不存在 <see cref="ProviderUpsert"/> 那种「null = 保留旧值」的语义。</para>
    ///
    /// <para>⚠️ <b>它不碰任何会话</b>：改完立刻对下一次请求生效的是「会话引用它」那条路
    /// （`SessionSpec.SystemPromptPreset` 存的是引用），本条命令**只是改那个被引用的东西**。
    /// ⇒ 不需要通知任何会话、也没有会话级事件可发。</para>
    /// </summary>
    public sealed record PresetSet(string Id, string Content) : CoreCommand;

    /// <summary>
    /// 删掉一份预设。判据同 <see cref="ProviderDelete"/>（同一个 id 闸、同样
    /// **不存在 ⇒ 拒绝，不是幂等成功**）。
    ///
    /// <para>⚠️ 删掉之后引用它的会话<b>不报错</b>——渲染回落到内置模板
    /// （`SystemBlock` 的第三条路：「没配过」）。这是刻意的：预设是**引用**，
    /// 而引用的目标可以消失；拒绝删除会逼用户先去改每个会话，那是把「删一个文件」
    /// 变成一次跨会话事务。</para>
    /// </summary>
    public sealed record PresetDelete(string Id) : CoreCommand;

    /// <summary>
    /// 列出有哪些预设。⚠️ **它走线**（不像 `provider list` 是控制台本地查询）：
    /// 头要做下拉，而头**只有一个入口**（命令 + 快照两层），没有第二套本地查询通道。
    ///
    /// <para>结果走 <c>Ack.Accepted.Note</c>——与 `provider.test` 同一条用法
    /// （「业务结果」放 Note，头不该依赖它，但控制台与头都看得见）。</para>
    /// </summary>
    public sealed record PresetList : CoreCommand;

    /// <summary>
    /// 建一个会话。**显式动作**——核心不替你决定该有个会话（`01` §8.1）。
    ///
    /// <para>名字 `Cwd` 取自设计（`03` §3 的 `session.create(projectKey?, preset?, cwd?)`）：
    /// 它就是新会话的**工作目录**，而工作目录决定它落在哪个项目
    /// （`ProjectKey.FromPath` 是纯函数）——所以「选项目」与「选工作目录」是同一件事。</para>
    ///
    /// <para>⚠️ <b>只有一条来路：目录。不给 ⇒ 回落核心的工作目录</b>（控制台 `session create`
    /// 一直就是这个行为，别改掉它）。</para>
    ///
    /// <para>⚠️ <b>为什么没有「按 projectKey 建」那一半</b>：**项目键单独一个字段是没法用的**——
    /// 键是从路径推出来的，反向推不回去；盘上唯一记着目录的地方是**会话的** `spec.json`，
    /// 而一个项目可能一个会话都不剩（全删了）。那样的命令会在<i>有时候</i>可用、
    /// 其余时候报一句用户无从补救的错。头那边本来就有路径（从会话 spec 推的），
    /// 直接交路径，两边都简单。</para>
    ///
    /// <para>⚠️ **没有「按项目键建」那一半**，理由见 <see cref="SessionCreate.Cwd"/> 的注释。</para>
    ///
    /// <para>⚠️ <b><see cref="Preset"/> 是「出生时施加一次」</b>（`01` §7.1）：它只在建会话这一刻
    /// 决定用哪份系统提示词，此后那个值住在 <c>spec.json</c> 里，要改走
    /// <see cref="SessionConfigure"/>。<b>预设不是第二个能改会话的地方</b>——
    /// 两个变更入口迟早会漂，这是本仓栽熟的形状。</para>
    /// </summary>
    public sealed record SessionCreate : CoreCommand
    {
        /// <summary>
        /// 新会话的工作目录（绝对或相对，核心会规范化）。
        /// **不存在 ⇒ 拒绝，核心不替你建目录**（建目录是策略，`00` §1.3）。
        /// </summary>
        public string? Cwd { get; init; }

        /// <summary>会话名，可选（可人为空 ⇒ 存 null，整个键不落盘）。</summary>
        public string? Title { get; init; }

        /// <summary>
        /// 用哪份系统提示词（`presets/&lt;id&gt;.md`）。**可选**：不给 ⇒ 会话存 `null`，
        /// 渲染时跟随全局默认（`CorePrompts.DefaultPreset`）。
        ///
        /// <para>⚠️ <b>这里不校验「那个预设文件存不存在」</b>——与 <see cref="PresetSet"/> 刻意不同。
        /// 文件不在不是错误：渲染会回落到内置模板（`SystemBlock` 那三条路的第二条是「没配过」）。
        /// 若在这里拒绝，「先建会话、以后再写预设」这条完全正常的路会被堵死，
        /// 而它恰恰是最常见的那条（用户还没写呢）。</para>
        /// </summary>
        public string? Preset { get; init; }

        /// <summary>
        /// **一次询问最多等多久**（分钟，`01` §3.8）。不给 ⇒ `null` = **不超时**（默认）。
        ///
        /// <para>⚠️ <b>给的是分钟而不是 <c>TimeSpan</c></b>：线上是 JSON、控制台是命令行，
        /// 两边都得有一个**人写得出来**的形态。`TimeSpan` 的线上形状（`"00:05:00"`）
        /// 对两个入口都是负担，而「等多久」这件事的常用单位就是分钟。</para>
        ///
        /// <para>⚠️ <b>必须 &gt; 0</b>：<see cref="SessionSpec.AskTimeout"/> 的注释明写
        /// <c>TimeSpan.Zero</c> 非法（「等 0 分钟」读不出意思）。
        /// ⇒ 0 或负数在这里就 rejected，**不让它悄悄等于「立刻超时」**
        /// ——那会让一次写错的参数看起来像「用户故意不让我等」。</para>
        /// </summary>
        public double? AskTimeoutMinutes { get; init; }
    }

    public sealed record SessionActivate(string SessionId) : CoreCommand;

    /// <summary>用户消息。**是块列表**不是字符串——附件就是这么进来的。</summary>
    public sealed record SessionSend(string SessionId, IReadOnlyList<Block> Blocks) : CoreCommand;

    public sealed record SessionStart(string SessionId) : CoreCommand;

    public sealed record SessionInterrupt(string SessionId) : CoreCommand;

    /// <summary>
    /// 设置一条行为控制轴。取值校验在 dispatcher（**线上也要吃**），控制台只做文本拆词。
    /// </summary>
    public sealed record SessionSetAxis(string SessionId, string Axis, string Value) : CoreCommand;

    /// <summary>
    /// 改会话的模型选择：换绑供应商 / 换模型。
    ///
    /// <para>⚠️ **两个字段都可空，null = 「本次没提到」= 保留旧值**——与
    /// <see cref="ProviderUpsert"/> 同一条语义（那两个前端各发各的，不该被逼着回传全量）。
    /// 两个都是 null ⇒ **拒绝**（「没说要改什么」不是一次成功的 no-op）。</para>
    ///
    /// <para>⚠️ **为什么不给 `SessionCreate` 加参数**：改绑是新会话与老会话**共用**的动作。
    /// 让 `create` 也收 provider 就会产生**第二条设置模型的路**，两条路 = 两套默认值处理 = 迟早分叉。
    /// 头的建会话流程因此是 `create` → 立刻 `configure`。</para>
    ///
    /// <para>⚠️ **不是「命令不合法」的那一类**，除非：会话不存在 / 供应商不存在 / 两个参数都没给。
    /// 「换过去了但那个供应商连不上」是**业务结果**，走 `error` 事件。</para>
    /// </summary>
    public sealed record SessionConfigure(
        string SessionId, string? ProviderId, string? ModelId, double? AskTimeoutMinutes = null)
        : CoreCommand;

    /// <summary>
    /// 把会话**摘出内存**。⚠️ **磁盘上什么都没变**——这正是它与
    /// <see cref="SessionArchive"/> 的分界：那是持久事实，这只是这份进程的内存账。
    ///
    /// <para>⚠️ **在跑 ⇒ 拒绝**，不是「原地掐断」：`Stepper` 的收尾会把 `state.json`
    /// 写回去，而目录一旦在它眼里「不该存在」，写回就是**半删状态**的制造机。
    /// 三条销毁命令共用这条判据。</para>
    ///
    /// <para>⚠️ **它切出一个读盘窗口**：会话被摘出去之后，一个**还在跑**的会话
    /// （头那一侧看起来还是「在跑」）若被重拉快照，会落到磁盘那份 **`Idle`**
    /// ⇒ 用户以为没在跑、再点一次发送 ⇒ 队列两条 ⇒ 烧钱。
    /// ⇒ 快照端点在激活表 miss 时**必须回落 `Activate`**（`ControlServer.TryFind` 与
    /// `SessionQueries.StateOf` 的调用方都写了这一条），不许直接交磁盘那份。</para>
    /// </summary>
    public sealed record SessionDeactivate(string SessionId) : CoreCommand;

    /// <summary>
    /// 归档 / 取消归档。<see cref="Archived"/> 是 **`bool?`**：
    /// <c>null</c> = 归档，<c>false</c> = **取消归档**。
    ///
    /// <para>⚠️ **不许把它写成 `bool Archived = false` 那种带默认值的形态。** 那样
    /// 每一个「只写了 `SessionArchive(id)`」的调用点会**静默变成取消归档**——
    /// 而这条命令没有别的测试拦得住（默认值让两种意图在类型上长得一样）。
    /// 三态是刻意的：唯一能撤销归档的方式就是重跑同一条命令。</para>
    ///
    /// <para>⚠️ **归档不是销毁**：目录不动、记录一条不少、`ListSessions` **照列**它
    /// （过滤在显示层：头折叠、控制台不折叠）。所以它**不释放声明**——
    /// 释放一条不是自己占的锁是越权，而 `session.delete` 才做，因为那时占用者**已经不存在了**。</para>
    /// </summary>
    public sealed record SessionArchive(string SessionId, bool? Archived = null) : CoreCommand;

    /// <summary>
    /// **删掉会话**——目录、记录、状态一起。**不可逆**：`history.jsonl` 是只追加永不重写的，
    /// 这一刀之后记录就没有了。
    ///
    /// <para>⚠️ **在跑 ⇒ 拒绝**（见 <see cref="SessionDeactivate"/> 的理由）。</para>
    ///
    /// <para>⚠️ 该会话占的**声明会被一并释放**（在别的会话的工作目录里也可能有），
    /// 回执里**点名**释放了哪几条。理由：模型失控、占着一堆锁的时候，
    /// 恰恰是最想删掉那个会话的时候——「有锁就拒」会让这条命令在最需要它的时候用不了。</para>
    ///
    /// <para>⚠️ 前端那个「键入 id 才放行」的确认是**防误点**，不是并发屏障：
    /// 真正的屏障是核心这边「检查 + 删」在同一个调用里（两个核心共享数据根时，
    /// 前端在离得远的地方做的检查挡不住互删）。</para>
    /// </summary>
    public sealed record SessionDelete(string SessionId) : CoreCommand;

    /// <summary>
    /// **回收一次**（`01` §8.0.2 / §11）：重算这个会话里哪些工具结果该降级，然后换渲染。
    ///
    /// <para>⚠️ <b>它是触发源，不是回收本身</b>——回收的判定在 `Reclaim.Decide`，
    /// 而今天**唯一的触发源就是这个手动命令**（用户裁定「现在暂且手动触发，后续再加自动」）。
    /// 这与 `00` §0 的宪法同向：回收改的是前缀**中段** ⇒ 每触发一次破一次缓存
    /// ⇒ 窗口本来就不该自动开。</para>
    ///
    /// <para>⚠️ <b>磁盘一字不动</b>（§8.0.2 一）：这个命令只换内存里那份回收集合，
    /// 记录、库、`state.json` 全都不写。⇒ 「回收」这个词在这里的准确含义是
    /// <b>「把字节换成指针」</b>，不是删除。</para>
    ///
    /// <para>⚠️ **全会话，没有范围参数**：§12.4 的 `release` 才是**作用域化**的那个
    /// （按声明树），而它依赖声明系统。今天没有范围概念 ⇒ 全会话。</para>
    /// </summary>
    public sealed record SessionGc(string SessionId) : CoreCommand;

    /// <summary>占用一片区域。路径可以是相对的，按会话的工作目录解析。</summary>
    /// <param name="Scope">
    /// **名字与形状都照协议**：release 的形参写作 `scope: [...]` ⇒ 它是**列表**。
    /// 叫 `Path` 会与协议不符（头的客户端按 `scope` 发帧 ⇒ 「命令形状不对」）。
    /// </param>
    public sealed record ClaimAcquire(string SessionId, IReadOnlyList<string> Scope) : CoreCommand;

    /// <summary>释放。**这是「模型失控时的逃生口」**——所以它**不检查**占用者是不是自己。</summary>
    /// <param name="Scope">同 <see cref="ClaimAcquire"/>：列表、名字照协议。</param>
    public sealed record ClaimRelease(string SessionId, IReadOnlyList<string> Scope) : CoreCommand;

    /// <summary>
    /// **回答一次询问**（`01` §3.8）。
    ///
    /// <para>⚠️ <b>它是挂起会话的<b>唯一</b>解除入口</b>——`session.send` 撞上
    /// <see cref="Kernel.SessionStatus.WaitingForInput"/> 时**拒绝**（理由：两条路都能解除
    /// ⇒ 判据漂）。「用户直接打字说别的」那条（`Superseded`）走的是**另一条**路：
    /// 它**先**把那次询问合成成一条结果，**再**当普通消息处理。</para>
    /// </summary>
    /// <param name="AskId">
    /// 哪一次询问（`AskRequest.Id`）。⚠️ <b>不是 `CallId`</b>——那是协议 id，
    /// 而这一个是核心的稳定 id（`01` §8.0.2 第七节的 id 分工）。
    /// </param>
    /// <param name="Outcome">
    /// `"submit"` = 作答 · `"cancel"` = 作废这次询问 · `"modify"` = 改了参数但**先不批**、
    /// 重新问一次。⚠️ 线上只认这三个字面量（同 `session.archive` 那条 `OptBool` 的取舍）。
    /// </param>
    /// <param name="Answers">
    /// 按问题 id 的答案。⚠️ `submit` 时**允许缺项**（用户可以不答某个问题）——
    /// 缺的那些会被如实写成「未回答」，而不是编一个空值（那是撒谎）。
    /// </param>
    /// <param name="Remember">
    /// 记忆档位（`AskMemory` 的名字）。<c>null</c> = **只这一次**。⚠️ 只对
    /// <see cref="Kernel.AskKind.Permission"/> 有意义——`Question` 的问**没有**记忆。
    /// </param>
    public sealed record ApprovalRespond(
        string SessionId,
        string AskId,
        string Outcome,
        IReadOnlyDictionary<string, string> Answers,
        string? Remember = null) : CoreCommand;
}

/// <summary>
/// 回执。**只有两类**。
/// </summary>
public abstract record Ack
{
    public required string CommandId { get; init; }

    /// <summary>分配型命令（`session.create`）**必须**回传新身份，否则后续命令无从指涉。</summary>
    public sealed record Accepted : Ack
    {
        public string? SessionId { get; init; }

        /// <summary>
        /// 给**人**看的一句补充（如保存后的协议名）。
        /// **它不是协议的一部分**——头不该依赖它，它只是省得前端重读一遍存储。
        /// </summary>
        public string? Note { get; init; }
    }

    /// <summary>
    /// **只用于「命令本身不合法」**：会话不存在、协议名未知、轴取值非法。
    /// **不用于业务结果**——「提供商没了」「模型 400」「上游 503」一个都不能走这里，
    /// 它们是**事件的失败**（`error` 事件），不是**命令的失败**。搞混了的话，
    /// 头会把一次网络抖动当成「你的命令写错了」。
    /// </summary>
    public sealed record Rejected : Ack
    {
        public required string Reason { get; init; }
    }
}

/// <summary>
/// 一次分派的产物。
/// <paramref name="Reply"/> 只在**同步完成模式**下非空（控制台用）——控制台没有视图，
/// 因此允许它「本地退化成同步拿回执」。控制面走异步模式：立刻拿到 <see cref="Ack"/>，
/// 模型回话经**事件**流出。
/// </summary>
public sealed record DispatchResult(Ack Ack, ModelReply? Reply = null)
{
    public bool IsRejected => Ack is Ack.Rejected;
}
