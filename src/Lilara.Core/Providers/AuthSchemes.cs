namespace Lilara.Core.Providers;

// 认证方式。官方 Anthropic 只认 x-api-key，本机网关只认 Authorization: Bearer——
// 后端怪癖，归提供商配置，不写死在适配器里。
public static class AuthSchemes
{
    public const string ApiKey = "api-key";
    public const string Bearer = "bearer";

    // 缺省 = 官方那套。
    public const string Default = ApiKey;
}
