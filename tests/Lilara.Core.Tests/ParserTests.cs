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
    /// <para>⚠️ 注意 `"axis set"`（**不带尾随空格**）这一例：`Parse` 的前缀判据是
    /// <c>"axis set "</c>（带空格），所以这一行**根本不进** `ParseAxisSet`，
    /// 会掉到最后的兜底 `(null, null)` ⇒ 被当成消息。它必须由 `Parse` 里一条
    /// **单独的等值判断**接住；`"axis set s1"` / `"axis set s1 safety"` 则走前缀那条、
    /// 在 `ParseAxisSet` 里因段数不足报错。</para>
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
}
