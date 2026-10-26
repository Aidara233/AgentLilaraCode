using Lilara.Core.Kernel;
using Lilara.Core.Session;

namespace Lilara.Core.Tests;

// 轴的唯一真相来源；钉的是「枚举与它声明的取值一致」。
public class AxisTests
{
    [Fact]
    public void Axes_AreExactlyTheOnesTheDocNames()
    {
        // 顺序决定快照里轴的排列，也就决定头的渲染顺序——顺序不稳等于每步重排。
        // ⚠️ "temperature" 是本表第一条**数值范围**轴（01 §3.5），排在最后 = 渲染在最后。
        // ⚠️ "mode"（2026-10-24 计划批）**追加在末尾**，没有插进行为轴那一组：
        // 表序同时是控制面 `/axes` 与人看的顺序，而 `01` §3.5 那张表还没为它腾位置
        // ⇒ 先按追加处理，等文档定序之后再一起搬（搬的时候这条与 `SlotTests` 那条要同时改）。
        Assert.Equal(
            ["initiative", "permission", "safety", "verify", "network", "effort", "temperature", "mode"],
            Axes.Names);
    }

    [Fact]
    public void NetworkAxis_IsInContext_AndDefaultsToOff()
    {
        Assert.Equal(["off", "search", "fetch", "all"], Axes.ValuesOf(Axes.Network));
        Assert.Equal("off", Axes.Defaults()[Axes.Network]);
        Assert.Equal("联网", Axes.LabelOf(Axes.Network));
        // ⚠️ 进上下文：模型要知道这个会话能联网到哪一步；否则它只会从工具被拒的结果里猜。
        Assert.True(Axes.InContext(Axes.Network));
    }

    [Fact]
    public void EveryAxis_HasAtLeastTwoValues()
    {
        foreach (var axis in Axes.Names)
            Assert.True(Axes.ValuesOf(axis)!.Count >= 2, $"轴 {axis} 至少要两个取值，否则它不是轴");
    }

    // ⚠️ **数值轴也必须有非空 Values**——这条钉的是「别把非枚举塞进 ValuesOf 的 null 里」。
    // 若数值轴返回 null，那么「未知轴」与「非枚举轴」就混成一个 null，
    // 而上面那条的 `ValuesOf(axis)!` 会当场 NRE；头那边 `for (const v of a.values)` 会
    // **静默炸掉整个轴面板**（TypeError 不冒泡到任何可见的地方）。
    [Fact]
    public void NumericAxis_StillHandsOutANonEmptyGrid()
    {
        var values = Axes.ValuesOf(Axes.Temperature);

        Assert.NotNull(values);
        Assert.NotEmpty(values!);
        Assert.Equal("0.0", values![0]);      // 网格两端按步长的位数补齐（步长 0.1 ⇒ 一位小数）
        Assert.Equal("2.0", values[^1]);
    }

    [Fact]
    public void Defaults_CoverEveryAxis_WithALegalValue()
    {
        var defaults = Axes.Defaults();
        Assert.Equal(Axes.Names.Count, defaults.Count);
        foreach (var (axis, value) in defaults)
            Assert.True(Axes.TryParse(axis, value, out _), $"默认值 {axis}={value} 不在合法取值里");
    }

    // 默认值是表里那个「最没有意外」的档，不是随手挑的。
    [Fact]
    public void Defaults_AreTheOnesTheDocImplies()
    {
        var d = Axes.Defaults();
        Assert.Equal("step", d["initiative"]);     // 步进：核心不替你决定该不该继续（§6.1）
        Assert.Equal("standard", d["permission"]); // 标准：写入类要问
        Assert.Equal("dev", d["safety"]);          // 开发：绝不能默认「生产」
        Assert.Equal("self", d["verify"]);         // 自检
        Assert.Equal("high", d["effort"]);         // 与建会话时的既有默认一致（CommandDispatcher 取 "high"）
    }

    [Fact]
    public void UnknownAxis_IsNotKnown() => Assert.False(Axes.IsKnown("mood"));

    [Fact]
    public void UnknownValue_IsRefused_AndTheAxisIsUnchanged()
    {
        // 这里的「未知值」是 production 而不是 prod——prod 是 safety 轴的合法取值。
        Assert.False(Axes.TryParse("safety", "production", out var canonical));
        Assert.Equal("", canonical);
    }

    // 取值是用户手打的：PROD 要认，且回吐表里的那个拼法。
    [Fact]
    public void ValueCase_IsInsensitive_AndNormalizesToTheTableSpelling()
    {
        Assert.True(Axes.TryParse("safety", "PROD", out var canonical));
        Assert.Equal("prod", canonical);
    }

