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

    public async Task<Stepper.StepOutcome> StepAsync(
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

        SemaphoreSlim sessionGate;
        lock (_gateLock)
        {
            if (!_sessionGates.TryGetValue(handle.SessionId, out sessionGate!))
                _sessionGates.Add(handle.SessionId, sessionGate = new SemaphoreSlim(1, 1));
        }

        await sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
