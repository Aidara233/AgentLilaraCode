using Lilara.Core.Console;
using Lilara.Core.Hosting;

namespace Lilara.Core.Tests;

/// <summary>
/// 控制台**文本解析**（<see cref="CoreCommandParser"/>）的测试。
///
/// <para>⚠️ 它**不**寄生在 <c>ReplTests</c> 里：那测的是 <c>Repl</c>（接线 + 显示），
/// 而这里测的是另一个类——解析器可以脱离 REPL 单独跑。</para>
///
/// <para>⚠️ 判据恒为 <c>(Command, Error)</c> 这个二元组，尤其是
/// **两者都 null = 「不是命令形态」⇒ 调用方按普通消息发出去**。
/// 「静默接管」就藏在这条路上：一条本该报错的命令掉进 <c>(null, null)</c>，
/// 用户以为设了轴，实际把那行字当消息发给了会话。</para>
/// </summary>
public class ParserTests
{
    /// <summary>`axis set` 的三段式：会话 · 轴 · 值，空格分隔（没有 `--` 标志，值里没有空格）。</summary>
    [Theory]
    [InlineData("axis set s1 safety prod", "s1", "safety", "prod")]
    [InlineData("axis set  s1   verify   strict", "s1", "verify", "strict")]   // 多空格
    public void AxisSet_ParsesThreeWords(string line, string sid, string axis, string value)
    {
        var (cmd, err) = CoreCommandParser.Parse(line);

        Assert.Null(err);
        var c = Assert.IsType<CoreCommand.SessionSetAxis>(cmd);
        Assert.Equal((sid, axis, value), (c.SessionId, c.Axis, c.Value));
    }

    /// <summary>
    /// ⚠️ **少一个词不能静默变成别的命令**。`axis set s1 safety`（缺值）若不报错，
    /// 会掉进「不是命令形态 ⇒ 当成消息发出去」那条路——用户以为设了轴，
    /// 实际是把这行字当消息发给了会话。**静默接管**的变体。
    ///
    /// <para>⚠️ **判据已经改了，别照着老话读。** 旧实现里 `"axis set"`（不带尾随空格）**不进**
    /// `ParseAxisSet`（前缀判据 `"axis set "` 带空格够不着它），要靠 `Parse` 里一条**单独的等值判断**
    /// 接住。现在入口是**词块判据**（`IsAxisSetVerb`，先切词再逐词比对）⇒ 这三例**都**落进
    /// `ParseAxisSet`、一律因段数不足报错。这条测试的**断言不变**（三例都不是消息、都报错），
    /// 变的只是它们走哪条路。</para>
    /// </summary>
    [Theory]
    [InlineData("axis set")]
    [InlineData("axis set s1")]
    [InlineData("axis set s1 safety")]
    public void AxisSet_WithTooFewWords_IsAnError_NotAMessage(string line)
    {
        var (cmd, err) = CoreCommandParser.Parse(line);

        Assert.Null(cmd);
        Assert.NotNull(err);
    }

    /// <summary>⚠️ `axis` 不是命令时仍然是消息——别把整段前缀吃死。</summary>
    [Fact]
    public void Axis_WithAnUnknownVerb_FallsThroughToBeingAMessage()
    {
        var (cmd, err) = CoreCommandParser.Parse("axis is a good thing");

        Assert.Null(cmd);
        Assert.Null(err);   // ⇒ 调用方按普通消息处理
    }

    /// <summary>
    /// ⚠️ **`axis set` 的词块判据必须是宽口径，且与 `claim` 同形**（终审 I1）：
    /// Tab / NBSP / 连续多个空格都算分隔符，**动词大小写不敏感**。
    ///
    /// <para>旧写法是 `StartsWith("axis set ", StringComparison.Ordinal)`（**只认字面空格**）
    /// + `Split(' ')`：`axis&lt;TAB&gt;set …` 与 `axis set&lt;NBSP&gt;…` **入口都不认** ⇒
    /// 掉进兜底 `(null, null)` ⇒ **当成消息发给了会话**——用户以为设了轴，实际烧了一次模型调用、
    /// 轴还没设。而 `axis  set …`（两个空格）在修掉 C1 之前落进 claim 词块、修掉之后**同样静默变成消息**。
    /// ⇒ 这一组一起钉住「宽口径贯通**入口**与**切词**两处」。</para>
    ///
    /// <para>⚠️ **NBSP（U+00A0）单列一条 `Fact`**（下面那条）：它从**网页复制粘贴**就会带进来，
    /// 但它是**不可见字符**，写进 `InlineData` 字面量会有「绿了却什么都没测」的风险。</para>
    /// </summary>
    [Theory]
    [InlineData("axis\tset s1 safety prod")]        // Tab
    [InlineData("axis  set  s1  safety  prod")]     // 连续多个空格
    [InlineData("Axis Set s1 safety prod")]         // 动词大小写：与 claim 同口径（不敏感）
    public void AxisSet_WithWideSeparatorsOrCapitalizedVerb_IsStillTheCommand(string line)
    {
        var (cmd, err) = CoreCommandParser.Parse(line);

        Assert.Null(err);
        var c = Assert.IsType<CoreCommand.SessionSetAxis>(cmd);
        Assert.Equal(("s1", "safety", "prod"), (c.SessionId, c.Axis, c.Value));
    }

