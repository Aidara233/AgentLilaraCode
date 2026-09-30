namespace Lilara.Core.Adapters;

/// <summary>
/// 没有可用适配器时的占位。
/// ⚠️ 它存在是为了让命令面**能起来**——没配提供商时你仍然要能收
/// `provider set` 和 `session create`，否则引导会自锁。
/// ⚠️ 它住在适配器这一层，不是 Program.cs 的私事：`StepperResolver` 要能命名它，
/// 而「解析不出适配器时用哪个」是核心的契约，不该埋在顶层语句里。
/// </summary>
public sealed class UnconfiguredAdapter(string message = "还没有配置提供商。") : IModelAdapter
{
    public Task<ModelReply> CallAsync(ModelRequest request, CancellationToken ct) =>
        throw new ModelCallFailedException(message);
}
