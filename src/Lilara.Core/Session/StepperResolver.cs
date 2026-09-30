using Lilara.Core.Adapters;
using Lilara.Core.Persistence;
using Lilara.Core.Providers;

namespace Lilara.Core.Session;

/// <summary>
/// 从 store 里解析出该用哪个适配器。
/// ⚠️ 它必须是个**可测的类型**，不能是 Program.cs 里的局部函数——
/// 这是「全新跑 → provider set → 发消息」那条首次使用路径的关键逻辑，
/// 埋在顶层语句里就没有任何测试能碰到它。
/// </summary>
public static class StepperResolver
{
    /// <summary>
    /// 优先用 <paramref name="preferredProviderId"/>（会话 spec 里快照的提供商），
    /// 找不到就退回任意一个；一个都没有则用占位适配器。
    /// </summary>
    public static Stepper Resolve(
        SessionStore store, ProviderStore providers, string? preferredProviderId = null)
    {
        var provider = preferredProviderId is null
            ? providers.List().FirstOrDefault()
            : providers.Load(preferredProviderId) ?? providers.List().FirstOrDefault();

        return provider is null
            ? new Stepper(store, new UnconfiguredAdapter())
            : new Stepper(store, AdapterFactory.Create(provider));
    }
}
