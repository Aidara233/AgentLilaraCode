using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lilara.Core.Kernel;

// 落盘与回读共用的 JSON 设置。
//
// ⚠️ **本文件的每一条设置都是磁盘格式的一部分，改任何一条都是格式变更。**
// 它们由 `DiskFormatGoldenTests` 逐条钉着——**那才是那些金样真正在钉的东西**。
// 金样里那些「逐字节相同」的断言（`WhenWritingNull` 让没设的字段整个键不出现、
// 字段顺序 = 声明顺序）本质上是**这一个对象**的性质，不是每条记录的独立性质。
// ⇒ 加字段不该去动金样里的期望串；**改了这里才该让一大片一起红**。
public static class Json
{
    /// <summary>
    /// 「格式版本」的键名。**每份 JSON 文件的顶层都有它。**
    ///
    /// <para>版本是**消费点的显式拒绝**所必需的：读到一份比自己新的文件时，
    /// 除了「拒绝」没有正确答案——静默重解释、或者崩在某个不相关的 <c>NullReferenceException</c> 上，
    /// 都会让用户看到一个**指错方向**的症状。</para>
    ///
    /// <para>⚠️ **它不管 `history.jsonl`**：那是只追加的，行与行之间可以来自不同版本，
    /// 而且坏行必须跳过（断电会留半行）。那份的升级策略见 `HistoryLog` 的注释。</para>
    /// </summary>
    public const string VersionKey = "version";

    /// <summary>
    /// 当前程序写出的格式版本。**只增不改**：字段语义变了、字段没了、判别式改名了 ⇒ +1。
    ///
    /// <para>纯加字段**也 +1**——因为加了之后老程序读新文件就是「静默忽略一个它不认识的键」，
    /// 那正是版本号要终结的那种不确定性（它今天恰好是无害的，明天可能不是：
    /// 如果那个新字段改变的是**已有字段的含义**，老程序会按旧含义解释新数据，且毫无察觉）。</para>
    /// </summary>
    public const int FormatVersion = 2;

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
