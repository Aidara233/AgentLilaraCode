using Lilara.Core.Kernel;
using Lilara.Core.Session;
using Lilara.Core.Storage;

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
        IReadOnlyDictionary<string, string>? axes = null) =>
        SystemBlock.Render(
            System, Premier, systemId, premierId, axes ?? Axes.Defaults());

    // ---- 尾槽位（轴表）----

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
        // （本批实测：把 `TailDeclaration` 换成任意一句，那条断言**照样绿**）。
        // ⇒ 用户点名要的**两件事**得各自被一句话钉住，而它们不能靠那个常量自证：
        //   ① 这不是用户说的话  ② 这不是工具返回的内容
        // ⚠️ 断言的是**语义的两个词**，不是那句原文——原文可以改，这两件事不许丢。
        Assert.Contains("不是你说的话", text);
        Assert.Contains("工具返回", text);
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
        Assert.Equal(
            ["effort", "initiative", "permission", "safety", "temperature", "verify"],
            Axes.RenderOrder);

        // 表序**保持不变**：它是控制面 `/axes` 与人看的序（`Axes.Names` 的注释）。
        Assert.Equal(
            ["initiative", "permission", "safety", "verify", "effort", "temperature"],
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
