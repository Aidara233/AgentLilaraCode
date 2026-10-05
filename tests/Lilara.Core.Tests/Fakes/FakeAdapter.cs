using Lilara.Core.Adapters;
using CanonicalBlock = Lilara.Core.Canonical.Block;

namespace Lilara.Core.Tests.Fakes;

// 假适配器：不联网、确定性强。
public sealed class FakeAdapter : IModelAdapter
{
    private readonly Queue<Func<ModelRequest, ModelReply>> _scripted = new();

    // 按调用顺序记下每一个进来的请求。
    public List<ModelRequest> Calls { get; } = [];

    // 假适配器不解析端点——按接口约定返回 null（占位语义）。
    public ResolvedEndpoint? Endpoint => null;

    // 假适配器声明的协议，由测试设定，默认 Claude 那条。
    public string Protocol { get; set; } = Protocols.ClaudeMessages;

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

    // 直接脚本一串块；推理块这类非文本载荷只能走这里。
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
