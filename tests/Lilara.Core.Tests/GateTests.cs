using Lilara.Core.Kernel;
using Lilara.Core.Session.Tools;

namespace Lilara.Core.Tests;

/// <summary>
/// **权限闸门**（`01` §3.5 那张 `档位 × 类别` 表 · §9.2 的分工）。
///
/// <para>⚠️ <b>这一组是本批唯一能真的测到那张表的东西</b>：今天三个内建
/// （`ls` / `read` / `fetch`）**全是只读**，而只读在所有档位（≥1）都通
/// ⇒ 闸门对它们**永远是 no-op**，唯一的可观测效果只有 `off` 档。
/// ⇒ 要覆盖那张 6×4 的表，必须有一个**写类的测试替身**。</para>
///
/// <para>⚠️ <b>替身放测试工程，不进产品代码</b>：产品里**没有**写盘的手
/// （`04` §5.1：「只读是结构性的，不是纪律性的」），所以产品代码里
/// 不该出现一个 `Writing` 工具。而测闸门**必须**有一个。</para>
///
/// <para>⚠️ <b>判据</b>：这里断言的是「**闸门怎么判**」，不是「替身干了什么」
/// ⇒ 替身是探针、闸门是被测物，两者没混（区别于本仓那条「探针 ≠ 被测物」）。</para>
/// </summary>
public class GateTests
{
    /// <summary>一个**不干活**的工具，只声明自己的类别——闸门测的就是那个声明。</summary>
    private sealed class Stub(PermissionClass cls) : ITool
    {
        public PermissionClass PermissionClass => cls;
        public string Name => "stub";
        public string Description => "测试替身";
        public string ParametersSchemaJson => """{"type":"object","properties":{}}""";
        public Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct) =>
            Task.FromResult(new ToolOutcome { Content = [new Block.TextBlock("跑了")] });
    }

    /// <summary>
    /// ⚠️ **照抄 `01` §3.5 那张表**，逐格钉住。表是**唯一来源**，`Gate` 里不许自己推。
    ///
    /// <para>⚠️ <b>那两格「问」在 2026-10-08（询问批）落地了</b>——在那之前它们被
    /// **刻意降级成 `Deny`**（「问」要转 `WaitingForInput`，而那个状态当时没有生产者）。
    /// ⇒ 本表现在钉的是**真值**（`Ask`），而降级那段历史写在 `Gate` 的 `Asks` 字段注释里。
    /// ⚠️ 那条降级的代价当时就写明了：默认档是 `standard`，降级之后**默认档写不了任何东西**。</para>
    /// </summary>
    [Theory]
    // 0 禁用 ⇒ 四类全拒
    [InlineData("off", PermissionClass.ReadOnly, Verdict.Deny)]
    [InlineData("off", PermissionClass.Consuming, Verdict.Deny)]
    [InlineData("off", PermissionClass.Writing, Verdict.Deny)]
    [InlineData("off", PermissionClass.Destructive, Verdict.Deny)]
    // 1 只读 ⇒ 只有 ReadOnly 通
    [InlineData("readonly", PermissionClass.ReadOnly, Verdict.Allow)]
    [InlineData("readonly", PermissionClass.Consuming, Verdict.Deny)]
    [InlineData("readonly", PermissionClass.Writing, Verdict.Deny)]
    [InlineData("readonly", PermissionClass.Destructive, Verdict.Deny)]
    // 2 调查 ⇒ ReadOnly + Consuming 通
    [InlineData("investigate", PermissionClass.ReadOnly, Verdict.Allow)]
    [InlineData("investigate", PermissionClass.Consuming, Verdict.Allow)]
    [InlineData("investigate", PermissionClass.Writing, Verdict.Deny)]
    [InlineData("investigate", PermissionClass.Destructive, Verdict.Deny)]
    // 3 标准 ⇒ 前两通，写入是**询问线**（表里的「问」）
    [InlineData("standard", PermissionClass.ReadOnly, Verdict.Allow)]
    [InlineData("standard", PermissionClass.Consuming, Verdict.Allow)]
    [InlineData("standard", PermissionClass.Writing, Verdict.Ask)]
    [InlineData("standard", PermissionClass.Destructive, Verdict.Deny)]
    // 4 授权 ⇒ 前三通，高危是**询问线**（表里的「问」）
    [InlineData("authorized", PermissionClass.ReadOnly, Verdict.Allow)]
    [InlineData("authorized", PermissionClass.Consuming, Verdict.Allow)]
    [InlineData("authorized", PermissionClass.Writing, Verdict.Allow)]
    [InlineData("authorized", PermissionClass.Destructive, Verdict.Ask)]
    // 5 信任 ⇒ 四类全通
    [InlineData("trusted", PermissionClass.ReadOnly, Verdict.Allow)]
    [InlineData("trusted", PermissionClass.Consuming, Verdict.Allow)]
    [InlineData("trusted", PermissionClass.Writing, Verdict.Allow)]
    [InlineData("trusted", PermissionClass.Destructive, Verdict.Allow)]
    public void Decide_MatchesTheTable(string axis, PermissionClass cls, Verdict expected)
    {
        Assert.Equal(expected, Gate.Decide(cls, axis));
    }

    /// <summary>
    /// ⚠️ **阶梯必须是单调的**：每升一档，**只移动一个边界**（`01` §3.5）。
    /// 逐格钉表能发现"某一格写错了"，但发现不了"整张表被排乱了"——
    /// 而单调性正是那张表的结构本身。
    /// </summary>
    [Fact]
    public void TheLadder_IsMonotonic()
    {
        // 表里从"拒"到"通"的先后（按 §3.5 的行序）。
        string[] ladder = ["off", "readonly", "investigate", "standard", "authorized", "trusted"];
        PermissionClass[] classes =
            [PermissionClass.ReadOnly, PermissionClass.Consuming, PermissionClass.Writing, PermissionClass.Destructive];

        foreach (var cls in classes)
        {
            var verdicts = ladder.Select(a => Gate.Decide(cls, a)).ToList();

            // 一旦放行，**不再回头**（升档不会把已经允许的东西又拦住）。
            var firstAllow = verdicts.IndexOf(Verdict.Allow);
            if (firstAllow < 0) continue;
            Assert.DoesNotContain(Verdict.Deny, verdicts.Skip(firstAllow));

            // ⚠️ **而「问」也只在放行之前**：`01` §3.5 的结构是「低档是**拒绝线**（硬）、
            // 高档是**询问线**（软）」⇒ 序必须是 拒 → 问 → 通，**不许**出现「通 → 问」
            // （那等于升了档反而多一次打扰）。⚠️ 这条是 2026-10-08 询问批补的：
            // 在那之前 `Decide` 根本不返回 `Ask`，所以这条形状**测不出来**。
            Assert.DoesNotContain(Verdict.Ask, verdicts.Skip(firstAllow));
        }
    }

    /// <summary>
    /// ⚠️ **未知档位一律当作最严的那一档处理**——与 `Axes.InContext` 那条
    /// 「不知道的一律当作不进上下文」同一条判据：**宁可漏放，不可错放**。
    ///
    /// <para>⚠️ 今天轴的值由 `Axes.TryParse` 把关，未知值进不来；但
    /// <b>盘上的 `state.json` 是用户可手改的</b>（`TryReadSpec` 那条已知边界），
    /// 而闸门是**安全**那一侧 —— 不能靠"上游一定拦住了"。</para>
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("不存在的档")]
    [InlineData("TRUSTED")]     // ⚠️ 大小写不敏感？**不**：轴名的值是规范串，未知就是未知
    public void Decide_WithAnUnknownLevel_DeniesEverything(string? axis)
    {
        foreach (var cls in Enum.GetValues<PermissionClass>())
        {
            Assert.Equal(Verdict.Deny, Gate.Decide(cls, axis));
        }
    }

    /// <summary>
    /// ⚠️ **拒绝理由必须说清是"哪一类被拦"+"当前档位是什么"**
    /// ——模型据此才知道该请求提权，而不是反复重试同一个调用。
    ///
    /// <para>⚠️ <b>档位印的是<b>规范值</b>（<c>readonly</c>），不是中文标签</b>：
    /// `Axes` 那张表只有**轴**的中文名，**没有逐值标签** ⇒ 硬翻就要造第二份真相
    /// （而那正是本仓反复栽的"两处判据各写一份"）。
    /// ⚠️ 而且规范值对模型**更有用**：它据此能**精确**告诉用户该把权限设成什么。</para>
    /// </summary>
    [Fact]
    public void DenyReason_NamesTheClassAndTheCurrentLevel()
    {
        var reason = Gate.DenyReason(PermissionClass.Writing, "readonly");

        Assert.Contains("写入", reason);       // 哪一类
        Assert.Contains("readonly", reason);   // 当前档位（规范值）
        Assert.Contains("permission", reason); // 该动哪条轴
    }

    /// <summary>
    /// ⚠️ **未知档位也要能说出一句人话**（不是 `null` 或空串）——
    /// 那条理由会**落进记录**，而记录是只追加的：一句空话会永久留在那儿。
    /// </summary>
    [Fact]
    public void DenyReason_WithAnUnknownLevel_StillReadsAsASentence()
    {
        var reason = Gate.DenyReason(PermissionClass.Writing, null);

        Assert.Contains("写入", reason);
        Assert.DoesNotContain("null", reason);
    }
}
