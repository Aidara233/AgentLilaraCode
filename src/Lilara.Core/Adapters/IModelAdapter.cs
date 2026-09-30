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

    /// <summary>
    /// 这个适配器实际会说的协议（`Protocols` 里的常量）。
    /// ⚠️ 存在的理由是**消除第二个真相来源**：会话 spec 里也存了一份协议快照（`01 §7.2`），
    /// 而适配器是按**当前** provider 定义选的。`provider set --protocol` 一改，两者就会分叉——
    /// 分叉的后果是 `ReasoningPolicy` 按 spec 那份判断能不能回放，
    /// 于是**在唯一能承载推理的那条链路上把推理静默丢掉**。
    /// ⇒ 发出去的请求必须用**这里**的协议，不是 spec 那份。
    /// </summary>
    string Protocol { get; }

    Task<ModelReply> CallAsync(ModelRequest request, CancellationToken ct);
}
