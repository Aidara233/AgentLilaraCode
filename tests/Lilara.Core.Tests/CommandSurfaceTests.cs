using System.Text.Json;
using Lilara.Core.Control;
using Lilara.Core.Console;
using Lilara.Core.Hosting;
using Lilara.Core.Kernel;

namespace Lilara.Core.Tests;

// 「一条命令有四条线」的守卫：类型 → 线上解析 → 控制台解析（→ 分派器）。
//
// ⚠️ **这个矩阵就是那张会腐的表**：加一条 `CoreCommand` 却漏接某条线 ⇒ 这里红。
// （它取代 `03 §3` 旁边那句「`CommandDispatcher` 有 15 个 case」的注释——
//  注释不传播，规则才传播。）
//
// ⚠️ **别把这条守卫说大**：它管的是「**有没有接线**」，不管「接得对不对」。
// 字段名拼错、线上认了而 dispatcher 语义不对，这些它全都看不见——那要靠真 WS 测试。
public class CommandSurfaceTests
{
    // 每条命令：类型 + 线上 `kind`（null = 线上没有）+ 控制台形态（null = 控制台没有）。
    //
    // ⚠️ 两个 null 都是**刻意的**，不是漏项：
    //   · `session.send` / `session.start` 控制台没有文本形态（`send` 就是「敲一行普通消息」，
    //     `start` 今天够不到，见 `03 §8.1`）
    //   · `claim.acquire` 线上没有（用户裁定，`03 §3`）
    private static readonly (Type Cmd, string? Wire, string? Console)[] Matrix =
    [
        (typeof(CoreCommand.ProviderUpsert),   "provider.upsert",   "provider set p --key k"),
        (typeof(CoreCommand.ProviderDelete),   "provider.delete",   "provider delete p"),
        (typeof(CoreCommand.ProviderTest),     "provider.test",     "provider test p"),
        (typeof(CoreCommand.ProviderModels),   "provider.models",   "provider models p"),

        // 联网（2026-10-22）：`network set` / `network test`。
        (typeof(CoreCommand.NetworkConfigure), "network.configure", "network set --enabled on"),
        (typeof(CoreCommand.NetworkTest),      "network.test",      "network test"),

        // shell（2026-10-23）：设备级配置 + 自检。⚠️ 这两条在本批落地时就漏进了这张矩阵
        // （矩阵是「类型 → 四条线」的守卫，漏一行 = 那条命令**没有任何检查**），收尾时补上。
        (typeof(CoreCommand.ShellConfigure),   "shell.configure",   "shell set --enabled on"),
        (typeof(CoreCommand.ShellTest),        "shell.test",        "shell test"),

        // 预设：系统提示词的存放（预设批）。三条**都有线上形态**——
        // 连 `preset list` 也有（它走线，不像 `provider list` 是控制台本地查询：
        // 头只有一个入口，而它要用这个做下拉）。
        (typeof(CoreCommand.PresetSet),        "preset.set",        "preset set default 你是助手"),
        (typeof(CoreCommand.PresetDelete),     "preset.delete",     "preset delete default"),
        (typeof(CoreCommand.PresetList),       "preset.list",       "preset list"),

        (typeof(CoreCommand.SessionCreate),    "session.create",    "session create"),
        (typeof(CoreCommand.SessionActivate),  "session.activate",  "session activate s1"),
        (typeof(CoreCommand.SessionSend),      "session.send",      null),
        // ⚠️ **2026-10-12 打断批补上文本形态**：原先这里是 `null`，而后果不是
        // 「少一条命令」——敲 `session start s1` 会**掉进 `Repl.AsMessage`**，
        // 被当成一条**消息发给模型**（用户以为在启动，实际是让模型读了一句话）。
        // ⚠️ 而它正是**唯一能在「崩在步中」之后把那个会话收回来**的命令
        //（那时队列是空的，`session.send` 用不上）⇒ 缺了它那个洞关不上。
        (typeof(CoreCommand.SessionStart),     "session.start",     "session start s1"),
        (typeof(CoreCommand.SessionInterrupt), "session.interrupt", "session interrupt s1"),
        (typeof(CoreCommand.SessionSetAxis),   "session.setAxis",   "axis set s1 safety prod"),
        (typeof(CoreCommand.SessionConfigure), "session.configure", "session configure s1 --model m"),
        (typeof(CoreCommand.SessionDeactivate),"session.deactivate","session deactivate s1"),
        (typeof(CoreCommand.SessionArchive),   "session.archive",   "session archive s1"),
        (typeof(CoreCommand.SessionDelete),    "session.delete",    "session delete s1"),
        // 回收：手动触发（`01` §8.0.2）。四条线都在——控制台那句是 `session gc <id>`。
        (typeof(CoreCommand.SessionGc),        "session.gc",        "session gc s1"),
        (typeof(CoreCommand.SessionCompact),   "session.compact",   "session compact s1"),

        (typeof(CoreCommand.ClaimAcquire),     null,                "claim src/foo"),
        (typeof(CoreCommand.ClaimRelease),     "claim.release",     "claim release src/foo"),

        // 计划（2026-10-24 计划批）：线上有 `session.plan`（另有 HTTP `PUT /sessions/{id}/plan`，
        // 两条走同一个 handler、同一个解析器）。
        // ⚠️ **控制台没有文本形态**：一棵树用一行文本表达不了，而勉强拼一个
        // `plan overwrite {json…}` 等于让那行里再嵌一层语法——读的人猜不准引号归谁。
        // ⇒ 与 `session.send` / `session.start` 那两格同样是 `null`，但**理由不同**，写在这里。
        (typeof(CoreCommand.SessionPlanEdit),  "session.plan",      null),
        // T0 是用户固定上下文、模型不能改；线上走专用 HTTP `/sessions/{id}/plan/t0`，
        // 没有 WS/控制台形态（同普通树 JSON 的理由：控制台单行命令不承载表单）。
        (typeof(CoreCommand.SessionT0Set),      null,                null),
        (typeof(CoreCommand.SessionT0Clear),    null,                null),

        // 询问：挂起的唯一解除入口（`01` §3.8）。四条线都在——
        // ⚠️ 控制台那条尤其不能缺：`approval …` 落回「当成消息」会**静默烧一次模型调用**
        // 而会话还挂在等待里。
        (typeof(CoreCommand.ApprovalRespond),  "approval.respond",  "approval allow s1 ask1"),
    ];