    /// <summary>
    /// ⚠️ **NBSP（U+00A0）也是空白，别让 `axis set` 漏回「当成消息」。**
    /// 与 `Claim_WithANbspSeparator_…` 同一类：它从**网页复制粘贴**就会带进来，
    /// 而入口判据若只认 `' '` 就会把这行字原样发给会话（烧一次模型调用、轴没设）。
    ///
    /// <para>⚠️ **NBSP 用 `(char)0x00A0` 显式拼**，不写进字面量：字面量里那个字符
    /// 肉眼分不出，编辑器/工具链一个「规范化空白」就能悄悄换成普通空格，
    /// 于是测试**绿了却什么都没测**（[[invisible-input-needs-byte-check]]）。</para>
    /// </summary>
    [Fact]
    public void AxisSet_WithAnNbspSeparator_IsStillTheCommand()
    {
        var line = "axis set" + (char)0x00A0 + "s1 safety prod";

        var (cmd, err) = CoreCommandParser.Parse(line);

        Assert.Null(err);
        var c = Assert.IsType<CoreCommand.SessionSetAxis>(cmd);
        Assert.Equal(("s1", "safety", "prod"), (c.SessionId, c.Axis, c.Value));
    }

    /// <summary>
    /// ⚠️ **光一个 `axis` 不能落回「当成消息」**——用户以为在设轴。
    /// 「光一个 `axis set`」那条由 `AxisSet_WithTooFewWords_…` 钉住；
    /// 这条补上单词那一半（旧版靠一条等值判断接住它，词块判据把它并了进来）。
    /// </summary>
    [Theory]
    [InlineData("axis")]
    [InlineData("  axis  ")]      // 首尾空白
    [InlineData("AXIS")]          // 动词大小写不敏感 ⇒ 同样报用法，不静默变成消息
    public void AxisAlone_IsAnError_NotAMessage(string line)
    {
        var (cmd, err) = CoreCommandParser.Parse(line);

        Assert.Null(cmd);
        Assert.NotNull(err);
    }

    /// <summary>
    /// ⚠️ 反向守卫：`words[1]` 不是 `set` 时**不许**吃进来。
    /// `axis settings …` 的 `words[1]` 是 `settings` ⇒ 落回普通消息——这正是旧前缀判据
    /// 带尾随空格（`"axis set "`）要防的那件事，词块判据以「逐词比对」达成同一效果。
    /// </summary>
    [Theory]
    [InlineData("axis settings s1 safety prod")]
    [InlineData("axis sete s1 safety prod")]
    public void AxisWithANonSetSecondWord_FallsThroughToBeingAMessage(string line)
    {
        var (cmd, err) = CoreCommandParser.Parse(line);

        Assert.Null(cmd);
        Assert.Null(err);
    }

    /// <summary>
    /// ⚠️ **必须传会话 id。** 不传等价于传 `null`（可选参数的默认值），
    /// 而 `WithSession` 对 `null` **一律报错** ⇒ `err` 非 null、`cmd` 为 null。
    /// ⚠️ **这曾经是一处计划级矛盾**：初版这两条测试写成 `Parse("claim src/foo")`（不传），
    /// 却又断言「产出 `ClaimAcquire`」——而下面的 `Claim_WithoutACurrentSession_IsAnError`
    /// 断言「不传 ⇒ 报错」。**C# 里「省略实参」与「显式传 null」用同一个可选参数、无法区分**
    /// ⇒ **没有任何实现能同时满足这两组断言**。⇒ 修法：这两条补上 `"s1"`（与同文件另两条一致）。
    /// </summary>
    [Fact]
    public void Claim_WithAPath_AcquiresOnTheCurrentSession()
    {
        var (cmd, err) = CoreCommandParser.Parse("claim src/foo", "s1");

        Assert.Null(err);
        Assert.Equal(["src/foo"], Assert.IsType<CoreCommand.ClaimAcquire>(cmd).Scope);
    }

