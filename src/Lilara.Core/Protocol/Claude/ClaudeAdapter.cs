using Anthropic;
using Lilara.Core.Kernel;

namespace Lilara.Core.Protocol.Claude;

// Claude Messages 适配器：走官方 C# SDK，不手搓 HTTP。构造请求 → 发出去 → 映射回来，翻译逻辑全在两个纯函数里。
public sealed class ClaudeAdapter : IModelAdapter
{
    public const string ProtocolId = Protocols.ClaudeMessages;

    private readonly AnthropicClient _client;

    /// <summary>
    /// 一次调用的时限（provider 的 `timeoutSeconds`）。⚠️ <c>null</c> = 没配 = **不给 SDK 加任何东西**
    /// （沿用它的默认值）；有值 ⇒ 用链接令牌包住这一次调用。
    /// </summary>
    private readonly TimeSpan? _timeout;

    // 解析出来的端点（含来源）。暴露出来是为了能显示——命令面启动时必须把它印出来。
    public ResolvedEndpoint? Endpoint { get; }

    /// <inheritdoc />
    public string Protocol => ProtocolId;

    /// <summary>实际采用的认证方式（解析后的，不是原始配置）。</summary>
    public string AuthScheme { get; }

    public ClaudeAdapter(ProviderDefinition provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        // 两种认证方式走不同属性：ApiKey → x-api-key，AuthToken → Authorization: Bearer。
        // 本机网关只认后者（ApiKey 打不通、AuthToken 通）。这是协议怪癖，不是可选优化。
        AuthScheme = provider.AuthScheme ?? AuthSchemes.Default;

        if (provider.Protocol != ProtocolId)
            throw new ArgumentException(
                $"ClaudeAdapter 只认协议 '{ProtocolId}'，收到 '{provider.Protocol}'",
                nameof(provider));

        // 始终显式设 BaseUrl，哪怕是解析到官方地址：不设的话客户端会自己去读 ANTHROPIC_BASE_URL，
        // 那条路径绕过 Endpoint 属性，「谁定的端点」就没人说得出来。
        var endpoint = EndpointResolver.Resolve(provider.Endpoint);

        _client = AuthScheme == AuthSchemes.Bearer
            ? new AnthropicClient { AuthToken = provider.ApiKey, BaseUrl = endpoint.Url }
            : new AnthropicClient { ApiKey = provider.ApiKey, BaseUrl = endpoint.Url };

        Endpoint = endpoint;

        // ⚠️ **没配就不设**（`null`）：这条链路一直交给 SDK 的默认值，替它定一个数字
        // 只会让「本来跑得完的推理」悄悄变短（见 `ProviderDefinition.TimeoutSeconds` 的注释）。
        // 配了就由我们自己的链接令牌管——见 `CallAsync`。
        _timeout = provider.EffectiveTimeoutSeconds is { } seconds
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }

    public async Task<ModelReply> CallAsync(ModelRequest request, CancellationToken ct)
    {
        // ⚠️ 配了时限才包一层链接令牌；没配就是原来的 `ct`（SDK 的默认值管超时）。
        using var deadline = _timeout is { } limit ? CancellationTokenSource.CreateLinkedTokenSource(ct) : null;
        if (deadline is not null) deadline.CancelAfter(_timeout!.Value);
        var token = deadline?.Token ?? ct;

        // Build 必须在 try 里面：它的三处 ArgumentException（时间线为空 / 消息为空 / 未知 effort）
        // 都是「这次调用失败了」的一种，逃出包装会以未捕获异常崩掉，而不是给用户一句能读懂的话。
        try
        {
            var parameters = ClaudeRequestBuilder.Build(request);
            var response = await _client.Messages.Create(parameters, cancellationToken: token);
            return ClaudeResponseMapper.Map(response);
        }
        catch (Exception ex) when (ShouldWrap(ex, ct))
        {
            // 超时与「真失败」分开说：SDK 抛出来的原话里没有「这是我们自己设的时限」这件事，
            // 照着它去查只会查到上游。判据是两把令牌谁被取消了（调用方取消已在 ShouldWrap 放行）。
            var why = deadline is { IsCancellationRequested: true } && !ct.IsCancellationRequested
                ? $"模型调用超时（{_timeout!.Value.TotalSeconds:0} 秒）。"
                  + "慢模型可以给该提供商调大 timeoutSeconds（`provider set <id> --timeout <秒>`）。"
                : Describe(ex);
            throw new ModelCallFailedException(why, ex) { Transient = IsTransient(ex) };
        }
    }

    // 转发到 Cancellation.ShouldWrap——判据只有一份，别在这里加逻辑。
    internal static bool ShouldWrap(Exception ex, CancellationToken ct) =>
        Cancellation.ShouldWrap(ex, ct);

