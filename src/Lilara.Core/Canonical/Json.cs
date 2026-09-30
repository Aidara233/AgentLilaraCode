using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lilara.Core.Canonical;

/// <summary>落盘与回读共用的 JSON 设置。</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // ⚠️ 不转义非 ASCII。Unsafe 是相对「注入 HTML」说的；
        // 本地文件存储正是它的正确用途，否则中文会被写成 \u4f60\u597d。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // ⚠️ 枚举按名落盘。整数枚举既不可读，也会因成员重排静默重解释既有历史——
        // history.jsonl 是只追加、永不重写的，写错一次就是一次数据迁移。
        Converters = { new JsonStringEnumConverter() },
    };
}