    /// <summary>⚠️ 同样必须传会话 id——理由见上一条。</summary>
    [Fact]
    public void ClaimRelease_WithAPath_Releases()
    {
        var (cmd, err) = CoreCommandParser.Parse("claim release src/foo", "s1");

        Assert.Null(err);
        Assert.Equal(["src/foo"], Assert.IsType<CoreCommand.ClaimRelease>(cmd).Scope);
    }

    /// <summary>
    /// ⚠️ **词之间可以有多个空格。** 初版用 `trimmed == "claim release"` 守精确单空格，
    /// 于是 `"claim  release"`（**两个空格**）落进 acquire 分支 ⇒
    /// **静默占一个叫 `release` 的路径**。
    /// ⚠️ `Trim()` 只去**首尾**空白，**内部多个空格它管不到**——
    /// 这条钉的正是 Trim 救不了的那一半。
    /// </summary>
    [Fact]
    public void ClaimRelease_WithExtraSpaces_StillReleases()
    {
        var (cmd, err) = CoreCommandParser.Parse("claim  release  src/foo", "s1");

        Assert.Null(err);
        Assert.Equal(["src/foo"], Assert.IsType<CoreCommand.ClaimRelease>(cmd).Scope);
    }

    /// <summary>
    /// ⚠️ **动词大小写不敏感**——`claim Release src` 必须**释放**，不是静默占一个
    /// 叫 `Release src` 的怪路径。
    /// ⚠️ 为什么动词该不敏感而 `axis set` 的**轴名**该敏感：后者是**协议标识符**
    ///（`Axes` 的注释钉过「`Safety` 不是 `safety`，应当拒绝而不是猜」），
    /// 而这里的主语是**控制台语法**（协议里叫 `claim.release`，带点）。
    /// ⚠️ 更关键的是**后果不同**：轴名写错 → 报错（可见）；动词写错 →
    /// 初版会**静默执行相反的动作**（可见性最差的那一档）。
    /// </summary>
    [Fact]
    public void ClaimRelease_IsCaseInsensitiveOnTheVerb()
    {
        var (cmd, err) = CoreCommandParser.Parse("claim Release src/foo", "s1");

        Assert.Null(err);
        Assert.Equal(["src/foo"], Assert.IsType<CoreCommand.ClaimRelease>(cmd).Scope);
    }

    /// <summary>`claim list` 的动词同样不敏感——别让它静默占一个叫 `LIST` 的路径。</summary>
    [Fact]
    public void ClaimList_IsCaseInsensitiveOnTheVerb()
    {
        var (cmd, err) = CoreCommandParser.Parse("claim LIST", "s1");

        Assert.Null(cmd);
        Assert.Null(err);            // (null, null) = 控制台查询，由 Repl 拦截
    }

    /// <summary>
    /// ⚠️ **`IsClaimList` 与 `Parse` 必须口径一致**——`Repl` 的拦截用的是前者。
    /// 两边不一致时（初版：解析器切词、`Repl` 精确匹配），
    /// `claim LIST` 被解析器认作查询、却被 `Repl` 放行 ⇒ **当成消息发出去**。
    /// ⚠️ 这条**逐例对拍两者**，是"不可能再漂"的守卫。
    /// </summary>
    [Theory]
    [InlineData("claim list")]
    [InlineData("claim  list")]      // 两个空格
    [InlineData("claim\tlist")]      // Tab
    [InlineData("claim LIST")]       // 大写
    [InlineData("claim List")]
    [InlineData("  claim list  ")]   // 首尾空白
    public void IsClaimList_AgreesWithParse_OnEverySpelling(string line)
    {
        // 解析器认它是「查询」（(null, null)）⇔ 谓词为真
        Assert.True(CoreCommandParser.IsClaimList(line));
        Assert.Equal((null, null), CoreCommandParser.Parse(line, "s1"));
    }

