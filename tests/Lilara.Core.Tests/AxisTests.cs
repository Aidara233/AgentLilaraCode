using Lilara.Core.Session;

namespace Lilara.Core.Tests;

/// <summary>
/// 五轴（`01` §3.5）的**唯一**真相来源。
/// ⚠️ 这些测试钉的是「枚举与它声明的取值一致」——两处各写一份是这类表最容易漂的地方。
/// </summary>
public class AxisTests
{
    [Fact]
    public void FiveAxes_AreExactlyTheOnesTheDocNames()
    {
        // ⚠️ 顺序**就是** `01` §3.5 表格的顺序：主动性 · 权限 · 安全 · 验证强度 · 思考强度。
        // 它决定快照里轴的排列，也就决定头的渲染顺序——顺序不稳等于每步重排。
        Assert.Equal(["initiative", "permission", "safety", "verify", "effort"], Axes.Names);
    }

    [Fact]
    public void EveryAxis_HasAtLeastTwoValues()
    {
        foreach (var axis in Axes.Names)
            Assert.True(Axes.ValuesOf(axis)!.Count >= 2, $"轴 {axis} 至少要两个取值，否则它不是轴");
    }

    [Fact]
    public void Defaults_CoverEveryAxis_WithALegalValue()
    {
        var defaults = Axes.Defaults();
        Assert.Equal(Axes.Names.Count, defaults.Count);
        foreach (var (axis, value) in defaults)
            Assert.True(Axes.TryParse(axis, value, out _), $"默认值 {axis}={value} 不在合法取值里");
    }

