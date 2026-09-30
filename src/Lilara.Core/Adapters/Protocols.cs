namespace Lilara.Core.Adapters;

/// <summary>
/// 协议标识。⚠️ **单一来源**——`AdapterFactory` 的分派、适配器自身的守卫、
/// `ReasoningPolicy` 的能力表，全读这里。
/// 之前踩过「Repl 硬编码协议字符串」的坑（同一事实两处各写一份），不要重蹈。
/// ⚠️ 这些字符串**会落盘**（`spec.json` / `providers/*.json`），改动即数据迁移。
/// </summary>
public static class Protocols
{
    public const string ClaudeMessages = "claude-messages";
    public const string OpenAiChat = "openai-chat";
    public const string OpenAiResponses = "openai-responses";

    public static readonly IReadOnlyList<string> All =
        [ClaudeMessages, OpenAiChat, OpenAiResponses];
}
