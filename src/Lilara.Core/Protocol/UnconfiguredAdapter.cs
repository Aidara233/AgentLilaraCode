namespace Lilara.Core.Protocol;

// 没有可用适配器时的占位：让命令面能起来——没配提供商时仍要能收 provider set 与 session create，
// 否则引导会自锁。
public sealed class UnconfiguredAdapter(string message = "还没有配置提供商。") : IModelAdapter
{
    /// <summary>占位没有端点：返回 null，调用方据此不印那行。</summary>
    public ResolvedEndpoint? Endpoint => null;

    /// <summary>占位不发声：给空串；调用方不该拿它发请求（CallAsync 会抛人话）。</summary>
    public string Protocol => "";

    public Task<ModelReply> CallAsync(ModelRequest request, CancellationToken ct) =>
        throw new ModelCallFailedException(message);
}
