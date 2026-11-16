namespace Lilara.Core.Kernel;

/// <summary>
/// **一个后台任务的状态**（2026-10-24，通用后台任务批改的名）。
///
/// <para>⚠️ <b>落盘与线上都用字符串字面量</b>（同 <see cref="SessionStatus"/>）：
/// 它是 `job.json` 的一部分，而那份文件要能被别的版本读。</para>
///
/// <para>⚠️ <b>四格的分界是「谁结束了它」</b>，不是「成功 / 失败」：</para>
/// <list type="bullet">
///   <item><see cref="Running"/>：进程还活着。⚠️ 重启之后**不会再出现**这一格——见 <see cref="Lost"/>。</item>
///   <item><see cref="Exited"/>：进程自己退出了（退出码在 <see cref="BackgroundJobInfo.ExitCode"/>）。</item>
///   <item><see cref="Killed"/>：被杀 / 超时被终止。谁杀的、为什么，写在 <see cref="BackgroundJobInfo.Reason"/>。</item>
///   <item><see cref="Lost"/>：**核心重启了，结局未知**。它既不是成功也不是失败——
///   那个进程后来怎么样了，我们确实不知道。</item>
/// </list>
/// </summary>
public static class BackgroundJobState
{
    public const string Running = "Running";
    public const string Exited = "Exited";
    public const string Killed = "Killed";
    public const string Lost = "Lost";
}

/// <summary>
/// **一个后台任务的全部事实**。
///
/// <para>⚠️ <b>它<b>不是</b>一条时间线记录</b>（`TimelineEvent` 没有它）：后台任务的输出是
/// **会追加的**，而时间线的形状是「一条事实、落盘之后不动」。要把一份不断增长的输出塞进
/// 时间线，只能靠「每步追加一条」——那会把每一步的上下文都撑一遍。
/// ⇒ 记录里只有**那一次工具调用的结果**（「已启动，id=e5f6」），活在盘上的这一份是**任务本体**。</para>
///
/// <para>⚠️ <b>它落在 <c>&lt;数据根&gt;/jobs/&lt;sessionId&gt;/&lt;jobId&gt;.json</c></b>，
/// 与输出文件 <c>&lt;jobId&gt;.log</c> 并排。⚠️ 这不是「又一处状态」：它是**进程事实的唯一副本**
/// ——进程句柄活在内存里，而内存会随核心一起没。重启之后，「这里曾经有一个任务」
/// 只能从盘上读回来（那时它一定是 <see cref="BackgroundJobState.Lost"/>）。</para>
///
/// <para>⚠️ <b>它<b>刻意</b>不认识「shell」这个词</b>（2026-10-24 用户裁定「整合起来，
/// 其余的可异步指令也一样」）：能后台跑的不止 `sh`——`dotnet_build` / `dotnet_test`
/// 同样是长任务，而它们**不该各自再发明一套任务表**。
/// ⇒ 种类由 <see cref="Kind"/> 这个**数据**回答，宿主因此不认识任何一种具体工具。</para>
/// </summary>
public sealed record BackgroundJobInfo
{
    /// <summary>格式版本。⚠️ 语义同 <see cref="SessionSpec.Version"/>：只增不改。</summary>
    public int Version { get; init; } = Json.FormatVersion;

    /// <summary>任务 id（12 位小写 hex，<see cref="Ids.New"/>）。⚠️ 只在一个会话内唯一。</summary>
    public required string Id { get; init; }

    /// <summary>归属会话。⚠️ **它是所有权判据**：`job_output` / `job_kill` 只认本会话的任务。</summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// **哪个工具起的**（`shell` / `dotnet_build` / `dotnet_test` …）。
    /// ⚠️ 它是**数据**，不是枚举：加一个可后台的工具不该改这个类型，
    /// 正如加一条轴不该改 `Axes` 的结构。主机侧只拿它来显示。
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>给人（与模型）看的一行命令摘要。⚠️ 不是可执行的东西——重跑它不保证有同样效果。</summary>
    public required string Label { get; init; }

