namespace Lilara.Core.Persistence;

/// <summary>
/// 会话持有的模型选择：解析结果的快照 + 指向定义的引用（01 §7.2）。
/// 会话必须能回答「这一步是哪个模型跑的」，否则重启后历史来路不明。
/// </summary>
public sealed record ModelSelection
{
    /// <summary>指向 <c>providers/</c> 里的定义。</summary>
    public required string ProviderId { get; init; }

    /// <summary>「换模型 = 改会话配置」改的是这个。</summary>
    public required string ModelId { get; init; }

    /// <summary>快照：建会话时协议是什么。老会话遇到新适配器也不慌。</summary>
    public required string Protocol { get; init; }

    /// <summary>思考强度轴（01 §3.5）。纯请求参数，绝不进上下文。</summary>
    public required string Effort { get; init; }

    public required int MaxTokens { get; init; }
}

/// <summary>创建时写一次，此后不可变（01 §8）。</summary>
public sealed record SessionSpec
{
    public required string SessionId { get; init; }
    public required string ProjectKey { get; init; }
    public required string WorkingDirectory { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required ModelSelection Model { get; init; }
}
