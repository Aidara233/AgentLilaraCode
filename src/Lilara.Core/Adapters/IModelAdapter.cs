namespace Lilara.Core.Adapters;

// 上游协议适配器：内建焊死在代码里，不做插件——出去的协议是产品形状，不该可换。
public interface IModelAdapter
{
    // 解析出来的端点（含来源）。暴露出来是为了能显示。可空：占位适配器返回 null，调用方不要印。
    ResolvedEndpoint? Endpoint { get; }

    // 实际会说的协议（Protocols 里的常量）。发出去的请求必须用这里的，不是会话 spec 里的协议快照——
    // 两者会因 provider set --protocol 分叉。
    string Protocol { get; }

    Task<ModelReply> CallAsync(ModelRequest request, CancellationToken ct);
}
