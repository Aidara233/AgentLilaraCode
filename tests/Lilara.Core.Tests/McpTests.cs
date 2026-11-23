using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Lilara.Core.Kernel;
using Lilara.Core.Mcp;

namespace Lilara.Core.Tests;

public sealed class McpNamesTests
{
    [Theory]
    [InlineData("files", "list", "files__list")]
    [InlineData("my-server", "read_file", "my-server__read_file")]
    [InlineData("fs", "read.file", "fs__read_file")]
    [InlineData("fs", "IMAGE", "fs__IMAGE")]
    public void Compose_SanitizesRemoteName_AndKeepsShortNamesReadable(
        string serverId, string remoteName, string expected) =>
        Assert.Equal(expected, McpNames.Compose(serverId, remoteName));

    [Fact]
    public void Compose_LongNames_AreStableAndWithinProviderLimit()
    {
        var remote = new string('x', 100) + "-a";
        var first = McpNames.Compose("fs", remote);
        var second = McpNames.Compose("fs", remote);
        var otherTail = McpNames.Compose("fs", new string('x', 100) + "-b");

        Assert.Equal(first, second);
        Assert.NotEqual(first, otherTail);
        Assert.InRange(first.Length, 1, McpNames.MaxToolNameLength);
        Assert.Matches("^[a-zA-Z0-9_-]{1,64}$", first);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("UPPER", false)]
    [InlineData("valid-id", true)]
    [InlineData("valid123", true)]
    [InlineData("with.dot", false)]
    public void IsValidServerId_UsesPluginIdRule(string id, bool expected) =>
        Assert.Equal(expected, McpNames.IsValidServerId(id));
}

public sealed class McpHttpEndpointPolicyTests
{
    [Theory]
    [InlineData("https://mcp.example.com/mcp", null, false, true)]
    [InlineData("http://mcp.example.com/mcp", null, false, false)]
    [InlineData("http://127.0.0.1:8080/mcp", null, true, true)]
    [InlineData("http://127.0.0.1:8080/mcp", "MCP_TOKEN", true, false)]
    [InlineData("ftp://mcp.example.com/mcp", null, false, false)]
    [InlineData("https://mcp.example.com/mcp#fragment", null, false, false)]
    public void Validate_EnforcesHttpAndCredentialRules(
        string endpoint, string? tokenVariable, bool allowPrivate, bool expected)
    {
        var config = new McpServerConfig
        {
            Id = "remote",
            Transport = "http",
            Endpoint = endpoint,
            BearerTokenEnvironmentVariable = tokenVariable,
            AllowPrivateNetwork = allowPrivate,
        };

        if (expected) Assert.Equal(endpoint, McpHttpEndpointPolicy.Validate(config).AbsoluteUri);
        else Assert.Throws<InvalidOperationException>(() => McpHttpEndpointPolicy.Validate(config));
    }

    [Fact]
    public void BuildHeaders_LoadsBearerSecretFromEnvironmentWithoutChangingItsSourceConfig()
    {
        var variable = "LILARA_MCP_TEST_" + Guid.NewGuid().ToString("N");
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "fixture-token-123");
            var config = new McpServerConfig
            {
                Id = "remote",
                Transport = "http",
                Endpoint = "https://mcp.example.com/mcp",
                BearerTokenEnvironmentVariable = variable,
            };

            var headers = McpHttpEndpointPolicy.BuildHeaders(config);

