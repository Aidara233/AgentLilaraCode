namespace Lilara.Core.Providers;

/// <summary>
/// 提供商定义，存全局（含 key）（01 §7 配置分层的全局层）。
/// 会话只存「引用 + 选择」，不存 key。
/// </summary>
public sealed record ProviderDefinition
{
    public required string Id { get; init; }

    /// <summary>协议类型。适配器按它分派（04 §1.2：内建，焊死）。</summary>
    public required string Protocol { get; init; }

    /// <summary>⚠️ 明文。数据根是本用户私有的（01 §8）。</summary>
    public required string ApiKey { get; init; }

    public required string DefaultModel { get; init; }

    /// <summary>
    /// 模态能力声明（03 §9.2）：判据是**提供商配置的一部分**，不是适配器的运行时契约。
    /// 最小闭环用不到（没有附件），但字段现在就留着。
    /// </summary>
    public required string Modality { get; init; }
}
