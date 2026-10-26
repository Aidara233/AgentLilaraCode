using Lilara.Core.Kernel;
using Lilara.Core.Storage;

namespace Lilara.Core.Session.Tools;

/// <summary>
/// 工具能碰到的东西——<see cref="IToolContext"/> 的实现（`04` §5.1）。
///
/// <para>⚠️ <b>它是只读的，而且这件事由类型保证</b>：类上只有读取方法。
/// 判据是「工具不该有写盘的手」，不是「工具作者应该自觉」。</para>
/// </summary>
/// <param name="workingDirectory">会话的工作目录。⚠️ **必须是绝对路径**——
/// 相对路径会让 <see cref="ResolvePath"/> 的判据依赖进程的当前目录，而那是全局可变状态
/// （`SessionHandle.Create` 已保证 spec 里存的是绝对路径）。</param>
/// <param name="blobs">库。读用（`fetch` 靠它按地址取字节）。
/// ⚠️ <b>「工具没有写库的手」这句在 2026-10-11 之后要读得准一点</b>：工具仍然**拿不到**
/// 这个对象，而 <see cref="EnsureStored"/> 给了它一支**专用笔**（存进去、换回一个地址）——
/// 那是 `update` 保住旧版本用的。本条不是「工具不能写盘」，是「工具不该自己决定快照存哪儿」。</param>
/// <param name="timeline">**记录**那一份时间线（不是渲染后的形态）。
/// ⚠️ 取地址必须从**记录**取：回收只换渲染、记录不动（`01` §8.0.2 一 / 八）——
/// 若拿渲染后的形态去算地址，被回收的那条算出来就是**那段注记**的哈希，`fetch` 当场永久取不回。</param>
/// <param name="floatingContext">**这一刻的尾块文本**（`01` §3.2，2026-10-17）。
/// 由建设点（`Stepper` 两处 / 控制台的待答询问探测）交进来，**不是**在这里现渲染：
/// 现渲染会让 `floating_context` 的返回值与核心合成注入的那份分叉。
/// 省略 = 空串（没有浮动块，或调用方确实不需要它）。</param>
public sealed class SessionToolContext(
    string workingDirectory, BlobStore blobs, IReadOnlyList<TimelineEvent>? timeline = null,
    string? floatingContext = null, NetworkAccess? network = null, bool goalActive = false,
    bool compactRequested = false, ShellAccess? shell = null, SubagentScope? subagents = null,
    SessionPlan? plan = null, string? mode = null, IReadOnlyList<string>? approvedCallIds = null,
    string? initiative = null)
    : IToolContext
{
    private readonly string _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));
    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public string WorkingDirectory => _root;

    /// <summary>
    /// ⚠️ <b>这个闸不是可选的</b>：模型给的路径是**不可信输入**，
    /// 而 <c>../../../../etc/passwd</c> 一类会让工具读出工作目录之外的东西。
    ///
    /// <para>判据是「**解析出来的绝对路径仍在工作目录之下**」——**形状校验**，
    /// 与 <c>BlobStore.IsSha256</c> / <c>CommandDispatcher.IsSafeProviderId</c> 同一条纪律。
    /// 先 <see cref="Path.GetFullPath(string)"/> 把 <c>..</c> 与混合分隔符**解析掉再判**，
    /// 而不是去数输入里有几个 <c>..</c>（那正是「像不像」的写法，绕得过去）。</para>
    ///
    /// <para>⚠️ <b>绝对路径由同一条判据挡掉，所以这里<b>没有</b>单独一句 `IsPathRooted`</b>
    /// （2026-10-06 实测的结果，不是省了）：<c>Path.Combine(root, "C:\Windows")</c> 返回的是
    /// **绝对的那个**，<c>GetFullPath</c> 之后包含判定照样失败 ⇒ 照样拒。
    /// 这里原先有一句 <c>IsPathRooted</c>，而它的注释声称「放行它等于让判据在越界输入上做算术」——
    /// **那句断言是假的**：摘掉它，<c>ToolTests</c> 882 条全绿。
    /// ⇒ 两句谓词管同一件事就是「两层判据」的漂移温床（本仓栽过），删掉多的那句。</para>
    ///
    /// <para>⚠️ <b>比较规则按平台选择</b>：Windows 大小写不敏感，Unix 大小写敏感。⚠️ <b>它拦不住符号链接 / 目录联接</b>
    /// ——路径只做词法规范化，链接目标未解析。</para>
    /// </summary>
    public string? ResolvePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;

        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(_root, relativePath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // 形状坏到算不出来 ⇒ 与「越界」同一条处理（都是「不给」）。
            return null;
        }

        // 判据：`full` 要么**就是**根，要么在「根 + 分隔符」之下。
        // ⚠️ 后半句那个分隔符不能省：不加的话 `C:\work-evil` 会被判成在 `C:\work` 之下。
        return full.Equals(_root, PathComparison)
               || full.StartsWith(Path.EndsInDirectorySeparator(_root) ? _root : _root + Path.DirectorySeparatorChar, PathComparison)
            ? full
            : null;
    }

    public byte[]? ReadBlob(string sha256) => blobs.TryGet(sha256);

    /// <summary>
    /// 存进库、交回地址。⚠️ <b>见 <see cref="IToolContext.EnsureStored"/> 的注释</b>：
    /// 这是本类**唯一**会改到东西的方法，而它改的**不是用户的文件**、是库，
    /// 且内容寻址 ⇒ 幂等 ⇒ 存两次不占两份空间。
    ///
    /// <para>⚠️ 本类顶上那句「**它是只读的，而且这件事由类型保证**」说的是
    /// **工作目录**（工具不该有改盘的手）——那条**没有被这条方法削弱**：
    /// 工具仍然拿不到 <c>BlobStore</c>，它只能把一个 byte[] 交出去换一个地址回来。
    /// 真正改盘的那一下（`update` 的 `AtomicFile.Write`）住在工具自己身上，
    /// 而它拿到的路径来自 <see cref="ResolvePath"/> 那**唯一**一道越界闸。</para>
    /// </summary>
    public string EnsureStored(byte[] content) => blobs.Put(content);

    /// <summary>
    /// 按记录的稳定 id 取回**当时那串块**。
    ///
    /// <para>⚠️ <b>走的是 <see cref="TimelineRender.Dereference"/>（`internal`），
    /// 不是这里另写一遍</b>：库对象有<b>两种</b>存法（原文 / 块数组 JSON），
    /// 读的人必须分得开——另写一遍就会漂，而漂的后果是 `read` 的内容被当 JSON 交给模型。</para>
    ///
    /// <para>⚠️ <b>算地址用的是<b>记录</b>那一份</b>（那个理由写在构造参数的注释里）。</para>
    /// </summary>
    public IReadOnlyList<Block>? SnapshotOf(string stableId)
    {
        if (timeline?.OfType<TimelineEvent.ToolResultEvent>().FirstOrDefault(r => r.Id == stableId)
            is not { } r) return null;

        return TimelineRender.Dereference(r, blobs).Content;
    }

    /// <summary>
    /// 按稳定 id 取回一条结果的**原始字节**——`fetch` 的分页窗口读它。
    ///
    /// <para>⚠️ <b>只有「单文本块」的结果有原始字节</b>（`TextBlock` 或指向 `text/plain` 的
    /// <see cref="Block.StoreBlock"/>）：混排 / 图片不是一个字节序列，给它们编一个偏移语义
    /// 就是发明第三种形状。⇒ 其余形态返回 <c>null</c>，由工具如实说「这条例结果不能按字节窗口取」。</para>
    ///
    /// <para>⚠️ <b>和 <see cref="SnapshotOf"/> 同源</b>：都从**记录**找、都走
    /// `TimelineRender.Dereference` 那套库对象读法——各写一份就会漂。</para>
    /// </summary>
    public byte[]? RawBytesOf(string stableId)
    {
        if (timeline?.OfType<TimelineEvent.ToolResultEvent>().FirstOrDefault(r => r.Id == stableId)
            is not { } r) return null;

        var content = TimelineRender.Dereference(r, blobs).Content;
        return content is [Block.TextBlock text]
            ? System.Text.Encoding.UTF8.GetBytes(text.Text)
            : null;
    }

    /// <summary>见 <see cref="IToolContext.FloatingContext"/>：建设点冻住的那份，由构造参数交进来。</summary>
    public string FloatingContext { get; } = floatingContext ?? "";

    /// <summary>见 <see cref="IToolContext.Network"/>：步开始时冻住的那份配置 + 轴值快照。</summary>
    public NetworkAccess? Network { get; } = network;

    /// <summary>见 <see cref="IToolContext.Shell"/>：步开始时冻住的那份 `shell.json` 快照。</summary>
    public ShellAccess? Shell { get; } = shell;

    /// <summary>见 <see cref="IToolContext.Subagents"/>：步开始时算好的派生关系快照。</summary>
    public SubagentScope? Subagents { get; } = subagents;

    /// <summary>见 <see cref="IToolContext.GoalActive"/>：步开始时的主动性轴快照。</summary>
    public bool GoalActive { get; } = goalActive;

    /// <summary>
    /// 见 <see cref="IToolContext.CompactRequested"/>：步开始时 `state.json` 里那条压缩指令的快照。
    /// ⚠️ **冻结在步开始**，与权限档位 / 联网配置同一条：一步之内两条 `compact` 调用必须按同一份指令判。
    /// </summary>
    public bool CompactRequested { get; } = compactRequested;

    /// <summary>
    /// 步开始时的计划树快照（2026-10-24）。⚠️ <b>它存在，是为了让 `plan` 的校验与落地是同一套判据</b>：
    /// 工具没有这棵树就只能「形状校验」——`insert` 的 `parentId` 存不存在它答不上来，
    /// 于是工具说「已登记」而落地时找不到父节点，改动**静默消失**。
    /// ⚠️ 冻结在步开始（同权限档位 / 联网）：一步之内多条 `plan` 调用按同一棵树判，
    /// 而它们**按调用序**依次生效（见 `Stepper.ApplyPlanCalls`）。
    /// </summary>
    public SessionPlan? Plan { get; } = plan;

    /// <summary>
    /// 步开始时的**工作模式**轴值（`plan` / `act`，2026-10-24）。⚠️ 冻结在步开始，同权限档位。
    /// <para>两个读者：`switch_plan_mode`（要不要真的切）与计划模式的硬闸（拦不拦写文件的工具）。</para>
    /// </summary>
    public string? Mode { get; } = mode;

    /// <summary>
    /// **这一次调用是否已被用户批准**（只对 `AskKind.Permission` 那条回复成立）。
    ///
    /// <para>⚠️ 它存在的理由很具体：`switch_plan_mode` 请求「退出规划模式」时会挂起、等用户点头；
    /// 批准之后这一次调用会**再跑一遍**（`ToolExecutor` 只在 `Question` 那条上跳过工具，
    /// `Permission` 那条的「批准」意思就是「让它跑」）。⇒ 工具必须能分辨
    /// 「第一次跑（要发问）」与「批准之后跑（要落地）」——否则它会对同一件事再发一次问，
    /// 会话就永远在等一个已经答过的问题。</para>
    /// </summary>
    public IReadOnlyList<string>? ApprovedCallIds { get; } = approvedCallIds;

    /// <summary>步开始时冻结的主动性档（包括 <c>iterate</c>）。</summary>
    public string? Initiative { get; } = initiative;

    /// <summary>这一次调用是否已经被用户批准过（见 <see cref="ApprovedCallIds"/>）。</summary>
    public bool IsApproved(string callId) =>
        ApprovedCallIds is { } ids && ids.Any(id => string.Equals(id, callId, StringComparison.Ordinal));
}
