namespace Lilara.Core.Adapters;

/// <summary>
/// 协议标识。⚠️ **单一来源**——`AdapterFactory` 的分派、适配器自身的守卫、
/// `ReasoningPolicy` 的能力表，全读这里。
/// ⚠️ 这些字符串**会落盘**（`spec.json` 的 `model.protocol`、`providers/*.json` 的 `protocol`），
/// 改动即数据迁移。
///
/// ⚠️ **但「单一来源」有一个必然的代价，必须知道**：
/// 因为所有代码路径都读这里，把 `OpenAiChat` 改成别的拼写，**类型系统内自洽、测试里也自洽**——
/// 于是整个系统绿色，而用户既有的 `providers/*.json` 突然不认。
/// **所以落盘的字符串必须由测试按字面量守**（见 `AdapterFactoryTests.Protocols_PinTheOnDiskWireValues`）。
/// 别把那处字面量「顺手统一」成常量——那样就没人守了。
/// </summary>
public static class Protocols
{
    public const string ClaudeMessages = "claude-messages";
    public const string OpenAiChat = "openai-chat";
    public const string OpenAiResponses = "openai-responses";

    public static readonly IReadOnlyList<string> All =
        [ClaudeMessages, OpenAiChat, OpenAiResponses];
}
