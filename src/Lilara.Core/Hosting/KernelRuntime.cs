using Lilara.Core.Kernel;
using Lilara.Core.Plugins;
using Lilara.Core.Protocol;
using Lilara.Core.Session;
using Lilara.Core.Storage;

namespace Lilara.Core.Hosting;

/// <summary>
/// Public composition boundary for embedding Lilara.Core in another host.
/// Owns the data-root lock and exposes only the operations needed to create,
/// advance, and inspect sessions.
/// </summary>
public sealed class KernelRuntime : IDisposable
{
    private readonly IDisposable _dataRootLock;
    private readonly PluginHost _plugins = new();
    private readonly Mcp.McpHost _mcp;
    private readonly Dictionary<string, SemaphoreSlim> _sessionGates = new(StringComparer.Ordinal);
    private readonly object _gateLock = new();
    private readonly Lock _mcpPublishGate = new();
    private bool _mcpEnabled = true;
    private IReadOnlyList<Lilara.Core.Mcp.McpServerView> _mcpServers = [];
    private IReadOnlyList<string> _mcpProblems = [];
    private bool _disposed;

    /// <summary>
    /// **这个进程亲手载入过的会话**（`wait` 探针用）。⚠️ 与 `CoreHost._activated` 同一个理由：
    /// `Running` 只写内存 ⇒ 不看这一份就只能看到盘上的 `Idle`，而那会让 `wait` 以为
    /// 子会话早就停了（**比等下去更坏**：它会让父在子还没干完时就去汇报）。
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SessionHandle> _live = new(StringComparer.Ordinal);

    private KernelRuntime(string dataRoot, string workingDirectory, IDisposable dataRootLock)
    {
        DataRoot = Path.GetFullPath(dataRoot);
        WorkingDirectory = Path.GetFullPath(workingDirectory);
        _dataRootLock = dataRootLock;
        Store = new SessionStore(DataRoot);
        Usage = new UsageStore(DataRoot);

        // ⚠️ **重启补偿在这一刻做**（同 `CoreHost`）：上一个进程被强杀时会把任务留在 `Running`，
        // 而那是一个假的活口——`jobs` 会一直说「在跑」，模型会等一个不会来的结果。
        var jobs = new Session.Tools.BackgroundJobHost(DataRoot);
        jobs.Recover();
        BackgroundJobs = jobs;

        // ⚠️ MCP 宿主与后台任务是同一条装配判据（进程级、一个数据根一份）。
        // 构造它**不拉起任何连接**——第一份连接由显式 `Mcp.ReloadAsync()` 触发，
        // 于是「嵌入方用不用 MCP」是一个可以看见的装配事实，不是启动时的副作用。
        _mcp = new Mcp.McpHost(new Mcp.McpStore(DataRoot), WorkingDirectory);
        _mcp.Changed += OnMcpChanged;
        _mcp.StatusChanged += OnMcpStatusChanged;
        var saved = new Mcp.McpStore(DataRoot).Load();
        _mcpEnabled = saved.Enabled;
    }

    public string DataRoot { get; }
    public string WorkingDirectory { get; }
    public SessionStore Store { get; }
    public UsageStore Usage { get; }
    public PluginHost Plugins => _plugins;

    /// <summary>
    /// **MCP 的连接宿主**。嵌入方要显式 `await Mcp.ReloadAsync()` 才会有 MCP 工具
    /// （构造它不连、不拉进程）——这与「`spec.json` 是唯一必须非空的东西」同向：
    /// 没有连接就没有工具，而那不是错误，是纯 chat。
    /// </summary>
    public Mcp.McpHost Mcp => _mcp;

    /// <summary>
    /// **后台任务的进程表**（2026-10-24）。⚠️ 嵌入方只要用 `KernelRuntime`，
    /// 后台能力就自动接上——这条是刻意的：让「后台能不能用」变成一个**装配事实**，
    /// 而不是每个宿主各记得接一次的约定。
    /// </summary>
    public IBackgroundJobs BackgroundJobs { get; }

    public static KernelRuntime Open(string dataRoot, string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var fullDataRoot = Path.GetFullPath(dataRoot);
        Directory.CreateDirectory(fullDataRoot);
        var dataRootLock = DataRootLock.Acquire(fullDataRoot);
        try
        {
            return new KernelRuntime(fullDataRoot, workingDirectory, dataRootLock);
        }
        catch
        {
            dataRootLock.Dispose();
            throw;
        }
    }

    public SessionHandle CreateSession(
        ModelSelection model,
        string? title = null,
        string? systemPromptPreset = null,
        TimeSpan? askTimeout = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(model);
        var handle = SessionHandle.Create(Store, WorkingDirectory, model, title,
            systemPromptPreset, askTimeout: askTimeout, plugins: _plugins);
        lock (_gateLock) _sessionGates.Add(handle.SessionId, new SemaphoreSlim(1, 1));
        _live[handle.SessionId] = handle;   // `wait` 探针要它（见 `_live` 的注释）
        return handle;
    }

    public SessionHandle LoadSession(string projectKey, string sessionId)
    {
        ThrowIfDisposed();
        var handle = SessionHandle.Load(Store, projectKey, sessionId);
        lock (_gateLock) _sessionGates.TryAdd(sessionId, new SemaphoreSlim(1, 1));
        _live[sessionId] = handle;   // `wait` 探针要它
        return handle;
    }

