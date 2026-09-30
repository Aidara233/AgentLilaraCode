namespace Lilara.Core.Adapters;

/// <summary>
/// 上游协议适配器。⚠️ 内建、焊死在代码里，不做插件（04 §1.2）——
/// 出去的协议是产品的形状，不该可换；进来的能力（MCP）才是用户的形状。
/// </summary>
public interface IModelAdapter
{
    Task<ModelReply> CallAsync(ModelRequest request, CancellationToken ct);
}