    /// <summary>工作目录（启动时定的；之后不再变）。</summary>
    public required string WorkingDirectory { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>取值见 <see cref="BackgroundJobState"/>。</summary>
    public required string State { get; init; }

    /// <summary>退出码。<c>null</c> = 还没退出 / 被杀了 / 结局未知。</summary>
    public int? ExitCode { get; init; }

    /// <summary>
    /// **结束的原因**——一句人话。⚠️ <b>这是用户点名要的那一格</b>（「一块退出，最后结果记得写原因」）：
    /// 核心退出时杀掉的写「核心退出」，`job_kill` 杀掉的写「你的 job_kill」，
    /// 超时被终止的写「超过 N 秒」，重启之后捡回来的写「上一次核心没有正常退出」。
    ///
    /// <para>⚠️ <b>它只在「有原因可说」时出现</b>：一个自己 <c>exit 0</c> 的进程没有原因
    /// ——那时退出码就是全部答案。编一句「正常结束」只会多一条要读的字。</para>
    /// </summary>
    public string? Reason { get; init; }

    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>输出文件已经写了多少字节（stdout 与 stderr 合在一起）。</summary>
    public long OutputBytes { get; init; }

    /// <summary>进程 id（诊断用）。⚠️ 重启之后它**没有意义**——那个号码早就是别人的了。</summary>
    public int? ProcessId { get; init; }

    /// <summary>
    /// 这一次的**超时上限**（秒）。<c>null</c> = 不限时。⚠️ 它必须落盘：
    /// 一个不带超时的任务重启之后被标成 `Lost` 时，「当初有没有时限」是那一行原因的一部分。
    /// </summary>
    public int? TimeoutSeconds { get; init; }
}

/// <summary>
/// **一次取输出**的产物——字节窗口 + 「到头了没有」。
///
/// <para>⚠️ <b>它必须带「总共有多少字节」</b>：模型读前 64 KiB 而不知道总量时，
/// 会把它当成全部（`01` §9.3 那条「宁可明着少，不可暗着假」）。</para>
///
/// <para>⚠️ <b><see cref="NextOffset"/> 是「再来一次该从哪读」</b>，而且它**一定可兑付**：
/// 它就是 <c>Offset + 实际交付的字节数</c>（回退到 UTF-8 字符边界之后的值），
/// 与 `TimelineRender.TruncationNoteText` 那一条同源。</para>
/// </summary>
public sealed record BackgroundJobRead(
    string Text, long Offset, long NextOffset, long TotalBytes, bool Running);

/// <summary>
/// **启动一个后台任务**的产物。⚠️ <b>失败也是内容</b>（同 <see cref="ToolOutcome"/>）：
/// <see cref="Error"/> 非 null ⇒ 一个进程都没起，而那句话是给模型照着做的。
/// </summary>
public sealed record BackgroundJobStart(BackgroundJobInfo? Job, string? Error);

/// <summary>
/// **要起一个什么**——纯数据（2026-10-24 用户裁定「整合起来」）。
///
/// <para>⚠️ <b>为什么是 <c>Executable + Arguments</c>，不是「一段脚本」</b>：
/// 那是 2026-10-24 之前那一版的形状（只认 `sh -lc &lt;脚本&gt;`），而它只为 `shell` 服务。
/// `dotnet` 那条路**自己拼 argv**（它有一层环境清理与参数白名单，见 `DotnetRunner`），
/// 硬塞回一段 shell 文本会引入引号问题——而引号出错的症状是**静默跑错命令**。
/// ⇒ 宿主只认「可执行文件 + 参数列表」，shell 那条传 <c>[-l, -c, 脚本]</c> 即可。</para>
///
/// <para>⚠️ <b>它<b>不</b>含 <c>ProcessStartInfo</c></b>：那是 `System.Diagnostics` 的类型，
/// 而词汇层要能被任何实现使用（测试替身、将来的远端宿主）。环境变量用纯数据表达：
/// <see cref="Environment"/> 里值为 <c>null</c> 的表示**删掉这个继承来的变量**。</para>
/// </summary>
public sealed record BackgroundJobRequest
{
    /// <summary>哪个工具起的（`shell` / `dotnet_build` …）。⚠️ 只是标签，宿主不按它分支。</summary>
    public required string Kind { get; init; }

    /// <summary>给人看的一行摘要。⚠️ 由工具给（它知道自己的语义），宿主不猜。</summary>
    public required string Label { get; init; }

    public required string Executable { get; init; }

    public required IReadOnlyList<string> Arguments { get; init; }

    public required string WorkingDirectory { get; init; }