    /// <summary>⚠️ 反向：**不是**查询的输入，谓词必须为假——否则会把用户的话吞掉。</summary>
    [Theory]
    [InlineData("claim")]
    [InlineData("claim src/foo")]
    [InlineData("claimlist")]         // 没有空格：不是命令
    [InlineData("claim list extra")]
    [InlineData("claims list")]
    public void IsClaimList_IsFalseForThingsThatAreNotTheQuery(string line) =>
        Assert.False(CoreCommandParser.IsClaimList(line));

    /// <summary>
    /// ⚠️ **NBSP（U+00A0）也是空白，别让它漏回「当成消息」。**
    /// ⚠️ 审阅实测的洞：`IsClaimList` 的边界用 <c>char.IsWhiteSpace</c>（**宽**），
    /// 而 `Parse` 的词块入口曾用 `StartsWith("claim ")` / `"claim\t"`（**窄**）⇒
    /// `claim` + **NBSP** + 路径 **两边都不认** ⇒ 落空 ⇒ **当成消息发给了模型**。
    /// ⚠️ 而 NBSP 从**网页复制粘贴**就会带进来。
    /// </summary>
    [Fact]
    public void Claim_WithANbspSeparator_IsStillTreatedAsTheCommand()
    {
        var (cmd, err) = CoreCommandParser.Parse("claim\u00A0src/foo", "s1");

        Assert.Null(err);
        Assert.Equal(["src/foo"], Assert.IsType<CoreCommand.ClaimAcquire>(cmd).Scope);
    }

    /// <summary>
    /// ⚠️ **`list` 那一臂只该剩「报错」**——单词的 `claim list` 已被 `IsClaimList` 在词块**之前**截走。
    /// ⇒ 它的 `(null, null)` 臂**不可达**（审阅实测：删掉它全量仍全绿 ⇒ 死代码、零覆盖）。
    /// </summary>
    [Fact]
    public void ClaimList_WithExtraWords_IsAnError()
    {
        var (cmd, err) = CoreCommandParser.Parse("claim list extra", "s1");

        Assert.Null(cmd);
        Assert.NotNull(err);
    }

    /// <summary>
    /// ⚠️ **路径只收一个词；多给了就报错，不猜。**
    /// 初版把多余词**拼进路径**（`"src/foo extra"`）——而控制台**没有引号语法**，
    /// 含空格的路径**无法表达** ⇒ 拼起来是**猜**，猜错的方向是**静默占错地方**。
    /// ⚠️ 与 `axis set` 对多余词的处置**保持一致**（T5：少了值就报错，不落回猜）。
    /// </summary>
    [Fact]
    public void Claim_WithMoreThanOneWord_IsAnError()
    {
        var (cmd, err) = CoreCommandParser.Parse("claim src/foo extra", "s1");

        Assert.Null(cmd);
        Assert.NotNull(err);
    }

    /// <summary>⚠️ 光一个 `claim` 不落回「当成消息」——否则用户以为占了，其实是发了个字。</summary>
    [Fact]
    public void Claim_WithNothing_IsAnError()
    {
        var (cmd, err) = CoreCommandParser.Parse("claim");

        Assert.Null(cmd);
        Assert.NotNull(err);
    }

    /// <summary>
    /// ⚠️ **`claim release` 少了路径，不许当成「占一个叫 release 的路径」。**
    /// 这是 `axis set` 少了尾随空格**同一个形状的缺陷**（T5 踩过）：
    /// 前缀判断放行了半个命令，剩下那半截被当成**参数**吃掉——
    /// 而它**不报错**，用户看到的是一个静默的错误动作。
    /// <para>⚠️ **判据已经改了，别照着老话写。** 本注释初版写的是
    /// 「判据：`trimmed == "claim release"` 要单独拦，**在 acquire 分支之前**」——
    /// **那是精确匹配时代的话**，而实现现在是**切词**（`Split`），代码里**已经没有那个字面量**。
    /// 那句会把后来者引回精确匹配的老路 —— 而那正是 `claim  release`（两个空格）漏掉的原因。
    /// ⇒ 现在的判据是**切词后 `words[0]` 是 `release` 而 `words.Length != 2`**。</para>
    /// </summary>
    [Fact]
    public void ClaimRelease_WithNothing_IsAnError()
    {
        var (cmd, err) = CoreCommandParser.Parse("claim release", "s1");

        Assert.Null(cmd);
        Assert.NotNull(err);
    }