    public IReadOnlyList<TimelineEvent> ReadTimeline(SessionHandle handle)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(handle);
        return Store.ReadTimeline(handle.ProjectKey, handle.SessionId);
    }

    /// <summary>
    /// Advance the session by one step with new user input.
    ///
    /// <para>The user message is appended <b>before</b> the model call — it is a fact that
    /// survives a failed call. Hosts that need to continue without new input (tool
    /// continuation, a bare start) use <see cref="ContinueAsync"/>.</para>
    /// </summary>
    public Task<Stepper.StepOutcome> StepAsync(
        SessionHandle handle,
        IReadOnlyList<Block> input,
        IModelAdapter adapter,
        IReadOnlyList<ITool>? tools = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(adapter);
        if (input.Count == 0)
            throw new ArgumentException(
                "没有输入时请用 ContinueAsync——一条空的用户消息会作为事实落进时间线。", nameof(input));

        return RunStepAsync(handle, input, adapter, tools, appendUserInput: true, cancellationToken);
    }

    /// <summary>Advance one step <b>without</b> adding a user message (tool continuation / start).</summary>
    public Task<Stepper.StepOutcome> ContinueAsync(
        SessionHandle handle,
        IModelAdapter adapter,
        IReadOnlyList<ITool>? tools = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(adapter);
        return RunStepAsync(handle, [], adapter, tools, appendUserInput: false, cancellationToken);
    }

    private async Task<Stepper.StepOutcome> RunStepAsync(
        SessionHandle handle,
        IReadOnlyList<Block> input,
        IModelAdapter adapter,
        IReadOnlyList<ITool>? tools,
        bool appendUserInput,
        CancellationToken cancellationToken)
    {
        SemaphoreSlim sessionGate;
        lock (_gateLock)
        {
            if (!_sessionGates.TryGetValue(handle.SessionId, out sessionGate!))
                _sessionGates.Add(handle.SessionId, sessionGate = new SemaphoreSlim(1, 1));
        }

        await sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // ⚠️ 追加在闸内：两条并发输入若在闸外落盘，时间线顺序会由调度决定，
            // 而模型看到的次序会与宿主以为的次序分叉。
            if (appendUserInput)
                Store.AppendEvent(handle.ProjectKey, handle.SessionId, new TimelineEvent.MessageEvent
                {
                    Id = Ids.New(),
                    At = CoreClock.Now(),
                    From = Speaker.User,
                    Blocks = input,
                });

            var stepper = new Stepper(Store, adapter, tools ?? DefaultTools(), _plugins, Usage,
                backgroundJobs: BackgroundJobs, sessionStatus: SessionStatusOf);
            return await stepper.StepAsync(handle, input, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            sessionGate.Release();
        }
    }

    /// <summary>
    /// 嵌入方没给工具表时用**同一份装配规则**（`内建 ⊕ MCP`）。
    /// ⚠️ 不能退回 `BuiltInTools.All` 了事：那会让「嵌入方有没有接 MCP」变成一次静默分叉
    /// ——同一个数据根、同一份 `mcp.json`，走 CLI 有 MCP 工具、走嵌入没有。
    /// </summary>
    private IReadOnlyList<ITool> DefaultTools() =>
        Session.Tools.ToolTable.Build(
            ("内建", Session.Tools.BuiltInTools.All), ("MCP", _mcp.Tools));

    /// <summary>
    /// **子会话状态的实况探针**（2026-10-27，`wait` 用）：内存优先、盘面回退。
    /// ⚠️ 读盘那半是必要的：一个**没被这个进程载入**的子会话，它的真相只在 `state.json` 里
    /// （`Running` 从不落盘 ⇒ 那一格最多是 `Idle` / `WaitingForInput`，不会骗人）。
    /// </summary>
    private string? SessionStatusOf(string sessionId)
    {
        if (_live.TryGetValue(sessionId, out var live)) return live.State.Status;

        var found = Store.FindSession(sessionId);
        return found is null ? null : Store.ReadState(found.Value.ProjectKey, sessionId).Status;
    }

    private void OnMcpChanged()
    {
        lock (_mcpPublishGate)
        {
            var view = _mcp.Snapshot();
            _mcpEnabled = view.Enabled;
            _mcpServers = view.Servers;
            _mcpProblems = view.Problems;
        }
    }

    private void OnMcpStatusChanged(Mcp.McpView view)
    {
        lock (_mcpPublishGate)
        {
            _mcpEnabled = view.Enabled;
            _mcpServers = view.Servers;
            _mcpProblems = view.Problems;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // ⚠️ **后台任务跟着这个核心一起走**（用户裁定：一块退出，并写清原因）。
        // 顺序在释放闸之前：那些任务与这些闸无关，而它们若还活着，`Dispose` 之后
        // 就再也没有人能停掉它们了（句柄随这个对象一起没）。
        BackgroundJobs.KillAll("核心退出了（KernelRuntime 被释放）。");

        // ⚠️ MCP 的子进程跟着一起走（同 BackgroundJobs 那条判据）。同步等它，是因为
        // `Dispose` 之后再也没有人能关掉那些连接了——而 stdio server 是**我们拉起来的进程**。
        _mcp.DisposeAsync().AsTask().GetAwaiter().GetResult();

        lock (_gateLock)
        {
            foreach (var gate in _sessionGates.Values) gate.Dispose();
            _sessionGates.Clear();
        }
        _dataRootLock.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
