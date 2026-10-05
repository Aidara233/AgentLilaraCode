using Lilara.Core.Kernel;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Lilara.Core.Protocol.OpenAi;

// OpenAI 两条链路共用的 HTTP 层：唯一出现 HttpClient 的地方，纯函数层绝不知道网络存在。
// 认证缺省是 Bearer（OpenAI 兼容端点的通用约定），与 Claude 的 x-api-key 相反。
public sealed class OpenAiHttp
{
    /// <summary>OpenAI 兼容协议的官方兜底地址。绝不能兜底到 Anthropic 的地址——那是两个不同的 API。</summary>
    public const string OfficialUrl = "https://api.openai.com";

    /// <summary>全类共用一个：核心是常驻进程，每会话新建会泄漏 socket。本类不写 DefaultRequestHeaders，共用安全。</summary>
    private static readonly HttpClient Client = new();

    private readonly string _key;

    public string AuthScheme { get; }
    public ResolvedEndpoint Endpoint { get; }

    public OpenAiHttp(ProviderDefinition provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        // 缺省与 Claude 相反：OpenAI 兼容端点默认就是 Bearer。
        AuthScheme = provider.AuthScheme ?? AuthSchemes.Bearer;

        // envVar: null = 不读任何环境变量：Claude 那条读 ANTHROPIC_BASE_URL 是被官方 SDK 逼的，
        // OpenAI 这条由我们自己拼 URL，能拦就拦——否则会被 ANTHROPIC_BASE_URL 悄悄导向 Anthropic 的代理。
        // 也不发明 OPENAI_BASE_URL 之类的名字（生态里没有公认的那个，猜一个就是新的静默接管源）。
        // 参数必须按名传：getEnv 是第 2 个形参。
        Endpoint = EndpointResolver.Resolve(provider.Endpoint, officialUrl: OfficialUrl, envVar: null);
        _key = provider.ApiKey;
    }

    public async Task<string> PostAsync(string path, string body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ComposeUrl(Endpoint.Url, path));
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        if (AuthScheme == AuthSchemes.Bearer)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        else
            request.Headers.TryAddWithoutValidation("x-api-key", _key);

        try
        {
            using var response = await Client.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                throw new ModelCallFailedException(Describe(response.StatusCode, text));

            return text;
        }
        // ModelCallFailedException 要原样放行，否则 401 的人话会被下面的 catch 再包一层。
        catch (ModelCallFailedException) { throw; }
        // 调用方取消要放行，判据见 ShouldWrap。
        catch (Exception ex) when (ShouldWrap(ex, ct))
        {
            throw new ModelCallFailedException($"模型调用失败：{ex.Message}", ex);
        }
    }

    // 转发到 Cancellation.ShouldWrap——判据只有一份，别在这里加逻辑。
    internal static bool ShouldWrap(Exception ex, CancellationToken ct) =>
        Cancellation.ShouldWrap(ex, ct);

    // 拼端点与路径。裁掉端点末尾的 `/`：path 都以 `/` 开头，而 --endpoint http://host:port/ 是常见写法，
    // 不裁会拼出 `//v1/...`。只裁拼接用的这一份，Endpoint.Url 保持用户配置的原样。
    // 裁 / 是由 URL 语法推出的防御，未被上游观测过。
    internal static string ComposeUrl(string endpointUrl, string path) =>
        endpointUrl.TrimEnd('/') + path;

    /// <summary>把 HTTP 失败翻译成一句人话。</summary>
    internal static string Describe(HttpStatusCode status, string body) => status switch
    {
        HttpStatusCode.Unauthorized =>
            "API key 无效或已吊销（401）。检查 provider 配置里的 key。",
        HttpStatusCode.Forbidden =>
            "这个 key 没有访问该模型的权限（403）。",
        HttpStatusCode.NotFound =>
            "端点或模型不存在（404）。检查 provider 的 endpoint 与 model。",
        HttpStatusCode.TooManyRequests =>
            "被限流了（429）。等一会儿再试。",
        >= HttpStatusCode.InternalServerError =>
            $"上游 {(int)status}。这是临时的，重试即可。",
        _ => $"模型调用失败（{(int)status}）：{Truncate(body)}",
    };

    internal static string Truncate(string s) => s.Length <= 200 ? s : s[..200] + "…";
}
