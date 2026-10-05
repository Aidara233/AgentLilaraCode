using System.Text.Json.Serialization;

namespace Lilara.Core.Kernel;

// 提供商定义，存全局（含 key）；会话只存「引用 + 选择」，不存 key。
public sealed record ProviderDefinition
{
    public required string Id { get; init; }

    // 协议类型。适配器按它分派（内建，焊死）。
    public required string Protocol { get; init; }

    // 明文。数据根是本用户私有的。
    public required string ApiKey { get; init; }

    public required string DefaultModel { get; init; }

    /// <summary>
    /// 这个提供商**可选的**模型清单（头把会话的模型画成下拉，选项就从这里来）。
    ///
    /// <para>⚠️ **加法式可空**：既有的 `providers/*.json` 没有这个键，缺失即反序列化成 `null`
    /// （与 <see cref="Endpoint"/> / <see cref="AuthScheme"/> 同一个先例）。磁盘格式不动。</para>
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
    // ANTHROPIC_BASE_URL 会静默接管请求去向。加法式字段：既有 providers/*.json
    // 里没有它，缺失即反序列化成 null。
    public string? Endpoint { get; init; }

    // 认证方式：ApiKey（x-api-key，官方）或 Bearer（Authorization: Bearer，本机网关）。
    // 可空——老 providers/*.json 没这个字段，缺失按 AuthSchemes.Default 处理。
    public string? AuthScheme { get; init; }

    // 「有没有 key」全项目只此一份判据，别在别处再拼一遍——空串与缺失等价（都是「没给」）。
    // 全空白今天算「有」，这一点的语义尚未裁决。
    public static bool HasKey(string? apiKey) => !string.IsNullOrEmpty(apiKey);

    // 转发到 HasKey，别在这里再拼一遍。JsonIgnore 不是可选的：Json.Options 没有
    // IgnoreReadOnlyProperties，少了它每个 providers/<id>.json 会多出一个 hasApiKey 键；
    // 它是派生值、不是数据，本就不该落盘。
    [JsonIgnore]
    public bool HasApiKey => HasKey(ApiKey);
}