    /// <summary>环境变量覆盖。⚠️ 值为 <c>null</c> = **删掉**这个继承来的变量（`DOTNET_*` 那条判据）。</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } =
        new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>超时（秒）。<c>null</c> = 不限时（默认）。⚠️ 到点**整棵进程树**被杀，原因写进元数据。</summary>
    public int? TimeoutSeconds { get; init; }
}

/// <summary>
/// **一个已经在跑的进程**——宿主之外的地方起的那一个，被交给宿主继续（2026-11-07）。
///
/// <para>⚠️ <b>它为什么存在</b>：一次**前台**工具调用跑到通用上限时，如果这个工具有异步模式
/// （`shell` / `dotnet_build` / `dotnet_test`），正确的结局是**让它继续跑**、把结果改成
/// 「已转到后台」（用户裁定）。⚠️ 而**不能**「杀掉、再以 background 重起一遍」：
/// 同一条命令跑两遍会写两次盘、改两次仓库，而那个症状看起来完全正常
/// ——这是这一整条设计里最不能走错的一步。</para>
///
/// <para>⚠️ <b>它不暴露 <c>System.Diagnostics.Process</c></b>：同
/// <see cref="BackgroundJobRequest"/> 避开 <c>ProcessStartInfo</c> 那条纪律
/// ——词汇层要能被任何实现使用（测试替身、将来的远端宿主）。</para>
///
/// <para>⚠️ <b>生命周期归宿主</b>：交出去之后工具**不许**再碰这个进程（不 kill、不 dispose）。
/// 宿主拿它跟已经起来的后台任务走**同一条**监视路径（<c>BackgroundJobHost.Watch</c>）。</para>
/// </summary>
public interface IRunningProcess : IDisposable
{
    /// <summary>
    /// 交给宿主写进 `job.json` 的那份描述。⚠️ <b>它的 <see cref="BackgroundJobRequest.TimeoutSeconds"/>
    /// 应当是 <c>null</c></b>：转后台之后按「后台默认不限时」算（同 `shell background` 那条）。
    /// </summary>
    BackgroundJobRequest Request { get; }

    bool HasExited { get; }

    /// <summary>退出码。<c>null</c> = 还没退出 / 拿不到（同 <see cref="BackgroundJobInfo.ExitCode"/>）。</summary>
    int? ExitCode { get; }

    /// <summary>进程 id（诊断用）。</summary>
    int? Pid { get; }

    /// <summary>等它退出。</summary>
    Task WaitForExitAsync(CancellationToken ct = default);

    /// <summary>
    /// **读泵**：把两条管道排空的那个任务。⚠️ 进程退出之后还要等它
    /// （管道里可能还剩最后一段），否则日志会**少一截尾部**而看起来完全正常。
    /// </summary>
    Task Output { get; }

    /// <summary>杀**整棵进程树**。⚠️ 幂等：已经退出时什么也不做。</summary>
    void Kill();

    /// <summary>
    /// **把输出改道给 <paramref name="onChunk"/>，并把已经攒下的那一份先交给它**。
    /// 返回那份已读文本（工具拿它写进自己的结果）。
    ///
    /// <para>⚠️ <b>先交旧的、再谈新的</b>：交接那一刻之前读到的输出只在工具的内存里，
    /// 不先写进日志的话，`job_output` 从 0 读起的会是**半截**
    /// （而「开头那段呢」这个问题没有任何答案）。</para>
    /// </summary>
    Task<string> AdoptAsync(Func<ReadOnlyMemory<byte>, Task> onChunk);
}

/// <summary>
/// **后台任务的宿主**——进程表的唯一所有者（2026-10-24）。
///
/// <para>⚠️ <b>它存在的理由：工具必须是纯的、无状态的</b>（`00` §5 铁律 1）。
/// 「一个还在跑的进程」是**有状态的**，所以它不能住在 <see cref="ITool"/> 里
/// ——那会让同一个工具的两个会话看见彼此的进程。⇒ 状态住在这个宿主里，
/// 而宿主是**进程级**的（一个数据根一份），由组合根创建、经 <see cref="IToolContext"/> 交到工具手上。</para>
///
/// <para>⚠️ <b>它是 Kernel 层的接口，不是实现</b>：与 <see cref="INetworkTransport"/> 同一个形状
/// （词汇层定契约、执行层给实现），理由也同一条——**测试不能真的起进程**
/// （而「起了以后能不能杀干净」又必须能测，所以生产实现与测试替身走同一个接口）。</para>
///
/// <para>⚠️ <b>所有方法都必须按会话过滤</b>：<c>sessionId</c> 是所有权判据，
/// 「A 会话能不能读 / 杀 B 会话的任务」的答案必须是**不能**。⚠️ 实现里查到不匹配时
/// 返回「没有这个任务」，而**不**返回「这不是你的」——后者是一句关于别人任务的确认。</para>
/// </summary>
public interface IBackgroundJobs
{
    /// <summary>
    /// 起一个后台进程。⚠️ <b>立即返回</b>——不等它跑完，也不等第一段输出。
    /// 失败方向：起不来 / 超额度 ⇒ <see cref="BackgroundJobStart.Error"/> 是一句人话，
    /// 一个进程都没起（超过上限**拒绝，不排队**：排队会让模型以为它启动了）。
    /// </summary>
    BackgroundJobStart Start(string sessionId, BackgroundJobRequest request);

