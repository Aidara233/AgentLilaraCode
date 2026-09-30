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
    /// 优先用 <paramref name="preferredProviderId"/>（会话 spec 里快照的提供商）。
    /// ⚠️ 指定了 preferred 却**解析不出来**时，**绝不替换成别的提供商**——
    /// 会话的 <c>ProviderId</c> 正是为了钉住「这一步是哪个提供商跑的」（01 §7.2），
    /// 随手换一个可能是**另一个 key + 另一个端点**，等于把数据发给了别人。
    /// 只有「全新跑、还没有绑定」（<paramref name="preferredProviderId"/> 为 null）
    /// 才退回任意一个。
    /// </summary>
    public static Stepper Resolve(
        SessionStore store, ProviderStore providers, string? preferredProviderId = null)
    {
        if (preferredProviderId is null)
        {
            var any = providers.List().FirstOrDefault();
            return any is null
                ? new Stepper(store, new UnconfiguredAdapter())
                : new Stepper(store, AdapterFactory.Create(any));
        }

        var bound = providers.Load(preferredProviderId);
        if (bound is null)
        {
            // 点名缺的是哪个——「还没有配置提供商」在这里是句误导人的话，
            // 用户明明配了（别的）提供商，只是这个会话绑的那个没了。
            return new Stepper(store, new UnconfiguredAdapter(
                $"会话绑定的提供商 '{preferredProviderId}' 已不存在。用 `provider set` 重新配一个。"));
        }

        return new Stepper(store, AdapterFactory.Create(bound));
    }
}