            Assert.Equal("Bearer fixture-token-123", headers["Authorization"]);
            Assert.Equal(variable, config.BearerTokenEnvironmentVariable);
        }
        finally { Environment.SetEnvironmentVariable(variable, previous); }
    }

    [Fact]
    public async Task Snapshot_RedactsEndpointUserInfoAndQueryEvenWhenConfigIsInvalid()
    {
        using var temp = new TempDirectory();
        var store = new McpStore(temp.Path);
        store.Save(new McpConfig
        {
            Enabled = true,
            Servers =
            [
                new McpServerConfig
                {
                    Id = "remote",
                    Enabled = true,
                    Transport = "http",
                    Endpoint = "https://alice:pw@mcp.example.com/path?token=secret",
                },
            ],
        });

        await using var host = new McpHost(store, temp.Path);
        await host.ReloadAsync();
        var snapshot = host.Snapshot();

        Assert.Equal("https://mcp.example.com/path", Assert.Single(snapshot.Servers).Endpoint);
        var rendered = System.Text.Json.JsonSerializer.Serialize(snapshot, Kernel.Json.Options);
        Assert.DoesNotContain("alice:pw", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("token=secret", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectAsync_RejectsMetadataAddressEvenWhenPrivateNetworkIsAllowed()
    {
        var endpoint = new System.Net.DnsEndPoint("169.254.169.254", 80);
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await McpHttpEndpointPolicy.ConnectAsync(endpoint, allowPrivate: true, CancellationToken.None));
    }
}

public sealed class McpStreamableHttpTests
{
    [Fact]
    public async Task ReloadAndCall_UsesStreamableHttpAndKeepsToolTableStable()
    {
        using var temp = new TempDirectory();
        using var listener = new HttpListener();
        var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var stop = new CancellationTokenSource();
        var server = ServeAsync(listener, stop.Token);

        try
        {
            var store = new McpStore(temp.Path);
            store.Save(new McpConfig
            {
                Enabled = true,
                Servers =
                [
                    new McpServerConfig
                    {
                        Id = "remote",
                        Enabled = true,
                        Transport = "http",
                        Endpoint = $"http://127.0.0.1:{port}/mcp",
                        AllowPrivateNetwork = true,
                        TimeoutSeconds = 1,
                    },
                ],
            });

            await using var host = new McpHost(store, temp.Path);
            await host.ReloadAsync();

            var tool = Assert.Single(host.Tools);
            Assert.Equal("remote__echo", tool.Name);
            var result = await host.CallAsync("remote", "echo", "{\"text\":\"hello\"}", CancellationToken.None);
            Assert.Null(result.Failure);
            Assert.False(result.IsError);
            Assert.Equal("echo:hello", Assert.IsType<McpText>(Assert.Single(result.Content)).Value);

            var timedOut = await host.CallAsync("remote", "echo", "{\"text\":\"slow\"}", CancellationToken.None);
            Assert.Contains("超过 1 秒", timedOut.Failure, StringComparison.Ordinal);
            await Task.Delay(1100); // let the fixture finish its intentionally slow response

            using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    host.CallAsync("remote", "echo", "{\"text\":\"slow\"}", cancel.Token));
            await Task.Delay(2200); // drain the canceled fixture request before reload

            var snapshot = host.Snapshot();
            Assert.Equal("connected", Assert.Single(snapshot.Servers).State);
            Assert.Equal($"http://127.0.0.1:{port}/mcp", Assert.Single(snapshot.Servers).Endpoint);

            await host.ReloadAsync(); // reload disposes the old HTTP session and establishes a fresh one
            Assert.Equal("connected", Assert.Single(host.Snapshot().Servers).State);

            stop.Cancel();
            listener.Stop();
            try { await server; }
            catch (HttpListenerException) { }
            catch (ObjectDisposedException) { }

            var unavailable = await host.CallAsync("remote", "echo", "{\"text\":\"after-stop\"}", CancellationToken.None);
            Assert.NotNull(unavailable.Failure);
            await host.ReloadAsync();
            Assert.Equal("error", Assert.Single(host.Snapshot().Servers).State);
        }
        finally
        {
            stop.Cancel();
            listener.Stop();
            try { await server; }
            catch (HttpListenerException) { }
            catch (ObjectDisposedException) { }
        }
    }

    private static async Task<JsonNode> BuildToolCallResultAsync(JsonNode request, CancellationToken ct)
    {
        var text = request["params"]?["arguments"]?["text"]?.GetValue<string>();
        if (text == "slow") await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "echo:" + text }),
            ["isError"] = false,
        };
    }

    private static async Task ServeAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync().ConfigureAwait(false); }
            catch (HttpListenerException) when (ct.IsCancellationRequested) { return; }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested) { return; }

            {
                if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.Ordinal))
                {
                    context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                    context.Response.Close();
                    continue;
                }

                using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                var request = JsonNode.Parse(body)!;
                var method = request["method"]?.GetValue<string>() ?? "";
                var id = request["id"]?.DeepClone();
                if (id is null)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.Accepted;
                    context.Response.Close();
                    continue;
                }

                if (method == "server/discover")
                {
                    // This SDK probes the SEP-2575 discovery method first; legacy servers signal -32601,
                    // after which it falls back to the standard initialize handshake.
                    var unavailable = new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = id,
                        ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found" },
                    };
                    var unavailableBytes = Encoding.UTF8.GetBytes(unavailable.ToJsonString());
                    context.Response.StatusCode = (int)HttpStatusCode.OK;
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = unavailableBytes.Length;
                    await context.Response.OutputStream.WriteAsync(unavailableBytes, ct).ConfigureAwait(false);
                    context.Response.Close();
                    continue;
                }

                JsonNode result = method switch
                {
                    "initialize" => new JsonObject
                    {
                        ["protocolVersion"] = request["params"]?["protocolVersion"]?.GetValue<string>() ?? "2025-11-25",
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject { ["name"] = "fixture", ["version"] = "1" },
                    },
                    "tools/list" => new JsonObject
                    {
                        ["tools"] = new JsonArray(new JsonObject
                        {
                            ["name"] = "echo",
                            ["description"] = "Echo text",
                            ["inputSchema"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject { ["text"] = new JsonObject { ["type"] = "string" } },
                                ["required"] = new JsonArray("text"),
                            },
                        }),
                    },
                    "tools/call" => await BuildToolCallResultAsync(request, ct),
                    "ping" => new JsonObject(),
                    _ => throw new InvalidOperationException($"Unexpected MCP request '{method}'."),
                };

                var response = new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id,
                    ["result"] = result,
                };
                var bytes = Encoding.UTF8.GetBytes(response.ToJsonString());
                context.Response.StatusCode = (int)HttpStatusCode.OK;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
                context.Response.Close();
            }
        }
    }
}

