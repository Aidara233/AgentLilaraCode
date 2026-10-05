namespace Lilara.Core.Kernel;

// 认证方式。官方 Anthropic 只认 x-api-key，本机网关只认 Authorization: Bearer——
// 后端怪癖，归提供商配置，不写死在适配器里。
public static class AuthSchemes
{
    public const string ApiKey = "api-key";
    public const string Bearer = "bearer";

    // 缺省 = 官方那套。
    public const string Default = ApiKey;

    /// <summary>
    /// 全部合法认证方式，**顺序即下拉的显示序**。
    ///
    /// <para>⚠️ 加一种认证方式时**只改这里**——`/provider-schema` 从这里读。
    /// 那份曾经是**手写数组** `new[] { ApiKey, Bearer }`，于是加常量时它不会跟上，
    /// 头就拿不到新的选项（「手抄的第二副本」的同族，`Axes` 的类注释里骂的就是这个形状）。</para>
    /// </summary>
    public static readonly IReadOnlyList<string> All = [ApiKey, Bearer];
}
