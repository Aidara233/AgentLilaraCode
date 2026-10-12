using System.Collections.Concurrent;
using Lilara.Core.Kernel;
using Lilara.Core.Storage;
using Lilara.Core.Providers;
using Lilara.Core.Session;

namespace Lilara.Core.Hosting;

/// <summary>
/// 核心的宿主：控制台与控制面**共用**的那份状态。
///
/// **它不是 per-session 活对象**：没有循环、没有定时器、没有订阅、没有端口。
/// 它只是**数据 + 几把闸**——事件订阅挂在核心级广播器上，不挂在这里。
/// </summary>
public sealed class CoreHost(string dataRoot, string workingDirectory)
{
    private readonly SessionStore _store = new(dataRoot);
    private readonly ProviderStore _providers = new(dataRoot);

    /// <summary>
    /// **哪些会话现在真的有东西在替它往下走**（`01` §11.6）。
    ///
    /// <para>⚠️ 它<b>不落盘</b>：判据是「有东西在动」，而那件事**只在内存里**
    /// （`Running` 从不落盘、队列与答复槽也都在内存）⇒ 进程一重启它就该是空的。</para>
    /// </summary>
    public LiveSessions Live { get; } = new();

    /// <summary>已激活的会话。快照端点先查这里——见 <see cref="TryGetActivated"/>。</summary>
    private readonly ConcurrentDictionary<string, SessionHandle> _activated = new();

    /// <summary>每会话一把闸：防**同一会话**两步并发（「会话内严格串行」是正确性条款）。</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

    /// <summary>每会话一个步的取消源。**每步一个**，不是长命一个——见 <see cref="SetCts"/>。</summary>
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cts = new();

    private readonly Dictionary<string, Stepper> _steppers = new();
    private readonly Lock _stepperLock = new();

    public SessionStore Store => _store;
    public ProviderStore Providers => _providers;
    public string WorkingDirectory { get; } = workingDirectory;

    /// <summary>
    /// **这一次启动，上一次是不是被强杀 / 断电弄死的**（`01` §12.8 的崩溃语义）。
    ///
    /// <para>判据只有一条：**心跳文件还在**（<see cref="CoreClock.HeartbeatPath"/>）。
    /// 正常退出（`quit` / Ctrl-C）会**删掉**它，而**强杀删不掉** ⇒
    /// 文件**在** = 上一次没走到那一步 = 崩了。</para>
    ///
    /// <para>⚠️ <b>这里的方向容易写反，而且写反了<b>不报错</b></b>（实测时我先写成了
    /// `!File.Exists`）：那样「正常退出」会被判成崩溃、「强杀」被判成正常，
    /// 而两条路的**唯一**可观差别就是那句横幅——**没有别的测试会红**。</para>
    ///
    /// <para>⚠️ **全新数据根不算崩**：没有 `projects/` 说明这台机器上还没用过核心，
    /// 那时报「上一次崩了」是一句**假话**（而且会吓到第一次启动的人）。</para>
    /// </summary>
    public bool RecoveredFromACrash { get; } =
        Directory.Exists(Path.Combine(dataRoot, "projects"))
        && File.Exists(CoreClock.HeartbeatPath(dataRoot));

    /// <summary>心跳那一趟的周期。⚠️ 见 `CommandDispatcher.SweepExpiredAsks`。</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);

    /// <summary>
    /// 数据根。暴露出来是为了**诊断**：核心跑在哪个数据根上，是「为什么我看不到我配的 provider」
    /// 这类问题唯一的答案来源（`GET /info` 与启动横幅都用它）。
    /// </summary>
    public string DataRoot { get; } = dataRoot;

    /// <summary>
    /// 载入一个会话并登记；已激活则返回**同一个实例**。
    /// 幂等是硬要求：两个实例会让 `State` 分叉，而 `State` 是可变属性。
    /// </summary>
    public SessionHandle Activate(string projectKey, string sessionId) =>
        _activated.GetOrAdd(sessionId, _ =>
        {
            // ⚠️ **碰一下就登记进 `Known`**：心跳那一趟要遍历「被碰过」的会话
            // （一个挂在等回答上的会话**没有任何东西在动** —— 那正是它挂着的定义 ——
            // 而它的询问可能有截止时刻）。见 `SweepExpiredAsks`。
            Live.Touch(sessionId);
            return SessionHandle.Load(_store, projectKey, sessionId);
        });

    /// <summary>
    /// 登记一个**已经建好**的 handle（`session.create` 用的）。
    /// 与 <see cref="Activate"/> 的区别：那个是「从磁盘载入」，这个是「手里已经有了」——
    /// 让 create 走 Activate 会白读一次盘，而且在文件刚写完的窗口里可能读到半截。
    /// </summary>
    public void Register(SessionHandle handle)
    {
        Live.Touch(handle.SessionId);   // 同 `Activate`：心跳要遍历它
        _activated[handle.SessionId] = handle;
    }

    /// <summary>
    /// 快照端点必须**先查这里**再回落磁盘。
    /// `Stepper` 在步开始时只把 `Running` 写进**内存**（刻意不落盘），到步末才写 `state.json`；
    /// 步进行中读磁盘会得到 **Idle** ⇒ 头渲染「空闲」⇒ 用户以为没在跑 ⇒ **再点一次发送** ⇒ 队列两条 ⇒ 烧钱。
    /// </summary>
    public bool TryGetActivated(string sessionId, out SessionHandle handle) =>
        _activated.TryGetValue(sessionId, out handle!);

    public IEnumerable<SessionHandle> Activated => _activated.Values;