public sealed class McpStoreTests
{
    [Fact]
    public void MissingOrCorruptConfig_IsDisabled()
    {
        using var temp = new TempDirectory();
        var store = new McpStore(temp.Path);

        Assert.False(store.Load().Enabled);
        File.WriteAllText(store.Path, "not json");
        Assert.False(store.Load().Enabled);
    }

    [Fact]
    public void SaveAndLoad_NormalizesBounds_AndKeepsSensitiveEnvironmentOutOfPublicView()
    {
        using var temp = new TempDirectory();
        var store = new McpStore(temp.Path);
        store.Save(new McpConfig
        {
            Enabled = true,
            Servers =
            [
                new McpServerConfig
                {
                    Id = " fs ",
                    TimeoutSeconds = int.MaxValue,
                    Environment = new Dictionary<string, string> { ["TOKEN"] = "${TOKEN}" },
                    ToolPermission = new Dictionary<string, string> { ["write"] = "Destructive" },
                },
            ],
        });

        var loaded = store.Load();
        var server = Assert.Single(loaded.Servers);
        Assert.True(loaded.Enabled);
        Assert.Equal("fs", server.Id);
        Assert.Equal(McpLimits.MaxTimeoutSeconds, server.TimeoutSeconds);
        Assert.Equal("${TOKEN}", server.Environment["TOKEN"]);
        Assert.Equal("Destructive", server.ToolPermission["write"]);
        Assert.DoesNotContain("${TOKEN}", System.Text.Json.JsonSerializer.Serialize(
            new McpView(false, [], []), Kernel.Json.Options));
    }
}

public sealed class ToolTableTests
{
    [Fact]
    public void Build_UsesOrdinalNameOrder_AndReportsBothOwnersOnCollision()
    {
        var a = new StubTool("zeta", PermissionClass.ReadOnly);
        var b = new StubTool("Alpha", PermissionClass.ReadOnly);
        var sorted = Lilara.Core.Session.Tools.ToolTable.Build(("built-in", new ITool[] { a, b }));
        Assert.Equal(new[] { "Alpha", "zeta" }, sorted.Select(t => t.Name));

        var collision = new StubTool("zeta", PermissionClass.Writing);
        var error = Assert.Throws<InvalidOperationException>(() =>
            Lilara.Core.Session.Tools.ToolTable.Build(
                ("built-in", new ITool[] { a }), ("MCP filesystem", new ITool[] { collision })));
        Assert.Contains("built-in", error.Message, StringComparison.Ordinal);
        Assert.Contains("MCP filesystem", error.Message, StringComparison.Ordinal);
    }

    private sealed class StubTool(string name, PermissionClass permission) : ITool
    {
        public string Name { get; } = name;
        public PermissionClass PermissionClass { get; } = permission;
        public string Description => "test";
        public string ParametersSchemaJson => "{\"type\":\"object\"}";
        public Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct) =>
            Task.FromResult(new ToolOutcome { Content = [] });
    }
}

internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "Lilara-McpTests-" + Guid.NewGuid().ToString("N"));

    public TempDirectory() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
