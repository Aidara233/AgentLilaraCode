using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Lilara.Core.Providers;

namespace Lilara.Core.Adapters.OpenAi;

/// <summary>
/// OpenAI 两条链路共用的 HTTP 层。
/// ⚠️ **本文件是整个计划里唯一出现 `HttpClient` 的地方**——纯函数层绝不知道网络存在。
/// ⚠️ 认证缺省是 **Bearer**（OpenAI 兼容端点的通用约定），与 Claude 那套（x-api-key）相反。
/// </summary>
public sealed class OpenAiHttp
{
    private readonly HttpClient _client = new();
    private readonly string _key;

    public string AuthScheme { get; }
    public ResolvedEndpoint Endpoint { get; }

    public OpenAiHttp(ProviderDefinition provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        // ⚠️ 缺省与 Claude 相反：OpenAI 兼容端点默认就是 Bearer
        AuthScheme = provider.AuthScheme ?? AuthSchemes.Bearer;
        Endpoint = EndpointResolver.Resolve(provider.Endpoint);
        _key = provider.ApiKey;
    }

    public async Task<string> PostAsync(string path, string body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint.Url + path);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        if (AuthScheme == AuthSchemes.Bearer)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        else
            request.Headers.TryAddWithoutValidation("x-api-key", _key);

        try
        {
            using var response = await _client.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                throw new ModelCallFailedException(Describe(response.StatusCode, text));

            return text;
        }
        // ⚠️ ModelCallFailedException 要**原样放行**——否则上面那句 401 的人话
        // 会被下面这个 catch 再包一层，变成「模型调用失败：API key 无效或已吊销（401）…」。
        // 这正是 Claude 那条踩过的坑（Build 的异常被重复包装）。
        catch (ModelCallFailedException) { throw; }
        // ⚠️ 调用方取消要放行。判据抽成 ShouldWrap——见类末的说明。
        catch (Exception ex) when (ShouldWrap(ex, ct))
        {
            throw new ModelCallFailedException($"模型调用失败：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// 这个异常该不该包成 <see cref="ModelCallFailedException"/>？
    /// ⚠️ **取消不是失败**：用户按 Ctrl-C 要原样冒出去（03 §3 <c>session.interrupt</c>、
    /// 01 §9 的工具取消钩子），不能被报成「模型调用失败」。
    /// 但反过来，<c>HttpClient</c> 超时**也**是以 <c>TaskCanceledException</c> 到达的，
    /// 那是一次真失败，必须包。所以判据是「**调用方自己的 token 被取消了没有**」，不是异常类型。
    ///
    /// <para>⚠️ <b>为什么是 `internal static` 而不是内联进 catch 过滤器</b>：抽成纯函数是为了
    /// 能把这张真值表离线测干净。对齐 Claude 的既有做法——<c>ClaudeAdapter.ShouldWrap</c>
    /// 早就是这个形状且有三条测试（普通失败 / 超时长相像取消 / 调用方取消），
    /// 内联进 catch 过滤器后就再也测不到了，是相对它的退步。</para>
    /// <para>⚠️ 两个 OpenAI 适配器都调这一份。<b>不与 Claude 共用</b>——那是两个独立的减法族各自的边界谓词，
    /// 各自有测试；出现第三份再抽（`InternalsVisibleTo` 已在 `Lilara.Core.csproj`）。</para>
    /// </summary>
    internal static bool ShouldWrap(Exception ex, CancellationToken ct) =>
        ex is not OperationCanceledException || !ct.IsCancellationRequested;

    /// <summary>把 HTTP 失败翻译成一句人话。</summary>
    private static string Describe(HttpStatusCode status, string body) => status switch
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

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200] + "…";
}
