using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lilara.Core.Kernel;

/// <summary>
/// **逐字**承载一段 JSON 的字符串字段：读进来什么样，写出去什么样。
///
/// <para>⚠️ **为什么不能直接用 <c>JsonElement</c>**（两条实测，都否证了那个想法）：</para>
/// <list type="number">
///   <item><b>它不保字节</b>：反序列化再序列化会**规范化**（键序 / 空白 / 转义都变）。
///         实测：`{ "zeta" : 1, "alpha": "a&amp;b" }` 转一圈回来不再是原串。</item>
///   <item><b>它不满足值相等</b>：两个结构完全相同的 `JsonElement` **不相等** ⇒
///         record 的自动相等会碎，而 `TimelineEvent` / `Block` 全是 record。</item>
/// </list>
///
/// <para>⇒ 用<b>原文文本</b>承载，配本转换器原样写出。工具参数**必须逐字回传**——
/// 那是协议铁律（`01` §9），而「解析成对象再拼回去」正是它要防的。</para>
///
/// <para>⚠️ <b>写出用 <see cref="Utf8JsonWriter.WriteRawValue(string, bool)"/>，
/// 不是「解析成 <c>JsonDocument</c> 再 <c>WriteTo</c>」</b>——后者同样会规范化，
/// 等于绕一圈又踩回上面第 ① 条。</para>
///
/// <para>⚠️ <b>本转换器只用来标在「参数」这类字段上</b>，不是全局的 <c>string</c> 转换器：
/// 全局装它会让**每个**字符串都被当成 JSON 解析（`"hi"` 会被当 JSON 文本解析成功，
/// 而 `你好` 直接抛）。标在字段上，意图也在字段上。</para>
/// </summary>
public sealed class JsonRawConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // ParseValue 消费掉整个值（对象 / 数组 / 标量都行），GetRawText 交回**原文**。
        using var doc = JsonDocument.ParseValue(ref reader);
        return doc.RootElement.GetRawText();
    }

    // skipInputValidation: false ⇒ 不是合法 JSON 就**当场抛**（写出方有 bug 时立刻显形），
    // 而不是把一段垃圾写进 history.jsonl —— 那份只追加、坏了就一直在。
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteRawValue(value, skipInputValidation: false);
}
