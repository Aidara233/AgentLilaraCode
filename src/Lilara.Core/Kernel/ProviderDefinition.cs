using System.Text.Json.Serialization;

namespace Lilara.Core.Kernel;

// 提供商定义，存全局（含 key）；会话只存「引用 + 选择」，不存 key。
public sealed record ProviderDefinition
{
    /// <summary>
    /// 磁盘格式版本。语义同 <see cref="SessionSpec.Version"/>。
    /// ⚠️ 它**进不了线上**：`/providers` 端点是逐个字段手选再交出去的（key 就是那样被挡在外面的），
    /// 所以加这个字段不会顺手把它漏给头。
    /// </summary>
    public int Version { get; init; } = Json.FormatVersion;

    public required string Id { get; init; }

    // 协议类型。适配器按它分派（内建，焊死）。
    public required string Protocol { get; init; }

    // 明文。数据根是本用户私有的。
    public required string ApiKey { get; init; }

    public required string DefaultModel { get; init; }

    /// <summary>
    /// **前缀缓存的显式断点**（2026-10-28）：`null` / `auto` = 不标记（默认，交给上游的隐式缓存）；
    /// `explicit` = 在请求里标断点（Anthropic 全部模型、OpenAI GPT-5.6+、以及按同一语义实现的网关）。
    ///
    /// <para>⚠️ <b>为什么是 provider 级而不是全局</b>：三种实现差得很远——Anthropic **没有**隐式缓存
    /// （不标就全价）、OpenAI 默认有隐式但只有 5.6+ 认显式断点、DeepSeek 系自动前缀缓存标了也没用。
    /// ⇒ 按端点开，而不是按代码开。</para>
    /// <para>⚠️ <b>可空是语义</b>：`null` = 「没声明」= `auto`。老 `providers/*.json` 里没有这个键，
    /// 读出来就是 `null` ⇒ 三条链路发包的字节与加这个字段之前**逐字节相同**。</para>
    /// </summary>
    public string? CacheMode { get; init; }

    /// <summary>
    /// **思考强度要不要进请求参数**（2026-10-28）：`null` / `auto` = 不发（默认，请求字节不变）；
    /// `reasoning` = 发（Chat 走顶层 `reasoning_effort`，Responses 走 `reasoning.effort`）。
    ///
    /// <para>⚠️ <b>默认关的理由</b>：`reasoning_effort` 不是所有端点都认——真 OpenAI 的推理模型认、
    /// DeepSeek 认，而 GLM / doubao / 网关代理对未知参数的处理不一致（有的 400，有的静默忽略）。
    /// 本仓的 provider 里这类端点是多数 ⇒ 无条件发等于拿能跑的会话换一条新字段。</para>
    ///
    /// <para>⚠️ <b>但「不发」也是一种缺陷，而且更难查</b>：`Axes.Effort` 是 `InContext = false`
    /// 的纯参数轴，它<b>只有</b>进请求参数这一条路。此前两条 OpenAI 链路一次都没读过
    /// `ModelRequest.Effort`（只有 Claude 读）⇒ 改 effort 时线上字节<b>逐字节相同</b>：
    /// 没有错误、没有日志，只有一个永远不变的档位。⇒ 开关是「关着但它存在」，不是「没有」。</para>
    ///
    /// <para>判据与取值见 <see cref="Protocol.EffortParam"/>。</para>
    /// </summary>
    public string? EffortParam { get; init; }

    /// <summary>
    /// **Responses 请求要不要 `include: ["reasoning.encrypted_content"]`**（2026-10-28）：
    /// `null` / `auto` = 不发（默认，请求字节不变）；`include` = 发。
    ///
    /// <para>⚠️ <b>它解决的是本仓架构下推理回传的唯一可行路径</b>：本仓<b>不用</b>
    /// `previous_response_id`（全量重放自己的时间线）⇒ 服务端那份推理拿不到，只能靠
    /// 客户端带着 `encrypted_content` 重放。没有这个字段，响应里的 reasoning 项就没有
    /// 加密载荷 ⇒ 即使响应侧把它记下来，下一轮发回去的也只是一段摘要（对真 OpenAI 而言
    /// 「不相关的推理项会被丢弃」）。</para>
    ///
    /// <para>⚠️ <b>为什么也要按端点开</b>：`include` 是 OpenAI 的形状，兼容网关不一定认
    /// （未知 `include` 值可能 400）；而 `encrypted_content` 会让<b>每条</b> reasoning 项
    /// 变大一坨密文——它对缓存前缀是**稳定的**（同一轮同一串字节），但体积是真的。</para>
    /// </summary>
    public string? ReasoningInclude { get; init; }

    /// <summary>
    /// 这个提供商**可选的**模型清单（头把会话的模型画成下拉，选项就从这里来）。
    ///
    /// <para>⚠️ **加法式可空**：缺失即反序列化成 `null`。可空是**语义**（「没声明清单」），
    /// 不再是「为了让既有 `providers/*.json` 逐字节不变」——那件事现在由**格式版本**承担。</para>
    ///
    /// <para>⚠️ **与 <see cref="DefaultModel"/> 的关系**：那一个仍是「没指定时用哪个」，
    /// 这一份是「有哪些可挑」。清单**可以**不含 <see cref="DefaultModel"/>——
    /// 没有强制包含的理由，读的一方（头）把当前值补进选项即可。
    /// `null` 或空 = 「没声明清单」⇒ 头退回一个普通文本框。**不是**「没有模型可用」。</para>
    /// </summary>
    public IReadOnlyList<string>? Models { get; init; }

