namespace Lilara.Core.Adapters;

// 协议标识：单一来源。
// 这些字符串会落盘（spec.json 的 model.protocol、providers/*.json 的 protocol），改动即数据迁移——
// 必须由测试按字面量守，别把那处字面量统一成常量，否则改了拼写系统全绿而用户的既有配置不认。
public static class Protocols
{
    public const string ClaudeMessages = "claude-messages";
    public const string OpenAiChat = "openai-chat";
    public const string OpenAiResponses = "openai-responses";

    public static readonly IReadOnlyList<string> All =
        [ClaudeMessages, OpenAiChat, OpenAiResponses];
}
