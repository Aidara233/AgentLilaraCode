namespace Lilara.Core.Kernel;

// 会话持有的模型选择：解析结果的快照 + 指向定义的引用。
public sealed record ModelSelection
{
    // 指向 providers/ 里的定义。
    public required string ProviderId { get; init; }

    public required string ModelId { get; init; }

    // 快照：**最近一次设定模型时**供应商的协议是什么。建会话时写一次，
    // `session.configure` 换供应商时**跟着重新快照**——它必须与 ProviderId 同代。
    // ⚠️ 别拿它当「现在真的走哪条协议」：`Stepper` 取的是**适配器**的 Protocol，
    // 因为 provider 定义可以被 `provider set --protocol` 改掉，那样两者会分叉。
    public required string Protocol { get; init; }

    // 思考强度轴。纯请求参数，绝不进上下文。
    public required string Effort { get; init; }

    public required int MaxTokens { get; init; }
}

// ⚠️ **不是「创建时写一次，此后不可变」**——那句曾经是真的，`session.configure` 之后不再是。
// 它现在是 handle 上**两份可变数据之一**（另一份是 SessionState），改它**必须**走
// `SessionHandle.UpdateSpecAndPersist`（与 State 同一把锁、同一条纪律）。
// ⚠️ 记录本身仍是 `init`-only：改的是「谁持有它」，不是「它能不能改」。
public sealed record SessionSpec
{
    public required string SessionId { get; init; }

    // 会话**出生地**，会话一旦建出来就不会再变（不像 Model 那样可改）。
    public required string ProjectKey { get; init; }
    public required string WorkingDirectory { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    // 可改：`session.configure` 换绑供应商 / 换模型。⚠️ 改 ProviderId 后必须
    // `CoreHost.InvalidateSteppers()`，否则请求会走旧供应商的适配器（旧 key、旧端点）。
    public required ModelSelection Model { get; init; }
}
