using Lilara.Core.Console;
using Lilara.Core.Hosting;

namespace Lilara.Core.Tests;

// 判据恒为 (Command, Error)：两者都 null = 不是命令形态 ⇒ 调用方按普通消息发出去。
public class ParserTests
{
    // axis set 的三段式：会话 · 轴 · 值，空格分隔。
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

    // 少一个词不能静默变成别的命令：掉进 (null, null) 就会被当消息发出去，用户以为设了轴。
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

    // axis 不是命令时仍然是消息——别把整段前缀吃死。
    [Fact]
    public void Axis_WithAnUnknownVerb_FallsThroughToBeingAMessage()
    {
        var (cmd, err) = CoreCommandParser.Parse("axis is a good thing");

        Assert.Null(cmd);
        Assert.Null(err);   // ⇒ 调用方按普通消息处理
    }

    // axis set 的词块判据必须宽口径（Tab / 连续空格都算分隔符，动词大小写不敏感）；
    // 入口判据只认字面空格时，这些写法会掉进 (null, null) ⇒ 当成消息发给了会话。
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

    // NBSP（U+00A0）也是空白，别让 axis set 漏回「当成消息」；用 (char)0x00A0 显式拼，不写字面量。
    [Fact]
    public void AxisSet_WithAnNbspSeparator_IsStillTheCommand()
    {
        var line = "axis set" + (char)0x00A0 + "s1 safety prod";

        var (cmd, err) = CoreCommandParser.Parse(line);

        Assert.Null(err);
        var c = Assert.IsType<CoreCommand.SessionSetAxis>(cmd);
        Assert.Equal(("s1", "safety", "prod"), (c.SessionId, c.Axis, c.Value));
    }

    // 光一个 axis 不能落回「当成消息」——用户以为在设轴。
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

    // 反向守卫：words[1] 不是 set 时不许吃进来（axis settings … 落回普通消息）。
    [Theory]
    [InlineData("axis settings s1 safety prod")]
    [InlineData("axis sete s1 safety prod")]
    public void AxisWithANonSetSecondWord_FallsThroughToBeingAMessage(string line)
    {
        var (cmd, err) = CoreCommandParser.Parse(line);

        Assert.Null(cmd);
        Assert.Null(err);
    }

    // 必须传会话 id：不传等价于传 null，WithSession 对 null 一律报错——
    // C# 里「省略实参」与「显式传 null」用同一个可选参数、无法区分。
    [Fact]
    public void Claim_WithAPath_AcquiresOnTheCurrentSession()
    {
        var (cmd, err) = CoreCommandParser.Parse("claim src/foo", "s1");

        Assert.Null(err);
        Assert.Equal(["src/foo"], Assert.IsType<CoreCommand.ClaimAcquire>(cmd).Scope);
    }

    // 同样必须传会话 id——理由见上一条。
    [Fact]
    public void ClaimRelease_WithAPath_Releases()
    {
        var (cmd, err) = CoreCommandParser.Parse("claim release src/foo", "s1");

        Assert.Null(err);
        Assert.Equal(["src/foo"], Assert.IsType<CoreCommand.ClaimRelease>(cmd).Scope);
    }

    // 词之间可以有多个空格：初版用精确匹配守单空格，claim release（两个空格）会落进 acquire 分支，
    // 静默占一个叫 release 的路径；Trim 只去首尾空白，管内部分隔。
    [Fact]
    public void ClaimRelease_WithExtraSpaces_StillReleases()
    {
        var (cmd, err) = CoreCommandParser.Parse("claim  release  src/foo", "s1");

        Assert.Null(err);
        Assert.Equal(["src/foo"], Assert.IsType<CoreCommand.ClaimRelease>(cmd).Scope);
    }

    // 动词大小写不敏感（claim Release src 必须释放）；而轴名该敏感——它是协议标识符。
    [Fact]
    public void ClaimRelease_IsCaseInsensitiveOnTheVerb()
    {
        var (cmd, err) = CoreCommandParser.Parse("claim Release src/foo", "s1");

        Assert.Null(err);
        Assert.Equal(["src/foo"], Assert.IsType<CoreCommand.ClaimRelease>(cmd).Scope);
    }