    /// <summary>
    /// **接管一个已经在跑的进程**（2026-11-07）：前台工具调用跑到通用上限、而它有异步模式时，
    /// 由工具把自己的进程连同读泵交过来，宿主从此按**普通后台任务**对待它
    /// （写 `job.json`、输出接进 `<jobId>.log`、走同一条 <c>Watch</c> 监视退出、`job_kill` 杀得掉）。
    ///
    /// <para>⚠️ <b>为什么是「接管」而不是「重起」</b>：见 <see cref="IRunningProcess"/> 的注释
    /// ——重起会让同一条命令跑两遍。</para>
    ///
    /// <para>⚠️ <b>失败方向</b>：拿不到会话归属 / 登记失败 ⇒ <c>null</c>，
    /// 那时**工具必须把进程杀掉**（不能既没交出去、又留着跑）——调用方负责那一步。</para>
    /// </summary>
    BackgroundJobInfo? Adopt(string sessionId, IRunningProcess running);

    /// <summary>本会话的全部任务，**新的在前**。⚠️ 只列本会话的。</summary>
    IReadOnlyList<BackgroundJobInfo> List(string sessionId);

    /// <summary>按 id 找一个（**本会话的**）。找不到 / 不属于本会话 ⇒ <c>null</c>。</summary>
    BackgroundJobInfo? Find(string sessionId, string jobId);

    /// <summary>按**字节窗口**读输出（读盘上那份日志，不是内存缓冲）。找不到 ⇒ <c>null</c>。</summary>
    BackgroundJobRead? Read(string sessionId, string jobId, long offset, int maxBytes);

    /// <summary>
    /// 杀掉（整棵进程树）。⚠️ <b>幂等</b>：对一个已经结束的任务再杀一次不算失败，
    /// 它返回**当时那份事实**（状态与原因都不被改写）。找不到 / 不属于本会话 ⇒ <c>null</c>。
    /// </summary>
    BackgroundJobInfo? Kill(string sessionId, string jobId, string reason);

    /// <summary>
    /// **杀掉全部还活着的任务**（核心退出时用）。⚠️ 不抛——退出路径上抛异常会把
    /// 「我在收尾」变成「我崩了」，而后者会盖掉真正的原因。
    /// </summary>
    void KillAll(string reason);

    /// <summary>
    /// **把上一次进程留下的残骸标成「结局未知」**（构造时跑一次）。⚠️ 这是强杀 / 断电
    /// 唯一的补偿点：正常退出走 <see cref="KillAll"/>，而 <c>kill -9</c> 走不到那里。
    ///
    /// <para>⚠️ 它<b>只改状态、不杀任何东西</b>：那些进程可能还活着（也可能早就没了），
    /// 而我们手上**没有句柄**——按 pid 去杀是另一件事（pid 会被复用，杀错是灾难）。</para>
    /// </summary>
    void Recover();

