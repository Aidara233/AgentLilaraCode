using System.Text.Json.Nodes;
using Lilara.Core.Kernel;

namespace Lilara.Core.Protocol.OpenAi;

// Responses 请求的**显式缓存断点**（2026-10-28）——单独一个文件，因为它是纯函数式后处理：
// 「哪一条能挂断点」是位置判据，不该和消息翻译混在一起。
public static class OpenAiResponsesCache
{
    /// <summary>
    /// 给 `input` 里**最后一条 `type=message`、且最后一个 part 是文本**的项打断点；返回是否打上。
    ///
    /// <para>⚠️ <b>跳过 `function_call` / `function_call_output`</b>：官方文档写明在
    /// `function_call_output` 上标的断点**会被接受但不写缓存**（Azure/OpenAI 的已知行为）
    /// ⇒ 标上去只会让人以为开了缓存。浮动读数那对正是 tool_use + tool_result，天然被跳过。</para>
    ///
    /// <para>⚠️ <b>为什么只打一个断点</b>：Responses 的头槽位走顶层 `instructions`（**字符串**），
    /// 挂不了 `prompt_cache_breakpoint` ⇒ 这一族只有历史那一个断点。这是协议形状的限制。</para>
    /// </summary>
    public static bool Mark(JsonArray input)
    {
        for (var i = input.Count - 1; i >= 0; i--)
        {
            if (input[i] is not JsonObject item) continue;
            if (!IsType(item, "message")) continue;
            if (item["content"] is not JsonArray parts || parts.Count == 0) continue;
            if (parts[^1] is not JsonObject last) continue;

            var partType = IsType(last, "input_text") ? "input_text"
                : IsType(last, "output_text") ? "output_text"
                : null;
            if (partType is null) continue;

            parts[^1] = new JsonObject
            {
                ["type"] = partType,
                ["text"] = last["text"]?.DeepClone(),
                ["prompt_cache_breakpoint"] = new JsonObject { ["mode"] = "explicit" },
            };
            return true;
        }

        return false;
    }

    private static bool IsType(JsonObject node, string expected) =>
        node["type"] is JsonValue v && v.TryGetValue<string>(out var actual)
        && string.Equals(actual, expected, StringComparison.Ordinal);
}