    // claim list 的动词同样不敏感——别让它静默占一个叫 LIST 的路径。
    [Fact]
    public void ClaimList_IsCaseInsensitiveOnTheVerb()
    {
        var (cmd, err) = CoreCommandParser.Parse("claim LIST", "s1");

        Assert.Null(cmd);
        Assert.Null(err);            // (null, null) = 控制台查询，由 Repl 拦截
    }

    // IsClaimList 与 Parse 必须口径一致（Repl 的拦截用的是前者）；
    // 不一致时 claim LIST 被解析器认作查询却被 Repl 放行 ⇒ 当成消息发出去。
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

    // 反向：不是查询的输入，谓词必须为假——否则会把用户的话吞掉。
    [Theory]
    [InlineData("claim")]
    [InlineData("claim src/foo")]
    [InlineData("claimlist")]         // 没有空格：不是命令
    [InlineData("claim list extra")]
    [InlineData("claims list")]
    public void IsClaimList_IsFalseForThingsThatAreNotTheQuery(string line) =>
        Assert.False(CoreCommandParser.IsClaimList(line));

    // NBSP 也是空白：IsClaimList 的边界用 char.IsWhiteSpace（宽），而 Parse 的词块入口曾用
    // StartsWith（窄），claim + NBSP + 路径两边都不认 ⇒ 当成消息发给模型。NBSP 从网页复制粘贴就会带进来。
    [Fact]
    public void Claim_WithANbspSeparator_IsStillTreatedAsTheCommand()
    {
        var (cmd, err) = CoreCommandParser.Parse("claim\u00A0src/foo", "s1");

        Assert.Null(err);
        Assert.Equal(["src/foo"], Assert.IsType<CoreCommand.ClaimAcquire>(cmd).Scope);
    }

    // list 那一臂只该剩「报错」——单词的 claim list 已被 IsClaimList 在词块之前截走，其 (null,null) 臂不可达。
    [Fact]
    public void ClaimList_WithExtraWords_IsAnError()
    {
        var (cmd, err) = CoreCommandParser.Parse("claim list extra", "s1");

        Assert.Null(cmd);
        Assert.NotNull(err);
    }

    // 路径只收一个词，多给了就报错：控制台没有引号语法，含空格的路径无法表达，拼起来是猜。
    [Fact]
    public void Claim_WithMoreThanOneWord_IsAnError()
    {
        var (cmd, err) = CoreCommandParser.Parse("claim src/foo extra", "s1");

        Assert.Null(cmd);
        Assert.NotNull(err);
    }

    // 光一个 claim 不落回「当成消息」——否则用户以为占了，其实是发了个字。
    [Fact]
    public void Claim_WithNothing_IsAnError()
    {
        var (cmd, err) = CoreCommandParser.Parse("claim");

        Assert.Null(cmd);
        Assert.NotNull(err);
    }

    // claim release 少了路径不许当成「占一个叫 release 的路径」；判据是切词后 words[0] 是 release 而 words.Length != 2。
    [Fact]
    public void ClaimRelease_WithNothing_IsAnError()
    {
        var (cmd, err) = CoreCommandParser.Parse("claim release", "s1");

        Assert.Null(cmd);
        Assert.NotNull(err);
    }

    // claim list 不是核心命令，是控制台的查询——返回 (null, null) 由 Repl 拦截。
    [Fact]
    public void ClaimList_IsNotACoreCommand() =>
        Assert.Equal((null, null), CoreCommandParser.Parse("claim list"));

