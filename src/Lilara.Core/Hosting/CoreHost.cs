using System.Collections.Concurrent;
using Lilara.Core.Persistence;
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
    /// 载入一个会话并登记；已激活则返回**同一个实例**。
    /// 幂等是硬要求：两个实例会让 `State` 分叉，而 `State` 是可变属性。
    /// </summary>
    public SessionHandle Activate(string projectKey, string sessionId) =>
        _activated.GetOrAdd(sessionId, _ => SessionHandle.Load(_store, projectKey, sessionId));

    /// <summary>
    /// 登记一个**已经建好**的 handle（`session.create` 用的）。
    /// 与 <see cref="Activate"/> 的区别：那个是「从磁盘载入」，这个是「手里已经有了」——
    /// 让 create 走 Activate 会白读一次盘，而且在文件刚写完的窗口里可能读到半截。
    /// </summary>
    public void Register(SessionHandle handle) => _activated[handle.SessionId] = handle;

    /// <summary>
    /// 快照端点必须**先查这里**再回落磁盘。
    /// `Stepper` 在步开始时只把 `Running` 写进**内存**（刻意不落盘），到步末才写 `state.json`；
    /// 步进行中读磁盘会得到 **Idle** ⇒ 头渲染「空闲」⇒ 用户以为没在跑 ⇒ **再点一次发送** ⇒ 队列两条 ⇒ 烧钱。
    /// </summary>
    public bool TryGetActivated(string sessionId, out SessionHandle handle) =>
        _activated.TryGetValue(sessionId, out handle!);

    public IEnumerable<SessionHandle> Activated => _activated.Values;

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