    /// <summary>
    /// 摘掉一个会话的激活项（`session.deactivate`）。返回它**本来**在不在激活表里
    /// ——调用方要据此说「摘掉了」还是「它本来就没激活」。
    ///
    /// <para>⚠️ 摘掉的只是**内存里那一项**。磁盘从来不存「激活」（`Activate`
    /// = 从盘载入 + 登记），所以**存储侧零改动**。</para>
    ///
    /// <para>⚠️ `_gates` / `_cts` **刻意不摘**：`GateFor` 是 `GetOrAdd` 懒创建的
    /// ⇒ 摘了下次自动重建；而 `_cts` 在**正常运行时也在**（那正是 `interrupt`
    /// 找得到它的原因），拿「它在不在」当「能不能摘」会整段误伤。</para>
    /// <para>⚠️ <b>摘掉时顺手把活跃性凭证也清掉</b>：一个被踢出去的会话**不该**再算活跃
    /// ——凭证的判据是「有东西在动」，而它那个把手已经不在表里了。
    /// ⚠️ 今天「在内存里」与「有东西在动」**同延**（§6.1 的队列随内存一起丢），
    /// 所以这一句是**保持两者一致**，不是新增语义。见 `03` §6 的记账。</para>
    ///
    /// <para>⚠️ <b>用 <see cref="LiveSessions.Forget"/> 而不是 <c>Clear</c></b>：
    /// 后者是「**这一趟**推进跑完了」的**配对**动作（`Mark` 的反面），
    /// 用在这里会让计数与真实推进次数**脱钩**——离线那个会话下一次 `Mark` 起来的数就少一格。
    /// 这两件事（一趟跑完 / 这个会话被踢出去）共用同一个计数字段，但**不是同一个事件**。</para>
    /// </summary>
    public bool Deactivate(string sessionId)
    {
        Live.Forget(sessionId);
        return _activated.TryRemove(sessionId, out _);
    }

    /// <summary>
    /// 内存里的状态优先，miss 才读盘。
    ///
    /// <para>⚠️ **判「在不在跑」必须走这里，不能走 <see cref="Activated"/>**：
    /// `session.create` 走 <see cref="Register"/> 登记，新会话一定在激活表里
    /// ⇒ 用激活表判「能不能删」会让**刚建的会话删不掉**。</para>
    ///
    /// <para>⚠️ 读盘那半用 `FindSession`（扫全部项目）而不是「本项目的」。调用方
    /// 多半已经知道 pk，但把它当参数传进来会有两个真相来源；会话 id 全局唯一，
    /// 扫一遍的代价是几个目录。</para>
    ///
    /// <para>读不出来时返回 <c>"Idle"</c>：这是**尽最大努力**的判据，不是权威。
    /// 返回 null 会逼每个调用方各写一遍兜底。</para>
    /// </summary>
    public string StatusOf(string sessionId)
    {
        if (_activated.TryGetValue(sessionId, out var live)) return live.State.Status;

        var found = _store.FindSession(sessionId);
        return found is null ? SessionStatus.Idle : _store.ReadState(found.Value.ProjectKey, sessionId).Status;
    }

    /// <summary>丢掉**某一个**会话的 stepper 缓存（`session.deactivate` / `delete` 用）。</summary>
    /// <remarks>
    /// ⚠️ 与 <see cref="InvalidateSteppers"/> 分开是刻意的：那个是「供应商定义变了、
    /// **所有**会话都可能受影响」，这个是「**这一个**会话走了」。用全量去顶替会让
    /// 别的会话白掉一次缓存（下一条注释里的「连累」就是同一件事）。
    /// </remarks>
    public void ForgetStepper(string sessionId)
    {
        lock (_stepperLock) _steppers.Remove(sessionId);
    }

    /// <summary>每会话一把。**不是**全局一把——全局一把会让多会话退化成串行。</summary>
    public SemaphoreSlim GateFor(string sessionId) =>
        _gates.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));

    public CancellationTokenSource? CtsFor(string sessionId) =>
        _cts.TryGetValue(sessionId, out var cts) ? cts : null;

    /// <summary>
    /// 装/清单个会话的取消源。
    /// **不许在这里 Dispose 旧的**：步正握着它的 token，Dispose 会让并发的
    /// <c>Cancel()</c> / <c>Token</c> 抛 <see cref="ObjectDisposedException"/>。
    /// 释放的责任归**步自己**（在它 finally 里、且已从表里摘掉之后）。
    /// </summary>
    public void SetCts(string sessionId, CancellationTokenSource? cts)
    {
        if (cts is null) _cts.TryRemove(sessionId, out _);
        else _cts[sessionId] = cts;
    }

    /// <summary>
    /// 按**该会话自己的** provider 解析适配器，并缓存。
    /// **不能共用一把**：两个会话绑不同 provider（不同 key、不同端点）时，
    /// 共用会把 B 的全量历史发到 A 的端点上——等于把数据发给了别人。
    /// </summary>
    public Stepper StepperFor(SessionHandle handle)
    {
        lock (_stepperLock)
        {
            if (_steppers.TryGetValue(handle.SessionId, out var cached)) return cached;

            var stepper = StepperResolver.Resolve(_store, _providers, handle.Spec.Model.ProviderId);
            _steppers[handle.SessionId] = stepper;
            return stepper;
        }
    }

    /// <summary>
    /// 丢掉全部 stepper 缓存。
    /// **调用点必须在 `CommandDispatcher` 的 `provider.upsert` 里**，
    /// 不能挂在控制台的 `IsProviderSetCommand` 上——那是个**字符串**判据，
    /// 线上 `provider.upsert` 走不到它 ⇒ 缓存**静默失效**。
    /// </summary>
    public void InvalidateSteppers()
    {
        lock (_stepperLock) _steppers.Clear();
    }
}
