using System.Text.Json;
using System.Text.RegularExpressions;
using Lilara.Core.Kernel;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Lilara.Core.Mcp;

/// <summary>
/// **MCP 的连接宿主**——核心级、一个数据根一份（与 `BackgroundJobs` 同一条装配判据）。
///
/// <para>⚠️ <b>它不是「MCP 子系统」</b>（`04` §4）：它只做三件事——按配置拉起连接、
/// 缓存 <c>tools/list</c>、转发 <c>tools/call</c>。工具表仍然是 <c>ToolTable</c> 派生的，
/// 这里只是「插件贡献」的一个来源。</para>
///
/// <para>⚠️ <b>掉线不改工具表</b>（`04` §4 明文）：已经进表的 <see cref="McpTool"/> 实例
/// 不会因为连接断掉而消失——它只会在调用时交回一句「不在线」。改表要等一次显式
/// <see cref="ReloadAsync"/>，因为工具表在请求最前端，改它最贵。</para>
/// </summary>
public sealed partial class McpHost : IAsyncDisposable
{
    private readonly McpStore _store;
    private readonly string _workingDirectory;
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private readonly Lock _lock = new();
    private readonly List<ServerRuntime> _servers = [];
    private readonly List<string> _problems = [];
    private IReadOnlyList<ITool> _tools = [];
    private McpConfig _config = new();
    private bool _disposed;

    public McpHost(McpStore store, string workingDirectory)
    {
        _store = store;
        _workingDirectory = workingDirectory;
    }

    public string ConfigPath => _store.Path;

    /// <summary>工具表变了。组合根清推进器缓存；McpHost 不认识 CoreHost。</summary>
    public event Action? Changed;

    /// <summary>连接状态变了（含 running/offline/error）。组合根负责投到 core topic。</summary>
    public event Action<McpView>? StatusChanged;

    public bool Enabled { get { lock (_lock) return _config.Enabled; } }
    public IReadOnlyList<ITool> Tools { get { lock (_lock) return _tools; } }

    /// <summary>读配置、拆旧连接、并行连新连接、重建工具表。只在显式动作里调。</summary>
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        await _reloadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var config = _store.Load();
            var old = Detach();
            foreach (var server in old) await server.DisposeAsync().ConfigureAwait(false);

            var problems = new List<string>();
            var runtimes = new List<ServerRuntime>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var tasks = new List<Task<ServerRuntime>>();

            foreach (var server in config.Servers)
            {
                if (!McpNames.IsValidServerId(server.Id))
                {
                    problems.Add($"server id '{server.Id}' 不合法（小写字母 / 数字 / 连字符，1–64 字符），已跳过。");
                    continue;
                }
                if (!seen.Add(server.Id))
                {
                    problems.Add($"server id '{server.Id}' 重复，后一条已跳过（前一条仍然生效）。");
                    continue;
                }
                if (server.Transport is not ("stdio" or "http"))
                {
                    problems.Add($"server '{server.Id}' 的 transport='{server.Transport}' 尚未支持（当前支持 stdio / Streamable HTTP）。");
                    runtimes.Add(new ServerRuntime(server) { State = "unsupported", Error = "transport 尚未支持" });
                    continue;
                }
                if (!config.Enabled || !server.Enabled)
                {
                    runtimes.Add(new ServerRuntime(server) { State = "disabled" });
                    continue;
                }
                tasks.Add(ConnectAsync(server, problems, ct));
            }

            runtimes.AddRange(await Task.WhenAll(tasks).ConfigureAwait(false));