    // claim 没有当前会话时报错，而不是产出一个空会话 id 的命令（空 id 会让 dispatcher 回「没有会话 ''」）。
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
        // 别把 Scope 塞进元组去比：它是 IReadOnlyList<string>，与字符串字面量放同一元组会泛型推断失败，
        // 落到 Equal(DateTime, DateTime) 那条候选上报 CS1503。逐项断言。
        var acquire = Assert.IsType<CoreCommand.ClaimAcquire>(cmd);
        Assert.Equal("s9", acquire.SessionId);
        Assert.Equal(["src/foo"], acquire.Scope);
    }

    // claim 入口判据必须先确认「前五个字符就是 claim」，不能只看第 6 个字符是不是空白。
    // 每条 InlineData 的首词必须恰好 5 个字符，否则它在坏代码上也会绿（零区分力）。
    [Theory]
    [InlineData("hello world")]           // hello  = 5 字符 ⇒ [5]=' '
    [InlineData("write a test")]          // write  = 5 字符 ⇒ [5]=' '
    [InlineData("there is a bug")]        // there  = 5 字符 ⇒ [5]=' '
    [InlineData("你好世界啊 测试")]         // 5 个 CJK 字符后是空格 ⇒ [5]=' '（UTF-16 下每字 1 char）
    public void OrdinaryMessage_WhoseFirstWordIsFiveChars_IsNotACommand(string line)
    {
        // 先核死这条用例真的有区分力：入口判据读的是 line[5]，故只有 [5] 是空白时才可能被坏代码打破。
        Assert.True(line.Length > 5 && char.IsWhiteSpace(line[5]),
            $"用例本身写坏了：'{line}' 的 [5] 不是空白 ⇒ 它在坏代码上也会绿，证明不了任何事。");

        var (cmd, err) = CoreCommandParser.Parse(line, "s1");

        // 两者都 null = 「不是命令形态」⇒ 调用方按普通消息发出去。
        Assert.Null(cmd);
        Assert.Null(err);
    }

    // claim / axis 两个词块的首词大小写口径必须一致：放宽只是多认一条命令，收紧会让 Claim src
    // 这类行字静默变成消息发给模型——两种错的方向不对称，故选宽。
    [Theory]
    [InlineData("claim src/foo", "s1")]
    [InlineData("ClAiM src/foo", "s1")]
    [InlineData("axis set s1 safety prod", null)]
    [InlineData("AXIS SET s1 safety prod", null)]
    public void BothVerbEntries_AreCaseInsensitiveOnTheirFirstWord(string line, string? sid)
    {
        var (cmd, err) = CoreCommandParser.Parse(line, sid);

        // 大小写变体必须与它的小写形态完全同解——不是「不报错就行」，而是真的产出同一个命令。
        Assert.NotNull(cmd);
        Assert.Null(err);
    }

    // 词边界：首词后必须紧跟空白或行尾，否则那行字被吞掉（claimlist / axisx）。
    [Theory]
    [InlineData("claimlist")]
    [InlineData("axisx")]
    public void FirstWordWithoutABoundary_FallsThroughToBeingAMessage(string line)
    {
        var (cmd, err) = CoreCommandParser.Parse(line, "s1");

        Assert.Null(cmd);
        Assert.Null(err);   // ⇒ 当成普通消息
    }

    // --key 不再是必填（能改不能看 ⇒ 头/控制台都不该被逼着重发 key）；省略 ⇒ ApiKey 为 null
    // ⇒ dispatcher 的语义是「保留旧的」；新 provider 省略 key 由 dispatcher 拒绝（在 CommandDispatcherTests 里）。
    [Fact]
    public void ProviderSet_WithoutKey_IsStillACommand_WithANullKey()
    {
        var (cmd, err) = CoreCommandParser.Parse("provider set p --protocol openai-chat");

        Assert.Null(err);
        var c = Assert.IsType<CoreCommand.ProviderUpsert>(cmd);
        Assert.Equal("p", c.Id);
        Assert.Null(c.ApiKey);                       // 「省略」到这里就是 null（= 保留旧的）
        Assert.Equal("openai-chat", c.Protocol);     // 而本次真的改的那一项照常带上
    }

    // 反向那半：显式给 key 仍然要落到命令上——别为了「可选」把它丢了。
    [Fact]
    public void ProviderSet_WithKey_StillCarriesIt()
    {
        var (cmd, err) = CoreCommandParser.Parse("provider set p --key sk-abc");

        Assert.Null(err);
        var c = Assert.IsType<CoreCommand.ProviderUpsert>(cmd);
        Assert.Equal("sk-abc", c.ApiKey);
    }

    // provider delete <id> 必须被解析成命令——少了这一臂会掉进 (null, null)，当成消息发给会话：
    // 用户以为删了，实际烧了一次模型调用，而 provider 还在盘上。
    [Fact]
    public void ProviderDelete_ParsesTheId()
    {
        var (cmd, err) = CoreCommandParser.Parse("provider delete p");

        Assert.Null(err);
        var c = Assert.IsType<CoreCommand.ProviderDelete>(cmd);
        Assert.Equal("p", c.Id);
    }

    // 少了 id 必须报错，不能落回「当成消息」；两半都要断言——只断 Command is null 无法区分
    // 「报错」与「不是命令形态（⇒ 当消息发出去）」，而后者才是危险的那一支。
    [Fact]
    public void ProviderDelete_WithoutAnId_IsAnError_NotAMessage()
    {
        var (cmd, err) = CoreCommandParser.Parse("provider delete");

        Assert.Null(cmd);
        Assert.NotNull(err);   // 不是 (null, null)：那一支会被 Repl 当成消息发出去
    }

    // 多给一个词要报错，不许猜：猜的代价是删错东西（provider delete p q 若静默取 p，命令与发生的事不是一回事）。
    [Fact]
    public void ProviderDelete_WithExtraWords_IsAnError()
    {
        var (cmd, err) = CoreCommandParser.Parse("provider delete p q");

        Assert.Null(cmd);
        Assert.NotNull(err);
    }

    // 反向对照：词边界。provider deletes p 与 provider deletep 必须落回 (null, null)（⇒ 当消息）。
    [Theory]
    [InlineData("provider deletes p")]
    [InlineData("provider deletep")]
    public void ProviderDelete_NeedsAWordBoundary(string line)
    {
        var (cmd, err) = CoreCommandParser.Parse(line);

        Assert.Null(cmd);
        Assert.Null(err);   // ⇒ 当成普通消息
    }

    // ---- session create：选目录 + 起名 ----

    // ⚠️ 光杆的 `session create` 必须继续成立——今天控制台的默认手感，
    // 而且它走的是**另一条分支**（词数 2），不是「标志循环什么都没解析到」。
    [Fact]
    public void SessionCreate_Bare_ProducesACommandWithNoOptions()
    {
        var (cmd, err) = CoreCommandParser.Parse("session create");

        Assert.Null(err);
        var c = Assert.IsType<CoreCommand.SessionCreate>(cmd);
        Assert.Null(c.Cwd);
        Assert.Null(c.Title);
    }

    [Fact]
    public void SessionCreate_ParsesBothFlags()
    {
        var (cmd, err) = CoreCommandParser.Parse("session create --cwd D:\\work\\proj --title 重构");

        Assert.Null(err);
        var c = Assert.IsType<CoreCommand.SessionCreate>(cmd);
        Assert.Equal("D:\\work\\proj", c.Cwd);
        Assert.Equal("重构", c.Title);
    }

    [Fact]
    public void SessionCreate_WithOnlyOneFlag_LeavesTheOtherNull()
    {
        var (cmd, err) = CoreCommandParser.Parse("session create --title 只有名字");

        Assert.Null(err);
        var c = Assert.IsType<CoreCommand.SessionCreate>(cmd);
        Assert.Null(c.Cwd);
        Assert.Equal("只有名字", c.Title);
    }

    // ⚠️ 未知标志必须拒绝：拼错的 `--cwd` 被静默吞掉的话，会话会照常建出来——
    // 而它落在**核心的工作目录**，不是用户想要的地方。用户不会发现。
    [Fact]
    public void SessionCreate_WithAnUnknownFlag_IsAnError()
    {
        var (cmd, err) = CoreCommandParser.Parse("session create --cdw D:\\work");

        Assert.Null(cmd);
        Assert.NotNull(err);
    }

    // 末尾标志缺值不能静默通过（与 provider set 同一份守卫）。
    [Fact]
    public void SessionCreate_WithATrailingFlag_IsAnError()
    {
        var (cmd, err) = CoreCommandParser.Parse("session create --cwd");

        Assert.Null(cmd);
        Assert.NotNull(err);
    }

    // `--ask-timeout`（`01` §3.8）：给的是**分钟数**，落成 `AskTimeoutMinutes`。
    // ⚠️ 这一层只把它解析成数字；「必须 > 0」那条判据住在命令层（与线上共用一份）。
    [Fact]
    public void SessionCreate_ParsesAskTimeout()
    {
        var (cmd, err) = CoreCommandParser.Parse("session create --ask-timeout 5");

        Assert.Null(err);
        var c = Assert.IsType<CoreCommand.SessionCreate>(cmd);
        Assert.Equal(5, c.AskTimeoutMinutes);
    }

    // 不给 ⇒ null = **不超时**（默认）。⚠️ 与「给了一个取不出来的值」要分开。
    [Fact]
    public void SessionCreate_WithoutAskTimeout_LeavesItNull()
    {
        var (cmd, err) = CoreCommandParser.Parse("session create --title t");

        Assert.Null(err);
        Assert.Null(Assert.IsType<CoreCommand.SessionCreate>(cmd).AskTimeoutMinutes);
    }

    // ⚠️ **不是数字 ⇒ 报错，不许静默当成「没给」**：那会让一次写错的参数
    // 变成一次「没有上限」的会话，而用户以为自己设了超时。
    [Fact]
    public void SessionCreate_WithANonNumericAskTimeout_IsAnError()
    {
        var (cmd, err) = CoreCommandParser.Parse("session create --ask-timeout 五分钟");

        Assert.Null(cmd);
        Assert.NotNull(err);
    }

    // `session configure` 同样收这一格。
    [Fact]
    public void SessionConfigure_ParsesAskTimeout()
    {
        var (cmd, err) = CoreCommandParser.Parse("session configure s1 --ask-timeout 2.5");

        Assert.Null(err);
        Assert.Equal(2.5, Assert.IsType<CoreCommand.SessionConfigure>(cmd).AskTimeoutMinutes);
    }

    // 反向对照：词边界。session creates … 必须落回 (null, null)（⇒ 当消息）。
    [Fact]
    public void SessionCreate_NeedsAWordBoundary()
    {
        var (cmd, err) = CoreCommandParser.Parse("session creates foo");

        Assert.Null(cmd);
        Assert.Null(err);
    }

    // ---- 销毁面：deactivate / archive / delete ----

    [Fact]
    public void SessionDeactivate_ParsesTheId()
    {
        var (cmd, err) = CoreCommandParser.Parse("session deactivate abc123");

        Assert.Null(err);
        Assert.Equal("abc123", Assert.IsType<CoreCommand.SessionDeactivate>(cmd).SessionId);
    }

    [Fact]
    public void SessionDelete_ParsesTheId()
    {
        var (cmd, err) = CoreCommandParser.Parse("session delete abc123");

        Assert.Null(err);
        Assert.Equal("abc123", Assert.IsType<CoreCommand.SessionDelete>(cmd).SessionId);
    }

    // ⚠️ 不给 `--unarchive` ⇒ `Archived` 是 **null**（三态的中性档）——
    // 写成 `default(bool?)` 之外的任何东西（比如 false）都会把「归档」变成「取消归档」。
    [Fact]
    public void SessionArchive_WithNoFlag_MeansArchive()
    {
        var (cmd, err) = CoreCommandParser.Parse("session archive abc123");

        Assert.Null(err);
        var c = Assert.IsType<CoreCommand.SessionArchive>(cmd);
        Assert.Equal("abc123", c.SessionId);
        Assert.Null(c.Archived);
    }

    [Fact]
    public void SessionArchive_WithUnarchiveFlag_MeansUnarchive()
    {
        var (cmd, err) = CoreCommandParser.Parse("session archive abc123 --unarchive");

        Assert.Null(err);
        Assert.False(Assert.IsType<CoreCommand.SessionArchive>(cmd).Archived);
    }

    // ⚠️ 这三条**光杆也必须报错**，不能落回「当成消息」——`session delete` 被当消息发出去，
    // 用户以为删了，而会话还在、还白烧一次模型调用。这就是 `session.start` 那个既有缺陷的形状。
    [Theory]
    [InlineData("session deactivate")]
    [InlineData("session archive")]
    [InlineData("session delete")]
    public void DestructiveVerbs_WithoutAnId_AreErrors_NotMessages(string line)
    {
        var (cmd, err) = CoreCommandParser.Parse(line);

        Assert.Null(cmd);
        Assert.NotNull(err);
    }

    // 多余的词不猜（猜错的方向是动错会话，其中一条不可逆）。
    [Theory]
    [InlineData("session delete a b")]
    [InlineData("session deactivate a b")]
    [InlineData("session archive a b c")]
    public void DestructiveVerbs_WithExtraWords_AreErrors(string line)
    {
        var (cmd, err) = CoreCommandParser.Parse(line);

        Assert.Null(cmd);
        Assert.NotNull(err);
    }

    // 反向对照：词边界。`session deletes x` / `session archivex` 必须落回 (null, null)（⇒ 当消息）。
    [Theory]
    [InlineData("session deletes abc")]
    [InlineData("session archives abc")]
    [InlineData("session archivex abc")]
    [InlineData("session deletex abc")]
    public void DestructiveVerbs_NeedAWordBoundary(string line)
    {
        var (cmd, err) = CoreCommandParser.Parse(line);

        Assert.Null(cmd);
        Assert.Null(err);
    }

    // ---- session configure：改绑供应商 / 换模型 ----

    [Fact]
    public void SessionConfigure_ParsesBothFlags()
    {
        var (cmd, err) = CoreCommandParser.Parse("session configure abc123 --provider p2 --model m9");

        Assert.Null(err);
        var c = Assert.IsType<CoreCommand.SessionConfigure>(cmd);
        Assert.Equal("abc123", c.SessionId);
        Assert.Equal("p2", c.ProviderId);
        Assert.Equal("m9", c.ModelId);
    }

    // 省略的标志到命令上就是 null（= 保留旧值），与 `provider set` 逐字同构。
    [Fact]
    public void SessionConfigure_WithOnlyOneFlag_LeavesTheOtherNull()
    {
        var (cmd, err) = CoreCommandParser.Parse("session configure abc123 --provider p2");

        Assert.Null(err);
        var c = Assert.IsType<CoreCommand.SessionConfigure>(cmd);
        Assert.Equal("p2", c.ProviderId);
        Assert.Null(c.ModelId);
    }

    // ⚠️ 未知标志必须报错。拼错的 `--provder` 若被静默吞掉，命令会照常发出去、回一句 ✓，
    // 而**改绑根本没发生**——用户以为换了供应商，实际还在旧的上面。
    [Fact]
    public void SessionConfigure_WithAnUnknownFlag_IsAnError()
    {
        var (cmd, err) = CoreCommandParser.Parse("session configure abc123 --provder p2");

        Assert.Null(cmd);
        Assert.NotNull(err);
    }

    // 光一个 `session configure` 不能落回「当成消息」——用户以为在改绑，实际烧了一次模型调用。
    [Fact]
    public void SessionConfigure_WithoutArguments_IsAnError()
    {
        var (cmd, err) = CoreCommandParser.Parse("session configure");

        Assert.Null(cmd);
        Assert.NotNull(err);
    }

    // 反向对照：词边界。session configures … 必须落回 (null, null)（⇒ 当消息）。
    [Fact]
    public void SessionConfigure_NeedsAWordBoundary()
    {
        var (cmd, err) = CoreCommandParser.Parse("session configures abc123 --provider p2");

        Assert.Null(cmd);
        Assert.Null(err);
    }

    // `--models a,b,c` → 清单。头的模型下拉靠它。
    [Fact]
    public void ProviderSet_WithModels_ParsesTheCommaSeparatedList()
    {
        var (cmd, err) = CoreCommandParser.Parse("provider set p --key k --models a,b,c");

        Assert.Null(err);
        var c = Assert.IsType<CoreCommand.ProviderUpsert>(cmd);
        Assert.Equal(["a", "b", "c"], c.Models);
    }

    // 省略 --models ⇒ null（= 保留旧清单），与其余五个字段同一条语义。
    [Fact]
    public void ProviderSet_WithoutModels_LeavesItNull()
    {
        var (cmd, _) = CoreCommandParser.Parse("provider set p --key k");

        Assert.Null(((CoreCommand.ProviderUpsert)cmd!).Models);
    }
}
