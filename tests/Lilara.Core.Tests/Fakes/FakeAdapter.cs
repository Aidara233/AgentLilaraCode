using Lilara.Core.Adapters;
using CanonicalBlock = Lilara.Core.Canonical.Block;

namespace Lilara.Core.Tests.Fakes;

/// <summary>
/// 假适配器：不联网、确定性强。
/// Stepper 的全部测试都跑在它上面——真适配器只做「构造请求 + 发出去 + 映射回来」。
/// </summary>
public sealed class FakeAdapter : IModelAdapter
{
    private readonly Queue<Func<ModelRequest, ModelReply>> _scripted = new();

    /// <summary>按调用顺序记下每一个进来的请求，供测试断言 Stepper 发了什么。</summary>
    public List<ModelRequest> Calls { get; } = [];

    /// <summary>假适配器不解析端点——按接口约定返回 null（占位语义）。</summary>
    public ResolvedEndpoint? Endpoint => null;

    public FakeAdapter Reply(string text, string stopReason = "end_turn") =>
        ReplyWith(_ => new ModelReply
        {
            Blocks = [new CanonicalBlock.TextBlock(text)],
            StopReason = stopReason,
            Refused = stopReason == "refusal",
        });

    public FakeAdapter ReplyWith(Func<ModelRequest, ModelReply> factory)
    {
        _scripted.Enqueue(factory);
        return this;
    }

    /// <summary>
    /// 直接脚本一串块。⚠️ 推理块这类非文本载荷只能走这里——
    /// 「只有推理块、没有正文」的回复是 Task 10 空正文用例的形状。
    /// </summary>
    public FakeAdapter ReplyBlocks(params CanonicalBlock[] blocks) =>
        ReplyWith(_ => new ModelReply
        {
            Blocks = blocks,
            StopReason = "end_turn",
            Refused = false,
        });

    public FakeAdapter Throws(Exception ex)
    {
        _scripted.Enqueue(_ => throw ex);
        return this;
    }

    public Task<ModelReply> CallAsync(ModelRequest request, CancellationToken ct)
    {
        Calls.Add(request);
        if (_scripted.Count == 0)
            throw new InvalidOperationException("FakeAdapter 没有预置回复了");

        return Task.FromResult(_scripted.Dequeue()(request));
    }
}
