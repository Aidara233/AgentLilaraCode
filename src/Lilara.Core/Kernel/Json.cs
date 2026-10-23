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
    ///
    /// <para>⚠️ <b>3（2026-10-06，最小工具批）</b>：<c>ToolResultEvent</c> 加了
    /// <c>dependsOn</c> 与 <c>fingerprint</c>（`00` §5 的三件里除快照外的两件）。
    /// ⚠️ 顺带说明一个**已知且刻意**的边界：<b><c>history.jsonl</c> 的行上没有版本号</b>
    /// （那份是只追加的，行与行可以来自不同版本，见 <see cref="VersionKey"/> 的注释）
    /// ⇒ 老核心读新行会**静默忽略**这两个键。代价是「三态判定退化成总是『不一致』」——
    /// 失败方向是**多一次重读**，不是错读。这是接受这个代价的前提，写下来免得将来被当成漏洞。</para>
    ///
    /// <para>⚠️ <b>4（2026-10-07，预设批）</b>：<c>SessionSpec</c> 加了 <c>systemPromptPreset</c>。
    /// ⚠️ 它**不是**「加个字段所以顺手 +1」——那条规则见上面那段。这里要 +1 是因为
    /// <b>它改变了一个已有位置的解释</b>：这个会话的**前缀最前面那块**（`system`）从此不是常量，
    /// 而老程序会把「缺这个键」读成「这套格式里根本没有系统提示词这回事」。
    /// ⇒ 两份程序对同一个会话发出的请求**逐字节不同**，而它们都以为自己在读同一份 spec。</para>
    ///
    /// <para>⚠️ <b>5（2026-10-12，打断批）</b>：<c>SessionState</c> 加了
    /// <c>pendingIntent</c>（「上一次那两处写，可能没写完」的痕迹）。
    /// ⚠️ 它<b>不</b>改任何已有字段的含义（老程序会静默忽略这个键，而那份文件里
    /// 本来就没有它 ⇒ 读到的还是同一件事）⇒ 按上面那段判据，这一半<b>不足以</b> +1。
    /// <b>+1 的理由是另一半</b>：`state.json` 从这一版起**可能与记录不一致**
    /// ——而「不一致」这件事在<b>上一次试图写它的那个进程</b>看来是可知的，
    /// 在一个不认这个键的老核心看来<b>完全不可见</b> ⇒ 它会照旧发出那条
    /// 「缺配对」的请求并拿到 400，且**永远不知道原因**。⇒ 这正是版本号要终结的那种
    /// 「静默地按旧含义解释新数据」。</para>
    /// </summary>
    // 7：会话绑定插件版本及默认值，状态保存插件轴；旧核心必须拒绝，避免丢配置。
    //
    // 8（2026-10-20，缓存明细批）：⚠️ 这一批**没有加任何新字段**，改的是两件既有事实——
    //   ① `usage.jsonl` 的**行**开始带 `formatVersion`（此前账本行上没有版本号；
    //      `VersionKey` 的注释说过它不管 `history.jsonl`，账本是同一类追加文件，同样得按行判）；
    //   ② OpenAI 系（chat / responses）的 `inputTokens` **含义变了**：此前是上游原值
    //      （`prompt_tokens`，**含**缓存命中），此后是「未命中输入」（`08` §3.1 的口径，
    //      与 Anthropic 的 `input_tokens` 对齐）。同一批 token 从此与 `cacheReadTokens` **不相交**，
    //      相加才是全部输入。同时解析器开始读 `prompt_tokens_details.cached_tokens` /
    //      `prompt_cache_hit_tokens`——旧行里那些调用记的是「缓存读取未报告」，不是 0。
    //   ⇒ 这正是版本号要终结的那类事：老核心读新行，**同一个键、同一个数、不同的意思**，
    //      而且它会照旧把「输入 + 缓存读取」当成两次独立输入去理解。
    //
    // 9（2026-10-22，压缩改成工具）：两处一起动，所以不是「纯加字段所以顺手 +1」——
    //   ① `state.json` 加了 `pendingCompact`（压缩指令）。老程序会**静默忽略**这个键
    //      ⇒ 一条刚下发的压缩指令在重启之后消失，而**双方都不知道**（用户看到的是
    //      「点了压缩，没反应」）；这正是上面那段说的「静默地按旧含义解释新数据」。
    //   ② `compaction.jsonl` 的检查点**不再写 `summary`**（摘要只在 `compact` 调用的参数里）。
    //      同一个文件里，新行少一个键；老程序按旧形态读它，会看到一个没有摘要的检查点。
    //      ⚠️ 本版之前写下的那些行**仍带 `summary`**（字段改成可空、没删）——它们是旧检查点的
    //      回落来源，渲染时只在「记录里找不到对应调用」时用（见 `ContextCompaction.SummaryOf`）。
    public const int FormatVersion = 9;

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