    // 模态能力声明：是提供商配置的一部分，不是适配器的运行时契约。最小闭环用不到。
    public required string Modality { get; init; }

    // 端点。空则用官方地址。必须显式设进客户端：只设 ApiKey 的话，环境里的
    // ANTHROPIC_BASE_URL 会静默接管请求去向。可空是**语义**（「没配端点」）。
    public string? Endpoint { get; init; }

    // 认证方式：ApiKey（x-api-key，官方）或 Bearer（Authorization: Bearer，本机网关）。
    // 可空——缺失按 AuthSchemes.Default 处理。
    public string? AuthScheme { get; init; }

    /// <summary>
    /// **一次模型调用的超时**（秒）。⚠️ 可空是**语义**（「没配」），不是「为了让老文件不变」——
    /// 那件事由格式版本承担（同 <see cref="Models"/>）。
    ///
    /// <para>⚠️ <b>`null` = 交给协议自己的默认</b>，两条链路**不一样**，这是刻意的：
    /// OpenAI 兼容链路此前吃的是 .NET `HttpClient` 的隐式 100 秒（`OpenAiHttp` 里那个共用客户端），
    /// 而 Claude 链路一直交给 SDK 的默认值。给一个统一的数字会让其中一条**悄悄变短**，
    /// 而「变短」的症状是「本来能跑完的推理被砍掉」——没人会把它归因到这次默认值改动。</para>
    ///
    /// <para>⚠️ <b>它管的是「一次请求」</b>，不是「一步」。重试是另一层（`RetryPolicy`，
    /// 十次退避且不封顶）：最坏的总时长 ≈ (超时 × 11) + 511.5 秒 ⇒ 把它调到分钟级时，
    /// 一个挂死的端点会把一步拖很久（100 秒默认值下约 27 分钟）。这是**用户明确要的那个自由度**，
    /// 不是漏洞；但改这个数的人要知道乘 11 这件事。</para>
    /// </summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>这个提供商**实际生效**的超时秒数（`null` ⇒ 协议自己的默认，见调用点）。</summary>
    [JsonIgnore]
    public int? EffectiveTimeoutSeconds => ProviderLimits.Normalize(TimeoutSeconds);

    // 「有没有 key」全项目只此一份判据，别在别处再拼一遍——空串与缺失等价（都是「没给」）。
    // 全空白今天算「有」，这一点的语义尚未裁决。
    public static bool HasKey(string? apiKey) => !string.IsNullOrEmpty(apiKey);

    // 转发到 HasKey，别在这里再拼一遍。JsonIgnore 不是可选的：Json.Options 没有
    // IgnoreReadOnlyProperties，少了它每个 providers/<id>.json 会多出一个 hasApiKey 键；
    // 它是派生值、不是数据，本就不该落盘。
    [JsonIgnore]
    public bool HasApiKey => HasKey(ApiKey);
}

/// <summary>
/// 模型调用超时的**硬界限**（与 <c>NetworkLimits</c> / <c>ShellLimits</c> 同一族：
/// 数值只有这一份，两条协议都从这里读）。
///
/// <para>⚠️ <b><see cref="DefaultTimeoutSeconds"/> 只对 OpenAI 兼容链路生效</b>
/// （它原来就是 .NET <c>HttpClient</c> 的隐式默认，这里把它变得可见、可解释）。
/// Claude 链路没配时**不套这个数**——那是 SDK 的默认值，见
/// <see cref="ProviderDefinition.TimeoutSeconds"/> 的注释。</para>
/// </summary>
public static class ProviderLimits
{
    /// <summary>
    /// OpenAI 兼容链路的单次请求默认超时（秒）。⚠️ 与 .NET <c>HttpClient</c> 的默认值**相同**，
    /// 因为这条链路此前就是它在管——把隐式默认写成显式常量，行为不变而读得懂。
    /// </summary>
    public const int DefaultTimeoutSeconds = 100;

    /// <summary>允许的下限（秒）。低于它几乎一定是配置写错了。</summary>
    public const int MinTimeoutSeconds = 1;

    /// <summary>
    /// 允许的上限（秒，30 分钟）。⚠️ 它是**单次请求**的上限，不是一步的——
    /// 重试最坏会把它乘 11（见 <see cref="ProviderDefinition.TimeoutSeconds"/> 的注释）。
    /// 取 30 分钟是因为慢推理实测会在几分钟量级；再长就该怀疑端点挂死了。
    /// </summary>
    public const int MaxTimeoutSeconds = 1800;

    /// <summary>把配置值收进合法区间。⚠️ 只收不放：`null` 原样还回去（= 没配）。</summary>
    public static int? Normalize(int? seconds) =>
        seconds is { } s ? Math.Clamp(s, MinTimeoutSeconds, MaxTimeoutSeconds) : null;
}
