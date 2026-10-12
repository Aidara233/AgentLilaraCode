using Lilara.Core.Hosting;
using Lilara.Core.Kernel;
using Lilara.Core.Protocol;
using Lilara.Core.Session;
using Lilara.Core.Storage;
using Lilara.Core.Tests.Fakes;

namespace Lilara.Core.Tests;

/// <summary>
/// **系统块从哪来**（`01` §3.1 头槽位那块；预设批 2026-10-07）。
///
/// <para>这一批要修的是一个实测出来的毛病：模型自己的话（「我扫过一遍」）留在时间线上，
/// 而支撑它的工具结果被换成注记 ⇒ 模型推断「我做那条消息的时候内容就已经是注记了 ⇒ <b>我当时在编</b>」
/// （会话 `aa48bbcc0a50` 的完整推理）。缺的不是注记，是「<b>回收是一件正常会发生的事</b>」这条知识。</para>
///
/// <para>⚠️ 下面每一条都在钉**三条路的区别**——它们在生产里长得像，但语义完全不同。</para>
/// </summary>
public sealed class PresetTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "lilara-preset-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _work = Path.Combine(
        Path.GetTempPath(), "lilara-presetwork-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly FakeAdapter _adapter = new();

    public PresetTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_work);
    }

    public void Dispose()
    {
        foreach (var d in new[] { _root, _work })
            try { Directory.Delete(d, recursive: true); } catch { /* 清理失败不该让测试红 */ }
    }

    private PresetStore Store => new(_root);

    /// <summary>首要块那个目录。⚠️ 与系统块**分开**（`PresetStore.PremierDir`）——两个槽位各读各的。</summary>
    private PresetStore PremierStore => new(_root, PresetStore.PremierDir);

    /// <summary>
    /// 装配三个槽。⚠️ **默认轴值是 <see cref="Axes.Defaults"/>**（不是空表）——
    /// 尾块的内容来自轴，给一个空表会让「尾块该不该有」这件事**全测成 false**，
    /// 而那正是本批要证明存在的那一格。要测「轴缺失」那条路就显式传空表。
    /// </summary>
    private PromptSlots Slots(
        string? systemId, string? premierId = null, IReadOnlyDictionary<string, string>? axes = null) =>
        SystemBlock.Render(Store, PremierStore, systemId, premierId, axes ?? Axes.Defaults());

    private (CoreHost Host, CommandDispatcher Dispatcher) NewHarness()
    {
        var host = new CoreHost(_root, _work);
        return (host, new CommandDispatcher(
            host, new EventBus(), _ => new Stepper(host.Store, _adapter), _ => _adapter));
    }

    private static void SaveProvider(CoreHost host) =>
        host.Providers.Save(new ProviderDefinition
        {
            Id = "p", Protocol = Protocols.ClaudeMessages, ApiKey = "k",
            DefaultModel = "m", Modality = "vision",
        });

    // ---- 三条路 ----

    /// <summary>没配过任何预设 ⇒ 回落内置模板，且**那句机制说明必须在**。</summary>
    [Fact]
    public void NoFile_FallsBackToBuiltIn()
    {
        var blocks = Slots(null).System;

        var text = Assert.IsType<Block.TextBlock>(Assert.Single(blocks));
        Assert.Equal(CorePrompts.SystemFallback, text.Text);

        // ⚠️ 探针：模板改空了这条会变成「断言一个空串」，而它照样绿。
        Assert.Contains("回收", text.Text);
        Assert.Contains("fetch", text.Text);
    }

    /// <summary>
    /// ⚠️ **文件在但为空 ⇒ 空块（纯 chat）**，与「文件不在」**不是同一件事**。
    ///
    /// <para>这条是这一批最容易写错的地方：把两者并成一个分支，会让「我要纯聊天」
    /// 静默变成「用回内置模板」——用户删不掉那段话，只能眼睁睁看着它回来。</para>
    /// </summary>
    [Fact]
    public void EmptyFile_IsPureChat_NotEmptyFallback()
    {
        Store.Save("default", "");

        Assert.Empty(Slots(null).System);
    }

    /// <summary>文件有内容 ⇒ 那一块就是它，逐字。</summary>
    [Fact]
    public void FileContent_IsUsedVerbatim()
    {
        const string mine = "你是测试用的。\n\n第二段。";
        Store.Save("default", mine);

        var text = Assert.IsType<Block.TextBlock>(Assert.Single(Slots(null).System));
        Assert.Equal(mine, text.Text);
    }

    /// <summary>会话指定了预设名 ⇒ 用它，**不是**用 default。</summary>
    [Fact]
    public void NamedPreset_WinsOverDefault()
    {
        Store.Save("default", "默认那份");
        Store.Save("task", "任务那份");

        var text = Assert.IsType<Block.TextBlock>(Assert.Single(Slots("task").System));
        Assert.Equal("任务那份", text.Text);
    }

    /// <summary>指定的预设不存在 ⇒ 仍然回落内置模板（「没配过」那一条，与名字无关）。</summary>
    [Fact]
    public void NamedPresetMissing_AlsoFallsBack()
    {
        Store.Save("default", "默认那份");

        var text = Assert.IsType<Block.TextBlock>(Assert.Single(Slots("typo").System));
        Assert.Equal(CorePrompts.SystemFallback, text.Text);
    }

    // ---- 块数恒定 ----

    /// <summary>
    /// ⚠️ **最多 1 块**（`00` §2.2「块数恒定」）。三条路各自数一遍——
    /// 这条不是「顺便」，因为块数漂移会**移动缓存断点**（块边界 = 失效爆炸半径的边界）。
    /// </summary>
    [Fact]
    public void AtMostOneBlock_OnEveryPath()
    {
        Assert.Single(Slots(null).System);          // 内置兜底

        Store.Save("default", "有内容");
        Assert.Single(Slots(null).System);          // 文件内容

        Store.Save("default", "");
        Assert.Empty(Slots(null).System);           // 纯 chat
    }

    // ---- 实时引用（`SessionSpec.SystemPromptPreset` 是引用，不是快照）----

    /// <summary>
    /// ⚠️ **改文件 ⇒ 下一次渲染就是新内容**（用户 2026-10-07 裁定的「实时引用」）。
    ///
    /// <para>这条是那条裁定的**定义**：会话存的是**引用**，而根因分析表明
    /// 用户要的正是「改一下，下一步就生效」。⚠️ 注意它说的是**下一次渲染**——
    /// 步内冻结那条在 `Stepper` 那边（同一步内不重读），两者不矛盾：
    /// <b>跨步实时、步内冻结</b>。</para>
    /// </summary>
    [Fact]
    public void EditingTheFile_TakesEffectOnTheNextRender()
    {
        Store.Save("default", "第一版");
        Assert.Equal("第一版",
            Assert.IsType<Block.TextBlock>(Assert.Single(Slots(null).System)).Text);

        Store.Save("default", "第二版");
        Assert.Equal("第二版",
            Assert.IsType<Block.TextBlock>(Assert.Single(Slots(null).System)).Text);
    }

    // ---- 存储层 ----

    /// <summary>
    /// ⚠️ `Load` 的 `null` 与 `""` **必须分得开**——这是上面三条路的判据来源。
    /// 合成一个的话，`SystemBlock` 那三个分支里必然有一段变成死代码。
    /// </summary>
    [Fact]
    public void Load_DistinguishesMissingFromEmpty()
    {
        Assert.Null(Store.Load("nope"));

        Store.Save("blank", "");
        Assert.Equal("", Store.Load("blank"));
    }

    /// <summary>目录不存在 ⇒ 空表，**不抛**（没建过预设是正常状态，不是错误）。</summary>
    [Fact]
    public void List_OnAFreshRoot_IsEmpty()
    {
        Assert.Empty(new PresetStore(Path.Combine(_root, "从来没有过")).List());
    }

    /// <summary>⚠️ 顺序必须确定（来自 id 本身，不是文件系统枚举序）——它会变成头那边的下拉。</summary>
    [Fact]
    public void List_IsSortedById()
    {
        Store.Save("zeta", "z");
        Store.Save("alpha", "a");
        Store.Save("mid", "m");

        Assert.Equal(["alpha", "mid", "zeta"], Store.List());
    }

    /// <summary>删掉之后引用它的会话不报错——渲染回落内置模板（引用可以消失，这是刻意的）。</summary>
    [Fact]
    public void Delete_ThenRender_FallsBackInsteadOfThrowing()
    {
        Store.Save("task", "任务那份");
        Assert.True(Store.Delete("task"));
        Assert.False(Store.Delete("task"));   // 不幂等：第二次没有文件可删

        Assert.Equal(CorePrompts.SystemFallback,
            Assert.IsType<Block.TextBlock>(Assert.Single(Slots("task").System)).Text);
    }

    /// <summary>
    /// ⚠️ **空文件删得掉**——判据是「文件在不在」，不是「`Load` 返回 null」。
    /// 后者会把「空预设」当成不存在 ⇒ 那个文件**永远删不掉**（本仓栽过的同一形状：
    /// `ProviderStore.Delete` 的注释里写着同一句）。
    /// </summary>
    [Fact]
    public void Delete_RemovesEvenAnEmptyPreset()
    {
        Store.Save("blank", "");
        Assert.True(Store.Delete("blank"));
        Assert.Null(Store.Load("blank"));
    }

    /// <summary>id 会变成文件名 ⇒ 不许穿出 `presets/`（闸在 dispatcher，这里钉那条闸本身）。</summary>
    [Theory]
    [InlineData("../escaped")]
    [InlineData("a/b")]
    [InlineData("..")]
    public void UnsafeId_IsRejectedByTheGate(string id)
    {
        // 闸住在 `CommandDispatcher`（两个前端共用一个判据），存储层自己不走它——
        // 所以这里断言的是**闸本身**，而不是 `PresetStore` 会拦。
        Assert.False(CommandDispatcher.IsSafeProviderId(id));
    }

    // ---- 命令面（三条线：dispatcher 收得到、闸在、回执说得清）----

    /// <summary>
    /// ⚠️ `preset set` 的**空内容是合法值**（纯 chat），不是「没给 content」。
    /// ⚠️ 顺带钉住回执：写完空内容之后，用户最想确认的就是「我真的存了个空的吗」。
    /// </summary>
    [Fact]
    public async Task PresetSet_AcceptsEmptyContent_AndSaysSo()
    {
        var (_, d) = NewHarness();

        var r = await d.DispatchAsync(new CoreCommand.PresetSet("default", ""), "x", false, CancellationToken.None);

        var ack = Assert.IsType<Ack.Accepted>(r.Ack);
        Assert.Contains("空预设", ack.Note!);
        Assert.Equal("", Store.Load("default"));
    }

    /// <summary>⚠️ 写进去的是什么，读出来就是什么——**不许折叠空白**（正文就是要发给模型的那段话）。</summary>
    [Fact]
    public async Task PresetSet_RoundTripsVerbatim()
    {
        const string body = "第一行\n    缩进的第二行\t带 Tab\n\n第三行";
        var (_, d) = NewHarness();

        await d.DispatchAsync(new CoreCommand.PresetSet("t", body), "x", false, CancellationToken.None);

        Assert.Equal(body, Store.Load("t"));
    }

    /// <summary>不安全的 id 必须**拒绝**，而且盘上不许落下任何东西（它会变成文件名）。</summary>
    [Theory]
    [InlineData("../escaped")]
    [InlineData("a/b")]
    public async Task PresetSet_RejectsUnsafeId(string id)
    {
        var (_, d) = NewHarness();

        var r = await d.DispatchAsync(new CoreCommand.PresetSet(id, "x"), "x", false, CancellationToken.None);

        Assert.IsType<Ack.Rejected>(r.Ack);
        Assert.False(File.Exists(Path.Combine(_root, "escaped.md")));
        Assert.False(Directory.Exists(Path.Combine(_root, "a")));
    }

    /// <summary>删不存在的预设 ⇒ **拒绝，不是幂等成功**（与 `provider.delete` 同一条判据）。</summary>
    [Fact]
    public async Task PresetDelete_Missing_IsRejected()
    {
        var (_, d) = NewHarness();

        var r = await d.DispatchAsync(new CoreCommand.PresetDelete("nope"), "x", false, CancellationToken.None);

        Assert.IsType<Ack.Rejected>(r.Ack);
    }

    /// <summary>空表也是一条**成功**的回执——「一个都没有」是初始状态，不是错误。</summary>
    [Fact]
    public async Task PresetList_OnEmpty_AcceptsWithAHint()
    {
        var (_, d) = NewHarness();

        var r = await d.DispatchAsync(new CoreCommand.PresetList(), "x", false, CancellationToken.None);

        Assert.Contains("还没有预设", Assert.IsType<Ack.Accepted>(r.Ack).Note!);
    }

    // ---- `session.create` 收 preset（`03` §3 的 `preset?`，实现到今天才落）----

    [Fact]
    public async Task SessionCreate_StoresThePresetReference()
    {
        var (host, d) = NewHarness();
        SaveProvider(host);

        var r = await d.DispatchAsync(
            new CoreCommand.SessionCreate { Preset = "task" }, "c1", false, CancellationToken.None);
        var sid = Assert.IsType<Ack.Accepted>(r.Ack).SessionId!;

        Assert.Equal("task", host.Store.ReadSpec(ProjectKey.FromPath(_work), sid).SystemPromptPreset);
    }

    /// <summary>
    /// ⚠️ **不给 preset ⇒ 存 `null`，不是存 `"default"`**。
    ///
    /// <para>两者看起来等价（渲染时都走 `default`），但它们**不是同一件事**：
    /// `null` = 「没指定 ⇒ 跟随全局」，而 `"default"` 是「我明确点名 default 那份」。
    /// 前者会跟着全局默认走，后者在「全局默认改了名字」时**不跟**。
    /// 而且 `null` + `WhenWritingNull` ⇒ 整个键不落盘，老文件的形状不变。</para>
    /// </summary>
    [Fact]
    public async Task SessionCreate_WithoutPreset_StoresNull()
    {
        var (host, d) = NewHarness();
        SaveProvider(host);

        var r = await d.DispatchAsync(new CoreCommand.SessionCreate(), "c1", false, CancellationToken.None);
        var sid = Assert.IsType<Ack.Accepted>(r.Ack).SessionId!;

        Assert.Null(host.Store.ReadSpec(ProjectKey.FromPath(_work), sid).SystemPromptPreset);
    }

    /// <summary>
    /// ⚠️ **建会话时不校验预设文件存不存在**——「先建会话、以后再写预设」是最常见的那条路。
    /// 在这里拒绝会把那条路堵死，而文件不在不是错误：渲染回落内置模板。
    /// </summary>
    [Fact]
    public async Task SessionCreate_WithMissingPresetFile_StillSucceeds()
    {
        var (host, d) = NewHarness();
        SaveProvider(host);

        var r = await d.DispatchAsync(
            new CoreCommand.SessionCreate { Preset = "还没有写的那个" }, "c1", false, CancellationToken.None);
        var sid = Assert.IsType<Ack.Accepted>(r.Ack).SessionId!;

        // 建出来了，而且渲染照样有块（内置模板）。
        Assert.Single(Slots(
            host.Store.ReadSpec(ProjectKey.FromPath(_work), sid).SystemPromptPreset).System);
    }

    // ---- 端到端：出站请求里真的带上了它（三个适配器共用的那一格）----

    /// <summary>
    /// ⚠️ **这条是这一批的验收**：预设的内容必须真的**出现在发给 provider 的请求里**。
    ///
    /// <para>前几条测的是「判定对不对」，这一条测的是「它接上了没有」——`Stepper` 里那行
    /// `SystemBlocks = []` 曾经是硬编码的，把判定写得再对，不接上去也是零。</para>
    /// </summary>
    [Fact]
    public async Task Send_PutsThePresetIntoTheOutgoingRequest()
    {
        var (host, d) = NewHarness();
        SaveProvider(host);
        Store.Save("mine", "你是被测试的那一份。");

        var r = await d.DispatchAsync(
            new CoreCommand.SessionCreate { Preset = "mine" }, "c1", false, CancellationToken.None);
        var sid = Assert.IsType<Ack.Accepted>(r.Ack).SessionId!;

        _adapter.Reply("好");
        await d.DispatchAsync(
            new CoreCommand.SessionSend(sid, [new Block.TextBlock("在吗")]), "s1", true, CancellationToken.None);

        var sent = Assert.IsType<Block.TextBlock>(Assert.Single(_adapter.Calls[^1].Slots.System));
        Assert.Equal("你是被测试的那一份。", sent.Text);
    }

    /// <summary>没配过任何预设 ⇒ 出站请求里带的是**内置模板**（那句话必须真的发得出去）。</summary>
    [Fact]
    public async Task Send_WithNoPreset_SendsTheBuiltInTemplate()
    {
        var (host, d) = NewHarness();
        SaveProvider(host);

        var r = await d.DispatchAsync(new CoreCommand.SessionCreate(), "c1", false, CancellationToken.None);
        var sid = Assert.IsType<Ack.Accepted>(r.Ack).SessionId!;

        _adapter.Reply("好");
        await d.DispatchAsync(
            new CoreCommand.SessionSend(sid, [new Block.TextBlock("在吗")]), "s1", true, CancellationToken.None);

        var sent = Assert.IsType<Block.TextBlock>(Assert.Single(_adapter.Calls[^1].Slots.System));
        Assert.Equal(CorePrompts.SystemFallback, sent.Text);
    }

    /// <summary>
    /// ⚠️ **那一步发出去的块是一个「值」，不是一个「视图」**——步跑完之后改文件，
    /// 已经记录下来的那个请求**不许跟着变**。
    ///
    /// <para>这条钉的是「<b>步内冻结</b>」那一半（`SystemBlock.Render` 的注释里写着：
    /// 跨步实时、步内冻结）。它防的具体形状是：把内容做成**延迟求值**的
    /// （`IEnumerable` / 一个每次读盘的 `IReadOnlyList` 实现）——
    /// 那样适配器在**稍后**枚举它时读到的是**另一个时刻**的文件，
    /// 于是「同一步的渲染逐字节相同」当场破，而破法是**静默的**：
    /// 重放同一步会得到不同的字节 ⇒ 每步破一次前缀。</para>
    /// </summary>
    [Fact]
    public async Task Send_TheBlockIsAValueAsOfStepTime_NotAView()
    {
        var (host, d) = NewHarness();
        SaveProvider(host);
        Store.Save("mine", "第一版");

        var r = await d.DispatchAsync(
            new CoreCommand.SessionCreate { Preset = "mine" }, "c1", false, CancellationToken.None);
        var sid = Assert.IsType<Ack.Accepted>(r.Ack).SessionId!;

        _adapter.Reply("好");
        await d.DispatchAsync(
            new CoreCommand.SessionSend(sid, [new Block.TextBlock("在吗")]), "s1", true, CancellationToken.None);

        // 步已经跑完，此刻改文件——记下来的那个请求必须还是「第一版」。
        Store.Save("mine", "第二版");

        var sent = Assert.IsType<Block.TextBlock>(Assert.Single(_adapter.Calls[^1].Slots.System));
        Assert.Equal("第一版", sent.Text);
    }
}
