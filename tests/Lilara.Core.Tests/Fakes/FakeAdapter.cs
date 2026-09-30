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
