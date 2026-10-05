using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lilara.Core.Canonical;

// 落盘与回读共用的 JSON 设置。
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // camelCase：判别式 kind 本就是小写，属性却大驼峰——同一对象只能有一种约定；
        // 控制面把这批类型给 Web 头，camelCase 正是那边要的形状。
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // 不转义非 ASCII：Unsafe 是相对「注入 HTML」说的，本地文件存储正是它的用途。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // 枚举按名落盘、值也是 camelCase：整数枚举不可读，也会因成员重排静默重解释既有历史。
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    // 稳定序列化入口。参数类型必须是 Block（基类），不能写成泛型 T——
    // Serialize(b) 推断成具体子类时会丢掉判别式 "kind"，出站 JSON 变成 {"text":"hi"}。
    public static string StableSerialize(Block value) =>
        JsonSerializer.Serialize(value, Options);
}