    // ---- ① 那张表必须覆盖全部类型 ----

    [Fact]
    public void Matrix_CoversEveryCoreCommandType()
    {
        var declared = typeof(CoreCommand).GetNestedTypes()
            .Where(t => !t.IsAbstract && t.IsSubclassOf(typeof(CoreCommand)))
            .ToHashSet();

        // ⚠️ 没有这一条，反射写错（比如筛选条件写反）会得到一个空集，
        // 下面三条断言**全部空真**、整条守卫变成摆设。
        Assert.NotEmpty(declared);

        // 新增类型没进表 ⇒ 红。
        Assert.Empty(declared.Except(Matrix.Select(r => r.Cmd)));
        // 表里留着已经不存在的类型 ⇒ 红。
        Assert.Empty(Matrix.Select(r => r.Cmd).Except(declared));
        // 同一个类型占两行 ⇒ 红（否则「覆盖了」可以是重复凑出来的）。
        Assert.Equal(Matrix.Length, Matrix.Select(r => r.Cmd).Distinct().Count());
    }

    // ---- ② 线上解析：每条 wire 形态都造出**那一个**类型 ----

    [Fact]
    public void WireLine_ProducesExactlyTheMatrixType()
    {
        foreach (var (cmd, wire, _) in Matrix)
        {
            if (wire is null) continue;

            using var doc = JsonDocument.Parse(WirePayload(wire));
            Assert.Equal(cmd, ControlServer.ParseCommand(doc.RootElement).GetType());
        }
    }

    // ---- ②补：线上**确实读到了**那几个「键名与属性名不一致 / 曾经被漏读」的字段 ----
    //
    // ⚠️ **这条是本批（2026-10-18）的判据，修之前它红**。
    //
    // ② 只断类型，而「类型对、字段全是默认值」在它眼里**完全合格**——于是：
    //   · `session.create` 的线上臂**根本没读 `preset`** ⇒ 头指定预设被静默丢弃；
    //   · `session.configure` 读的是 `"model"`，而签名 / `/state` / 头发的都是 `modelId`
    //     ⇒ 换模型被静默丢弃。
    // 两处回执照样 `accepted:true`，**没有任何东西会报错**。
    //
    // ⚠️ 比的是**值**不是类型（同那条分隔符测试的分寸）：这里断言的就是「真的读到了」。
    [Fact]
    public void WireLine_ReadsTheFieldsWhoseKeyNamesDifferFromThePropertyNames()
    {
        var created = ControlServer.ParseCommand(JsonDocument.Parse(
            """{"kind":"session.create","cwd":"E:/w","title":"t","preset":"task","askTimeoutMinutes":30}""")
            .RootElement);
        var create = Assert.IsType<CoreCommand.SessionCreate>(created);
        Assert.Equal("E:/w", create.Cwd);
        Assert.Equal("t", create.Title);
        Assert.Equal("task", create.Preset);
        Assert.Equal(30, create.AskTimeoutMinutes);

        var configured = ControlServer.ParseCommand(JsonDocument.Parse(
            """{"kind":"session.configure","sessionId":"s1","providerId":"p","modelId":"m","askTimeoutMinutes":5}""")
            .RootElement);
        var configure = Assert.IsType<CoreCommand.SessionConfigure>(configured);
        Assert.Equal("p", configure.ProviderId);
        Assert.Equal("m", configure.ModelId);
        Assert.Equal(5, configure.AskTimeoutMinutes);
    }