    /// <summary>⚠️ 默认值必须是 `01` §3.5 表里那个「最没有意外」的档，不是随手挑的。</summary>
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
        // ⚠️ 这里的「未知值」是 `production`，**不是** `prod`——
        // `prod` 是 safety 轴的**合法**取值（Step 3 的表），拿它当未知值这条测试必红。
        Assert.False(Axes.TryParse("safety", "production", out var canonical));
        Assert.Equal("", canonical);
    }

    /// <summary>取值是用户手打的，`PROD` 要认，并且回吐的是**表里的那个拼法**。</summary>
    [Fact]
    public void ValueCase_IsInsensitive_AndNormalizesToTheTableSpelling()
    {
        Assert.True(Axes.TryParse("safety", "PROD", out var canonical));
        Assert.Equal("prod", canonical);
    }

    /// <summary>
    /// ⚠️ 轴名走 `StringComparer.Ordinal`，**敏感是有意的**：它是协议标识符，
    /// `Safety` 不是 `safety`，应当拒绝而不是替用户猜。别"顺手统一"成忽略大小写。
    /// </summary>
    [Fact]
    public void AxisName_IsCaseSensitive()
    {
        Assert.False(Axes.TryParse("Safety", "dev", out var canonical));
        Assert.Equal("", canonical);
    }

    /// <summary>
    /// ⚠️ 表是**进程级静态**的，交出去的取值一旦可写，改一处就等于改了所有人的表
    /// （`TryParse` / `LegalValuesText` / **任何从本表读的调用方**会一起变，且**没有任何报错**）。
    /// 这条钉的是「交出来的是只读视图」——不是「当前值恰好是 dev|staging|prod」。
    /// </summary>
    [Fact]
    public void ValuesOf_HandsOutSomethingTheCallerCannotMutate()
    {
        var list = (IList<string>)Axes.ValuesOf("safety")!;
        Assert.Throws<NotSupportedException>(() => list[0] = "production");
        Assert.Equal("dev", Axes.ValuesOf("safety")![0]);   // 静态表毫发无损
    }

    /// <summary>
    /// ⚠️ 合法值清单必须是**从枚举拼出来的**，不是手抄的字符串。
    /// 手抄的清单在加档时会静默过期，而错误信息恰恰是用户唯一的线索。
    /// </summary>
    [Fact]
    public void LegalValuesText_ListsEveryValue_FromTheTableNotAHandCopy()
    {
        var text = Axes.LegalValuesText("safety");
        foreach (var v in Axes.ValuesOf("safety")!) Assert.Contains(v, text);
        Assert.Equal(string.Join("|", Axes.ValuesOf("safety")!), text);
    }

    /// <summary>
    /// ⚠️ **老会话（磁盘上没有 `axes`）载入后必须拿到一整套默认值**，而不是空字典。
    /// 空字典会让头渲染五个空白下拉，用户分不清「没设过」与「头读不到」。
    ///
    /// <para>⚠️ 断言钉的是 `handle.State.Axes`（**不是**别的什么地方挂着的轴）：
    /// 轴只有这一份存储，`Load` 必须把它补进 `State` 本身，
    /// 否则「读轴」和「读其它状态」会走两条路，而它们迟早分叉。</para>
    ///
    /// <para>⚠️ **spec 里的 effort 刻意写 `"low"`，不是表默认值 `"high"`。**
    /// 写 `"high"` 时这条测试对「`Load` 该不该按 spec 播种」是**瞎的**——
    /// 播种与不播种都会得到 `"high"`，两种实现无法区分。`"low"` 才有区分力：
    /// 少了播种，这里会读到 `"high"`。</para>
    /// </summary>
    [Fact]
    public void Load_OfALegacyStateFile_FillsInTheDefaults()
    {
        var root = Path.Combine(Path.GetTempPath(), "lilara-axis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new Lilara.Core.Persistence.SessionStore(root);
            var pk = Lilara.Core.Projects.ProjectKey.FromPath(@"C:\work");
            store.WriteState(pk, new Lilara.Core.Persistence.SessionState
            {
                SessionId = "s1", Status = "Idle", Turn = 0, Pending = [],   // ⚠️ 刻意不设 Axes
            });
            store.WriteSpec(pk, new Lilara.Core.Persistence.SessionSpec
            {
                SessionId = "s1", ProjectKey = pk, WorkingDirectory = @"C:\work",
                CreatedAt = DateTimeOffset.UnixEpoch,
                Model = new Lilara.Core.Persistence.ModelSelection
                {
                    ProviderId = "p", ModelId = "m", Protocol = "claude-messages",
                    Effort = "low", MaxTokens = 16000,
                },
            });

            var handle = SessionHandle.Load(store, pk, "s1");

            // ⚠️ 表默认值的五条，**除了 effort**——effort 来自 spec 的播种。
            var expected = new Dictionary<string, string>(Axes.Defaults(), StringComparer.Ordinal)
            {
                [Axes.Effort] = "low",
            };
            Assert.Equal(expected, handle.State.Axes);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// ⚠️ **「有键但不全」的 `axes` 也必须补齐**——判据是**逐键**齐全，不是「整份非空」。
    /// 旧判据（整份 `Count > 0` 就不补）对一份「有键但不全」的字典**不补齐**，
    /// 而 `Stepper` 读 `Axes.Effort` 用的是**索引器** ⇒ `KeyNotFoundException`。
    ///
    /// <para>⚠️ 这不是假想：**加第 6 条轴的那一天，所有 5 键的老会话都属于这一类**
    /// ——正是 `Load` 注释声称「加轴会自动生效」的那件事。</para>
    ///
    /// <para>⚠️ 同时钉住**覆盖顺序**：磁盘上存的那份排最后，所以
    /// `effort = "max"` 必须**盖过** spec 里的 `"low"`。这条顺序是 T3 的命根子——
    /// 反过来的话，用户 `setAxis` 改的轴会在**下一次重启时被 spec 悄悄改回去**。</para>
    /// </summary>
    [Fact]
    public void Load_OfAPartialAxesFile_TopsUpTheMissingKeys_AndLetsTheStoredOnesWin()
    {
        var root = Path.Combine(Path.GetTempPath(), "lilara-axis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new Lilara.Core.Persistence.SessionStore(root);
            var pk = Lilara.Core.Projects.ProjectKey.FromPath(@"C:\work");
            store.WriteState(pk, new Lilara.Core.Persistence.SessionState
            {
                SessionId = "s1", Status = "Idle", Turn = 0, Pending = [],
                // ⚠️ 5 条轴里只有 2 条：既不全、又合法
                Axes = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [Axes.Initiative] = "goal",
                    [Axes.Effort] = "max",
                },
            });
            store.WriteSpec(pk, new Lilara.Core.Persistence.SessionSpec
            {
                SessionId = "s1", ProjectKey = pk, WorkingDirectory = @"C:\work",
                CreatedAt = DateTimeOffset.UnixEpoch,
                Model = new Lilara.Core.Persistence.ModelSelection
                {
                    ProviderId = "p", ModelId = "m", Protocol = "claude-messages",
                    Effort = "low", MaxTokens = 16000,   // ⚠️ 与磁盘上那份**故意不同**
                },
            });

            var handle = SessionHandle.Load(store, pk, "s1");
            var axes = handle.State.Axes!;

            Assert.Equal("goal", axes[Axes.Initiative]);       // 存的留着
            Assert.Equal("max", axes[Axes.Effort]);            // ⚠️ 磁盘的盖过 spec 的 "low"
            Assert.Equal("standard", axes[Axes.Permission]);   // 缺的按默认值补齐
            Assert.Equal("dev", axes[Axes.Safety]);
            Assert.Equal("self", axes[Axes.Verify]);
            Assert.Equal(Axes.Names.Count, axes.Count);        // 逐键齐全，一条不缺
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// ⚠️ **`null` 轴名 = 未知轴**，三条入口都不许抛。
    ///
    /// <para>来路是**线上**：`WireJson.Options` 没开 `RespectNullableAnnotations`，
    /// <c>{"kind":"session.setAxis","axis":null}</c> 能落进 `session.setAxis`；
    /// 而 `Dictionary` 的 key 不许为 null。修前实测的栈：
    /// <c>IsKnown(null) ⇒ ContainsKey(null) ⇒ ArgumentNullException「Parameter 'key'」</c>
    /// （T3 的 `SetAxis_WithANullAxis_IsRejected_NotACrash` 修前就是红的）。</para>
    ///
    /// <para>⚠️ **这条为什么必须在这里**（而不是只靠 `session.setAxis` 那条命令测试）：
    /// 命令路径在 `IsKnown` 那一关就返回了，**走不到 `ValuesOf` / `TryParse`**——
    /// 只靠命令测试的话，那两条的判空**删掉也不会红**。
    /// 轴名查询一共就这三条（`LegalValuesText` 是 `ValuesOf` 的包装，`Defaults` 不接轴名），
    /// 契约由本文件守。</para>
    /// </summary>
    [Fact]
    public void NullAxis_IsTreatedAsUnknown_ByAllThreeEntryPoints()
    {
        Assert.False(Axes.IsKnown(null));
        Assert.Null(Axes.ValuesOf(null));
        Assert.False(Axes.TryParse(null, "dev", out var canonical));
        Assert.Equal("", canonical);            // 与「未知轴」一致，不回吐半截
        Assert.Equal("", Axes.LegalValuesText(null));
    }

    /// <summary>
    /// ⚠️ `Labels` 与 `Names` 是同一条要求的两个视图，键集必须一致——
    /// 少一个键就是头的某一格空白，而用户分不清「没标签」与「头坏了」。
    /// </summary>
    [Fact]
    public void Labels_CoverExactlyTheSameAxesAsNames()
    {
        foreach (var n in Axes.Names)
        {
            var label = Axes.LabelOf(n);
            Assert.False(string.IsNullOrWhiteSpace(label), $"轴 '{n}' 没有标签");
            // ⚠️ 断言它**不是回退成档名**：`LabelOf` 对未知轴返回原样，
            // 所以「返回 non-empty」单独还不够——中文名与英文档名不可能相等。
            Assert.NotEqual(n, label);
        }
    }
}