    /// <summary>
    /// **这次失败值不值得再发一次**（`RetryPolicy` 读它）。
    ///
    /// <para>⚠️ <b>判据的主体是「状态码」，不是「哪一个异常类」</b>：SDK 的
    /// <see cref="Anthropic.Exceptions.AnthropicApiException"/> 上带着
    /// <c>StatusCode</c>（本地反射核过），而它同时是 4xx / 5xx 那一族的基类 ⇒
    /// 拿状态码去过 <see cref="RetryPolicy.IsTransientStatus"/> 与 OpenAI 那条链路**是同一张表**。
    /// 按类名各写一份会让两条协议在「529 算不算 5xx」「408 算不算临时」这种地方悄悄分叉。</para>
    ///
    /// <para>⚠️ <b>例外一条是 `AnthropicIOException`</b>：它<b>不继承</b> `AnthropicApiException`
    /// （反射确认：直接继承 <c>AnthropicException</c>），表示「请求没发出去 / 没拿到响应」
    /// （本地实测：连一个拒绝连接的端口拿到的就是它）⇒ 与 OpenAI 那条 `ShouldWrap` 的兜底同一类，
    /// 一律瞬态。</para>
    ///
    /// <para>⚠️ 其余一律 <c>false</c>：400 / 422（请求内容的问题）、`AnthropicInvalidDataException`
    /// （响应读不出来——那是我们或上游的形状问题，再发一次同样读不出来）。
    /// ⚠️ 本方法**不会被用户取消走到**：`ShouldWrap` 在那之前已经把取消放行了。</para>
    /// </summary>
    internal static bool IsTransient(Exception ex) => ex switch
    {
        Anthropic.Exceptions.AnthropicApiException api => RetryPolicy.IsTransientStatus(api.StatusCode),
        Anthropic.Exceptions.AnthropicIOException => true,
        _ => false,
    };

    /// <summary>
    /// 把 SDK 的异常翻译成一句人话。
    ///
    /// <para>⚠️ <b>原则：能拿到上游原因的地方，一定带上去。</b>
    /// 400 那一支原先只印一句「多半是参数不合法」——而**上游已经在那句响应体里写明是哪里不合法了**，
    /// 我们把它扔了。实测（2026-10-08）：一个真实的 400 只换来一句「检查模型 id 与 max tokens」，
    /// 而真正的原因无从得知（同族：这个文件下面那段 401 的注释讲的就是同一个毛病）。</para>
    /// </summary>
    internal static string Describe(Exception ex) => ex switch
    {
        // ⚠️ **带上上游说的话**：`AnthropicException.Message` 里就含响应体，
        // 而 400 是**唯一**一类「原因只在上游那边、我们这边猜不出来」的错误。
        Anthropic.Exceptions.AnthropicBadRequestException =>
            "请求被上游拒绝（400）。上游说：" + ex.Message,
        Anthropic.Exceptions.AnthropicUnauthorizedException =>
            "API key 无效或已吊销（401）。检查 `provider set` 里的 --key。",
        Anthropic.Exceptions.AnthropicForbiddenException =>
            "这个 key 没有访问该模型的权限（403）。",
        Anthropic.Exceptions.AnthropicNotFoundException =>
            "模型 id 不存在（404）。检查 --model 拼写。",
        Anthropic.Exceptions.AnthropicRateLimitException =>
            "被限流了（429）。要等一会儿——会自动重试。",
        Anthropic.Exceptions.Anthropic5xxException =>
            "上游 5xx。这是临时的——会自动重试。",

        // 上游回了**不符合 Anthropic 规范的响应体**时，SDK 内部的 STJ 会抛这句
        //（实测：本机网关 401 回 `{"error":"Authentication required"}`——规范要求 `error` 是
        // **对象**、里面再放 `type`/`message`，而它是个**字符串**；SDK 去读 `.error.type` 就抛在这）。
        // ⚠️ 它不是 `AnthropicException` 子类（**认不出 HTTP 状态码**）⇒ 不接的话会落进兜底，
        // 把 STJ 的机器话原样吐给用户——「模型调用失败：The requested operation requires an
        // element of type 'Object'…」。那条消息里**没有一个字**能指向真正的原因。
        _ when ex.Message.Contains("requires an element of type", StringComparison.Ordinal) =>
            "上游回的响应体不符合 Anthropic 规范（没能读出错误的对象结构）。"
            + "多半是**端点不对**，或**认证没被接受**——本机网关通常只认 `--auth bearer`，"
            + "官方 anthropic.com 只认 `--auth api-key`。",

        _ => $"模型调用失败：{ex.Message}",
    };
}