    // ---- ②补之二：`remember` 的拼法**只有一个来源** ----
    //
    // ⚠️ 它曾经是 `string?`，解析层取原文、dispatcher 比 `nameof(AskMemory.Tool)`（`"Tool"`），
    // 而线上所有生产方给的是 `"tool"` ⇒ **「记住这个工具」从没生效过**，且无报错。
    // 现在类型是枚举、由 `WireJson.Options` 的转换器接管（与 `/state` 的 `ask.remember` 同源）。
    // 这里钉两件事：**两种拼法都认**（大小写不敏感是 STJ 的默认），**乱值落回「只这一次」**。
    [Theory]
    [InlineData("\"tool\"", AskMemory.Tool)]
    [InlineData("\"Tool\"", AskMemory.Tool)]
    [InlineData("\"args\"", AskMemory.Args)]
    [InlineData("\"Args\"", AskMemory.Args)]
    [InlineData("null", null)]
    [InlineData("\"nonsense\"", null)]
    public void WireLine_RememberAcceptsTheWireSpellingAndFallsBackToNull(string json, AskMemory? expected)
    {
        var parsed = ControlServer.ParseCommand(JsonDocument.Parse(
            $$"""{"kind":"approval.respond","sessionId":"s1","askId":"a1","outcome":"submit","remember":{{json}}}""")
            .RootElement);

        Assert.Equal(expected, Assert.IsType<CoreCommand.ApprovalRespond>(parsed).Remember);
    }

    // 输入数据，**不是 `ParseCommand` 的复刻**——每种命令一个最小合法载荷。
    private static string WirePayload(string kind) => kind switch
    {
        "session.send" => """{"kind":"session.send","sessionId":"s1","blocks":[]}""",
        "claim.release" => """{"kind":"claim.release","sessionId":"s1","scope":["src/foo"]}""",
        "session.setAxis" => """{"kind":"session.setAxis","sessionId":"s1","axis":"safety","value":"prod"}""",
        "session.configure" => """{"kind":"session.configure","sessionId":"s1","modelId":"m"}""",
        // ⚠️ 预设这几条**不能走那个兜底载荷**：它会塞一个 `sessionId`，而这三条
        // 各自要的是 `id`（预设名）与 `content`。用兜底的话会漏测「真的读到了那两个键没有」。
        "preset.set" => """{"kind":"preset.set","id":"default","content":"你是助手"}""",
        "preset.delete" => """{"kind":"preset.delete","id":"default"}""",
        "preset.list" => """{"kind":"preset.list"}""",
        "approval.respond" => """{"kind":"approval.respond","sessionId":"s1","askId":"a1","outcome":"submit"}""",
        // 联网：显式载荷，因为兜底那张塞的 `id` / `sessionId` 这两个键它一个都不读。
        "network.configure" => """{"kind":"network.configure","enabled":true,"endpoint":"https://api.tavily.com/search","maxResults":5}""",
        "network.test" => """{"kind":"network.test"}""",
        _ => $$"""{"kind":"{{kind}}","id":"x","sessionId":"s1"}""",
    };

    // ---- ③ 控制台形态：每条都解析成**那一个**类型 ----

    [Fact]
    public void ConsoleLine_ProducesExactlyTheMatrixType()
    {
        foreach (var (cmd, _, line) in Matrix)
        {
            if (line is null) continue;

            var (parsed, err) = CoreCommandParser.Parse(line, "s1");

            Assert.Null(err);
            Assert.Equal(cmd, parsed!.GetType());
        }
    }

    // ---- ④ 分隔符：Tab / NBSP 与空格**等价** ----
    //
    // ⚠️ **这条是本批「修 M5」的判据**：修复之前它会红。
    // 缺陷形状：入口判据写的是字面空格（`StartsWith("provider delete ", …)`），
    // 而同文件别处的判据走宽口径（`char.IsWhiteSpace`）⇒ `provider delete<TAB>p` 两边都不认，
    // 掉进 `Repl.AsMessage` ⇒ **当成消息发给模型**：用户以为删了、provider 还在盘上、还烧一次调用。
    //
    // ⚠️ 比的是**值**不是类型：类型相同而某个标志被静默丢掉（`session create\t--cwd\tD:\x` 解析出
    // `Cwd == null`）是一条**照样绿**的漏网路径。
    [Theory]
    [InlineData('\t')]
    [InlineData(' ')]   // NBSP
    public void ConsoleLine_WithAnyWhitespaceSeparator_ParsesIdenticallyToSpaces(char separator)
    {
        foreach (var (_, _, line) in Matrix)
        {
            if (line is null) continue;

            var spaced = CoreCommandParser.Parse(line, "s1").Command;
            Assert.NotNull(spaced);

            var (parsed, err) = CoreCommandParser.Parse(line.Replace(' ', separator), "s1");

            Assert.Null(err);
            Assert.NotNull(parsed);
            Assert.Equal(spaced.GetType(), parsed.GetType());

            // ⚠️ record 的默认相等对 `IReadOnlyList` 字段是**引用相等** ⇒ 比 ToString 也弱
            //（两个内容相同的 List 印出来一样）⇒ 只有序列化后的 JSON 比得到值。
            Assert.Equal(Serialize(spaced), Serialize(parsed));
        }
    }

    private static string Serialize(CoreCommand cmd) =>
        JsonSerializer.Serialize(cmd, cmd.GetType(), WireJson.Options);
}
