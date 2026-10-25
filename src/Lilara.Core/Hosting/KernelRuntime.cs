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
    private readonly Dictionary<string, SemaphoreSlim> _sessionGates = new(StringComparer.Ordinal);
    private readonly object _gateLock = new();
    private bool _disposed;

    private KernelRuntime(string dataRoot, string workingDirectory, IDisposable dataRootLock)
    {
        DataRoot = Path.GetFullPath(dataRoot);
        WorkingDirectory = Path.GetFullPath(workingDirectory);
        _dataRootLock = dataRootLock;
        Store = new SessionStore(DataRoot);
        Usage = new UsageStore(DataRoot);
    }

    public string DataRoot { get; }
    public string WorkingDirectory { get; }
    public SessionStore Store { get; }
    public UsageStore Usage { get; }
    public PluginHost Plugins => _plugins;

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
        return handle;
    }

    public SessionHandle LoadSession(string projectKey, string sessionId)
    {
        ThrowIfDisposed();
        var handle = SessionHandle.Load(Store, projectKey, sessionId);
        lock (_gateLock) _sessionGates.TryAdd(sessionId, new SemaphoreSlim(1, 1));
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

            var stepper = new Stepper(Store, adapter, tools, _plugins, Usage);
            return await stepper.StepAsync(handle, input, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            sessionGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gateLock)
        {
            foreach (var gate in _sessionGates.Values) gate.Dispose();
            _sessionGates.Clear();
        }
        _dataRootLock.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
