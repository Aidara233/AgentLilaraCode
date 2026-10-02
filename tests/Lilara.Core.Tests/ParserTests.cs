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
}
