using Lilara.Core.Kernel;

namespace Lilara.Core.Session.Tools;

/// <summary>
/// **核心自带的工具表**——装配点（`00` §1.3 机制与策略分离）。
///
/// <para>⚠️ <b>这与「核心自带零个工具」矛盾吗？不矛盾，但需要说清楚</b>
/// （`04` §1.3 那句话要拆成两半读）：那一句说的是<b>核心不提供「内容」</b>
/// ——`read` / `write` / `bash` 这类**干活的能力**全部来自插件。
/// 而这里的三个属于**机制**：`ls` / `read` 是「模型能看见自己的工作目录」的入口，
/// `fetch` 是「模型能看见**自己历史**」的入口——两条路径都是核心的加载 / 渲染机制本身。</para>
///
/// <para>⚠️ <b>三个挤在同一份列表里，而这不是偶然</b>：`fetch` 的取回能力来自
/// **句柄里的数据根**（`04` §5.1），与别的工具走同一条路——它<b>不是核心特权</b>
/// （`01` §8.0.2 六，用户裁定）。所以它照样住在这里，而不是被捧进核心。</para>
///
/// <para>⚠️ <b>「内建」是一条<b>属性</b>（不可卸载），不是第三种来源</b>
/// ——三种来源只有：内建 / 插件 / MCP。判据同 `04` §1.2 给协议适配器的那条：
/// 做成可卸载的插件，等于把「能跑」变成可选项。</para>
///
/// <para>⚠️ <b>它今天是个静态列表，而这是刻意的</b>：工具表是**派生**的
/// （`04` §1.3），插件宿主还不存在，所以派生源只有这一个。
/// 插件落地时这里会变成「内建 ⊕ 插件贡献」，而<b>调用方不用改</b>——
/// 它们拿到的始终是一份有序的 <see cref="ITool"/> 列表。</para>
///
/// <para>⚠️ <b>顺序必须确定</b>（工具表在 Claude 协议下位于前缀最前端，
/// `00` §7 判据 5）：它是一份**字面量数组**，不经过任何容器枚举。</para>
///
/// <para>⚠️ <b>两个写类内建（`update` / `write`，2026-10-11）</b>——在它们之前四个全是只读，
/// 于是 `Gate` 那张表的「问」那两格**从来没有生产者**（只读在所有档位都自动放行）。
/// ⇒ `01` §3.5 的 `| 标准(3) × 写入 = 问 |` 那一格**第一次在生产里可达**。</para>
///
/// <para>⚠️ **两个都仍然属于「机制」那一侧**，判据同上面那条拆读：它们贡献的不是
/// 「干活的能力」（跑命令、装包、连数据库——那些全在插件那侧），而是
/// **「模型能读写自己的工作目录」这条机制的入口**，与 `ls` / `read` 给的是同一类东西：
/// 合起来才构成「这个核心**能操作**一个工作目录」这件最小的事。</para>
///
/// <para>⚠️ <b>为什么是两个而不是一个</b>：`write` 整份写，`update` 定点改。
/// 判据是**模型该用哪个**——改一个已存在的文件时，整份写要求它把全文写对，
/// 而漏一行是**静默**的；定点改动只碰它点到的那段。
/// ⇒ 两个都留着，靠 `Description` 各自说清适用面（`00` §1.3：谁的知识谁负责）。</para>
///
/// <para>⚠️ <b>`release`（2026-10-11）属于<b>同一条判据的另一半</b>，而且是这一列里最纯的一个</b>：
/// 它连文件都不碰——它做的是**说出一句话**（「这批资源我用完了」），
/// 而核心把这句话记成一条 <see cref="Kernel.TimelineEvent.ReleaseEvent"/>（线）。
/// ⇒ 它是 `01` §12.4「按任务边界回收」的**唯一生产者**：在它之前那一整节是纸上的
/// （线上的 `claim.release` 只改 `ClaimStore`、**时间线一个字不写** ⇒ 事后没有锚可倒查）。
/// ⚠️ 与 `update` / `write` 的分工：那两个**动工作目录**，这一个**动上下文**——
/// 而两者都是「核心的加载 / 渲染机制的入口」，同 `fetch` 给的是「模型能看见自己历史」。</para>
///
/// <para><see cref="SystemInfoTool"/> 是只读的按需设备快照：报告 CPU、内存、磁盘和可用时的 NVIDIA GPU 状态，不持续采样。</para>
///
/// <para>⚠️ <b><see cref="SearchTool"/>（2026-10-22）是「模型能看见自己的工作目录」的第三个入口</b>：
/// `ls` 回答「这一层有什么」，`read` 回答「这一个是什么」，`search` 回答「这一片里哪里有它」。
/// 它只读、不走网络，判据与 `ls` / `read` 同一条——是**机制**，不是插件提供的「干活能力」。</para>
///
/// <para>⚠️ <b><see cref="FloatingContextTool"/>（2026-10-17）是这一列里唯一一个「核心自己每步都会合成调用它」的工具</b>：
/// 浮动块不再拼进 user 消息，而是由 <c>Stepper.FloatingContextPair</c> 在时间线末尾
/// 合成一对 <c>tool_use</c> + <c>tool_result</c>——那条合成的调用引用的就是这个**声明过的名字**。
/// ⚠️ 合成本身**不经过执行器**；本表里的实例只在模型**自己**真调一次时执行（兜底）。
/// 它仍然算「机制」：开的是「模型能看见核心每步注入的那块状态」这扇门。</para>
///
/// <para>⚠️ <b><see cref="CompactTool"/>（2026-10-22）是「核心特权工具」那一族的第一个实例</b>
/// （`01` §11.2 第 1 条：「压缩工具是核心特权工具，插件不能实现——它改变加载边界」）：
/// 它只说出「用这段摘要截断此前的历史」，而**真正的截断**（检查点 / 清指令 / 开窗）在推进器里
/// ——同 `release` 的「线落盘在推进器」与 `finish_goal` 的「拨轴在推进器」。
/// ⚠️ 它是这几个里唯一**不归闸门管**的（`ITool.GatedByPermission == false`）：它不改变世界，
/// 而它只在用户下发的指令期内生效（`pendingCompact`）——指令就是授权。</para>
///
/// <para>⚠️ <b><see cref="ArchiveChildTool"/>（2026-10-26）是子 agent 那一族的第五个</b>：
/// 它把「派生关系能被收起」这扇门打开——一个已经落定、结论也收下的子会话**不必**每步
/// 都在尾块里占一行。⚠️ 收起是**可逆标记**（`SessionSpec.ArchivedAt`），不是删除：
/// 记录一条不少，控制台 `session archive &lt;id&gt; --unarchive` 就把它放回来。
/// ⇒ 它与 `release` 同一条判据：工具只说一句话，落地在核心。</para>
/// </summary>
public static class BuiltInTools
{
    public static IReadOnlyList<ITool> All { get; } =
    [
        new ListTool(), new ReadTool(), new SearchTool(), new FetchTool(), new AskTool(),
        new UpdateTool(), new WriteTool(), new ReleaseTool(),
        new MakeDirectoryTool(), new DeleteTool(), new PathTool(), new SystemInfoTool(), new GitReadTool(), new GitWriteTool(),
        new DotnetBuildTool(), new DotnetTestTool(), new FloatingContextTool(),
        // 2026-10-23：shell。⚠️ 它是**高危**类（与 `delete` 同级）——在工作目录里跑任意命令，
        // 后果核心事后挽不回来；边界只有权限轴、设备开关、超时与输出上限。
        // ⚠️ 它常驻：找不到 sh 时也照样声明（工具表不动），调用只回一句人话。
        new ShellTool(),
        // 2026-10-22：联网与 goal 收尾。三个都常驻——配置和轴值**不进工具声明**，
        // 因此拨轴 / 改配置不会破前缀；关着时它们只回一句人话。
        new WebSearchTool(), new WebFetchTool(), new FinishGoalTool(),
        // 2026-10-22：压缩从「核心自己跑一次模型调用」改成工具。常驻的理由与上面同源
        // （工具表不动、指令走尾块），而它只在用户下发过指令时才生效。
        new CompactTool(), new PlanTool(), new AdvancePlanTool(), new PlanModeTool(),
        // 2026-10-24：子 agent 的一族。四个都是**机制入口**（开的是「会话能被派生 /
        // 能被对话」这几扇门），不是「干活的能力」——判据同上面那条拆读。
        // ⚠️ `tell_parent` 与 `subagent_report` 在顶层会话里会回一句「你没有父会话」/
        // 「没有待读的回报」：**工具表不动**（它属于前缀最前端，改它最贵），
        // 关着时只说人话——与 web_search / finish_goal 同一条做法。
        // 2026-10-26：`archive_child` 进这一族——它开的是「派生关系能被收起」这扇门
        // （可逆标记，`SessionSpec.ArchivedAt`），与 `release` 画线的判据同源。
        new SpawnTool(), new TellChildTool(), new TellParentTool(), new SubagentReportTool(),
        new ArchiveChildTool(),
    ];
}