    /// <summary>`claim list` 不是核心命令，是控制台的查询——返回 (null, null) 由 Repl 拦截。</summary>
    [Fact]
    public void ClaimList_IsNotACoreCommand() =>
        Assert.Equal((null, null), CoreCommandParser.Parse("claim list"));

    /// <summary>
    /// ⚠️ **`claim` 没有当前会话时报错，而不是产出一个空会话 id 的命令**。
    /// 空 id 会让 dispatcher 回「没有会话 ''」——用户看到的是引号里空空的，
    /// 不知道问题出在「没选会话」上。
    /// </summary>
    [Fact]
    public void Claim_WithoutACurrentSession_IsAnError()
    {
        var (cmd, err) = CoreCommandParser.Parse("claim src/foo", currentSessionId: null);

        Assert.Null(cmd);
        Assert.Contains("session", err!);
    }

    [Fact]
    public void Claim_WithACurrentSession_UsesIt()
    {
        var (cmd, err) = CoreCommandParser.Parse("claim src/foo", "s9");

        Assert.Null(err);
        // ⚠️ **别把 `Scope` 塞进元组去比**——它是 `IReadOnlyList<string>`，与字符串字面量
        // 放同一个元组会**类型对不上**：泛型推断失败，编译器落到 `Equal(DateTime, DateTime)`
        // 那条候选上报 CS1503（而非"类型不匹配"这种一眼能看懂的消息）。
        // ⚠️ 这段是**控制器批量改 `scope` 时留下的**：它把 `.Path` 机械换成 `.Scope`，
        // **没管元组的形状**。⇒ 逐项断言，形状与上面两条测试一致。
        var acquire = Assert.IsType<CoreCommand.ClaimAcquire>(cmd);
        Assert.Equal("s9", acquire.SessionId);
        Assert.Equal(["src/foo"], acquire.Scope);
    }

    /// <summary>
    /// ⚠️ **`claim` 入口判据必须先确认「前五个字符就是 `claim`」。**
    /// 它一度写成 `trimmed.Length > 5 &amp;&amp; char.IsWhiteSpace(trimmed[5])`——
    /// **只看第 6 个字符是不是空白，从不看前五个是什么**。
    /// ⇒ 任何**首词恰好 5 个字符**的普通消息都被静默变成 `claim.acquire`：
    /// `hello world` 会在 `&lt;工作目录&gt;/world` 落一条声明（并回 `✓ 已占 world`），
    /// **话根本没发出去**；`write a test` 会回一句无关的语法错。
    ///
    /// <para>⚠️ 这是 [[one-predicate-for-both-layers]] 那类缺陷的**又一次现身**，
    /// 而且就在「把判据收敛成一处」的那次修复里：`IsClaimList` **有**前缀校验，
    /// 而它旁边这个入口判据**没有**——同一份判据的两份副本，只改了一处。</para>
    ///
    /// <para>⚠️ 485 条测试全绿是因为：所有「消息类」测试的首词**恰好都绕开了 index 5**。</para>
    ///
    /// <para>⚠️ **每条 `InlineData` 的首词都必须恰好 5 个字符**——否则 `trimmed[5]`
    /// 不是空白，**这条用例在坏代码上也会绿**（零区分力）。我第一稿写了两条假用例：
    /// `show me the file`（`show` 只有 4 字符，`[5]='m'`）与一条中文的（`[5]='o'`）——
    /// **两条都证明不了任何事**。这正是 [[invisible-input-needs-byte-check]] 要防的那类。</para>
    /// </summary>
    [Theory]
    [InlineData("hello world")]           // hello  = 5 字符 ⇒ [5]=' '
    [InlineData("write a test")]          // write  = 5 字符 ⇒ [5]=' '
    [InlineData("there is a bug")]        // there  = 5 字符 ⇒ [5]=' '
    [InlineData("你好世界啊 测试")]         // 5 个 CJK 字符后是空格 ⇒ [5]=' '（UTF-16 下每字 1 char）
    public void OrdinaryMessage_WhoseFirstWordIsFiveChars_IsNotACommand(string line)
    {
        // ⚠️ **先核死这条用例真的有区分力**：`Parse` 的入口判据读的是 `line[5]`，
        // ⇒ 本断言只有在 `line[5]` 是空白时才可能被坏代码打破。
        Assert.True(line.Length > 5 && char.IsWhiteSpace(line[5]),
            $"用例本身写坏了：'{line}' 的 [5] 不是空白 ⇒ 它在坏代码上也会绿，证明不了任何事。");

        var (cmd, err) = CoreCommandParser.Parse(line, "s1");

        // ⚠️ **两者都 null = 「不是命令形态」⇒ 调用方按普通消息发出去。**
        Assert.Null(cmd);
        Assert.Null(err);
    }

