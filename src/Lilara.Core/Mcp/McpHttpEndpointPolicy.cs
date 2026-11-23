using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Lilara.Core.Network;

namespace Lilara.Core.Mcp;

/// <summary>
/// MCP remote endpoint policy. The endpoint is device configuration (not model input),
/// but DNS still resolves at connection time and must be pinned to the checked address.
/// </summary>
internal static partial class McpHttpEndpointPolicy
{
    public static Uri Validate(McpServerConfig config)
    {
        if (!Uri.TryCreate(config.Endpoint, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("HTTP transport requires an absolute http(s) endpoint without user-info or fragment.");

        var hasToken = !string.IsNullOrWhiteSpace(config.BearerTokenEnvironmentVariable);
        if (hasToken && uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Bearer authentication requires an HTTPS MCP endpoint.");
        if (uri.Scheme == Uri.UriSchemeHttp && !config.AllowPrivateNetwork)
            throw new InvalidOperationException("Plain HTTP MCP endpoints require explicit allowPrivateNetwork=true; use HTTPS for remote servers.");

        return uri;
    }

    public static Dictionary<string, string> BuildHeaders(McpServerConfig config)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var variable = config.BearerTokenEnvironmentVariable;
        if (string.IsNullOrWhiteSpace(variable)) return headers;
        if (!EnvironmentVariableName().IsMatch(variable))
            throw new InvalidOperationException("Bearer token environment variable name is invalid.");
        var token = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException($"Bearer token environment variable '{variable}' is empty or undefined.");
        if (token.Any(c => !(char.IsAsciiLetterOrDigit(c) || "-._~+/=".Contains(c))))
            throw new InvalidOperationException($"Bearer token environment variable '{variable}' contains characters outside the Bearer token syntax.");
        headers["Authorization"] = "Bearer " + token;
        return headers;
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentVariableName();

    public static HttpClient CreateClient(McpServerConfig config)
    {
        Validate(config);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(McpLimits.ConnectTimeoutSeconds),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = (context, ct) => ConnectAsync(context.DnsEndPoint, config.AllowPrivateNetwork, ct),
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static async ValueTask<Stream> ConnectAsync(DnsEndPoint endpoint, bool allowPrivate, CancellationToken ct)
    {
        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(endpoint.Host, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            throw new HttpRequestException($"MCP endpoint host '{endpoint.Host}' DNS lookup failed: {ex.Message}", ex);
        }

        // Always refuse cloud metadata/link-local addresses, even when private-network access is enabled.
        var allowed = addresses.Where(ip => !IsMetadataAddress(ip)
            && (allowPrivate || AddressPolicy.IsPublic(ip))).ToArray();
        if (allowed.Length == 0)
            throw new HttpRequestException($"MCP endpoint '{endpoint.Host}' resolved only to blocked local/private addresses.");

        Exception? last = null;
        foreach (var address in allowed)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                last = ex;
                if (ex is OperationCanceledException) throw;
            }
        }
        throw new HttpRequestException($"Could not connect to MCP endpoint host '{endpoint.Host}'.", last);
    }

    private static readonly IPAddress[] ExplicitMetadataAddresses =
    [
        IPAddress.Parse("100.100.100.200"), // Alibaba Cloud metadata
        IPAddress.Parse("fd00:ec2::254"),   // AWS IPv6 metadata
        IPAddress.Parse("fd20:ce::254"),     // Google Cloud metadata
    ];

    private static bool IsMetadataAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        return (address.AddressFamily == AddressFamily.InterNetwork && bytes[0] == 169 && bytes[1] == 254)
            || ExplicitMetadataAddresses.Any(ip => ip.Equals(address));
    }
}
