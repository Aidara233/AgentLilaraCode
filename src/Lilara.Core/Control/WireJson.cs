using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lilara.Core.Control;

/// <summary>
/// **线上**的 JSON 设置。⚠️ **与 `Canonical.Json.Options` 是两个不同的实例，这是刻意的。**
///
/// <para>为什么不能共用（G3）：</para>
/// <list type="number">
///   <item><b>污染落盘</b>：`Json.Options.Converters` 是个**可变的 `List`** 挂在
///     `public static readonly` 的共享实例上。为了 wire 往上加一个 converter，
///     就同时改了 `history.jsonl` / `state.json` / `spec.json` 的写法。</item>
///   <item><b>演进被耦合死</b>：camelCase + 枚举按名意味着**磁盘契约与线上契约是同一份**。
///     哪天真要改 `ReasoningKind` 的成员名，磁盘迁移与线上协议变更会被迫同时发生——
///     而**头是独立进程、独立发布节奏**，它没法跟着一起改。</item>
/// </list>
///
/// <para>⚠️ 代价是策略值抄了一遍。那是**刻意的重复**：两份契约从此可以各自演进。
/// 出现「改了一处忘了另一处」时，问的不是「能不能抽出来」，而是「这两个契约是不是该一样」。</para>
/// </summary>
public static class WireJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
