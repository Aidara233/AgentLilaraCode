using Lilara.Core.Kernel;
using Lilara.Core.Session;
using Lilara.Core.Storage;
using Lilara.Core.Plugins;

namespace Lilara.Core.Tests;

/// <summary>
/// **三块提示词各自的产物**（`01` §3.1 头槽位两块 + §3.2 尾槽位一块）。
///
/// <para>⚠️ <b>本文件钉的是「哪一块从哪来」，`PresetTests` 钉的是「系统块那三条路」</b>
/// ——两处都在调 `SystemBlock.Render`，但问的问题不同：那边问「文件在不在 / 空不空」，
/// 这边问「**三块的独立性**」——一块在不在，不许影响另两块。</para>
///
/// <para>⚠️ 这一批之前，后两块**根本不存在**（尾槽位连类型都没有），所以整个文件是新的。</para>
/// </summary>
public sealed class SlotTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "lilara-slots-" + Guid.NewGuid().ToString("N")[..8]);

    public SlotTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 清理失败不该让测试红 */ }
    }

    private PresetStore System => new(_root, PresetStore.SystemDir);
    private PresetStore Premier => new(_root, PresetStore.PremierDir);

    private PromptSlots Render(
        string? systemId = null, string? premierId = null,
        IReadOnlyDictionary<string, string>? axes = null, SessionPlan? plan = null) =>
        SystemBlock.Render(
            System, Premier, systemId, premierId, axes ?? Axes.Defaults(), plan: plan);

    // ---- 计划树与它的那句话（2026-10-24）----
    //
    // ⚠️ 这一组钉两件此前**没有任何测试碰过**的事：
    //   ① 计划树真的渲染进尾块（`PlanTree.Render` 有单测也没有用——`SystemBlock` 不传它就是死代码）；
    //   ② 「当前步」那句话**只在主动性 = step 时出现**（用户裁定：停点是提示词行为，不是硬打断），
    //      而 `mode=plan` 时换成规划模式那句。这两句都是**提示词**，没有它们模型不会按计划走。

    private static SessionPlan PlanOf(params PlanNode[] nodes) =>
        new() { Nodes = nodes, UpdatedAt = DateTimeOffset.UnixEpoch };

    private static readonly SessionPlan SamplePlan = PlanOf(
        new PlanNode { Id = "a", Title = "读代码", Body = "先把它读一遍" },
        new PlanNode
        {
            Id = "b",
            Title = "改代码",
            Children = [new PlanNode { Id = "b1", Title = "改一个文件", Body = "改 ToolExecutor" }],
        });

    private static string TailOf(PromptSlots slots) =>
        Assert.IsType<Block.TextBlock>(Assert.Single(slots.Tail)).Text;

    [Fact]
    public void Tail_CarriesThePlanTree_WithTheCurrentLeafMarked()
    {
        var text = TailOf(Render(plan: SamplePlan));

        Assert.Contains("计划（剩余树", text, StringComparison.Ordinal);
        // ▶ = 当前叶子（最先遇到的那个），○ = 其余叶子，▸ = 分支。
        Assert.Contains("▶ 读代码 [a]", text, StringComparison.Ordinal);
        Assert.Contains("▸ 改代码 [b]", text, StringComparison.Ordinal);
        Assert.Contains("○ 改一个文件 [b1]", text, StringComparison.Ordinal);
        // 叶子正文必须出现——它才是「这一步到底做什么」的来源。
        Assert.Contains("正文：先把它读一遍", text, StringComparison.Ordinal);
        Assert.Contains("正文：改 ToolExecutor", text, StringComparison.Ordinal);

        // 没有计划时那一整段**不出现**（不是印一个空标题）。
        Assert.DoesNotContain("计划（剩余树", TailOf(Render()), StringComparison.Ordinal);
    }

    [Theory]
    // 主动性 = step ⇒ 「当前步」那句；它是停点的**唯一**来源（没有硬打断）。
    [InlineData("initiative", "step", "当前步：以计划里 ▶ 那条叶子为准")]
    // 规划模式 ⇒ 只规划、不动手（与权限轴无关：能做什么仍由权限判）。
    [InlineData("mode", "plan", "规划模式：现在只调整计划")]
    public void Tail_ExplainsThePlanOnlyWhereItChangesWhatToDo(string axis, string value, string expected)
    {
        var axes = new Dictionary<string, string>(Axes.Defaults(), StringComparer.Ordinal) { [axis] = value };
        Assert.Contains(expected, TailOf(Render(axes: axes, plan: SamplePlan)), StringComparison.Ordinal);
    }

    [Fact]
    public void Tail_DoesNotTellTheModelToFollowAPlan_WhenThereIsNoPlan()
    {
        // ⚠️ 两句话都**以「有计划」为前提**：计划为空时它们会让模型去找一个不存在的 ▶。
        foreach (var (axis, value) in new[] { ("initiative", "step"), ("mode", "plan") })
        {
            var axes = new Dictionary<string, string>(Axes.Defaults(), StringComparer.Ordinal) { [axis] = value };
            var text = TailOf(Render(axes: axes));
            Assert.DoesNotContain("当前步：以计划里", text, StringComparison.Ordinal);
            Assert.DoesNotContain("规划模式：现在只调整计划", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Tail_DoesNotMentionThePlanStep_WhenInitiativeIsNotStep()
    {
        // standard / goal 各有自己的语义（等用户 / 持续推进到完成）；
        // 把计划的叶子说成「当前步」会**改写那一档的含义**。
        foreach (var initiative in new[] { "standard", "goal" })
        {
            var axes = new Dictionary<string, string>(Axes.Defaults(), StringComparer.Ordinal)
            {
                [Axes.Initiative] = initiative,
            };
            Assert.DoesNotContain("当前步：以计划里", TailOf(Render(axes: axes, plan: SamplePlan)),
                StringComparison.Ordinal);
        }
    }

    // ---- 尾槽位（轴表）----

    [Theory]
    [InlineData("initiative", "step", "等待下一步指示")]
    [InlineData("initiative", "standard", "需要用户决策")]
    [InlineData("initiative", "goal", "完成前继续")]
    [InlineData("permission", "off", "所有工具调用均被拒绝")]
    [InlineData("permission", "readonly", "消耗资源、写入和高危类工具均被拒绝")]
    [InlineData("permission", "investigate", "写入和高危类工具被拒绝")]
    [InlineData("permission", "standard", "写入类工具需要审批，高危类工具被拒绝")]
    [InlineData("permission", "authorized", "高危类工具需要审批")]
    [InlineData("permission", "trusted", "权限闸门允许所有工具类别")]
    [InlineData("safety", "dev", "开发环境")]
    [InlineData("safety", "staging", "预发布环境")]
    [InlineData("safety", "prod", "生产环境")]
    [InlineData("verify", "none", "明确未执行验证")]
    [InlineData("verify", "self", "相关编译、测试或检查")]
    [InlineData("verify", "strict", "验证未通过时不得声称完成")]
    public void Tail_ExplainsOnlyTheSelectedLevel(string axis, string level, string meaning)
    {
        var slots = Render(axes: new Dictionary<string, string> { [axis] = level });
        var tail = Assert.IsType<Block.TextBlock>(Assert.Single(slots.Tail)).Text;
        var system = Assert.IsType<Block.TextBlock>(Assert.Single(slots.System)).Text;
        Assert.Contains(meaning, tail);
        foreach (var value in Axes.ValuesOf(axis)!)
        {
            var description = Axes.DescriptionOf(axis, value);
            Assert.False(string.IsNullOrWhiteSpace(description));
            if (value != level) Assert.DoesNotContain(description!, tail);
            Assert.DoesNotContain(description!, system);
        }
    }

    [Fact]
    public void LevelChangesOnlyChangeFloatingText_AndSystemTeachesItsOriginAndLifetime()
    {
        System.Save("default", "用户自定义系统原文");
        var before = Render(axes: new Dictionary<string, string> { [Axes.Verify] = "none" });
        var after = Render(axes: new Dictionary<string, string> { [Axes.Verify] = "strict" });
        var system = Assert.IsType<Block.TextBlock>(Assert.Single(after.System)).Text;
        Assert.Equal(before.System, after.System);
        Assert.NotEqual(before.Tail, after.Tail);
        Assert.StartsWith("用户自定义系统原文\n", system);
        Assert.Contains("浮动上下文", system);
        // ⚠️ 2026-10-23 改口：这几条钉的是**语义**，措辞随「不出现『你』」那条一起改过。
        // ① 来源（谁生成的）② 寿命（一步一份、下一步换掉）③ 权限不由此授予。
        Assert.Contains("Lilara 核心", system);
        Assert.Contains("同一步内保持一致", system);
        Assert.Contains("不自行授予权限", system);
        // ⚠️ 第二人称禁令也钉在系统块上：一块**环境读数**不该变成一封给读者的信。
        Assert.DoesNotContain("你此前", system);
        Assert.Contains("浮动上下文", CorePrompts.SystemFallback);
    }

    [Fact]
    public void PluginOnlyTailStillDeclaresItsFrameworkOrigin_AndEmptySystemStaysAbsent()
    {
        System.Save("default", "");
        var plugin = new PromptContributionSnapshot([], [], [new()
            { PluginId = "demo", Key = "field", Slot = PromptSlot.Floating, Text = "plugin-context" }]);
        var slots = SystemBlock.Render(System, Premier, null, null, new Dictionary<string, string>(), plugin);
        Assert.Empty(slots.System);
        var tail = Assert.IsType<Block.TextBlock>(Assert.Single(slots.Tail)).Text;
        // ⚠️ 2026-10-17：尾块改走末尾合成的 `floating_context` 调用，自述句随之改口。
        // 这条的意图不变——**只贡献插件浮动字段的尾块也得自己声明来源**——所以钉的是
        // 「谁生成、何时生成」这一层含义，不是旧措辞里的字面词。
        // ⚠️ 2026-10-23：自述从一段第二人称的话改成一行**字段**（`来源 lilara 核心 · 每步生成 · 不落盘`）。
        Assert.Contains("来源 lilara 核心", tail);
        Assert.Contains("每步生成", tail);
        Assert.Contains("plugin-context", tail);
        Assert.DoesNotContain("权限", tail);
    }

    [Fact]
    public void PluginSystemContributionAlsoReceivesExplanationWithAnEmptyPreset()
    {
        System.Save("default", "");
        var plugin = new PromptContributionSnapshot([new()
            { PluginId = "demo", Key = "system", Slot = PromptSlot.System, Stable = true, Text = "plugin-system" }], [], []);
        var slots = SystemBlock.Render(System, Premier, null, null, new Dictionary<string, string>(), plugin);
        var system = Assert.IsType<Block.TextBlock>(Assert.Single(slots.System)).Text;
        Assert.StartsWith("[demo/system]\nplugin-system\n", system);
        Assert.Contains("浮动上下文", system);
        Assert.DoesNotContain("fetch", system);
        Assert.Empty(slots.Tail);
    }

    /// <summary>
    /// ⚠️ **尾块第一次真的进上下文，而它同时钉三件事**：自我声明在 · 轴的**标签**在 ·
    /// 轴的**当前值**在。少任何一件，这个块都还在，但模型读不懂它是什么。
    /// </summary>
    [Fact]
    public void Tail_CarriesItsOwnDeclarationAndTheAxisValues()
    {
        var axes = new Dictionary<string, string>(Axes.Defaults(), StringComparer.Ordinal)
        {
            [Axes.Permission] = "readonly",
            [Axes.Safety] = "prod",
        };

        var text = Assert.IsType<Block.TextBlock>(Assert.Single(Render(axes: axes).Tail)).Text;

        Assert.Contains(CorePrompts.TailDeclaration, text);   // 自己声明自己是框架写的
        Assert.Contains("权限 readonly", text);               // 标签 + **当前**值（不是默认值）
        Assert.Contains("安全 prod", text);
        Assert.Contains(CorePrompts.TailOpen, text);
        Assert.Contains(CorePrompts.TailClose, text);

        // ⚠️ **探针：上面那句 `Contains(TailDeclaration, …)` 是 X == X，它自己证明不了任何事**
        // （实测：把 `TailDeclaration` 换成任意一句，那条断言**照样绿**）。
        // ⇒ 用户点名要的**三件事**得各自被一句话钉住，而它们不能靠那个常量自证。
        //
        // ⚠️ **2026-10-23 改口：钉的东西换了一半。** 原来是「不是用户消息」「不是你发起」
        // 「每一步都会出现 / 记起」——三句都在**替读者否认它自己的记录**。
        // 而现在钉的是同一批事实的**可核对形式**：
        //   ① 谁生成、何时生成 ⇒ 一行字段（「来源 lilara 核心 · 每步生成 · 不落盘」）
        //   ② 怎么认出来 ⇒ `floating-` 前缀 + `[floating_context]` 框架头
        //      （那一半在 `FloatingContextTests` 里钉，因为它属于合成对的形状）
        //   ③ **不许出现第二人称与否定句** —— 这条是本批的要害，所以它是一条 `DoesNotContain`。
        // 判据（`00` §7 判据 4）：结构上排除，而非规则禁止。要求读者相信一句关于它
        // 自己历史的话，是「规则禁止」；给形状，才是「结构排除」。
        Assert.Contains("来源 lilara 核心", text);
        Assert.Contains("每步生成", text);
        // ⚠️ 「不落盘」从尾块**挪进了系统块**（2026-10-23）：那是一条**寿命**约定，
        // 属于「一步都不变」的全局说明，而尾块每步全价支付——把维护者才关心的话
        // 从每步都发的块里挪走，是这条槽位「有界」那条硬约束的直接后果。
        Assert.DoesNotContain("不落盘", text);

        // ⚠️ 这三条不是措辞洁癖：句子里的「你」会把一块**环境读数**变成一封**给读者本人的信**，
        // 而「不是你」会让它去核一件它没有账的事（实测：这套文案在位期间模型连调二十次，
        // 每次都拿回同一份字节 —— 怀疑并没有因为读到「不是你」而停下）。
        Assert.DoesNotContain("你", CorePrompts.TailDeclaration);
        Assert.DoesNotContain("不是", CorePrompts.TailDeclaration);
        Assert.DoesNotContain("不是你发起", text);
    }

    /// <summary>
    /// ⚠️ **`Axes.InContext` 第一次有了消费者**——它此前只用来给头的下拉画灰。
    /// ⇒ 这条钉的是「进上下文的判定**就是**那个属性」，不是另写一份名单。
    ///
    /// <para>⚠️ 双向都钉：`effort` / `temperature` **不许**出现（纯请求参数，模型不必知道），
    /// 而其余四条**必须**出现。只钉前一半的话，把整个轴表删空照样绿。</para>
    /// </summary>
    [Fact]
    public void Tail_IncludesExactlyTheAxesThatAreInContext()
    {
        var text = Assert.IsType<Block.TextBlock>(Assert.Single(Render().Tail)).Text;

        foreach (var axis in Axes.Names)
        {
            var label = Axes.LabelOf(axis);
            if (Axes.InContext(axis)) Assert.Contains(label, text);
            else Assert.DoesNotContain(label, text);
        }

        // 探针：两条 §3.5 明确「完全不进浮动」的轴，点名写死——
        // 光靠上面那个循环的话，`InContext` 被改成恒 false 时这条会退化成空断言。
        Assert.DoesNotContain("思考强度", text);
        Assert.DoesNotContain("温度", text);
    }

    /// <summary>
    /// ⚠️ **尾块按 <see cref="Axes.RenderOrder"/> 迭代**，而**不是**按 <c>State.Axes</c>
    /// 那个字典的枚举序——后者未定义，**每次可能不同**，而它会让尾块的字节每步都变
    /// （=`00` §0 那条母原则被违反）。
    ///
    /// <para>⚠️ <b>这条今天<b>证明不了</b>「用了 ordinal 而不是文化序」——如实写在这里</b>：
    /// 六条轴名全是小写 ASCII，<c>OrderBy(n =&gt; n)</c>（ICU）与
    /// <c>OrderBy(n =&gt; n, StringComparer.Ordinal)</c> 在 <c>en-US</c> 与 <c>tr-TR</c> 下
    /// **给出同一个序** ⇒ 把那一行改成文化序，下面的断言**照样绿**（本批实测过）。
    /// ⇒ <b><see cref="Axes.RenderOrder"/> 上那个 <c>StringComparer.Ordinal</c> 是条纪律，不是被测性质</b>；
    /// 它防的是「将来某条轴名里出现 <c>i</c>/<c>I</c> 这类会翻序的字符」。
    /// ⚠️ 真正被测的是**序列本身**：下面那个 <c>Assert.Equal</c> 一旦有人重排轴线就会红。</para>
    /// </summary>
    [Fact]
    public void Tail_AxesFollowTheDeclaredRenderOrder()
    {
        // 序列被钉死——重排/增删轴会红（见上面那条注释：文化序那一半钉不住）。
        // ⚠️ `mode`（2026-10-24 计划批）在 ordinal 序里落在 `initiative` 之后——那是**排序结果**，
        // 不是被人排到那儿的；表序里它仍追加在末尾（见下面那条与 `AxisTests` 的同款注释）。
        Assert.Equal(
            ["effort", "initiative", "mode", "network", "permission", "safety", "temperature", "verify"],
            Axes.RenderOrder);

        // 表序**保持不变**：它是控制面 `/axes` 与人看的序（`Axes.Names` 的注释）。
        Assert.Equal(
            ["initiative", "permission", "safety", "verify", "network", "effort", "temperature", "mode"],
            Axes.Names);

        // ⚠️ 再看文本里的**先后**与 `RenderOrder` 一致（而不是与表序一致）。
        var text = Assert.IsType<Block.TextBlock>(Assert.Single(Render().Tail)).Text;
        var expected = Axes.RenderOrder
            .Where(Axes.InContext)
            .Where(a => text.Contains(Axes.LabelOf(a), StringComparison.Ordinal))
            .ToArray();

        var positions = expected
            .Select(a => text.IndexOf(Axes.LabelOf(a), StringComparison.Ordinal))
            .ToList();

        Assert.Equal(expected.Length, positions.Count);
        Assert.Equal(positions.OrderBy(p => p), positions);
    }

    /// <summary>
    /// ⚠️ **不许从 `Dictionary` 迭代**（`State.Axes` 就是字典，它的枚举序未定义）。
    /// 判据：轴值字典的插入顺序**反过来**时，产出的字节必须**逐字相同**。
    ///
    /// <para>这是「渲染是纯函数」那条铁律在**输入形状**上的一个具体实例：
    /// 同一个语义（同样的轴值）、不同的容器状态 ⇒ 必须同样的字节，否则每步破前缀。</para>
    /// </summary>
    [Fact]
    public void Tail_IsByteIdentical_NoMatterWhatOrderTheDictionaryEnumeratesIn()
    {
        var forward = new Dictionary<string, string>(StringComparer.Ordinal);
        var backward = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var axis in Axes.Names) forward[axis] = "dev";
        foreach (var axis in Enumerable.Reverse(Axes.Names)) backward[axis] = "dev";

        var a = Assert.IsType<Block.TextBlock>(Assert.Single(Render(axes: forward).Tail)).Text;
        var b = Assert.IsType<Block.TextBlock>(Assert.Single(Render(axes: backward).Tail)).Text;

        Assert.Equal(a, b);
    }

    /// <summary>
    /// ⚠️ **轴值缺一条 ⇒ 那一条根本不出现**，不是印成一个空值（同「缺席要显示成缺席」）。
    /// 而**全部缺 ⇒ 没有尾块**——不能留下一个只有自我声明、没有内容的空壳。
    /// </summary>
    [Fact]
    public void Tail_OnAPartialAxisTable_PrintsOnlyWhatItHas()
    {
        var partial = new Dictionary<string, string>(StringComparer.Ordinal) { [Axes.Safety] = "prod" };

        var text = Assert.IsType<Block.TextBlock>(Assert.Single(Render(axes: partial).Tail)).Text;
        Assert.Contains("安全 prod", text);
        Assert.DoesNotContain("权限", text);

        // ⚠️ 全空 ⇒ 连块一起没有——一个只有 `[lilara 上下文]…[/lilara 上下文]` 的块
        // 会让模型去猜中间被吃掉了什么，而那是一句我们编出来的假信息。
        Assert.Empty(Render(axes: new Dictionary<string, string>(StringComparer.Ordinal)).Tail);
    }

    // ---- 首要块 ----

    /// <summary>
    /// ⚠️ **没配过就没有块**——这与系统块**刻意不同**（那个回落内置模板）。
    ///
    /// <para>判据：核心**不替用户编人设**（`01` §3.1）。造一段不承担任何机制的约定，
    /// 是用软请求冒充机械兜底——那是 `00` §7 判据 5 明确不许的。</para>
    /// </summary>
    [Fact]
    public void Premier_IsAbsentWhenUnconfigured_NeverAFabricatedDefault()
    {
        Assert.Empty(Render().Premier);

        // ⚠️ 探针：与系统块**同时**断言，证明「一个块没了不会连累另一个」——
        // 若两块的判定被并成一个（本批最容易犯的错），下面这句会红。
        Assert.NotEmpty(Render().System);
    }

    /// <summary>配了就用它，逐字；而**没配过系统块**不影响它有内容（两块独立）。</summary>
    [Fact]
    public void Premier_IsUsedVerbatim_AndIsIndependentOfTheSystemBlock()
    {
        const string mine = "你是专门做代码审查的。\n\n先读再评。";
        Premier.Save("default", mine);

        var text = Assert.IsType<Block.TextBlock>(Assert.Single(Render().Premier)).Text;

        Assert.Contains(mine, text);                     // 正文逐字
        Assert.Contains(CorePrompts.PremierOpen, text);  // 而且带着自己的声明包边
        Assert.Contains(CorePrompts.PremierClose, text);
    }

    /// <summary>
    /// ⚠️ **首要块与系统块读的是两个目录**——同名 id 不会互相串。
    ///
    /// <para>这条挡的是「装配器把两个 store 接错」：那会让插件贡献（首要块）
    /// 落进位置 0（系统块），而 `01` §3.1 全部的可写面论证正是建立在
    /// 「插件**不可**注入系统块」上。</para>
    /// </summary>
    [Fact]
    public void TwoSlots_ReadTwoSeparateDirectories()
    {
        System.Save("default", "系统那份");
        Premier.Save("default", "首要那份");

        var slots = Render();

        Assert.Contains("系统那份", Assert.IsType<Block.TextBlock>(Assert.Single(slots.System)).Text);
        Assert.Contains("首要那份", Assert.IsType<Block.TextBlock>(Assert.Single(slots.Premier)).Text);
    }

    /// <summary>
    /// ⚠️ **空文件 = 没有块**，与「文件不在」**同向**——这与系统块那三条路**不同**
    /// （那边空文件是「纯聊天」，是一条**有意义**的声明）。
    ///
    /// <para>为什么不同：系统块的空是**用户明说了「不要系统提示词」**（纯 chat 预设），
    /// 而首要块的空只是「这个文件是空的」——没有第二种含义可表达，所以两者不必分开。</para>
    /// </summary>
    [Fact]
    public void Premier_EmptyFile_IsJustAbsent()
    {
        Premier.Save("default", "");

        Assert.Empty(Render().Premier);
    }

    /// <summary>会话点名了另一个 id ⇒ 用它（两个槽位各自解析各自的 id）。</summary>
    [Fact]
    public void Premier_IdIsResolvedIndependently()
    {
        Premier.Save("default", "默认那份");
        Premier.Save("review", "审查那份");

        var text = Assert.IsType<Block.TextBlock>(Assert.Single(Render(premierId: "review").Premier)).Text;
        Assert.Contains("审查那份", text);
    }

    // ---- 裸核心 ----

    /// <summary>
    /// ⚠️ **三块都空是合法形态**（`04` §1.3 推论 1：没有任何插件 = 纯 chat）。
    /// 空文件把系统块与首要块同时关掉之后，`PromptSlots` 里只剩尾块——而它照常在有。
    /// </summary>
    [Fact]
    public void AllSlotsEmpty_ExceptTheTail_IsALegalShape()
    {
        System.Save("default", "");     // 系统块：纯 chat
        // 首要块：文件不在

        var slots = Render();

        Assert.Empty(slots.System);
        Assert.Empty(slots.Premier);
        Assert.NotEmpty(slots.Tail);    // 尾块只跟轴走，与那两块无关
    }
}
