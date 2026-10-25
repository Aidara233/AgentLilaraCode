using System.Net.Http.Headers;
using System.Text.Json;
using Lilara.Core.Kernel;
using Lilara.Core.Protocol.OpenAi;

namespace Lilara.Core.Protocol;

/// <summary>从上游临时查询模型目录；不读写 provider 配置，也不调用模型生成接口。</summary>
public sealed class ProviderModelCatalog
{
    private static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly TimeSpan _timeout;

    public ProviderModelCatalog(TimeSpan? timeout = null) => _timeout = timeout ?? TimeSpan.FromSeconds(10);

    public async Task<IReadOnlyList<string>> ListAsync(ProviderDefinition provider, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var (url, claude) = Resolve(provider);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var auth = provider.AuthScheme ?? (claude ? AuthSchemes.Default : AuthSchemes.Bearer);
        if (claude)
        {
            if (auth == AuthSchemes.Bearer) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);
            else request.Headers.TryAddWithoutValidation("x-api-key", provider.ApiKey);
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }
        else if (auth == AuthSchemes.Bearer)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);
        else
            request.Headers.TryAddWithoutValidation("api-key", provider.ApiKey);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_timeout);
        try
        {
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            var body = await response.Content.ReadAsStringAsync(deadline.Token);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"上游返回 {(int)response.StatusCode}：{Trim(body)}");
            return claude ? ParseClaude(body) : ParseOpenAi(body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"查询模型列表超过 {_timeout.TotalSeconds:0} 秒。");
        }
    }

    public static (string Url, bool Claude) Resolve(ProviderDefinition provider)
    {
        if (provider.Protocol == Protocols.ClaudeMessages)
        {
            var endpoint = EndpointResolver.Resolve(provider.Endpoint);
            return (OpenAiHttp.ComposeUrl(endpoint.Url, "/v1/models"), true);
        }
        if (provider.Protocol is Protocols.OpenAiChat or Protocols.OpenAiResponses)
        {
            var endpoint = EndpointResolver.Resolve(provider.Endpoint, officialUrl: OpenAiHttp.OfficialUrl, envVar: null);
            return (OpenAiHttp.ComposeUrl(endpoint.Url, "/v1/models"), false);
        }
        throw new InvalidOperationException($"不支持查询协议 '{provider.Protocol}' 的模型列表。");
    }

    public static IReadOnlyList<string> ParseOpenAi(string json) => ParseDataIds(json);
    public static IReadOnlyList<string> ParseClaude(string json) => ParseDataIds(json);

    private static IReadOnlyList<string> ParseDataIds(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("模型列表响应缺少 data 数组。");
        var ids = new List<string>();
        foreach (var item in data.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(id.GetString()))
                ids.Add(id.GetString()!);
        return ids.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static string Trim(string body) => body.Length <= 300 ? body : body[..300] + "…";
}