    /// <summary>
    /// **一个任务走到终态了**（2026-10-27，自动唤醒）。⚠️ 三个终态都走它：
    /// 自己退出 / 被杀 / 超时被停——判据是「不会再动了」，不是「成功」。
    ///
    /// <para>⚠️ <b>它<b>不</b>包含「重启之后标成 Lost 的那一批</b>：那不是刚发生的事
    /// （可能好几天前就结束了），唤醒一个会话去读它只会平白烧一次模型调用。
    /// ⇒ `Recover()` 里**不**触发这个事件。</para>
    ///
    /// <para>⚠️ <b>它可能在**任何线程**上被触发</b>（读泵的守护任务、超时计时器、`job_kill`）。
    /// 订阅方要自己保证线程安全。⚠️ 而实现方必须**吞掉订阅方的异常**：
    /// 这里跑在「状态已经写对之后」，抛出去会变成一条没人观测的后台任务异常。</para>
    ///
    /// <para>⚠️ <b>核心退出时的 `KillAll` 不触发它</b>：那时进程正在关，
    /// 唤醒一个会话去跑一步模型调用是**最坏的一条**（它会被下一次 `Dispose` 打断，
    /// 而且用户已经按了退出）。</para>
    /// </summary>
    event Action<BackgroundJobInfo>? Finished;
}

/// <summary>
/// 后台任务的**容量上限**（2026-10-24）。⚠️ 与 <see cref="ShellLimits"/> 分开：
/// 那些管「一次前台调用能跑多久」，这些管「同时能有多少个后台任务、每个能写多大」。
///
/// <para>⚠️ <b>两个数都是占位值</b>，与 <c>ToolLimits.MaxResultBytes</c> 同一条纪律：
/// 只裁定「必须有所限制」，没有说多少。**别把这里的数当成设计结论**。</para>
/// </summary>
public static class BackgroundJobLimits
{
    /// <summary>
    /// ⚠️ **这个会话能同时跑几个后台任务——不限**（用户 2026-10-27 裁定）：
    /// 「会话并发限制数量不限」。
    ///
    /// <para>⚠️ <b>原来这里有一个 4 的占位上限，被删掉了。</b>判据是：后台任务的数量
    /// **本来就由用户的机器决定**（一个 dev server + 一次构建 + 一次测试是常态），
    /// 而一个拍脑袋的数会让模型在第六个任务上收到一句它无法改变规则的拒绝。
    /// ⇒ 真正的边界交给操作系统与本机负载。</para>
    ///
    /// <para>⚠️ <b>代价如实记着</b>：一个失控的模型可以起任意多个进程，而核心不拦。
    /// 逃生口是用户侧（`job kill` 命令 / 头里的任务面板）与核心退出时的 `KillAll`。
    /// **别以为这里还有一道闸**——没有。</para>
    /// </summary>
    // No numeric quota: concurrent task count is intentionally unrestricted.

    /// <summary>单个任务的输出**最多保留多少字节**（超出部分丢弃，但进程**继续跑**）。占位值。</summary>
    /// <remarks>
    /// ⚠️ 判据与 `ShRunner.ReadBoundedAsync` 那条逐字相同：到上限后**继续排空管道**
    /// 但不保留——不排空的话，子进程写满管道缓冲（约 64 KiB）会**自己卡死**，
    /// 而那个症状看起来完全不像「输出太多」。
    /// ⚠️ 于是「超出」这件事必须**说出来**（读到头时明确写「后面还有 N 字节被丢弃」）。
    /// </remarks>
    public const int MaxOutputBytes = 4 * 1024 * 1024;

    /// <summary>`job_output` 一次最多交付多少字节。占位值。</summary>
    /// <remarks>
    /// ⚠️ 它比 `ToolLimits.MaxResultBytes`（256 KiB）小得多，而这是刻意的：
    /// 后台输出是**日志**，模型来读的时候通常只差最后一段。
    /// </remarks>
    public const int MaxReadBytes = 64 * 1024;

    /// <summary>
    /// **`job_output` 对着一个还在跑的任务最多等多久**（2026-11-13，用户裁定
    /// 「模型主动调用 `job_output` 的时候视为 `wait`，防止模型反复轮询」）。
    ///
    /// <para>⚠️ <b>它必须小于 <see cref="ToolCallLimits.DefaultTimeoutSeconds"/>（600 秒）</b>：
    /// 到了那个通用上限，这一条会变成一句「超时被终止、这一条没有结果」——而这里等的是
    /// **别人的进程**，说「被终止」是假信息（那个任务还在跑，只是我们不等了）。
    /// ⇒ 自己先到点，交一份**正常的结果**（输出照给 + 一句「它还在跑」）。</para>
    ///
    /// <para>⚠️ <b>为什么不是「无限等」</b>：一个 dev server 永远不退出。等它等于让这一步
    /// 挂到通用上限——而用户要的是「别轮询」，不是「一气等到天荒地老」。
    /// 到点之后如实说「它还在跑」，并且提醒：任务结束时核心会**自动叫醒**会话
    /// （`IBackgroundJobs.Finished`），所以它本来就不需要再问一次。</para>
    /// </summary>
    public const int MaxWaitSeconds = 300;

    /// <summary>一个任务结束后**元数据保留多久**。占位值。⚠️ 清理只发生在下一次启动。</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    /// <summary>
    /// `timeoutSeconds` 的合法区间。⚠️ <b>下限 1 秒</b>（0 与负数读不出意思，而
    /// 「不超时」的表达是**省略这个键**，不是给 0——同 `SessionCreate.AskTimeoutMinutes` 那条）。
    /// ⚠️ 上限 3600 秒：比 `ShellLimits.MaxTimeoutSeconds`（600）宽，因为后台本来就是给长活用的；
    /// 再长就该由用户自己 `job_kill`，那才是「后台」的意思。
    /// </summary>
    public const int MinTimeoutSeconds = 1;
    public const int MaxTimeoutSeconds = 3600;
}