    // 轴名走 StringComparer.Ordinal，敏感是有意的：Safety 不是 safety，应拒绝而不是替用户猜。
    [Fact]
    public void AxisName_IsCaseSensitive()
    {
        Assert.False(Axes.TryParse("Safety", "dev", out var canonical));
        Assert.Equal("", canonical);
    }

    // 表是进程级静态的，交出去的取值必须只读，否则改一处等于改了所有人的表（且没有任何报错）。
    [Fact]
    public void ValuesOf_HandsOutSomethingTheCallerCannotMutate()
    {
        var list = (IList<string>)Axes.ValuesOf("safety")!;
        Assert.Throws<NotSupportedException>(() => list[0] = "production");
        Assert.Equal("dev", Axes.ValuesOf("safety")![0]);   // 静态表毫发无损
    }

    // 合法值清单必须从枚举拼出来，手抄的会在加档时静默过期，而错误信息是用户唯一的线索。
    [Fact]
    public void LegalValuesText_ListsEveryValue_FromTheTableNotAHandCopy()
    {
        var text = Axes.LegalValuesText("safety");
        foreach (var v in Axes.ValuesOf("safety")!) Assert.Contains(v, text);
        Assert.Equal(string.Join("|", Axes.ValuesOf("safety")!), text);
    }

