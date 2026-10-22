using Lilara.Core.Kernel;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

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

    /// <summary>
    /// **最近一次 `PostAsync` 收到的 HTTP 状态码**（成功或失败都记；没发请求 / 传输失败时为 `null`）。
    /// ⚠️ 只为可测性：`Describe` 拿不到「这是哪一次请求」，而抛出的异常上也不带状态码
    /// ⇒ 「那个瞬态标志真的被打上了」只能在这一层被观察。
    /// ⚠️ 普通字段、**不是** `AsyncLocal`：后者在读的那个 `async` 方法里是另一份副本（实测读到 null）。
    /// </summary>
    internal static System.Net.HttpStatusCode? LastStatus;

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

    public async Task<string> PostAsync(
        string path, string body, CancellationToken ct, HttpMessageHandler? transport = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ComposeUrl(Endpoint.Url, path));
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        if (AuthScheme == AuthSchemes.Bearer)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        else
            request.Headers.TryAddWithoutValidation("x-api-key", _key);

        // ⚠️ 生产路径走共用的那个 client（不泄漏 socket）；测试传 `transport` 时才现造一个。
        using var oneOff = transport is null ? null : new HttpClient(transport, disposeHandler: false);
        var client = oneOff ?? Client;

        try
        {
            LastStatus = null;
            using var response = await client.SendAsync(request, ct);
            LastStatus = response.StatusCode;
            var text = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                throw new ModelCallFailedException(Describe(response.StatusCode, text))
                {
                    // ⚠️ 打标只能在这一行：`Describe` 是纯函数、拿不到「这是哪一次请求」，
                    // 这个异常也不带状态码字段 ⇒ 这是唯一知道「该不该再发一次」的地方。
                    // 判据本身在 `RetryPolicy.IsTransientStatus`，两条协议共用同一张表。
                    Transient = RetryPolicy.IsTransientStatus(response.StatusCode),
                };

            return text;
        }
        // ModelCallFailedException 要原样放行，否则 401 的人话会被下面的 catch 再包一层。
        catch (ModelCallFailedException) { throw; }
        // 调用方取消要放行，判据见 ShouldWrap。
        catch (Exception ex) when (ShouldWrap(ex, ct))
        {
            // ⚠️ **这里的一切都是瞬态**：没拿到响应（连不上 / DNS / 超时），或者拿到了一个读不动的响应。
            // 调用方自己的取消已经在 `ShouldWrap` 那一关被放行了，所以走到这里的一定是「这一次没成」。
            throw new ModelCallFailedException($"模型调用失败：{ex.Message}", ex) { Transient = true };
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
        // 401/403 把上游的原话带上——真正的病因常常只在那里
        //（实测：`"cause":"API Key 所属分组已删除"`，而笼统的「没有权限」让人无从下手）。
        HttpStatusCode.Unauthorized =>
            "API key 无效或已吊销（401）。检查 provider 配置里的 key。" + UpstreamSays(body),
        HttpStatusCode.Forbidden =>
            "这个 key 没有访问该模型的权限（403）。" + UpstreamSays(body),
        HttpStatusCode.NotFound =>
            "端点或模型不存在（404）。检查 provider 的 endpoint 与 model。",
        HttpStatusCode.TooManyRequests =>
            "被限流了（429）。要等一会儿——会自动重试。",
        >= HttpStatusCode.InternalServerError =>
            $"上游 {(int)status}。这是临时的——会自动重试。",
        _ => $"模型调用失败（{(int)status}）：{Truncate(body)}",
    };

    // 上游说了一句话就带上它。空/读不出 ⇒ 空串（不印半截「上游说：」）。
    internal static string UpstreamSays(string body)
    {
        var detail = Detail(body);
        return detail.Length == 0 ? "" : $" 上游说：{detail}";
    }

    // 从错误体里抠人话。两种形状都见：`{"error":{"message":"…"}}`（OpenAI 规范）
    // 与 `{"error":"…"}`（简写，本机网关实测有）。
    // ⚠️ 全程 ValueKind 判据：`TryGetProperty` 在非对象元素上抛
    //（三位协议链路的 T6/T8 都踩过这个坑）。
    internal static string Detail(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Truncate(body);
            if (!root.TryGetProperty("error", out var err)) return Truncate(body);

            if (err.ValueKind == JsonValueKind.String) return Truncate(err.GetString() ?? "");
            if (err.ValueKind == JsonValueKind.Object
                && err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                return Truncate(m.GetString() ?? "");

            return Truncate(body);
        }
        catch (JsonException)
        {
            return Truncate(body);
        }
    }

    internal static string Truncate(string s) => s.Length <= 200 ? s : s[..200] + "…";
}
