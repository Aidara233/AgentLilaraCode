namespace Lilara.Core.Adapters;

/// <summary>
/// 上游协议适配器。⚠️ 内建、焊死在代码里，不做插件（04 §1.2）——
/// 出去的协议是产品的形状，不该可换；进来的能力（MCP）才是用户的形状。
/// </summary>
public interface IModelAdapter
{
    /// <summary>
    /// 解析出来的端点（含**来源**）。
    /// ⚠️ 暴露出来是为了**能显示**——「静默接管」才是问题，「接管」本身不是（见 EndpointResolver 类注释）。
    /// ⚠️ 可空：占位适配器（尚未配置提供商）没有端点，返回 null，调用方**不要印**。
    /// </summary>
    ResolvedEndpoint? Endpoint { get; }

    Task<ModelReply> CallAsync(ModelRequest request, CancellationToken ct);
}
