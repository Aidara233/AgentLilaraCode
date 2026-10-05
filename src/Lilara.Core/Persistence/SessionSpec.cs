namespace Lilara.Core.Persistence;

// 会话持有的模型选择：解析结果的快照 + 指向定义的引用。
public sealed record ModelSelection
{
    // 指向 providers/ 里的定义。
    public required string ProviderId { get; init; }

    public required string ModelId { get; init; }

    // 快照：建会话时协议是什么。
    public required string Protocol { get; init; }

    // 思考强度轴。纯请求参数，绝不进上下文。
    public required string Effort { get; init; }

    public required int MaxTokens { get; init; }
}

// 创建时写一次，此后不可变。
public sealed record SessionSpec
{
    public required string SessionId { get; init; }
    public required string ProjectKey { get; init; }
    public required string WorkingDirectory { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required ModelSelection Model { get; init; }
}
