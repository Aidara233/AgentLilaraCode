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
    /// 判据：`trimmed == "claim release"` 要单独拦，**在 acquire 分支之前**。
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
