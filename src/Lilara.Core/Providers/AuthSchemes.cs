namespace Lilara.Core.Providers;

/// <summary>
/// 认证方式。⚠️ 和端点一样是**后端怪癖**——官方 Anthropic 只认 `x-api-key`，
/// 而本机网关（ApiPlan 中转站）只认 `Authorization: Bearer`（实测：ApiKey 打不通，AuthToken 通）。
/// 归提供商配置，不写死在适配器里。
/// </summary>
public static class AuthSchemes
{
    public const string ApiKey = "api-key";
    public const string Bearer = "bearer";

    /// <summary>缺省 = 官方那套。</summary>
    public const string Default = ApiKey;
}