    /// <summary>
    /// ⚠️ **`claim` / `axis` 两个词块的「首词大小写口径」必须一致**——这是一条**对拍测试**。
    ///
    /// <para>它们曾是**两份各写一遍的判据**，而且**漂了**：`IsClaimVerb` 判首词用 `Ordinal`
    ///（敏感）、`IsAxisSetVerb` 用 `OrdinalIgnoreCase`（不敏感），而**两处注释都写着
    /// 「同形 / 与 claim 对齐」——那句话当时是假的**。
    /// ⇒ 现在两者共用 `StartsWithToken`（不敏感）；本测试把这条口径**钉死**，
    /// 免得下一次有人「顺手优化」其中一个时又把它拆开。</para>
    ///
    /// <para>⚠️ **方向要说清**：放宽（不敏感）**只是多认一条命令**（可见的报错 / 正确的动作）；
    /// 收紧则会让 `Claim src` 这类行字**静默变成消息发给模型**（烧钱、不可见）。
    /// 两种错的方向不对称，所以这里选宽。</para>
    /// </summary>
    [Theory]
    [InlineData("claim src/foo", "s1")]
    [InlineData("ClAiM src/foo", "s1")]
    [InlineData("axis set s1 safety prod", null)]
    [InlineData("AXIS SET s1 safety prod", null)]
    public void BothVerbEntries_AreCaseInsensitiveOnTheirFirstWord(string line, string? sid)
    {
        var (cmd, err) = CoreCommandParser.Parse(line, sid);

        // ⚠️ 大小写变体必须**与它的小写形态完全同解**——不是「不报错就行」，而是**真的产出同一个命令**。
        Assert.NotNull(cmd);
        Assert.Null(err);
    }

    /// <summary>⚠️ 词边界：首词后必须紧跟空白或行尾，否则那行字被吞掉（`claimlist` / `axisx`）。</summary>
    [Theory]
    [InlineData("claimlist")]
    [InlineData("axisx")]
    public void FirstWordWithoutABoundary_FallsThroughToBeingAMessage(string line)
    {
        var (cmd, err) = CoreCommandParser.Parse(line, "s1");

        Assert.Null(cmd);
        Assert.Null(err);   // ⇒ 当成普通消息
    }

    /// <summary>
    /// ⚠️ **`--key` 不再是必填**（R-B：能改不能看 ⇒ 头/控制台都不该被逼着重发 key）。
    /// 省略 ⇒ <c>ApiKey</c> 为 null ⇒ dispatcher 的语义是「保留旧的」；
    /// 而**新** provider 省略 key 由 dispatcher 拒绝（理由点名 id）——那在 `CommandDispatcherTests` 里。
    ///
    /// <para>⚠️ **这条钉的是四条线里的「控制台」那条**（`CoreCommandParser`）。
    /// 只留 `--key` 必填的话，控制台会回一句「用法：… --key …」而**头那条路照常**——
    /// 两个前端分叉，正是 G14/G16 要防的「静默失效」。线上那条在 `ControlServerTests`。</para>
    /// </summary>
    [Fact]
    public void ProviderSet_WithoutKey_IsStillACommand_WithANullKey()
    {
        var (cmd, err) = CoreCommandParser.Parse("provider set p --protocol openai-chat");

        Assert.Null(err);
        var c = Assert.IsType<CoreCommand.ProviderUpsert>(cmd);
        Assert.Equal("p", c.Id);
        Assert.Null(c.ApiKey);                       // ⚠️ 「省略」到这里就是 null（= 保留旧的）
        Assert.Equal("openai-chat", c.Protocol);     // 而本次真的改的那一项照常带上
    }

    /// <summary>⚠️ 反向那半：显式给 key 仍然要落到命令上——别为了「可选」把它丢了。</summary>
    [Fact]
    public void ProviderSet_WithKey_StillCarriesIt()
    {
        var (cmd, err) = CoreCommandParser.Parse("provider set p --key sk-abc");

        Assert.Null(err);
        var c = Assert.IsType<CoreCommand.ProviderUpsert>(cmd);
        Assert.Equal("sk-abc", c.ApiKey);
    }
}
