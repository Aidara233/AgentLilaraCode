using System.Text.Json.Serialization;

namespace Lilara.Core.Providers;

/// <summary>
/// 提供商定义，存全局（含 key）（01 §7 配置分层的全局层）。
/// 会话只存「引用 + 选择」，不存 key。
/// </summary>
public sealed record ProviderDefinition
{
    public required string Id { get; init; }

    /// <summary>协议类型。适配器按它分派（04 §1.2：内建，焊死）。</summary>
    public required string Protocol { get; init; }

    /// <summary>⚠️ 明文。数据根是本用户私有的（01 §8）。</summary>
    public required string ApiKey { get; init; }

    public required string DefaultModel { get; init; }

    /// <summary>
    /// 模态能力声明（03 §9.2）：判据是**提供商配置的一部分**，不是适配器的运行时契约。
    /// 最小闭环用不到（没有附件），但字段现在就留着。
    /// </summary>
    public required string Modality { get; init; }

    /// <summary>
    /// 端点。空则用官方地址（`01` §7：全局层持有「端点、key、协议类型」）。
    /// ⚠️ 必须显式设进客户端：只设 ApiKey 的话，环境里的 `ANTHROPIC_BASE_URL`
    /// 会**静默接管**请求去向，而核心对此毫不知情——一个管理全部会话的无头进程
    /// 不该让环境决定它把数据发到哪。
    /// ⚠️ 加法式字段：既有 providers/*.json 里没有它，必须照旧能反序列化（为 null）。
    /// </summary>
    public string? Endpoint { get; init; }

    /// <summary>
    /// 认证方式：<see cref="AuthSchemes.ApiKey"/>（x-api-key，官方）或
    /// <see cref="AuthSchemes.Bearer"/>（Authorization: Bearer，本机网关）。
    /// ⚠️ 可空——老 providers/*.json 没这个字段，缺失按 <see cref="AuthSchemes.Default"/> 处理。
    /// </summary>
    public string? AuthScheme { get; init; }

    /// <summary>
    /// ⚠️ **「有没有 key」全项目只此一份判据——别在别处再拼一遍。**
    ///
    /// <para>它曾经被拼成两份、而且漂了：守卫用 `is null`、`/providers` 用 `!IsNullOrEmpty`
    /// ⇒ `"apiKey": ""` 既**绕过守卫**（存下空 key），又**抹掉已有 provider 的 key**。
    /// ⇒ 空串与缺失**等价**（都是「没给」）。</para>
    ///
    /// <para>⚠️ **`空串 ≠ 给过 key`；但「全空白」算不算，是一个**未裁决的契约问题**（不在本轮范围）。**
    /// 今天 `HasKey("   ")` 为 true ⇒ 拿全空白当 key 的新 provider 会被**接受并落盘**，
    /// 而每一次请求都会鉴权失败——正是本判据要防的「配好了却发不出去」。
    /// ⚠️ 这条**不是本轮引入的**（旧判据 `is null` 也放行 `"   "`），且今天**没有任何测试钉住这个选择**。</para>
    ///
    /// <para>⚠️ **改它本身是一处的事**（三处调用都走这个函数）——但那是**改契约**，不是改拼写。
    /// 别把它与「两份拼写」混为一谈：拼写问题已经由本函数收掉了。</para>
    /// </summary>
    public static bool HasKey(string? apiKey) => !string.IsNullOrEmpty(apiKey);

    /// <summary>
    /// ⚠️ 转发到 <see cref="HasKey"/>——**别在这里再拼一遍 `IsNullOrEmpty`**。
    ///
    /// <para>⚠️ `[JsonIgnore]` **不是可选的**：`ProviderStore.Save` 用 `Canonical.Json.Options`
    /// 直接序列化本类型，而那份设置**没有** `IgnoreReadOnlyProperties` ⇒ 少了它，
    /// 每个 `providers/&lt;id&gt;.json` 会**多出一个 `hasApiKey` 键**（实测见报告 R2.2）——
    /// 磁盘格式的一次静默变更，而**现有测试没有一条会红**（`DiskFormatGoldenTests` 不含本类型）。
    /// 它是**派生值、不是数据** ⇒ 本就不该落盘。</para>
    /// </summary>
    [JsonIgnore]
    public bool HasApiKey => HasKey(ApiKey);
}