            lock (_lock)
            {
                _config = config;
                _servers.Clear();
                _servers.AddRange(runtimes);
                _problems.Clear();
                _tools = BuildTools(problems);
                _problems.AddRange(problems);
            }
        }
        finally { _reloadGate.Release(); }

        Changed?.Invoke();
        StatusChanged?.Invoke(Snapshot());
    }

    /// <summary>真的连一次（显式动作），列工具后断开。不改主机表，也不要求总开关打开。</summary>
    public async Task<McpTestResult> TestAsync(string id, CancellationToken ct = default)
    {
        var problems = new List<string>();
        var config = _store.Load();
        var server = config.Servers.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
        if (server is null) return new McpTestResult(id, false, [], problems, "没有这个 server 的登记项。");
        if (server.Transport is not ("stdio" or "http"))
            return new McpTestResult(id, false, [], problems, $"transport='{server.Transport}' 尚未支持。");
        if (!McpNames.IsValidServerId(server.Id))
            return new McpTestResult(id, false, [], problems, "server id 不合法。");

        var runtime = new ServerRuntime(server);
        try
        {
            await ConnectCoreAsync(runtime, problems, ct).ConfigureAwait(false);
            return runtime.State != "connected"
                ? new McpTestResult(id, false, [], problems, runtime.Error)
                : new McpTestResult(id, true, runtime.Tools.Select(t => t.RemoteName).ToList(), problems, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new McpTestResult(id, false, [], problems, ex.Message); }
        finally { await runtime.DisposeAsync().ConfigureAwait(false); }
    }

    public async Task<McpCallResult> CallAsync(
        string serverId, string remoteTool, string argumentsJson, CancellationToken ct)
    {
        ServerRuntime? server;
        lock (_lock)
            server = _servers.FirstOrDefault(s => string.Equals(s.Config.Id, serverId, StringComparison.Ordinal));

        if (server is null)
            return McpCallResult.Fail($"没有 server '{serverId}' 的连接。{McpLimits.OfflineSuffix}");

        McpClient client;
        lock (_lock)
        {
            if (server.Client is null)
                return McpCallResult.Fail($"MCP 服务 '{serverId}' 不在线"
                    + (server.Error is { } error ? $"（{error}）" : "") + McpLimits.OfflineSuffix);
            client = server.Client;
            if (!server.CallGate.Wait(0))
                return McpCallResult.Fail($"MCP 服务 '{serverId}' 正在处理另一个工具调用，请稍后重试。");
        }

        try { return await CallConnectedAsync(client, server, serverId, remoteTool, argumentsJson, ct).ConfigureAwait(false); }
        finally { server.CallGate.Release(); }
    }

    private async Task<McpCallResult> CallConnectedAsync(
        McpClient client, ServerRuntime server, string serverId, string remoteTool,
        string argumentsJson, CancellationToken ct)
    {
        IReadOnlyDictionary<string, object?> arguments;
        try { arguments = ParseArguments(argumentsJson); }
        catch (JsonException ex) { return McpCallResult.Fail($"参数不是合法的 JSON 对象：{ex.Message}"); }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(server.Config.TimeoutSeconds));
        try
        {
            var result = await client.CallToolAsync(remoteTool, arguments, null, null,
                cancellationToken: timeout.Token).ConfigureAwait(false);
            return new McpCallResult(MapContent(result), result.IsError == true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return McpCallResult.Fail($"调用 '{remoteTool}' 超过 {server.Config.TimeoutSeconds} 秒，已取消（server '{serverId}'）。");
        }
        catch (OperationCanceledException) { throw; }
        catch (McpProtocolException ex)
        {
            return new McpCallResult([new McpText($"MCP 服务 '{serverId}' 拒绝了这次调用：{ex.Message}")], true);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException or ObjectDisposedException)
        {
            await MarkOfflineAsync(server, client, ex.Message).ConfigureAwait(false);
            StatusChanged?.Invoke(Snapshot());
            return McpCallResult.Fail($"MCP 服务 '{serverId}' 掉线了：{ex.Message}{McpLimits.OfflineSuffix}");
        }
    }

    /// <summary>
    /// 配置刚保存、但没有 reload 时通知控制面刷新 `/mcp`。
    /// ⚠️ 只发状态快照，不拉起连接、不改工具表。
    /// </summary>
    public void NotifyConfigurationChanged() => StatusChanged?.Invoke(Snapshot());

    public McpView Snapshot()
    {
        var saved = _store.Load();
        lock (_lock)
        {
            var active = _servers.ToDictionary(s => s.Config.Id, StringComparer.Ordinal);
            var views = new List<McpServerView>();
            foreach (var config in saved.Servers)
            {
                active.TryGetValue(config.Id, out var runtime);
                var pending = runtime is not null &&
                    (!saved.Enabled.Equals(_config.Enabled) || !SameConfig(config, runtime.Config));
                var state = runtime is null
                    ? (saved.Enabled && config.Enabled ? "registered" : "disabled")
                    : pending ? "pending-reload" : runtime.State;
                var error = pending
                    ? "配置已保存；当前连接仍按上一次 reload 的设置运行，请执行 `mcp.reload` 应用。"
                    : runtime?.Error;

                views.Add(CreateView(config, state, error, runtime));
            }

            // 配置文件被直接改动/损坏而尚未 reload 时，也暴露仍存活的旧连接，避免 UI 误以为已断开。
            foreach (var runtime in _servers.Where(s => saved.Servers.All(c => c.Id != s.Config.Id)))
                views.Add(CreateView(runtime.Config, "pending-removal",
                    "配置中已没有此项；旧连接仍在运行，执行 `mcp.reload` 后才会关闭。", runtime));

            return new McpView(saved.Enabled, views, _problems.ToArray());
        }
    }

    private static McpServerView CreateView(
        McpServerConfig config, string state, string? error, ServerRuntime? runtime) =>
        new(config.Id, config.Enabled, config.Transport, SafeEndpoint(config),
            !string.IsNullOrWhiteSpace(config.BearerTokenEnvironmentVariable),
            config.BearerTokenEnvironmentVariable, config.AllowPrivateNetwork, config.TimeoutSeconds,
            config.ToolPermission.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            state, SafeError(config, error),
            runtime?.Tools.Select(t => new McpToolView(t.Tool.Name, t.RemoteName, t.Description, t.Permission)).ToList() ?? [],
            runtime?.StderrTail() ?? []);

    private static bool SameConfig(McpServerConfig left, McpServerConfig right) =>
        left.Id == right.Id && left.Enabled == right.Enabled && left.Transport == right.Transport &&
        left.Command == right.Command && left.WorkingDirectory == right.WorkingDirectory &&
        left.Endpoint == right.Endpoint &&
        left.BearerTokenEnvironmentVariable == right.BearerTokenEnvironmentVariable &&
        left.AllowPrivateNetwork == right.AllowPrivateNetwork && left.TimeoutSeconds == right.TimeoutSeconds &&
        left.Arguments.SequenceEqual(right.Arguments, StringComparer.Ordinal) &&
        MapEqual(left.Environment, right.Environment) && MapEqual(left.ToolPermission, right.ToolPermission);

    private static bool MapEqual(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value)
            && string.Equals(pair.Value, value, StringComparison.Ordinal));

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        var servers = Detach();
        foreach (var server in servers) await server.DisposeAsync().ConfigureAwait(false);
        _reloadGate.Dispose();
    }

    private List<ServerRuntime> Detach()
    {
        lock (_lock)
        {
            var copy = _servers.ToList();
            _servers.Clear();
            _tools = [];
            _problems.Clear();
            return copy;
        }
    }

    private async Task<ServerRuntime> ConnectAsync(McpServerConfig config, List<string> problems, CancellationToken ct)
    {
        var runtime = new ServerRuntime(config);
        var localProblems = new List<string>();
        try { await ConnectCoreAsync(runtime, localProblems, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            runtime.State = "error";
            runtime.Error = "启动被取消";
            await runtime.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            runtime.State = "error";
            runtime.Error = ex.Message;
            await runtime.DisposeAsync().ConfigureAwait(false);
        }
        lock (problems) problems.AddRange(localProblems);
        return runtime;
    }

    private async Task ConnectCoreAsync(ServerRuntime runtime, List<string> problems, CancellationToken ct)
    {
        if (runtime.Config.Transport == "stdio" && string.IsNullOrWhiteSpace(runtime.Config.Command))
        {
            runtime.State = "error";
            runtime.Error = "stdio transport 没有配置 command。";
            return;
        }

        if (runtime.Config.Transport == "http")
        {
            try { _ = McpHttpEndpointPolicy.Validate(runtime.Config); }
            catch (InvalidOperationException ex)
            {
                runtime.State = "error";
                runtime.Error = ex.Message;
                return;
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(McpLimits.ConnectTimeoutSeconds));
        try
        {
            IClientTransport transport;
            if (runtime.Config.Transport == "stdio")
            {
                transport = new StdioClientTransport(new StdioClientTransportOptions
                {
                    Name = runtime.Config.Id,
                    Command = runtime.Config.Command,
                    Arguments = runtime.Config.Arguments.ToList(),
                    WorkingDirectory = runtime.Config.WorkingDirectory ?? _workingDirectory,
                    InheritEnvironmentVariables = false,
                    EnvironmentVariables = ResolveEnvironment(runtime.Config, problems),
                    StandardErrorLines = runtime.AppendStderr,
                    ShutdownTimeout = TimeSpan.FromSeconds(5),
                });
            }
            else
            {
                var endpoint = McpHttpEndpointPolicy.Validate(runtime.Config);
                var headers = McpHttpEndpointPolicy.BuildHeaders(runtime.Config);
                var client = McpHttpEndpointPolicy.CreateClient(runtime.Config);
                transport = new HttpClientTransport(new HttpClientTransportOptions
                {
                    Name = runtime.Config.Id,
                    Endpoint = endpoint,
                    TransportMode = HttpTransportMode.StreamableHttp,
                    ConnectionTimeout = TimeSpan.FromSeconds(McpLimits.ConnectTimeoutSeconds),
                    AdditionalHeaders = headers,
                    MaxReconnectionAttempts = 0,
                }, client, loggerFactory: null, ownsHttpClient: true);
            }
            runtime.PendingTransport = transport;
            runtime.Client = await McpClient.CreateAsync(transport,
                new McpClientOptions { InitializationTimeout = TimeSpan.FromSeconds(McpLimits.ConnectTimeoutSeconds) },
                loggerFactory: null, cancellationToken: timeout.Token).ConfigureAwait(false);
            runtime.PendingTransport = null; // McpClient now owns and disposes the transport.

            var tools = await runtime.Client.ListToolsAsync(cancellationToken: timeout.Token).ConfigureAwait(false);
            if (tools.Count > McpLimits.MaxToolsPerServer)
                throw new InvalidOperationException($"server '{runtime.Config.Id}' 贡献 {tools.Count} 个工具，超过上限 {McpLimits.MaxToolsPerServer}。");

            foreach (var tool in tools)
            {
                var schema = tool.ProtocolTool.InputSchema.GetRawText();
                if (System.Text.Encoding.UTF8.GetByteCount(schema) > McpLimits.MaxSchemaBytes)
                    throw new InvalidOperationException($"工具 '{tool.Name}' 的参数 schema 超过 {McpLimits.MaxSchemaBytes} 字节。");
                if (System.Text.Encoding.UTF8.GetByteCount(tool.Description ?? "") > McpLimits.MaxDescriptionBytes)
                    throw new InvalidOperationException($"工具 '{tool.Name}' 的描述超过 {McpLimits.MaxDescriptionBytes} 字节。");
            }

            runtime.Tools = tools.Select(tool =>
            {
                var remote = tool.Name;
                var permission = ParsePermission(runtime.Config, remote, problems);
                var name = McpNames.Compose(runtime.Config.Id, remote);
                var schema = tool.ProtocolTool.InputSchema.GetRawText();
                return new ToolEntry(new McpTool(this, runtime.Config.Id, remote, name,
                    tool.Description ?? "", schema, permission, runtime.Config.Transport == "http"),
                    remote, tool.Description ?? "", permission);
            }).ToList();
            runtime.State = "connected";
            runtime.Error = null;
        }
        catch
        {
            throw;
        }
    }

    private static string? SafeEndpoint(McpServerConfig config)
    {
        if (config.Transport != "http" || !Uri.TryCreate(config.Endpoint, UriKind.Absolute, out var uri)) return null;
        // endpoint 可能配置错，甚至含有不该出现的 user-info；快照必须也能安全地呈现错误配置。
        var safe = new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri;
        return safe.GetLeftPart(UriPartial.Path);
    }

    private static string? SafeError(McpServerConfig config, string? error)
    {
        if (error is null || string.IsNullOrWhiteSpace(config.Endpoint)) return error;
        var safe = SafeEndpoint(config) ?? "<endpoint>";
        error = error.Replace(config.Endpoint, safe, StringComparison.Ordinal);
        if (Uri.TryCreate(config.Endpoint, UriKind.Absolute, out var uri))
            error = error.Replace(uri.AbsoluteUri, safe, StringComparison.Ordinal);
        return error;
    }

    private static PermissionClass ParsePermission(McpServerConfig config, string remoteTool, List<string> problems)
    {
        if (!config.ToolPermission.TryGetValue(remoteTool, out var text) || string.IsNullOrWhiteSpace(text))
            return PermissionClass.Writing;
        if (Enum.TryParse<PermissionClass>(text.Trim(), true, out var value) && Enum.IsDefined(value)) return value;
        problems.Add($"server '{config.Id}' 的工具 '{remoteTool}' 权限类别 '{text}' 不认识，已按 Writing 处理。");
        return PermissionClass.Writing;
    }

    private Dictionary<string, string?> ResolveEnvironment(McpServerConfig config, List<string> problems)
    {
        var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        foreach (var pair in config.Environment)
        {
            environment[pair.Key] = VariableReference().Replace(pair.Value, match =>
            {
                var value = Environment.GetEnvironmentVariable(match.Groups[1].Value);
                if (value is null)
                {
                    problems.Add($"server '{config.Id}' 的环境变量 '{pair.Key}' 引用了未定义的 ${{{match.Groups[1].Value}}}，已按空串处理。");
                    return "";
                }
                return value;
            });
        }
        return environment;
    }

    private ITool[] BuildTools(List<string> problems)
    {
        var tools = new List<ITool>();
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var server in _servers)
        foreach (var entry in server.Tools)
        {
            if (seen.TryGetValue(entry.Tool.Name, out var owner))
            {
                problems.Add($"工具名 '{entry.Tool.Name}' 冲突：server '{owner}' 与 '{server.Config.Id}' 都产出了它，后一个已跳过。");
                continue;
            }
            seen.Add(entry.Tool.Name, server.Config.Id);
            tools.Add(entry.Tool);
        }
        return tools.OrderBy(t => t.Name, StringComparer.Ordinal).ToArray();
    }

    private async Task MarkOfflineAsync(ServerRuntime server, McpClient client, string error)
    {
        var shouldDispose = false;
        lock (_lock)
        {
            if (ReferenceEquals(server.Client, client))
            {
                server.State = "offline";
                server.Error = error;
                server.Client = null;
                shouldDispose = true;
            }
        }
        if (shouldDispose)
        {
            try { await client.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { /* transport is already gone; keep the offline state */ }
        }
    }

    private static IReadOnlyDictionary<string, object?> ParseArguments(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, object?>();
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("参数必须是 JSON 对象。");
        return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone(), StringComparer.Ordinal);
    }

    private static IReadOnlyList<McpContent> MapContent(CallToolResult result)
    {
        var content = new List<McpContent>();
        foreach (var block in result.Content)
        {
            content.Add(block switch
            {
                TextContentBlock text => new McpText(text.Text),
                ImageContentBlock image => new McpImage(image.DecodedData.ToArray(), image.MimeType),
                AudioContentBlock => new McpUnsupported("audio"),
                ResourceLinkBlock link => new McpText($"[资源链接 {link.Uri}] {link.Name ?? ""}".TrimEnd()),
                EmbeddedResourceBlock embedded => embedded.Resource switch
                {
                    TextResourceContents text => new McpResource(text.Text, null, text.MimeType, text.Uri),
                    BlobResourceContents blob => new McpResource(null, blob.DecodedData.ToArray(), blob.MimeType, blob.Uri),
                    _ => new McpUnsupported("resource"),
                },
                _ => new McpUnsupported(block.Type ?? "unknown"),
            });
        }
        if (result.StructuredContent is { } structured) content.Add(new McpStructured(structured.GetRawText()));
        return content;
    }

    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex VariableReference();

    private sealed class ServerRuntime(McpServerConfig config) : IAsyncDisposable
    {
        private readonly Queue<string> _stderr = new();
        private readonly Lock _gate = new();
        public McpServerConfig Config { get; } = config;
        public McpClient? Client { get; set; }
        public IClientTransport? PendingTransport { get; set; }
        public SemaphoreSlim CallGate { get; } = new(1, 1);
        public List<ToolEntry> Tools { get; set; } = [];
        public string State { get; set; } = "disabled";
        public string? Error { get; set; }
        public void AppendStderr(string line)
        {
            lock (_gate)
            {
                _stderr.Enqueue(line);
                while (_stderr.Count > McpLimits.MaxStderrLines) _stderr.Dequeue();
            }
        }
        public IReadOnlyList<string> StderrTail() { lock (_gate) return _stderr.ToArray(); }
        public async ValueTask DisposeAsync()
        {
            await CallGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var client = Client;
                Client = null;
                if (client is not null)
                {
                    try { await client.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception) { }
                }
                else if (PendingTransport is IAsyncDisposable transport)
                {
                    PendingTransport = null;
                    try { await transport.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception) { }
                }
            }
            finally
            {
                // SemaphoreSlim owns no external handle unless WaitHandle was requested; leave it alive
                // so repeated/best-effort host disposal remains idempotent for this runtime.
                CallGate.Release();
            }
        }
    }

    private sealed record ToolEntry(McpTool Tool, string RemoteName, string Description, PermissionClass Permission);
}