    // 老会话（磁盘上没有 axes）载入后必须拿到整套默认值，而不是空字典（空字典会让头渲染五个空白下拉）；
    // 断言钉的是 handle.State.Axes，轴只有这一份存储。
    // spec 里的 effort 刻意写 "low"：写 "high" 时这条对「该不该按 spec 播种」是瞎的。
    [Fact]
    public void Load_OfALegacyStateFile_FillsInTheDefaults()
    {
        var root = Path.Combine(Path.GetTempPath(), "lilara-axis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new Lilara.Core.Storage.SessionStore(root);
            var pk = Lilara.Core.Kernel.ProjectKey.FromPath(@"C:\work");
            store.WriteState(pk, new Lilara.Core.Kernel.SessionState
            {
                SessionId = "s1", Status = "Idle", Turn = 0, Pending = [],   // 刻意不设 Axes
            });
            store.WriteSpec(pk, new Lilara.Core.Kernel.SessionSpec
            {
                SessionId = "s1", ProjectKey = pk, WorkingDirectory = @"C:\work",
                CreatedAt = DateTimeOffset.UnixEpoch,
                Model = new Lilara.Core.Kernel.ModelSelection
                {
                    ProviderId = "p", ModelId = "m", Protocol = "claude-messages",
                    Effort = "low", MaxTokens = 16000,
                },
            });

            var handle = SessionHandle.Load(store, pk, "s1");

            // 表默认值的五条，除了 effort——effort 来自 spec 的播种。
            var expected = new Dictionary<string, string>(Axes.Defaults(), StringComparer.Ordinal)
            {
                [Axes.Effort] = "low",
            };
            Assert.Equal(expected, handle.State.Axes);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    // 「有键但不全」的 axes 也必须逐键补齐（旧判据「整份非空」漏了它，而 Stepper 用索引器读 ⇒ KeyNotFoundException）。
    // 覆盖顺序：磁盘上存的那份排最后，故 effort="max" 必须盖过 spec 里的 "low"——
    // 反过来的话，setAxis 改的轴会在下一次重启时被 spec 悄悄改回去。
    [Fact]
    public void Load_OfAPartialAxesFile_TopsUpTheMissingKeys_AndLetsTheStoredOnesWin()
    {
        var root = Path.Combine(Path.GetTempPath(), "lilara-axis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new Lilara.Core.Storage.SessionStore(root);
            var pk = Lilara.Core.Kernel.ProjectKey.FromPath(@"C:\work");
            store.WriteState(pk, new Lilara.Core.Kernel.SessionState
            {
                SessionId = "s1", Status = "Idle", Turn = 0, Pending = [],
                // 5 条轴里只有 2 条：既不全、又合法
                Axes = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [Axes.Initiative] = "goal",
                    [Axes.Effort] = "max",
                },
            });
            store.WriteSpec(pk, new Lilara.Core.Kernel.SessionSpec
            {
                SessionId = "s1", ProjectKey = pk, WorkingDirectory = @"C:\work",
                CreatedAt = DateTimeOffset.UnixEpoch,
                Model = new Lilara.Core.Kernel.ModelSelection
                {
                    ProviderId = "p", ModelId = "m", Protocol = "claude-messages",
                    Effort = "low", MaxTokens = 16000,   // 与磁盘上那份故意不同
                },
            });

            var handle = SessionHandle.Load(store, pk, "s1");
            var axes = handle.State.Axes!;

            Assert.Equal("goal", axes[Axes.Initiative]);       // 存的留着
            Assert.Equal("max", axes[Axes.Effort]);            // 磁盘的盖过 spec 的 "low"
            Assert.Equal("standard", axes[Axes.Permission]);   // 缺的按默认值补齐
            Assert.Equal("dev", axes[Axes.Safety]);
            Assert.Equal("self", axes[Axes.Verify]);
            Assert.Equal(Axes.Names.Count, axes.Count);        // 逐键齐全，一条不缺
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    // null 轴名 = 未知轴，吃轴名的每个入口都不许抛（Dictionary 的 key 不许为 null）。
    // 命令路径在 IsKnown 那关就返回，走不到其余入口，故那几条的判空契约由本文件守。
    [Fact]
    public void NullAxis_IsTreatedAsUnknown_ByEveryAxisNameEntryPoint()
    {
        Assert.False(Axes.IsKnown(null));
        Assert.Null(Axes.ValuesOf(null));
        Assert.Equal("", Axes.LegalValuesText(null));
        Assert.False(Axes.TryParse(null, "dev", out var canonical));
        Assert.Equal("", canonical);            // 与「未知轴」一致，不回吐半截
        Assert.Equal("", Axes.LabelOf(null));   // null 轴名没有标签可回吐
        Assert.False(Axes.InContext(null));     // 叫不出名字的轴，一律当作「不进上下文」
    }

    // 每条轴都要有自己的中文标签、不是回退成档名——少一个真标签就是头的某一格空白。
    // 断言 label != name 是关键：LabelOf 对未知轴原样返回，故「non-empty」单独还不够。
    [Fact]
    public void EveryAxis_HasItsOwnChineseLabel_NotTheFallbackName()
    {
        foreach (var n in Axes.Names)
        {
            var label = Axes.LabelOf(n);
            Assert.False(string.IsNullOrWhiteSpace(label), $"轴 '{n}' 没有标签");
            Assert.NotEqual(n, label);
        }
    }

    // 「只有 effort 不进上下文」需要守卫：只断言 effort 侧是 false 时，
    // 把 InContext 写成恒 false 全部测试仍绿，而头上五个下拉全被画灰。
    // ⚠️ temperature 是**第二条** false 的轴（同样是纯请求参数，与 effort 同侧）。
    [Theory]
    [InlineData(Axes.Initiative, true)]
    [InlineData(Axes.Permission, true)]
    [InlineData(Axes.Safety, true)]
    [InlineData(Axes.Verify, true)]
    [InlineData(Axes.Effort, false)]
    [InlineData(Axes.Temperature, false)]
    public void InContext_IsTrueForEveryAxisExceptEffort(string axis, bool expected) =>
        Assert.Equal(expected, Axes.InContext(axis));

    // ---- 数值范围轴 ----

    // ⚠️ 网格与解析器必须**互相覆盖**：每个网格值都要能被 TryParse 接受、且规范化成它自己。
    // 这是浮点最容易悄悄错的地方（`7 * 0.1` 是 `0.7000000000000001`），
    // 而错了的表现是「头下拉里的值和用户手打同一个数，核心却说不合法」。
    [Fact]
    public void EveryGridValue_RoundTripsThroughTryParse()
    {
        foreach (var v in Axes.ValuesOf(Axes.Temperature)!)
        {
            Assert.True(Axes.TryParse(Axes.Temperature, v, out var canonical), $"网格值 {v} 被自己拒了");
            Assert.Equal(v, canonical);
        }
    }

    [Theory]
    [InlineData("0", "0.0")]
    [InlineData("0.7", "0.7")]
    [InlineData("1", "1.0")]      // 手打整数也要规范化成网格那个拼法
    [InlineData("2", "2.0")]
    [InlineData(" 1.5 ", "1.5")]  // 前后空白：double.TryParse 默认允许，规范化后落回网格拼法
    public void Temperature_ParsesAndNormalizes(string input, string expected)
    {
        Assert.True(Axes.TryParse(Axes.Temperature, input, out var canonical));
        Assert.Equal(expected, canonical);
    }

    // 越界必须拒（实测本机网关的范围就是 [0,2]，见 Axes.Temperature 的注释）。
    [Theory]
    [InlineData("2.1")]
    [InlineData("-0.1")]
    [InlineData("100")]
    public void Temperature_OutOfRange_IsRefused(string input) =>
        Assert.False(Axes.TryParse(Axes.Temperature, input, out _));

    // ⚠️ **落在网格之间要拒，不能悄悄吸附**：0.75 存成 0.8 是另一种行为，而用户不会知道。
    [Fact]
    public void Temperature_OffTheGrid_IsRefused_NotSilentlySnapped()
    {
        Assert.False(Axes.TryParse(Axes.Temperature, "0.75", out var canonical));
        Assert.Equal("", canonical);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("0,7")]     // 逗号小数：InvariantCulture 下不合法（区域设置在这里没有话语权）
    [InlineData("1e1")]     // 科学计数法在范围外
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void Temperature_NonNumeric_IsRefused(string input) =>
        Assert.False(Axes.TryParse(Axes.Temperature, input, out _));

    // 数值轴的合法值文案**不是**把网格拼出来（21 个数印进错误信息没法读）——给范围与步长。
    [Fact]
    public void LegalValuesText_ForANumericAxis_ShowsTheRange_NotTheWholeGrid()
    {
        var text = Axes.LegalValuesText(Axes.Temperature);

        Assert.Contains("0", text);
        Assert.Contains("2", text);
        Assert.Contains("0.1", text);            // 步长是用户真正需要的信息
        Assert.DoesNotContain("|", text);        // 不是枚举那种竖线清单
        Assert.True(text.Length < 40, $"数值轴的文案不该是一长串：{text}");
    }

    // RangeOf 的 null **不表示未知轴**——这个区别要钉住，否则有人会把
    // `RangeOf(x) is not null` 当成「轴存在」的判据，于是五条枚举轴全被判成不存在。
    [Fact]
    public void RangeOf_IsNullForEnumeratedAxes_ButTheAxisStillExists()
    {
        Assert.Null(Axes.RangeOf(Axes.Safety));
        Assert.True(Axes.IsKnown(Axes.Safety));       // ← 存在性要用 IsKnown 判
        Assert.False(Axes.IsNumeric(Axes.Safety));

        Assert.NotNull(Axes.RangeOf(Axes.Temperature));
        Assert.True(Axes.IsNumeric(Axes.Temperature));

        Assert.Null(Axes.RangeOf(null));              // 未知轴
        Assert.Null(Axes.RangeOf("mood"));
    }

    // NumberOrNull 是「轴 → 请求」那条路上唯一的转换点。
    [Fact]
    public void NumberOrNull_ConvertsCanonicalStrings_AndRejectsTheRest()
    {
        Assert.Equal(0.7, Axes.NumberOrNull(Axes.Temperature, "0.7"));
        Assert.Equal(2.0, Axes.NumberOrNull(Axes.Temperature, "2.0"));

        Assert.Null(Axes.NumberOrNull(Axes.Temperature, "3"));      // 越界
        Assert.Null(Axes.NumberOrNull(Axes.Temperature, "0,7"));    // 区域设置不许插话
        Assert.Null(Axes.NumberOrNull(Axes.Safety, "dev"));         // 枚举轴没有数值
        Assert.Null(Axes.NumberOrNull("mood", "1"));                // 未知轴
        Assert.Null(Axes.NumberOrNull(Axes.Temperature, null));
    }

    // 默认值必须是 1.0（两家 API 各自的默认采样温度）——**不是**看着更讨喜的 0.7，
    // 那会把每一个既有会话的采样行为悄悄改掉（用户从没要求过）。
    [Fact]
    public void Temperature_Default_IsTheNeutralOne()
    {
        Assert.Equal("1.0", Axes.Defaults()[Axes.Temperature]);
        Assert.Equal(1.0, Axes.NumberOrNull(Axes.Temperature, Axes.Defaults()[Axes.Temperature]));
    }

    // permission 的档位顺序是契约：头的跨轴告警按 values.indexOf 判档位，
    // 重排或插入第 6 档会让头给出错误档位而测试不红。
    // AlertFrom / AlertSafety 必须落在各自轴的取值里，否则告警静默失灵。
    [Fact]
    public void PermissionValues_KeepTheGateOrder_AndTheAlertEndsAreReal()
    {
        Assert.Equal(
            ["off", "readonly", "investigate", "standard", "authorized", "trusted"],
            Axes.ValuesOf(Axes.Permission));
        Assert.Contains(Axes.AlertFrom, Axes.ValuesOf(Axes.Permission)!);
        Assert.Contains(Axes.AlertSafety, Axes.ValuesOf(Axes.Safety)!);
    }
}
