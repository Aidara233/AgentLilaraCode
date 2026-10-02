# 头的四条义务 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 `05` §4 的四条义务从「一条都没履行」推到「1 / 2 / 4 做全，3 据实阻塞」。

**Architecture:** 先在**核心**补齐两个自成闭环的子系统——五轴（行为控制，`01` §3.5）与声明（资源占用，`01` §12）——再按 `05` §5 的三栏重写头。头只显示、不重建渲染（`05` §3）。

**Tech Stack:** .NET 10 / C# 13 · xUnit · 原生 HTML + JS（零构建，用户裁定）

**Spec:** `docs/05-头设计.md`（头的义务与布局）· `docs/01-会话设计.md` §3.5 / §5 / §8 / §12 · `docs/03-控制面协议.md` §3 / §4 / §5 / §6 / §7 · `docs/00-设计哲学.md` §2.4

---

## 用户裁定（本次范围，不再重问）

| 问题 | 裁定 |
|---|---|
| 义务 3（审批）怎么办 | **1/2/4 做全，3 明说阻塞**——核心侧闸门的前提是「工具」，而工具不存在（`01` §9.1：无插件 ⇒ 工具表空集）。头显示「待决策」区并**明标核心侧闸门尚未实现**，不做成空壳冒充 |
| 前端 | **零构建**：原生 HTML + JS，无打包、无依赖 |
| 核心 / 头 严格度 | 核心**严格执行**（逐任务 + 独立审阅）；头**糊弄**（`05` §1.1 的可替换件）。**修法**：核心任务（T1–T8）各一次独立审阅；头的两个任务（T9/T10）**合并成一次**审阅 |
| `session.send` 语义 | **保文档**：一次步吞掉整队列（`01` §6.1） |

---

## Global Constraints

- **G1 前缀稳定性是最高原则。** 任何「同一贡献者集合 → 不同字节」的路径都是缺陷。
- **G2 头不重新实现渲染**（`05` §3）。解析骨架、要托管内容、渲染成 UI 块——全部归核心。头拿到的是渲染产物。
- **G3 线格式与磁盘格式是两个契约**（既有 `WireJson` / `Json.Options` 分离）。新增的线上字段进 `WireJson`，新增的落盘字段进 `Json`，**不许互相借用实例**。
- **G4 `Serialize<T>(x)` 的静态类型决定形状。** 多态类型必须从**声明类型**序列化（`WireEvent.StableSerialize` 的形状），泛型推断成子类会**静默丢判别式**。
- **G5 命令的唯一执行点**是 `CommandDispatcher`（`04` §1.1）。控制台与头走同一个命令集、同一个回执。控制台只保留两样专有物：**文本解析**与**显示**。
- **G6 `rejected` 只用于「命令本身不合法」**（`03` §3）；业务失败走 `error` **事件**。
- **G7 注释里的断言必须有证据。** 全称句、实测数字、「我验过」——**先跑再写**。变异验证必须先确认变异落上了（见项目记忆 `mutation-must-be-confirmed`）。
- **G8 没有 help 命令是既有缺口，不在本计划修。** 但新增的轴值合法性由 `Rejected.Reason` 用**枚举实际值**拼，不许出现手抄列表（会与枚举漂移）。
- **G9 磁盘格式迁移必须显式。** `SessionState` 加字段要**先量**它是否改变既有金样的字节（`DefaultIgnoreCondition = WhenWritingNull` 是否兜住），并把答案写进注释。
- **G10 同一个值出现在计划的多处时，它们是同一个契约的多份副本。** 改了表就得改测试、改了默认值就得改注释——
  **不许让两处同名的值各自演进**。⚠️ 这条是本计划 T1 的实测教训：brief 里表写 `prod` 合法、测试写 `prod` 非法，
  两者互斥而**全绿不可能同时成立**。与既有记忆 `reused-artifacts-must-be-one-text` 同源。
- **G11 已执行任务里的期望计数是「历史记录」，不是「当前契约」。**
  本计划是**边执行边改**的，所以后面任务里加的测试会让前面任务的计数过时
  （例：T1 段写着「T1 的 11 条」，而 `AxisTests.cs` 现在有 16 个测试方法——
  T4b/T6 期间我在同一个文件里加了守卫）。
  ⇒ **读到已完成任务里的 `Expected: 通过 N` 时，N 是那次执行当时看到的数，别再拿它对账。**
  ⇒ 但它在**未执行**的任务里是**活指令**，必须与实测一致——派发前数一遍（G7）。
  判据很简单：**这个任务已经 complete 了吗？** 是 ⇒ 历史；否 ⇒ 活指令。

---

## Review Focus

按「最可能咬到使用者」排序。每一条都在下面点名了它的测试落在哪个任务。

| # | 输入 / 条件 | 合理的人会期望 | 落在 |
|---|---|---|---|
| 1 | 老 `state.json`（**没有** `axes` 键）被读入 | 五轴显示**默认值**，不是空白、不是崩溃 | T4 |
| 2 | `session.setAxis` 传一个**枚举里没有**的值 | 回 `rejected` 并**列出合法值**；轴值不变 | T2 |
| 3 | 同一会话**两次同轴**设值 | 第二次是幂等 no-op，**不发事件**（`01` §5：无迁移则无 `axis.changed`） | T2 |
| 4 | `claim.acquire` 一个**已被别人占用**的路径 | 回 `rejected` 并**点名占它的那个会话**——这是「别人的工作集会被作废」唯一的可见时刻 | **T7**（不是 T6：`rejected` 是**命令**的产物，而 T6 只产 `Claims.FindConflict`，它返回 `Claim?`、碰不到回执。⚠️ 也**不是 T10**：T10 的验收是**故意**用产生 `rejected` 的路径去演示冲突上浮，该路径**必须**回 `rejected` 才算对） |
| 5 | `GET /claims` 时**根本没有 `claims.json`** | 返回 `[]`，不是 404、不是 500（「没有声明」是合法且常见的形态） | T7 |
| 6 | 头收到 `resync_required` 之后 | 能**重新订阅并复活**（`03` §7 说这是唯一的降级路径） | T10 |
| 7 | 头连着**零会话**的核心启动 | 走 `05` §2.2 的**引导**（先问提供商、再建会话），而不是空页面 | T9 |
| 8 | 核心**只监本机**，头是独立 origin | CORS 预检通过、WS 握手上得去 | T7 |
| 9 | 用户把头上下拉的 effort 从 `high` 拨到 `max` | **下一次请求真的带 `max`**——而不是头显示 `max`、模型收到 `high` | **T3**（执行中发现，见 T2 的「执行中补的缺陷」） |

### ⚠️ 一个会随这个改动出现的**静默重复**（T2 的注释里要点名）

修掉 effort 那个洞之后，`Spec.Model.Effort` 就**只剩「创建时的播种源」一个用途**了——
而它的名字（`Model.Effort`）读起来像「当前的 effort」。两者会在用户改轴之后**分叉**：

```
Spec.Model.Effort = "high"   ← 建会话时的
handle.State.Axes["effort"] = "max" ← 用户刚改的
```

**今天不出错**（`Stepper` 只读轴），但下一个读到 `Spec.Model.Effort` 的人会以为那是当前值。
⇒ T2 必须在 `SessionSpec.cs` 的 `Model.ModelSelection.Effort` 上加一条注释，
**点名它是快照、不是当前值，当前值在 `handle.State.Axes`**。
这不算「顺手加的东西」——它是这次改动**制造出来**的歧义，不写就是留给下一个人的坑。

---

## 文件结构

**新建（核心）**

| 文件 | 职责 |
|---|---|
| `src/Lilara.Core/Session/Axes.cs` | 五轴的**唯一**真相来源：轴名、合法值、默认值、`axis.changed` 的触发判据 |
| `src/Lilara.Core/Session/Claims.cs` | 声明的**唯一**真相来源：路径规范化、冲突判据（前缀）、占用者查询 |
| `src/Lilara.Core/Persistence/ClaimStore.cs` | `<工作目录>/.lilara/claims.json` 的读写（`AtomicFile`） |

**修改（核心）**

| 文件 | 改什么 |
|---|---|
| `src/Lilara.Core/Persistence/SessionState.cs` | 加 `Axes`（可空）——**磁盘迁移，先量** |
| `src/Lilara.Core/Session/SessionHandle.cs` | `Load` 时把 null 的 `Axes` 补成默认值（老 `state.json` 的兼容点） |
| `src/Lilara.Core/Hosting/CoreCommand.cs` | 加 `SessionSetAxis` / `ClaimAcquire` / `ClaimRelease` |
| `src/Lilara.Core/Hosting/CommandDispatcher.cs` | 三个新 case + `axis.changed` / `claim.*` 的发布 |
| `src/Lilara.Core/Control/WireEvent.cs` | 加 `AxisChanged` / `ClaimAcquired` / `ClaimReleased` |
| `src/Lilara.Core/Control/ControlServer.cs` | 加 `GET /claims`；`/sessions/{id}/state` 带三轴 + 类型化槽位 |
| `src/Lilara.Core/Console/CoreCommandParser.cs` | `axis set` / `claim` 两个子命令 |
| `src/Lilara.Core/Program.cs` | 启动横幅补一行用法（G8：不引入 help 框架）——⚠️ **归 T8**，与 `claim` 那行一起加（执行时发现它原来在 T5 的 Files 表里当孤儿：brief 零命中 ⇒ 实现者看不到） |

**重写（头）**

| 文件 | 改什么 |
|---|---|
| `head/wwwroot/index.html` | 三栏（`05` §5）+ 五轴 + 浮动槽 + 待决策区 |
| `head/wwwroot/app.js` | 引导流程、轴状态、声明视图、冲突上浮、resync 复活 |
| `head/Program.cs` | **不动**（除了一条注释：说明它为什么只托管静态资源） |

---

## 明确不做的偏离（要写进代码注释）

| # | 偏离 | 为什么 |
|---|---|---|
| D1 | **义务 3 阻塞** | 权限闸门的前提是工具；工具集从插件宿主派生而插件宿主不存在。做成空壳等于让用户分不清「没待审批」与「闸门坏了」 |
| D2 | **`WaitingForInput` / `Blocked` 仍是死状态** | 它们的产生者分别是闸门与退避重试，都不存在；本计划**不假装**它们会出现 |
| D3 | **声明不接写入守卫** | `01` §12.2 的「写入时自动检查」要求有写入路径（工具）；本计划只做「声明主动」那一格 + 冲突可见 |
| D4 | **声明不做「同一派生树内不阻塞」** | `01` §12.1 的例外需要派生树概念，而 `child.spawned` 不存在 ⇒ **没有派生树，那个例外没有输入可作用**。⚠️ **不是「多会话下会过阻塞」**（那个说法是错的）：本计划的声明**不阻塞任何东西**（D3），所以漏做这个例外不可能多拦下一件事——它至多在树内产生一次**误报**冲突（一个多余的可见信号）。等阻塞机制与派生树一并出现时，这个例外要一起补。写进注释 |
| D5 | **头不重算前缀指纹** | `05` §1 说头应「重新算并显示」。指纹的输入是工具表 ∥ 系统块 ∥ 首要块，前两者现在是空集——算出来是一个常量，**显示一个常量不是可见性** |
| D6 | **不做 `term.delta` / 附件 / 库 / 控制点 / `dirty.changed`** | 保持既有偏离（见控制面分支的台账） |
| D7 | **不做长历史分页 / 虚拟滚动 / 多标签页** | `05` §11 的「本文自身待展开」，本计划不含 |
| D8 | **`effort` 的档位仍是两份副本**（`Axes.Table` 与 `ClaudeRequestBuilder.ToEffort` 的 switch） | 执行中由评审发现，控制器裁定**本次不修**：修它要动适配器层（`ClaudeRequestBuilder`），而那是**既有**形态、非本计划引入。⚠️ **但它是 G10 的活标本**——两处手抄同一组档名，`Axes` 加一档而 `ToEffort` 不加 ⇒ 用户在头上下拉选中却被请求层抛异常（`ToEffort` 对未知值是 `_ => throw`）。⇒ **记为已知偏离，不是已知正确**。修法（将来）：让 `ToEffort` 从 `Axes` 取档名，或加一条「`Axes.ValuesOf(Effort)` ⊆ `ToEffort` 的臂」的对拍测试 |
| D9 | **声明不做符号链接解析** | `01` §12.1 写「Windows 下需处理大小写不敏感**与符号链接**」，本计划只做前半（派 T6 之前实测发现零覆盖）。不是判它不重要：**是本机做不成可验证的测试**——实测 `New-Item -ItemType SymbolicLink` 失败于「此操作需要管理员权限」，而 G7 要求变异先确认落上再跑，一条跑不了的测试等于没有 ⇒ 那种测试会**看起来很绿**。后果方向是**漏报**（两个路径经不同链接指向同一处时不冲突），不是误报。修法：判据换成 `FileSystemInfo.ResolveLinkTarget` 后比较，并在**有权限的**环境补测试 |

---

### Task 1: 五轴的真相来源

**Files:**
- Create: `src/Lilara.Core/Session/Axes.cs`
- Test: `tests/Lilara.Core.Tests/AxisTests.cs`

**Interfaces:**
- Consumes: 无
- Produces: `Lilara.Core.Session.Axes`（静态类）—— `Names` (`IReadOnlyList<string>`) · `ValuesOf(string axis) → IReadOnlyList<string>?` · `TryParse(string axis, string value, out string canonical) → bool` · `Defaults() → IReadOnlyDictionary<string,string>` · `IsKnown(string axis) → bool` · `LegalValuesText(string axis) → string`；常量 `Axes.Initiative = "initiative"` 等五个

- [ ] **Step 1: 写失败的测试**

```csharp
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

    /// <summary>
    /// ⚠️ 测试名直说：这几个默认值是**本计划定的**，不是 `01` §3.5 **蕴含**的。
    /// §3.5 只给档名（开发 / 迭代 / 生产），**没有默认值列**——去找出处会找不到。
    /// ⇒ 名字改成 `…ThePlanPicks`，免得下一任读者以为文档能兜底。
    /// </summary>
    [Fact]
    public void Defaults_AreTheOnesThePlanPicks()
    {
        var d = Axes.Defaults();
        Assert.Equal("step", d["initiative"]);     // 步进：核心不替你决定该不该继续（§6.1）
        Assert.Equal("standard", d["permission"]); // 标准：写入类要问
        Assert.Equal("dev", d["safety"]);          // 开发：绝不能默认「生产」
        Assert.Equal("self", d["verify"]);         // 自检
        // ⚠️ 出处是 `CommandDispatcher.HandleSessionCreate` 调的那句
        // `ToSelection(provider, null, "high", 16000)`——**不是 `ProviderStore`**，
        // 那里只是把 effort 参数**透传**，没有任何默认值。
        Assert.Equal("high", d["effort"]);
    }

    [Fact]
    public void UnknownAxis_IsNotKnown() => Assert.False(Axes.IsKnown("mood"));

    [Fact]
    public void UnknownValue_IsRefused_AndTheAxisIsUnchanged()
    {
        // ⚠️ **注意这个字面量**：`"production"` 不是 `"prod"`。
        // 表里 `[Safety] = ["dev","staging","prod"]` ⇒ **`prod` 是合法值**，
        // 拿它当「未知值」那条测试**第一把必红**。
        // 本计划第一版就写了 `"prod"`——见 G10：同一个值出现多处时它们是**同一个契约的副本**。
        Assert.False(Axes.TryParse("safety", "production", out var canonical));
        Assert.Equal("", canonical);
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
        Assert.Equal(string.Join("|", Axes.ValuesOf("safety")), text);
    }
}
```

- [ ] **Step 2: 跑测试确认它失败**

Run: `dotnet test --filter "FullyQualifiedName~AxisTests" -v q`
Expected: 编译失败，`CS0103: 名称 "Axes" 不存在`

- [ ] **Step 3: 写实现**

```csharp
namespace Lilara.Core.Session;

/// <summary>
/// 行为控制轴（`01` §3.5）——**五轴的唯一真相来源**。
///
/// <para>⚠️ 它是一张**表**，不是五个独立的散落判断。轴名、合法值、默认值三样放在一起，
/// 是因为它们必须同步演进：加一个档而忘了改默认值或错误信息，正是这类表最常见的漂移。
/// 任何别处（头的下拉、控制台的解析、错误信息）都**从这张表读**，不许手抄。</para>
///
/// <para>⚠️ **`effort` 在表里，但它不进上下文**（`01` §3.5：纯请求参数）。
/// 它仍然是一条轴：用户要能改它，机械层（请求参数）也要跟着变。
/// 「给头看 ≠ 给模型看」——头显示它是正确的，把它塞进提示词才是错的。</para>
/// </summary>
public static class Axes
{
    public const string Initiative = "initiative";
    public const string Permission = "permission";
    public const string Safety = "safety";
    public const string Verify = "verify";
    public const string Effort = "effort";

    /// <summary>⚠️ 顺序**就是**渲染顺序，别按字母排。</summary>
    public static readonly IReadOnlyList<string> Names =
        [Initiative, Permission, Safety, Verify, Effort];

    private static readonly Dictionary<string, string[]> Table = new(StringComparer.Ordinal)
    {
        // 主动性 = 循环的停止条件（§3.5）。档名用英文，与线上协议一致。
        [Initiative] = ["step", "standard", "goal"],

        // 权限 = 闸门。0..5 单调阶梯，低档是拒绝线、高档是询问线。
        [Permission] = ["off", "readonly", "investigate", "standard", "authorized", "trusted"],

        // 安全 = 纯提示。⚠️ 没有「机械兜底」——不设「生产 ⇒ 权限封顶」（§3.5 明说不许）。
        [Safety] = ["dev", "staging", "prod"],

        [Verify] = ["none", "self", "strict"],

        // ⚠️ 与 `ClaudeRequestBuilder.ToEffort` 的取值**必须一致**——
        // 少了对应关系，轴改了而请求参数没改，用户看到的是「设了没用」。
        [Effort] = ["low", "medium", "high", "xhigh", "max"],
    };

    private static readonly Dictionary<string, string> DefaultTable = new(StringComparer.Ordinal)
    {
        [Initiative] = "step",
        [Permission] = "standard",
        [Safety] = "dev",       // ⚠️ 绝不能默认「生产」——那会让告警每步都响
        [Verify] = "self",
        [Effort] = "high",
    };

    public static bool IsKnown(string axis) => Table.ContainsKey(axis);

    public static IReadOnlyList<string>? ValuesOf(string axis) =>
        Table.TryGetValue(axis, out var v) ? v : null;

    /// <summary>合法值清单（错误信息用）。⚠️ 从表里拼，不手抄。</summary>
    public static string LegalValuesText(string axis) =>
        ValuesOf(axis) is { } v ? string.Join("|", v) : "";

    /// <summary>校验并规范化。大小写不敏感（用户会写 `Prod`）。</summary>
    public static bool TryParse(string axis, string value, out string canonical)
    {
        canonical = "";
        if (!Table.TryGetValue(axis, out var values)) return false;

        foreach (var v in values)
        {
            if (!string.Equals(v, value, StringComparison.OrdinalIgnoreCase)) continue;
            canonical = v;
            return true;
        }
        return false;
    }

    /// <summary>默认值。**每次返回新字典**：调用方要往 handle 上挂，共享可变实例会串会话。</summary>
    public static IReadOnlyDictionary<string, string> Defaults() =>
        new Dictionary<string, string>(DefaultTable, StringComparer.Ordinal);
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test --filter "FullyQualifiedName~AxisTests" -v q`
Expected: 通过 7，失败 0

- [ ] **Step 5: 确认变异被抓住（G7）**

把 `Table[Safety]` 改成 `["dev", "staging", "prod", "test"]` 再跑——`LegalValuesText` 那条**必须仍然绿**（它是从表拼的，这正是重点）；把 `Defaults` 里的 `safety` 改成 `"production"` 再跑——`Defaults_AreTheOnesThePlanPicks` **必须红**。

⚠️ **改完先 grep 确认字符串真的变了再跑**（项目记忆 `mutation-must-be-confirmed`：变异没生效时输出与「存活」无法区分）。

- [ ] **Step 6: 提交**

```bash
git add src/Lilara.Core/Session/Axes.cs tests/Lilara.Core.Tests/AxisTests.cs
git commit -m "feat(core): 五轴真相来源（轴名/合法值/默认值一张表）+ 取值一致性测试"
```

---

### Task 2: 状态加轴 —— 先量磁盘迁移

**Files:**
- Modify: `src/Lilara.Core/Persistence/SessionState.cs`
- Modify: `src/Lilara.Core/Session/SessionHandle.cs:123-129`（`Load`）与 `Create`
- Modify: **`src/Lilara.Core/Persistence/SessionSpec.cs`** —— ⚠️ 见下面的「执行中补的缺陷」
- Modify: **`src/Lilara.Core/Session/Stepper.cs:69`** —— ⚠️ 见下面的「执行中补的缺陷」
- Modify: `tests/Lilara.Core.Tests/DiskFormatGoldenTests.cs`
- Test: `tests/Lilara.Core.Tests/AxisTests.cs`（追加）

> ### ⚠️ 执行中补的第二处（T2 审阅发现，控制器核准）：`SessionSpec.Model.Effort` 是**快照**，注释没说
>
> 计划在 Review Focus 里写了「T2 必须在 `SessionSpec.cs` 的 `Model.ModelSelection.Effort` 上加一条注释，
> 点名它是快照、不是当前值」——**但控制器写 T2 的 brief 时漏把它带进去**，实现者不可能知道。
>
> ⚠️ **这个坑已经现实发生了一次**：`Repl.cs:205` 的 `session status` 正是把
> `Spec.Model.Effort` 当**当前值**印出来的那个「下一个人」。已归 T5 修。
>
> ⇒ 注释内容：**`Effort` 是建会话时冻结的快照，只用于给轴播种；当前值在 `SessionState.Axes[Axes.Effort]`**。
> 不写的话，下一个读它的人会以为那是当前值——而两者在用户改过轴之后**必然分叉**。
>
> ⚠️ **附带一条更一般的教训**：计划把要求写在「Review Focus」节，而 brief 生成脚本只抽
> 「Task N」那一段 ⇒ **写在 Review Focus 里的强制项不会进 brief**。
> 本计划里 Review Focus 表还写着「setAxis 的两条判据落在 T2」而它们实际由 T3 交付——
> **那张表的任务编号是漂的**。以各任务正文为准。

> ### ⚠️ 执行中补的缺陷（T1 的实现者发现，控制器核准）
>
> **原计划有一个会让「设了没用」的洞。** `Stepper.cs:69` 写的是
> `Effort = handle.Spec.Model.Effort`——**建会话时冻结的快照**。
> ⇒ 按原计划，`session.setAxis(sid, "effort", "max")` 改的是 `handle.State.Axes["effort"]`，
> 而**请求里发的仍然是 `Spec.Model.Effort`**。用户选 `max`、头显示 `max`、**模型收到 `high`**。
>
> 讽刺的是 T1 的 brief 里**已经写了这条警告**（「少了对应关系，轴改了而请求参数没改，
> 用户看到的是『设了没用』」）——**写完警告，然后把警告描述的东西造了出来**。
>
> **修法（三处）**，本任务负责前两处，第三处归 T3：
>
> ① `Stepper` 从**轴**读 effort（轴是**活**的真相，Spec 是**创建时**的快照，两者职责不同）；
> ② `Create` 把轴的 effort **从 spec 播种**，而不是用表里的硬编码默认值——
>    否则提供商配的是 `low` 时会造出一个 Spec 说 `low`、轴说 `high` 的会话，
>    **出生即分叉**；
> ③ T3 加一条测试：`setAxis("effort","max")` 之后**下一次模型请求真的带着 `max`**。
>    那才是这条链路的证据——少了它，上面两处只是两个没人验的改动。

**Interfaces:**
- Consumes: `Axes`（T1）
- Produces: `SessionState.Axes`（`IReadOnlyDictionary<string,string>?`，**可空**）· `SessionHandle.State.Axes`（**`Load` / `Create` 之后保证非空**）· `SessionHandle.SaveAxes(SessionStore)`

> ### ⚠️ 执行中改掉的一处设计（T2 审阅，控制器裁定）
>
> **原设计多了一个 `SessionHandle.Axes` 属性，与 `SessionHandle.State.Axes` 是两份——删掉了。**
>
> `SessionHandle.State` **本来就是活的内存状态**（`Status` / `Turn` 都住那儿，
> `Stepper` 只把 `Running` 写内存不落盘）。另造一个 `handle.Axes` 是把**同一份东西放了两个地方**，
> 而两者会分叉（`Load` 只补其中一个）。这正是本代码库视为缺陷的那类。
>
> ⇒ **一律走 `State.Axes`**：`Load` 用 `with` 把默认值补进 `State`；`Create` 把播种值放进构造
> `SessionState` 的那一步；`Stepper` 读 `handle.State.Axes[Axes.Effort]`。
>
> ⚠️ **类型仍可空**（老 `state.json` 读出来是 null），但 **`Load` / `Create` 出来的一定非空**——
> 把「何时非空」写进注释，别让下一个读到的人以为要处处判空。
>
> 下面 Step 4 的代码块是**原始设计**（含那个多余属性）。以 git 里的提交为准；
> 差异就是「删掉 `SessionHandle.Axes`，全部走 `State.Axes`」。

- [ ] **Step 1: 先量——加字段会不会改变既有金样的字节**

在写任何实现之前，先回答一个**可测量**的问题：`SessionState` 加一个可空的 `Axes` 之后，`DiskFormatGoldenTests.SessionState_KeepsItsExactBytes` 那句断言还成立吗？

`Json.Options` 的 `DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull`（`Canonical/Json.cs:13`）在**属性值为 null 时省略该键**。所以只要测试构造的 `state` 不设 `Axes`，序列化结果**逐字节不变**。

⚠️ **但这句话必须当场验证，不能推理了就算**（G7）。做法：先只加属性、不设值，跑那条金样测试。

- [ ] **Step 2: 写失败的测试（含上面的量）**

在 `DiskFormatGoldenTests.cs` 追加：

```csharp
/// <summary>
/// ⚠️ **老 `state.json` 里没有 `axes` 键，它必须仍然是合法的。**
/// `Json.Options` 的 `WhenWritingNull` 让「没设轴」的 state 序列化结果**逐字节不变**——
/// 这是本计划唯一一次磁盘迁移，而它之所以安全正是因为这条。
/// 把它钉成测试：将来谁把 `Axes` 改成 `required` 或给它一个非 null 默认值，这条会红。
/// </summary>
[Fact]
public void SessionState_WithoutAxes_IsByteIdenticalToBeforeTheAxisFeature()
{
    var state = new SessionState { SessionId = "abc123", Status = "Idle", Turn = 0, Pending = [] };

    Assert.Equal(
        """{"sessionId":"abc123","status":"Idle","turn":0,"pending":[]}""",
        System.Text.Json.JsonSerializer.Serialize(state, Json.Options));
}

/// <summary>设了轴就写出来——形状要能一眼看出来，别嵌套成对象套对象。</summary>
[Fact]
public void SessionState_WithAxes_SerializesThemAsAFlatObject()
{
    var state = new SessionState
    {
        SessionId = "abc123", Status = "Idle", Turn = 0, Pending = [],
        Axes = new Dictionary<string, string> { ["safety"] = "prod" },
    };

    Assert.Equal(
        """{"sessionId":"abc123","status":"Idle","turn":0,"pending":[],"axes":{"safety":"prod"}}""",
        System.Text.Json.JsonSerializer.Serialize(state, Json.Options));
}
```

在 `AxisTests.cs` 追加：

```csharp
/// <summary>
/// ⚠️ **老会话（磁盘上没有 `axes`）载入后必须拿到默认值**，而不是空字典。
/// 空字典会让头渲染五个空白下拉，用户分不清「没设过」与「头读不到」。
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
                Effort = "high", MaxTokens = 16000,
            },
        });

        var handle = SessionHandle.Load(store, pk, "s1");

        Assert.Equal(Axes.Defaults(), handle.State.Axes!);
    }
    finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}
```

- [ ] **Step 3: 跑测试确认它失败**

Run: `dotnet test --filter "FullyQualifiedName~SessionState_With|FullyQualifiedName~Load_OfALegacy" -v q`
Expected: 编译失败（`Axes` 属性不存在）。⚠️ 同时**单独跑一次金样**（`--filter FullyQualifiedName~SessionState_WithoutAxes`）确认它在**加属性之前**是绿的——那是基线。

- [ ] **Step 4: 写实现**

`SessionState.cs` —— 加在 `Pending` 之后：

```csharp
    /// <summary>
    /// 行为控制轴的当前值（`01` §3.5）。
    ///
    /// <para>⚠️ **可空是刻意的，而且它是磁盘迁移的安全阀**：老 `state.json` 里没有这个键，
    /// 读出来是 <c>null</c>；`Json.Options` 的 <c>WhenWritingNull</c> 让「没设轴」的序列化
    /// **逐字节不变**（有金样钉着）。<see cref="SessionHandle.Load"/> 负责把 null 补成默认值，
    /// 所以**只有 `Load` 之后才保证非空**——别在别处假设它非空。</para>
    ///
    /// <para>⚠️ **轴进的是状态，不是上下文**——`01` §3.5：`effort` 完全不进浮动。
    /// 这里存的是「用户设了什么」，怎么用是各轴自己的事。</para>
    /// </summary>
    public IReadOnlyDictionary<string, string>? Axes { get; init; }
```

`SessionHandle.cs` —— 加属性并改 `Load`：

```csharp
    /// <summary>
    /// 轴的当前值。⚠️ **非空**——<see cref="Load"/> 已经把磁盘上的 null 补成默认值。
    /// 加一条轴时**只改 <see cref="Axes.Defaults"/>**：这里是按默认值补齐的，
    /// 不是按硬编码名单，所以新轴对老会话自动生效。
    ///
    /// <para>⚠️ **`set` 而不是 `init`**：`session.setAxis`（Task 3）要改它，
    /// 而 T3 的处理器**不**走 `with`——`SessionHandle` 是可变的活对象，
    /// 换一个新实例会让激活表里那份**指向旧实例**（状态分叉）。
    /// 改属性 + `SaveAxes` 才是对的。</para>
    /// </summary>
    public required IReadOnlyDictionary<string, string> Axes { get; set; }
```

```csharp
    public static SessionHandle Load(SessionStore store, string projectKey, string sessionId)
    {
        var state = store.ReadState(projectKey, sessionId);
        return new SessionHandle
        {
            ProjectKey = projectKey,
            Spec = store.ReadSpec(projectKey, sessionId),
            State = state,
            Directory = store.SessionDirectory(projectKey, sessionId),

            // ⚠️ **老 state.json 没有 `axes` 键** ⇒ 读出来是 null ⇒ 在这里补默认值。
            // 不补的话，头会渲染五个空白下拉，用户分不清「没设过」与「头读不到」。
            // ⚠️ 按 `Axes.Defaults()` 补齐而不是按硬编码名单——加一条新轴时，
            // 老会话也会自动拿到它的默认值，不需要第二处修改。
            Axes = state.Axes is { Count: > 0 }
                ? state.Axes
                : Axes.Defaults(),
        };
    }
```

⚠️ `Create` 也要给 `Axes` 赋值——新会话直接拿默认值。把 `Create` 末尾那个
`return new SessionHandle { … };` 换成：

```csharp
        var handle = new SessionHandle
        {
            ProjectKey = projectKey,
            Spec = spec,
            State = state,
            Directory = store.SessionDirectory(projectKey, spec.SessionId),

            // ⚠️ **轴的 effort 从 spec 播种，不是从表里的硬编码默认值。**
            // 提供商配的是 `effort=low` 时会造出一个 **Spec 说 low、轴说 high** 的会话——
            // **出生即分叉**，而 `Stepper` 从轴读 ⇒ 用户从没设过轴，effort 却变了。
            // 其余四条轴没有「会话配置里的镜像」，所以仍取默认值。
            Axes = WithEffortFrom(Axes.Defaults(), spec.Model.Effort),
        };

        // ⚠️ 默认值也**要落盘**：不落的话第一次重启时 `state.Axes` 是 null，
        // `Load` 会补一份新的默认值——结果一样，但磁盘上「没设过」与「设成了默认值」
        // 就永远分不出来，而将来改默认值会让老会话**静默改变行为**。
        handle.SaveAxes(store);
        return handle;
```

```csharp
    /// <summary>把 spec 里冻结的 effort 播种进轴——见 `Create` 的注释。</summary>
    private static IReadOnlyDictionary<string, string> WithEffortFrom(
        IReadOnlyDictionary<string, string> defaults, string effort)
    {
        var d = new Dictionary<string, string>(defaults, StringComparer.Ordinal);
        if (Axes.ValuesOf(Axes.Effort)!.Contains(effort, StringComparer.OrdinalIgnoreCase))
            d[Axes.Effort] = effort;
        return d;
    }
```

⚠️ 那个 `if` 不是多余的：老的 `spec.json` 里 effort 可能是**表里没有的值**
（`ClaudeRequestBuilder.ToEffort` 抛异常的那类拼写错误）。种子进轴会让头的下拉
**没有任何一个选项被选中**——那是比「悄悄用默认值」更坏的显示。表里没有就退回默认值。

⚠️ **`Stepper.cs:69` 改成从轴读**：

```csharp
                // ⚠️ **从轴读，不从 Spec 读。** Spec 是**建会话时冻结的快照**，
                // 而轴是**活**的（`session.setAxis` 改的就是它）。
                // 按 Spec 读的话，用户在头上把 effort 拨到 `max`、头显示 `max`，
                // 而**请求里发的还是建会话时那个值**——「设了没用」，且无从发现。
                // ⚠️ 其余四条轴**不进请求**（`01` §3.5：只有 effort 是纯机械轴），
                // 所以这里只读一条。将来哪条轴有了机械层，在这里加它的读取。
                Effort = handle.State.Axes![Axes.Effort],
```

并加一个写回点（轴变了要落盘，否则重启即丢）：

```csharp
    /// <summary>
    /// 把当前轴写进 `state.json`。
    /// ⚠️ **整文件重写**（`state.json` 本来就是每步原子重写，`01` §8）——
    /// 但要**基于 <see cref="State"/> 现读的副本**，不能另构造一个，否则会把 `Turn` / `Status` 抹掉。
    /// </summary>
    public void SaveAxes(SessionStore store)
    {
        State = State with { Axes = Axes };
        store.WriteState(ProjectKey, State);
    }
```

- [ ] **Step 5: 跑测试确认通过（两条金样 + 补齐缺省）**

Run: `dotnet test -v q`
Expected: 全绿。**尤其是** `SessionState_KeepsItsExactBytes`（既有那条）——它证明没设轴时字节不变。

- [ ] **Step 6: 提交**

```bash
git add src/Lilara.Core/Persistence/SessionState.cs src/Lilara.Core/Session/SessionHandle.cs tests/Lilara.Core.Tests/
git commit -m "feat(core): 状态带轴（可空字段 + 读时补默认值）；磁盘迁移由 WhenWritingNull 兜住，金样钉死"
```

---

### Task 3: `session.setAxis` 命令

**Files:**
- Modify: `src/Lilara.Core/Hosting/CoreCommand.cs`
- Modify: `src/Lilara.Core/Control/WireEvent.cs`
- Modify: `src/Lilara.Core/Hosting/CommandDispatcher.cs`
- Modify: `src/Lilara.Core/Session/Axes.cs`（null 轴名守卫——`IsKnown`/`ValuesOf`/`TryParse` 三条入口）
- Test: `tests/Lilara.Core.Tests/AxisCommandTests.cs`（**新建**，见正文）· `tests/Lilara.Core.Tests/WireFormatTests.cs`（追加）

**Interfaces:**
- Consumes: `SessionHandle.Axes` / `.SaveAxes`（T2）· `Axes`（T1）
- Produces: `CoreCommand.SessionSetAxis(string SessionId, string Axis, string Value)` · `WireEvent.AxisChanged(string SessionId, string Axis, string Value)`

> ### ⚠️ 全部改动的**唯一目的**：让「改轴」真的到达模型请求
>
> `01` §3.5 说 `effort` 是**纯机械轴**——走请求参数。T2 已经把 `Stepper` 改成从轴读，
> 所以本任务加完命令之后，链路才是通的：`setAxis` → `SaveAxes` 落盘 → `Stepper` 从轴读 → 请求带新值。
>
> ⚠️ **T1 的 brief 里已经写过这条警告，而计划当时没把它接上**：
> 「少了对应关系，轴改了而请求参数没改，用户看到的是『**设了没用**』」。
> ⇒ 那个测试（`SetAxis_Effort_ActuallyReachesTheModelRequest`）**必须真的走完整条链路**，
> 断言**适配器收到的 `ModelRequest.Effort`**——断言 `handle.State.Axes` 对这条缺陷**没有区分力**（修复前后都是新值）。

- [ ] **Step 1: 写失败的测试**

```csharp
/// <summary>
/// 五轴（`01` §3.5）的命令路径。
/// ⚠️ 这三条一起钉住「轴是**状态**、不是事件」这个区别：设值**发事件**，
/// 而**重复设同一个值不发**——事件是**迁移**的产物（`01` §5）。
///
/// <para>⚠️ **放在独立文件 `AxisCommandTests.cs`**（不是追加进 `CommandDispatcherTests.cs`）：
/// 它有**自己的 harness**（双临时目录 + provider + FakeAdapter），
/// 塞进既有测试类要么复制它的字段、要么撞它的文件名前缀。
/// 独立文件是这类「同一套约定、另一组夹具」的常规做法（`ControlServerTests` 也是这么来的）。</para>
/// </summary>
public class AxisCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lilara-axis-cmd-" + Guid.NewGuid().ToString("N"));
    private readonly string _work = Path.Combine(Path.GetTempPath(), "lilara-axis-cmdwork-" + Guid.NewGuid().ToString("N"));

    public AxisCommandTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
    }

    private (CoreHost Host, CommandDispatcher Dispatcher, EventBus Bus, SessionHandle Session) Harness()
    {
        var host = new CoreHost(_root, _work);
        host.Providers.Save(new ProviderDefinition
        {
            Id = "p", Protocol = Protocols.ClaudeMessages, ApiKey = "k",
            DefaultModel = "m", Modality = "vision",
        });
        var bus = new EventBus();
        var adapter = new Fakes.FakeAdapter();
        var d = new CommandDispatcher(host, bus, h => new Stepper(host.Store, adapter));
        var created = d.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None)
            .GetAwaiter().GetResult();
        var sid = ((Ack.Accepted)created.Ack).SessionId!;
        host.TryGetActivated(sid, out var h);
        return (host, d, bus, h);
    }

    private static string NewSession(CommandDispatcher d)
    {
        var r = d.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None)
            .GetAwaiter().GetResult();
        return ((Ack.Accepted)r.Ack).SessionId!;
    }

    [Fact]
    public async Task SetAxis_ChangesTheValue_AndPublishesAxisChanged()
    {
        var (_, d, bus, session) = Harness();
        using var sub = bus.Subscribe(EventBus.SessionTopic(session.SessionId));

        var r = await d.DispatchAsync(
            new CoreCommand.SessionSetAxis(session.SessionId, "safety", "prod"), "a1", false, CancellationToken.None);

        Assert.IsType<Ack.Accepted>(r.Ack);
        Assert.Equal("prod", session.State.Axes!["safety"]);
        Assert.True(sub.Reader.TryRead(out var p), "改轴必须发 axis.changed——头靠它更新那个下拉");
        var evt = Assert.IsType<WireEvent.AxisChanged>(p.Payload);
        Assert.Equal("safety", evt.Axis);
        Assert.Equal("prod", evt.Value);
    }

    /// <summary>
    /// ⚠️ **重复设同一个值不发事件**（`01` §5：事件是**迁移**的产物）。
    /// 发了的话，头每收到一条都要重渲染那个下拉，而用户的鼠标正悬在上面。
    /// </summary>
    [Fact]
    public async Task SetAxis_ToTheSameValue_IsANoOpAndPublishesNothing()
    {
        var (_, d, bus, session) = Harness();
        await d.DispatchAsync(new CoreCommand.SessionSetAxis(session.SessionId, "safety", "prod"), "a1", false, CancellationToken.None);

        using var sub = bus.Subscribe(EventBus.SessionTopic(session.SessionId));
        var r = await d.DispatchAsync(new CoreCommand.SessionSetAxis(session.SessionId, "safety", "prod"), "a2", false, CancellationToken.None);

        Assert.IsType<Ack.Accepted>(r.Ack);
        Assert.False(sub.Reader.TryRead(out _), "同一个值不是迁移，不该发事件");
    }

    /// <summary>
    /// ⚠️ **非法取值 ⇒ `rejected`**（G6 / `03` §3：命令本身不合法）。
    /// 而理由必须**列出合法值**——用户唯一的线索就是它。
    /// </summary>
    [Fact]
    public async Task SetAxis_WithAnUnknownValue_IsRejected_AndListsTheLegalOnes()
    {
        var (_, d, _, session) = Harness();

        var r = await d.DispatchAsync(
            new CoreCommand.SessionSetAxis(session.SessionId, "safety", "production"), "a1", false, CancellationToken.None);

        var rejected = Assert.IsType<Ack.Rejected>(r.Ack);
        foreach (var v in Axes.ValuesOf("safety")!) Assert.Contains(v, rejected.Reason);
        Assert.Equal("dev", session.State.Axes!["safety"]);   // ⚠️ 值没被改
    }

    /// <summary>
    /// ⚠️ **拒绝顺序是会话 → 轴 → 值**。这里刻意用一个**不存在的会话 + 不认识的轴**，
    /// 断言报的是**轴不认识**（会话那关先过）。反过来的话，一个真实的用户
    /// （会话 id 敲错了、轴也敲错了）会被告知「轴不认识」，于是去改轴——而真正错的是会话 id。
    /// </summary>
    [Fact]
    public async Task SetAxis_OnAnUnknownAxis_IsRejected_AndListsTheAxes()
    {
        var (_, d, _, session) = Harness();

        var r = await d.DispatchAsync(
            new CoreCommand.SessionSetAxis(session.SessionId, "mood", "happy"), "a1", false, CancellationToken.None);

        var reason = Assert.IsType<Ack.Rejected>(r.Ack).Reason;
        foreach (var a in Axes.Names) Assert.Contains(a, reason);
    }

    /// <summary>⚠️ 会话不存在时**先报会话**——别让用户去改一个没错的轴名。</summary>
    [Fact]
    public async Task SetAxis_OnAnUnknownSession_IsRejected_AndNamesTheSession()
    {
        var (_, d, _, _) = Harness();

        var r = await d.DispatchAsync(
            new CoreCommand.SessionSetAxis("nope", "mood", "happy"), "a1", false, CancellationToken.None);

        Assert.Contains("nope", Assert.IsType<Ack.Rejected>(r.Ack).Reason);
    }

    /// <summary>
    /// ⚠️ **`axis: null` 必须回 `rejected`，不能抛异常。**
    ///
    /// <para>线上那条路是可达的：`WireJson.Options` **没有**开
    /// `RespectNullableAnnotations`（`WireJson.cs:25-32`），所以
    /// <c>{"kind":"session.setAxis","axis":null}</c> 能一路落到
    /// `Axes.IsKnown(null)` ⇒ `Dictionary.TryGetValue(null)` ⇒ **`ArgumentNullException`**。
    /// 而控制面的接收循环是**兜底 catch**（宽泛 `catch (Exception)`），
    /// 所以连接不会死——但用户看到的是「帧形状不对」，而真正的问题是**轴名没给**。</para>
    ///
    /// <para>⇒ 修在 `Axes` 的三个入口（它们是这个契约的拥有者），**不是**在这里加空判断：
    /// `ValuesOf` 声明的是「未知 → null」，而 **null 就是未知**。</para>
    /// </summary>
    [Fact]
    public async Task SetAxis_WithANullAxis_IsRejected_NotACrash()
    {
        var (_, d, _, session) = Harness();

        var r = await d.DispatchAsync(
            new CoreCommand.SessionSetAxis(session.SessionId, null!, "dev"), "a1", false, CancellationToken.None);

        Assert.Contains("没有", Assert.IsType<Ack.Rejected>(r.Ack).Reason);
    }

    /// <summary>⚠️ 轴是状态，**必须落盘**——否则重启即丢，而用户以为设过了。</summary>
    [Fact]
    public async Task SetAxis_PersistsToDisk()
    {
        var (host, d, _, session) = Harness();
        await d.DispatchAsync(new CoreCommand.SessionSetAxis(session.SessionId, "verify", "strict"), "a1", false, CancellationToken.None);

        var reloaded = SessionHandle.Load(host.Store, session.ProjectKey, session.SessionId);
        Assert.Equal("strict", reloaded.State.Axes!["verify"]);
    }

    /// <summary>
    /// ⚠️ **值大小写不敏感**（`Axes.TryParse` 刻意如此），但**存的是表里的规范形**。
    /// 存原样的话，`Prod` 与 `prod` 会同时存在于不同会话，而头的下拉是按规范值渲染的
    /// ⇒ 那个选项显示不出来，用户看到「设了但没生效」。
    /// </summary>
    [Fact]
    public async Task SetAxis_NormalizesTheCase()
    {
        var (_, d, _, session) = Harness();

        await d.DispatchAsync(new CoreCommand.SessionSetAxis(session.SessionId, "safety", "Prod"), "a1", false, CancellationToken.None);

        Assert.Equal("prod", session.State.Axes!["safety"]);
    }

    /// <summary>
    /// ⚠️ **这条才是「五轴真的接上了」的证据**——上面那些只证明「值存对了」。
    ///
    /// <para>背景：`Stepper` 原先把 effort 读自 `Spec.Model.Effort`（**建会话时冻结的快照**），
    /// 而轴是**活**的。两者分叉时，用户在头上拨到 `max`、头显示 `max`、
    /// 而**请求里发的还是建会话时的值**——「设了没用」，且无从发现。
    /// 这条测试走完整条路：**改轴 → 发消息 → 检查适配器收到的请求**。</para>
    ///
    /// <para>⚠️ 断言的是**适配器实际收到的 `ModelRequest.Effort`**，不是 `handle.State.Axes`——
    /// 后者在修复前后**都是 `max`**，对这条缺陷没有区分力。</para>
    /// </summary>
    [Fact]
    public async Task SetAxis_Effort_ActuallyReachesTheModelRequest()
    {
        var host = new CoreHost(_root, _work);
        host.Providers.Save(new ProviderDefinition
        {
            Id = "p", Protocol = Protocols.ClaudeMessages, ApiKey = "k",
            DefaultModel = "m", Modality = "vision",
        });
        var adapter = new Fakes.FakeAdapter().Reply("ok");
        var d = new CommandDispatcher(host, new EventBus(), h => new Stepper(host.Store, adapter));

        var created = await d.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
        var sid = ((Ack.Accepted)created.Ack).SessionId!;

        await d.DispatchAsync(new CoreCommand.SessionSetAxis(sid, "effort", "max"), "a1", false, CancellationToken.None);
        await d.DispatchAsync(
            new CoreCommand.SessionSend(sid, [new Block.TextBlock("hi")]), "a2", true, CancellationToken.None);

        Assert.NotEmpty(adapter.Calls);
        Assert.Equal("max", adapter.Calls[^1].Effort);
    }
}
```

- [ ] **Step 2: 跑测试确认它失败**

Run: `dotnet test --filter "FullyQualifiedName~AxisCommandTests" -v q`
Expected: 编译失败，`CoreCommand.SessionSetAxis` / `WireEvent.AxisChanged` 不存在

- [ ] **Step 3: 写实现**

`CoreCommand.cs` —— 加在 `SessionInterrupt` 之后：

```csharp
    /// <summary>
    /// 设置一条行为控制轴（`01` §3.5）。
    /// ⚠️ 取值校验在 dispatcher（**线上也要吃**），控制台只做文本拆词。
    /// </summary>
    public sealed record SessionSetAxis(string SessionId, string Axis, string Value) : CoreCommand;
```

`WireEvent.cs` —— 加注册与 record：

```csharp
[JsonDerivedType(typeof(AxisChanged), "axis.changed")]
```

```csharp
    /// <summary>
    /// 一条轴变了（`03` §4 / `01` §3.5）。
    /// ⚠️ **只在真的迁移时发**——重复设同一个值是 no-op，不发事件。
    /// 头据此更新那个下拉；若是 `safety` 或 `permission`，还要重算跨轴告警。
    /// </summary>
    public sealed record AxisChanged(string SessionId, string Axis, string Value) : WireEvent;
```

`CommandDispatcher.cs` —— 加 case 与处理器：

```csharp
                case CoreCommand.SessionSetAxis x:
                    return HandleSessionSetAxis(x, commandId);
```

```csharp
    /// <summary>
    /// 设置一条轴。⚠️ 三个拒绝理由的**顺序**是刻意的：会话 → 轴 → 值。
    /// 反过来的话，「会话不存在 + 轴名也写错」会报「轴不认识」，把用户引向错的方向。
    /// </summary>
    private DispatchResult HandleSessionSetAxis(CoreCommand.SessionSetAxis x, string id)
    {        if (!TryFind(x.SessionId, out var handle, out var rejection)) return rejection!;

        if (!Axes.IsKnown(x.Axis))
            return Reject(id, $"没有 '{x.Axis}' 这条轴。可用的：{string.Join("|", Axes.Names)}");

        // ⚠️ 合法值清单**从表里拼**（G8）——手抄一份会在加档时静默过期，
        // 而这条错误信息是用户唯一的线索。
        if (!Axes.TryParse(x.Axis, x.Value, out var canonical))
            return Reject(id, $"'{x.Axis}' 不取 '{x.Value}'。可用的：{Axes.LegalValuesText(x.Axis)}");

        // ⚠️ 相同值不是迁移 ⇒ 不发事件（`01` §5）。见测试里的理由。
        if (handle.State.Axes!.TryGetValue(x.Axis, out var current) && current == canonical)
            return new DispatchResult(new Ack.Accepted { CommandId = id });

        var next = new Dictionary<string, string>(handle.State.Axes!, StringComparer.Ordinal) { [x.Axis] = canonical };
        handle.State = handle.State with { Axes = next };
        handle.SaveAxes(_host.Store);

        _bus.Publish(EventBus.SessionTopic(handle.SessionId),
            new WireEvent.AxisChanged(handle.SessionId, x.Axis, canonical));

        return new DispatchResult(new Ack.Accepted { CommandId = id });
    }
```

⚠️ **上面那句 `TryFind` 是执行中改的，原计划写的是 `_host.TryGetActivated`——那是控制器的错。**
`TryFind`（`CommandDispatcher.cs:393`）先查激活表、**miss 才扫项目目录回落到磁盘并 `Activate`**；
而 `_host.TryGetActivated` 只看激活表。⇒ 原写法会让**盘上有、但还没激活**的会话
被回一句「会话不存在」——**假话**：它存在，只是没在内存里。

⚠️ 其它**所有** handler（`SessionActivate` / `SessionSend` / `SessionStart` / `SessionInterrupt`）
都走 `TryFind`。本任务是唯一例外的那个——而实现者**照 brief 做了并主动报了出来**。
⇒ 纪律：**新 handler 一律走 `TryFind`**；要绕过它得有理由，并写进注释。

⚠️ **为什么这个错值得记**：它不会让任何既有测试变红（测试里会话都已经激活了），
只会在一台**重启过的核心**上、用户对着控制台敲 `axis set` 时，回一句错的话。

⚠️ `handle.State.Axes` 那个 `!`：`Load` / `Create` 之后保证非空（T2 的约定）。
`SessionState` 是 **record**，`with` 给它换一份副本；而 `SessionHandle` 是**可变的活对象**，
宿主的激活表里存的是**它**的引用，所以 `handle.State` 这个**属性**被赋新值，**不是**换 handle 实例。
⇒ 表里那份仍然指向同一个 handle，`State` 换成了带新轴的副本。**状态不分叉。**

⚠️ 反过来做（`handle = handle with { … }` 或 `new SessionHandle { … }`）就会分叉：
激活表里那份指向**旧实例**，而 dispatcher 手里是新的——两边各改各的。
（与 `Repl.Current` 的 setter 那条注释同源的顾虑。）

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test --filter "FullyQualifiedName~AxisCommandTests" -v q`
Expected: 通过 9，失败 0

- [ ] **Step 5: 提交**

```bash
git add src/Lilara.Core/Hosting/ src/Lilara.Core/Session/Axes.cs src/Lilara.Core/Control/WireEvent.cs tests/Lilara.Core.Tests/
git commit -m "feat(core): session.setAxis（迁移才发 axis.changed / 非法值列合法值 / 拒绝顺序会话→轴→值 / 落盘）"
```

---

### Task 4: 快照带轴 + `GET /axes` —— 头的初始值

**Files:**
- Modify: `src/Lilara.Core/Session/Axes.cs`（补中文标签 / `InContext` / 告警两端）
- Modify: `src/Lilara.Core/Control/ControlServer.cs`（快照 + 新增 `GET /axes`）
- Test: `tests/Lilara.Core.Tests/ControlServerTests.cs`（追加）· `tests/Lilara.Core.Tests/AxisTests.cs`（追加）

**Interfaces:**
- Consumes: `SessionHandle.State.Axes`（T2）· `Axes`（T1）
- Produces:
  - `GET /sessions/{id}/state` 新增 `axes`（对象）与三元素 `slots`（`system` / `premier` / `floating`，块数组）
  - **`GET /axes`** → `{ axes: [{ name, label, values, inContext }], alertSafety, alertFrom }`
  - `Axes.LabelOf(string axis)` · `Axes.InContext(string axis)` · `Axes.AlertSafety` · `Axes.AlertFrom`

> ### ⚠️ `GET /axes` 是**必须的**，不是锦上添花（评审发现，控制器核准）
>
> `Axes.cs:8` 的类头写着「任何别处（**头的下拉**、控制台的解析、错误信息）都**从这张表读**，
> 不许手抄」，而**计划 T9 的头里是一份手抄的 `AXES` 常量**（含手抄的 `effort: true` 标记 = 又一份副本）。
> ⇒ **计划违反了它自己的 G8。**
>
> 加这个端点，头一个字面量都不留：加一条轴、改一个档，只改核心的 `Axes.Table` 一处。
>
> ⚠️ **中文标签也归核心**（`05` §3：渲染归核心，头只显示）——
> 而下拉的 `主动性` / `安全` 这些**就是**渲染。`01` §3.5 的表格里有中文名，
> 但 `Axes.Table` 现在只有英文档名 ⇒ 本任务把中文名**加进 `Axes`**（那是唯一真相来源）。
>
> ⚠️ **告警的两端也交出去**（`alertSafety` = 哪个安全档代表生产，`alertFrom` = 权限从哪一档算「授权及以上」）。
> 头里写字面量的话，轴里改一个档名就会让告警**静默失效**——用户以为它在守，而它再也不响。
>
> ⚠️ **头的告警判据要按「档位序」**，不是「是不是这两个值之一」：权限是**单调阶梯**
> （`01` §3.5：0 禁用 … 5 信任），所以正确的写法是「`values.indexOf(当前) >= values.indexOf(alertFrom)`」。
> 枚举 `{authorized, trusted}` 恰好等于今天的结果，但权限轴加第 6 档时枚举会静默漏掉它，序数写法自动覆盖。

- [ ] **Step 1: 写失败的测试**

```csharp
/// <summary>
/// ⚠️ **初始轴值必须来自快照**（`05` §11 #4）：`axis.changed` 只在订阅**之后**才有，
/// 头刚打开页面时没有任何事件可等。少了这一项，五个下拉全是空的，
/// 而用户看到的「空」与「默认值」在视觉上无法区分。
/// </summary>
[Fact]
public async Task State_CarriesTheFiveAxes()
{
    var created = await _dispatcher.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
    var sid = ((Ack.Accepted)created.Ack).SessionId!;

    var state = await Client.GetStringAsync(Http($"/sessions/{sid}/state"));
    using var doc = JsonDocument.Parse(state);
    var axes = doc.RootElement.GetProperty("axes");

    foreach (var axis in Axes.Names)
        Assert.True(axes.TryGetProperty(axis, out _), $"快照少了轴 '{axis}'");
    Assert.Equal("dev", axes.GetProperty("safety").GetString());
}

/// <summary>
/// ⚠️ **三个槽位**（`01` §3.1 头两个 + §3.2 尾槽位「浮动」）。
/// `05` §7.1 的「空的也要显示」要求它**恒在**且是**数组**——
/// 省略字段会让头分不清「空」与「没有这个概念」（既有注释已写明这条）。
/// </summary>
[Fact]
public async Task State_CarriesThreeSlots_FloatingIncluded()
{
    var created = await _dispatcher.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
    var sid = ((Ack.Accepted)created.Ack).SessionId!;

    using var doc = JsonDocument.Parse(await Client.GetStringAsync(Http($"/sessions/{sid}/state")));
    var slots = doc.RootElement.GetProperty("slots");

    Assert.Equal(JsonValueKind.Array, slots.GetProperty("system").ValueKind);
    Assert.Equal(JsonValueKind.Array, slots.GetProperty("premier").ValueKind);
    Assert.Equal(JsonValueKind.Array, slots.GetProperty("floating").ValueKind);
    Assert.Empty(slots.GetProperty("floating").EnumerateArray());
}

/// <summary>
/// ⚠️ **`GET /axes` 是头唯一的轴来源**（G8：头不许手抄）。
/// 少了它，头里那份手抄的 `AXES` 常量就是第二份契约——而 `Axes.cs:8` 明说「不许手抄」。
///
/// <para>⚠️ 这条测试**同时钉住「中文标签归核心」**：`05` §3「渲染归核心，头只显示」——
/// 而下拉的 `主动性` / `安全` 就是渲染。头拿到英文档名自己是画不出中文的。</para>
/// </summary>
[Fact]
public async Task Axes_Endpoint_HandsOutEverythingTheHeadNeeds()
{
    using var doc = JsonDocument.Parse(await Client.GetStringAsync(Http("/axes")));
    var axes = doc.RootElement.GetProperty("axes").EnumerateArray().ToList();

    Assert.Equal(Axes.Names.Count, axes.Count);
    // ⚠️ 顺序**就是** `Axes.Names` 的顺序——它决定头的渲染顺序（与 T1 那条同名约定一致）。
    Assert.Equal(Axes.Names, axes.Select(a => a.GetProperty("name").GetString()!).ToList());

    foreach (var a in axes)
    {
        Assert.False(string.IsNullOrWhiteSpace(a.GetProperty("label").GetString()),
            $"轴 {a.GetProperty("name").GetString()} 没有标签——头画不出中文名");
        Assert.NotEmpty(a.GetProperty("values").EnumerateArray());
    }

    // ⚠️ **`inContext` 必须是真值而不是漏字段**：`effort` 是 false（`01` §3.5：不进上下文），
    // 头据此把它画灰。少了这个字段，头分不清「不进上下文」与「核心没这个信息」。
    var effort = axes.Single(a => a.GetProperty("name").GetString() == "effort");
    Assert.False(effort.GetProperty("inContext").GetBoolean());

    // ⚠️ **告警的两端也归核心**：头里写字面量的话，轴里改一个档名就会让告警**静默失效**。
    Assert.Equal("prod", doc.RootElement.GetProperty("alertSafety").GetString());
    Assert.Equal("authorized", doc.RootElement.GetProperty("alertFrom").GetString());
}
```

⚠️ **`Axes` 要新增的东西**（这些是上面那条测试的前提，也是「标签归核心」的落点）：

```csharp
    /// <summary>
    /// 轴的中文名（`01` §3.5 的表格）。⚠️ **它归核心**（`05` §3：渲染归核心，头只显示）——
    /// 头的下拉要画 `主动性` / `安全`，而它拿到的只有英文档名。
    /// ⚠️ **原来这里写的是「与 `Names` 同序、同一个来源」——那句是假的**，见下面那段。
    /// </summary>
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        [Initiative] = "主动性",
        [Permission] = "权限",
        [Safety] = "安全",
        [Verify] = "验证强度",
        [Effort] = "思考强度",
    };

    public static string LabelOf(string axis) => Labels.GetValueOrDefault(axis, axis);

    /// <summary>
    /// 这条轴的取值**进不进上下文**（`01` §3.5）。
    /// ⚠️ 只有 `effort` 是 `false`——它是**纯请求参数**，模型不必被告知自己的推理预算。
    /// 头据此把它画灰；写进提示词才是错的（那句话是全系统每步都付全价的槽位）。
    /// </summary>
    public static bool InContext(string axis) => axis != Effort;

    /// <summary>安全轴里代表「生产」的档名（`01` §3.5 的跨轴告警的一端）。</summary>
    public const string AlertSafety = "prod";

    /// <summary>权限轴里「授权及以上」的**起始档**（`01` §3.5 的跨轴告警的另一端）。</summary>
    public const string AlertFrom = "authorized";
```

> ### ⚠️ G10 第三次：`Names` / `Labels` / `Table` / `DefaultTable` 是**四份平行容器**
>
> **执行时发现（T4 实现者的顾虑 2），控制器核准，作为 T4 审阅的修复项。**
>
> 上面那段注释原本写「`Labels` 与 `Names` **同序**，且**同一个来源**」——**那句是假的**：
> 代码里 `Names` 是一个 `IReadOnlyList`、`Labels` 是一个 `Dictionary`，各写一份字面量。
> ⚠️ 而 `Dictionary` 的**枚举序在 .NET 下未定义**（`01` §9.1 原话：
> 「一次 `Remove` 后新 `Add` 会复用空槽跑到前面」）⇒ 「标签跟着档名走」只在**碰巧**时成立。
>
> **正解不是加一条顺序测试**（测试只能发现漂移、不能阻止），**是让盘上只有一个来源**：
>
> ```csharp
>     /// <summary>
>     /// 轴的**全部**静态事实，按渲染顺序排（`01` §3.5 表格的顺序）。
>     /// ⚠️ **单一张表**：轴名 / 中文名 / 合法值 / 默认值 / 进不进上下文**都从这里读**。
>     /// 拆成几个平行容器就会漂——`Names` 有序而 `Dictionary` 枚举序未定义（`01` §9.1）。
>     /// </summary>
>     private sealed record Spec(
>         string Name, string Label, IReadOnlyList<string> Values, string Default, bool InContext);
>
>     private static readonly IReadOnlyList<Spec> Table =
>     [
>         new(Initiative, "主动性", Array.AsReadOnly<string>(["step", "standard", "goal"]),
>             "step", InContext: true),
>         new(Permission, "权限",
>             Array.AsReadOnly<string>(["off", "readonly", "investigate", "standard", "authorized", "trusted"]),
>             "standard", InContext: true),
>         new(Safety, "安全", Array.AsReadOnly<string>(["dev", "staging", "prod"]),
>             "dev", InContext: true),
>         new(Verify, "验证强度", Array.AsReadOnly<string>(["none", "self", "strict"]),
>             "self", InContext: true),
>         // ⚠️ `effort` 是**唯一** `InContext = false` 的（`01` §3.5：纯请求参数，不进上下文）。
>         new(Effort, "思考强度", Array.AsReadOnly<string>(["low", "medium", "high", "xhigh", "max"]),
>             "high", InContext: false),
>     ];
>
>     private static Spec? Find(string? axis) =>
>         axis is null ? null : Table.FirstOrDefault(s => s.Name == axis);
>
>     public static IReadOnlyList<string> Names => Table.Select(s => s.Name).ToList();
>     public static string LabelOf(string axis) => Find(axis)?.Label ?? axis;
>     public static bool InContext(string axis) => Find(axis)?.InContext ?? true;
>     public static IReadOnlyList<string>? ValuesOf(string? axis) => Find(axis)?.Values;
>     public static bool IsKnown(string? axis) => Find(axis) is not null;
>     public static IReadOnlyDictionary<string, string> Defaults() =>
>         Table.ToDictionary(s => s.Name, s => s.Default, StringComparer.Ordinal);
> ```
>
> ⚠️ **`TryParse` 改成从 `Find(axis)?.Values` 取**，其余不动。
> ⚠️ **`ValuesOf` 仍返回 `Array.AsReadOnly` 的视图**（T1 那条修复的成果）——别退回裸数组。
> ⚠️ **`Names` 变成派生的** ⇒ 顺序**自动**跟着走，不再需要「同序」这个约定。
> ⚠️ **T1 的 11 条测试一条都不许改断言值**——它们是这次重构的安全网。
> ⚠️ 代价：T4 的改动比原计划大。**值**——它换掉的是本项目已经付过四次代价的那个模式。

⚠️ 再加一条 **`Labels` 与 `Names` 同键** 的测试（两份列表最容易漂的地方）：

```csharp
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
```

- [ ] **Step 2: 跑测试确认它失败**

Run: `dotnet test --filter "FullyQualifiedName~State_Carries" -v q`
Expected: 两条都红——`axes` 键不存在；`slots.floating` 不存在

- [ ] **Step 3: 写实现**

`ControlServer.cs` 的 `/sessions/{id}/state` 处理器，把返回体那段换掉：

```csharp
            return Results.Json(
                new
                {
                    handle.SessionId,
                    State = SessionQueries.StateOf(host, handle),

                    // ⚠️ **`HighWater` 不能在这轮改动里掉队。**
                    // 执行时控制器写的替换块**把它删掉了**——而它上面那十几行注释
                    // （`ControlServer.cs` 里那段「先取高水位再取内容」）正是它存在的理由：
                    // 头拿它去 `subscribe(since:)`，「拉快照 → 订阅」之间落的事件才不漏不重。
                    // ⇒ 少了它，整条原子性就没了，而且**只在并发下看得出来**。
                    // ⚠️ 更要紧的是：弄红它的是既有的 `History_And_State_CarryTheSeqHighWaterMark`，
                    // 而**那条不在下面 Step 4 的筛选里** ⇒ 只跑筛选取不出这个错。
                    // （实现者保留了它并加注释——这是它自己判断的，不是我 brief 里的。）
                    HighWater = highWater,

                    // ⚠️ **轴必须进快照**（`05` §11 #4）：`axis.changed` 只在订阅之后才有，
                    // 头刚打开页面时无事件可等。少了它，五个下拉全是空的——
                    // 而「空」与「默认值」在视觉上分不出来。
                    // ⚠️ 轴只有一份，住在 `State` 里（`SessionHandle` 上不该有第二份——两份会分叉）。
                    // `Load` / `Create` 之后它保证非空，见 `SessionHandle.CompleteAxes`。
                    Axes = handle.State.Axes,
                    // ⚠️ **三个槽**：`01` §3.1 的头两个（系统 / 首要）+ §3.2 的**尾槽位**（浮动）。
                    // 第一版只有两个——它把 `01` 的「头槽位」当成全部，漏了尾槽位；
                    // 而尾槽位正是五轴显示的地方（§3.2「策略：主动性 · 权限 · 安全 · 验证强度」）。
                    // ⇒ 漏了它，义务 4 就没有落点。
                    //
                    // ⚠️ `slots` 是**渲染产物**（`05` §3.1：核心渲染、头显示）。
                    // 核心现在还没有装配器（`04` §1.3：裸核心连系统提示词都没有），
                    // 所以三个都恒为空——但 `05` §7.1 要的正是「**空的也要显示**」：
                    // 用户要能看见「它真的是空的」，而不是以为头坏了。
                    // ⇒ 如实交出空数组，**不省略字段**。
                    //
                    // ⚠️ **浮动槽的内容由头拼**：它的字段（态势 / 策略 / 其他）核心已经
                    // 用 `Axes` 如实交出来了，而「怎么排」是**显示**（`05` §3 归头）。
                    // 换个头可以排成别样——那是 §1.1 允许的。
                    Slots = new
                    {
                        System = Array.Empty<object>(),
                        Premier = Array.Empty<object>(),
                        Floating = Array.Empty<object>(),
                    },
                },
                WireJson.Options);
```

以及 **`GET /axes`**（放在 `MapSnapshots` 里，与其它快照端点同处）：

```csharp
        // ⚠️ **头唯一的轴来源**（G8：头不许手抄轴表）。
        // ⚠️ **标签也交出去**（`05` §3：渲染归核心）——头拿到英文档名画不出中文名。
        // ⚠️ **告警的两端也交出去**：头里写字面量的话，轴里改一个档名就让告警**静默失效**。
        app.MapGet("/axes", () => Results.Json(new
        {
            Axes = Session.Axes.Names.Select(n => new
            {
                Name = n,
                Label = Session.Axes.LabelOf(n),
                Values = Session.Axes.ValuesOf(n),
                InContext = Session.Axes.InContext(n),
            }),
            AlertSafety = Session.Axes.AlertSafety,
            AlertFrom = Session.Axes.AlertFrom,
        }, WireJson.Options));
```

⚠️ `Axes.Names` 的顺序**就是**渲染顺序（T1 定的约定），这里**不要**排序。

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test -v q`（**全量，不要只跑 filter**）
Expected: 全绿。

⚠️ **这一步不要只跑 `--filter`。** 本任务改的是 `/state` 的返回体，
而**既有**的 `History_And_State_CarryTheSeqHighWaterMark` 也在测它——
用一个只匹配新测试名的 filter，**取不出「我把既有字段删了」这个错**。
（执行时控制器写的替换块真的漏了 `HighWater`，正是靠全量才该被发现。）

⚠️ **本任务新增的 4 条**可以单跑确认：
`--filter "FullyQualifiedName~State_Carries|FullyQualifiedName~Axes_Endpoint|FullyQualifiedName~Labels_Cover"` → 通过 4。

- [ ] **Step 5: 提交**

```bash
git add src/Lilara.Core/Session/Axes.cs src/Lilara.Core/Control/ControlServer.cs tests/Lilara.Core.Tests/
git commit -m "feat(core): 快照带五轴与三个槽；GET /axes（头不再手抄轴表，标签与告警两端也归核心）"
```

---

### Task 4b: 控制面的 `ParseCommand` 接上 `session.setAxis`（**评审发现的计划级空洞**）

**Files:**
- Modify: `src/Lilara.Core/Control/ControlServer.cs`（`ParseCommand` 的 switch）
- Test: `tests/Lilara.Core.Tests/ControlServerTests.cs`（追加）

**Interfaces:**
- Consumes: `CoreCommand.SessionSetAxis`（T3）
- Produces: 线上可用的 `session.setAxis` 帧

> ### ⚠️ 这个任务原本**不存在**——`ParseCommand` 里没有它，而**全计划 grep `ParseCommand` 零命中**
>
> T3 的审阅者发现：`ControlServer.ParseCommand`（`ControlServer.cs:430`）的 switch 有
> `session.create` / `activate` / `interrupt` / `start` / `send` / `provider.upsert`，
> **没有 `session.setAxis`** ⇒ 落到 `_ => throw new ArgumentException($"认不出的命令 '{kind}'")`
> ⇒ 被包成「命令形状不对」。
>
> **后果**：① 头（T9）发的第一条轴命令**永远失败**——用户拨下拉，看起来没反应；
> ② `Axes.cs:131` 那句「线上帧可达（`WireJson.Options` 没开 `RespectNullableAnnotations`）」
> **是假的**——它验了两端（`WireJson` 与兜底 catch），**漏了中间那一环**。
>
> ⚠️ 这正是本项目反复付代价的那类：**因果链只验了看着像的两端**。
> 而这一次它同时暴露了一个**分发机制**的问题：我的计划把命令分成了 T3（核心侧语义）
> 与 T5（控制台解析），**而线上解析是第三条线，没人认领**。

- [ ] **Step 1: 写失败的测试**

```csharp
/// <summary>
/// ⚠️ **线上的 `session.setAxis` 必须真的能到 dispatcher。**
/// T3 只在**类型**层加了命令，`ParseCommand` 的 switch 没跟上 ⇒ 帧落到 `_ => throw`
/// ⇒ 头（`05` §7.4 的轴下拉）发出来的东西全被回「命令形状不对」。
/// ⚠️ 这条测试走**真的 WS**，不是直接调 dispatcher——后者会在实现缺失时照样绿。
/// </summary>
[Fact]
public async Task WebSocket_SetAxis_ReachesTheDispatcher()
{
    var created = await _dispatcher.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
    var sid = ((Ack.Accepted)created.Ack).SessionId!;

    using var ws = new ClientWebSocket();
    await ws.ConnectAsync(new Uri(Ws()), CancellationToken.None);
    await SendAsync(ws, "{\"kind\":\"command\",\"id\":\"a1\",\"payload\":{\"kind\":\"session.setAxis\","
        + "\"sessionId\":\"" + sid + "\",\"axis\":\"safety\",\"value\":\"prod\"}}");

    var ack = await ReceiveAsync(ws);
    Assert.Equal("ack", ack.GetProperty("kind").GetString());
    Assert.True(ack.GetProperty("accepted").GetBoolean(),
        "认不出这条命令——`ParseCommand` 的 switch 漏了它：" + ack.GetProperty("reason").GetString());

    // ⚠️ 判据是「**命令真的生效了**」，不只是「回执说 accepted」——
    // 回执可以说 accepted 而语义根本没跑（那正是「形状对但接不上」的形态）。
    var state = await Client.GetStringAsync(Http($"/sessions/{sid}/state"));
    using var doc = JsonDocument.Parse(state);
    Assert.Equal("prod", doc.RootElement.GetProperty("axes").GetProperty("safety").GetString());
}
```

- [ ] **Step 2: 跑测试确认它失败**

Run: `dotnet test --filter "FullyQualifiedName~WebSocket_SetAxis" -v q`
Expected: 红——`accepted` 是 `false`，理由里是「认不出的命令 'session.setAxis'」

- [ ] **Step 3: 写实现**

`ControlServer.ParseCommand` 的 switch 里加一条（放在 `session.send` 之后）：

```csharp
            // ⚠️ **线上解析是命令的第三条线**（核心语义在 dispatcher、控制台解析在 `CoreCommandParser`）——
            // 少一条这条命令就只在别的两条线上存在，而头发的帧会静默落到 `_ => throw`。
            // T3 加命令时漏了这里，而**计划里没有任何任务认领它**（全计划 grep `ParseCommand` 零命中）。
            "session.setAxis" => new CoreCommand.SessionSetAxis(
                payload.GetProperty("sessionId").GetString()!,
                payload.GetProperty("axis").GetString()!,
                payload.GetProperty("value").GetString()!),
```

⚠️ **顺手清掉 `Axes.cs` 里那句假话**：把「线上帧可达」改成
「线上**待接**（`ParseCommand` 尚无此命令；这里守的是契约，不是今天的路径）」——
或者在本任务把它接上之后，改成可证的形状（命令已接上，注释也照样要能核）。
⚠️ **同时改 `tests/Lilara.Core.Tests/AxisCommandTests.cs` 与 `AxisTests.cs` 里那两处同一句。**

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test -v q`（**全量**，理由同 T4）
Expected: 全绿。

- [ ] **Step 5: 提交**

```bash
git add src/Lilara.Core/Control/ControlServer.cs src/Lilara.Core/Session/Axes.cs tests/Lilara.Core.Tests/
git commit -m "fix(core): 控制面 ParseCommand 接上 session.setAxis（原来线上不可达——头发的轴命令全被回「认不出」）"
```

---

### Task 5: 控制台的 `axis set`

**Files:**
- Modify: `src/Lilara.Core/Console/CoreCommandParser.cs`
- Modify: `src/Lilara.Core/Console/Repl.cs`（回执格式 switch **+ `PrintStatus`，见「执行中补的缺陷」**）
- Test: `tests/Lilara.Core.Tests/ParserTests.cs`（**新建**——解析器测试不该寄生在 `ReplTests` 里，
  它们测的是 `CoreCommandParser` 而不是 `Repl`）· `tests/Lilara.Core.Tests/ReplTests.cs`（追加
  ——**显示**是 `Repl` 的事，那条 effort 测试就该落在这里）

⚠️ **启动横幅那行不在本任务**——它在 **T8**（T8 本来就要改 `Program.cs:108` 加 `claim`，
两行一起加）。执行时发现它在 T5 的 Files 表里**当了个孤儿**：brief 里零命中（零命中 ⇒ 实现者根本看不到它）。

**Interfaces:**
- Consumes: `CoreCommand.SessionSetAxis`（T3）
- Produces: 控制台子命令 `axis set <sessionId> <axis> <value>`

- [ ] **Step 1: 写失败的测试**

```csharp
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
/// **单独的等值判断**接住，见下面的实现。</para>
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
```

- [ ] **Step 2: 跑测试确认它失败**

Run: `dotnet test --filter "FullyQualifiedName~AxisSet_" -v q`
Expected: `AxisSet_ParsesThreeWords` 红（返回的是 `(null, null)` ⇒ 被当成消息）；
`AxisSet_WithTooFewWords` 里 `"axis set s1 safety"` 那条也红，另外两条已经是绿的（兜底恰好接住）

- [ ] **Step 3: 写实现**

`CoreCommandParser.cs` —— 在 `Parse` 里、`provider set` 那行之前插入。

⚠️ **两句都要有**：前缀判据（带空格）接住「词太多/正好」的形态，
等值判据接住「正好就是 `axis set`」——**后者不带尾随空格，进不了前缀那一条**。

```csharp
        // ⚠️ **前缀判据必须带尾随空格**（`"axis set "` 而不是 `"axis set"`）：
        // 后者会把 `axis settings` 也吃进来。既有 `session create` 就没带空格，
        // 于是 `session createX` 会被当成建会话命令——**本计划不跟着犯这个错**。
        if (trimmed.StartsWith("axis set ", StringComparison.Ordinal))
            return ParseAxisSet(trimmed);

        // ⚠️ 这一句接的是「光一个 `axis set`」——它**不带尾随空格**，
        // 所以上面那条前缀判据够不着它，会掉到最后的兜底 ⇒ 被当成消息发给会话。
        // 用户以为在设轴，实际发了一行字出去。
        if (trimmed is "axis set" or "axis")
            return (null, "用法：axis set <会话 id> <轴> <值>");
```

```csharp
    /// <summary>
    /// `axis set &lt;sessionId&gt; &lt;axis&gt; &lt;value&gt;`
    /// ⚠️ **段数必须正好五段**（`axis` + `set` + 会话 + 轴 + 值）。少了不能落回「当成消息」——
    /// 用户以为设了轴，实际是把这行字发给了会话。那是静默接管。
    ///
    /// <para>⚠️ **原来是四段、取 `parts[1..3]`——那是控制器的错**（执行时实现者核出）。
    /// `"axis set s1 safety prod".Split(' ')` 是 **5** 段（`axis` 与 `set` 各一段），
    /// 所以按四段写，**计划里自己的那条测试输入永远绿不了**。
    /// ⇒ 标尺是**测试输入**，不是我想的那个数字。</para>
    /// </summary>
    private static (CoreCommand?, string?) ParseAxisSet(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5)
            return (null, "用法：axis set <会话 id> <轴> <值>   （轴：" + string.Join("|", Axes.Names)
                + "；取值见 `status`）");

        return (new CoreCommand.SessionSetAxis(parts[2], parts[3], parts[4]), null);
    }
```

`Repl.cs` —— 在 `DispatchAsync` 的回执格式 switch 里加一条：

```csharp
                // ⚠️ 轴改了要**立刻重印策略那一行**——它就在 `status` 里，
                // 而用户在控制台改完轴最想知道的就是「现在是什么」。
                case CoreCommand.SessionSetAxis set:
                    _out.WriteLine($"✓ {set.SessionId} 的轴已更新");
                    // ⚠️ **只在改的就是当前会话时才重印**，否则印的是**别的**会话的状态（张冠李戴）。
                    // ⚠️ **用 `set.SessionId` 而不是 `Current!`**（控制器的错，执行时核出）：
                    // `axis set` 可以指向任意已有会话，而 `Current` 可以是 null
                    // ⇒ `Current!.SessionId` 会 **NRE**，而那个 `try` 只 catch 两种业务异常
                    // ⇒ **异常掀翻整个 REPL 循环**（常驻进程）。
                    if (Current is not null && Current.SessionId == set.SessionId) PrintStatus();
                    break;
```

`Repl.cs` 的 `PrintStatus` 里补一行轴：

```csharp
        _out.WriteLine("策略 " + string.Join(" · ",
            Lilara.Core.Session.Axes.Names.Select(n => $"{n}={Current.State.Axes!.GetValueOrDefault(n, "?")}")));
```

> ### ⚠️ 执行中补的缺陷（T2 的实现者发现，控制器核准）
>
> **`Repl.cs:205` 印的是 `Current.Spec.Model.Effort`——冻结的快照。**
> 轴一改，控制台的 `session status` 就会与**请求里实际发的** effort 分叉。
> 这是**同一个「设了没用」，换到了显示层**：用户改了轴、控制台说还是旧值，
> 或者（更坏）控制台说新值、而请求发的是旧的。
>
> ⇒ `PrintStatus` 里那行 `模型 {ModelId} · effort {Spec.Model.Effort}` 要改成
> **从轴读**：`effort {Current.State.Axes!["effort"]}`。
> ⚠️ 顺带把「模型」与「effort」分开——模型来自 spec（那是**不会变**的），
> effort 来自轴（**会变**）。两者现在写在同一个 `WriteLine` 里，读起来像同一个来源。
>
> ⚠️ **加一条测试**：改轴之后 `session status` 的输出里是**新值**。
> 断言**输出文本**而不是内部状态——内部状态在修复前后都一样，对这条缺陷没有区分力
> （与 T3 那条 `SetAxis_Effort_ActuallyReachesTheModelRequest` 同一个道理）。
>
> ```csharp
> /// <summary>
> /// ⚠️ **`session status` 印的必须是轴的当前值，不是 `Spec.Model.Effort`（冻结的快照）。**
> /// 与 T3 那条同一个病灶、换到显示层：用户改了轴，控制台说还是旧值。
> ///
> /// <para>⚠️ 判据是**输出文本**。断言 `handle.State.Axes["effort"]` 对这条缺陷**没有区分力**
> /// ——修复前后它都是新值，因为坏的只是「印哪一份」。</para>
> /// </summary>
> [Fact]
> public async Task Status_PrintsTheAxisEffort_NotTheFrozenSpecOne()
> {
>     var (repl, dispatcher, session) = NewHarness();   // 照 ReplTests 既有约定
>
>     // ⚠️ 先确认起点**不是** max，否则「印了新值」与「印了旧值」看不出区别。
>     // ⚠️ **必须先真敲一次 `status`**（控制器的错，执行时核出）：
>     // `StringWriter` 刚构造出来是**空的**，直接 `var before = _out.ToString()` 再断言
>     // ⇒ **无条件红**（那条断言测的是「缓冲区非空」，不是「基线是 high」）。
>     _out.GetStringBuilder().Clear();
>     await repl.ExecuteAsync("status", CancellationToken.None);
>     Assert.Contains("effort high", _out.ToString());
>
>     await dispatcher.DispatchAsync(
>         new CoreCommand.SessionSetAxis(session.SessionId, "effort", "max"), "a1", true, CancellationToken.None);
>     _out.GetStringBuilder().Clear();
>
>     await repl.ExecuteAsync("status", CancellationToken.None);
>
>     Assert.Contains("effort max", _out.ToString());
> }
> ```
>
> ⚠️ `NewHarness` 与 `_out` 照 `ReplTests` 既有约定取（它已经有 `StringWriter` 捕获输出）。
> ⚠️ **变异确认**：把 `PrintStatus` 改回读 `Spec.Model.Effort`，这条必须红。

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test --filter "FullyQualifiedName~AxisSet_|FullyQualifiedName~Status_PrintsTheAxis" -v q`
Expected: 通过 7，失败 0（Theory 的 6 个用例 + 那条 effort 显示）

- [ ] **Step 5: 提交**

```bash
git add src/Lilara.Core/Console/ tests/Lilara.Core.Tests/
git commit -m "feat(console): axis set（段数守卫：少了报错，不落回当成消息）+ status 印轴的 effort 而不是冻结的 spec"
```

⚠️ `Repl.cs` 的改动（回执格式 + `PrintStatus`）**也要进这个提交**——
它与解析器是同一件事的两半（解析出来得有地方印），分开提交会让中间那个 commit 编译不过。

---

### Task 6: 声明的真相来源与冲突判据

**Files:**
- Create: `src/Lilara.Core/Session/Claims.cs`
- Test: `tests/Lilara.Core.Tests/ClaimTests.cs`

**Interfaces:**
- Consumes: 无
- Produces: `Lilara.Core.Session.Claims`（静态类）—— `Normalize(string path, string workingDirectory) → string` · `Conflicts(string a, string b) → bool` · `FindConflict(IEnumerable<Claim> existing, string sessionId, string normalizedPath) → Claim?`；`record Claim(string SessionId, string ProjectKey, string Path, DateTimeOffset At)`

- [ ] **Step 1: 写失败的测试**

```csharp
using Lilara.Core.Session;

namespace Lilara.Core.Tests;

/// <summary>
/// 声明的冲突判据（`01` §12.1）。
/// ⚠️ 规则只有一句：**一个路径是另一个的前缀就是冲突**——但它有两个真实世界的陷阱
/// （Windows 大小写不敏感、`C:\a` 不该被 `C:\ab` 命中），两条都钉在下面。
/// </summary>
public class ClaimTests
{
    private const string Work = @"C:\repo";

    /// <summary>
    /// ⚠️ **断言必须写小写字面量。** 实测（`Path.GetFullPath(@"C:\repo\src\..\a.cs")` 在 Windows 上
    /// 返回**原样的大小写**、**不**规范化它——规范化只存在于 `Normalize` 自己的 `ToLowerInvariant`）：
    /// 写成 `Assert.Equal(@"C:\repo\src\a.cs", …)` 会与实现的 `ToLowerInvariant` 契约冲突，
    /// **全绿不可能同时成立**。落盘格式是规范化的，所以钉小写才是钉那份契约。
    /// </summary>
    [Fact]
    public void Normalize_MakesRelativePathsAbsoluteAgainstTheWorkingDirectory() =>
        Assert.Equal(@"c:\repo\src\a.cs", Claims.Normalize(@"src\a.cs", Work));

    /// <summary>⚠️ 大小写不敏感是**无条件**的（实现里是无条件 `ToLowerInvariant`），不是「只在 Windows 上」。</summary>
    [Fact]
    public void Normalize_IsCaseInsensitive_OnTheDriveAndThePath() =>
        Assert.Equal(Claims.Normalize(@"C:\Repo\Src\A.cs", Work), Claims.Normalize(@"c:\repo\src\a.cs", Work));

    /// <summary>
    /// ⚠️ **这条钉的其实是「输入永远经由 `GetFullPath`」。** 实测：Windows 上
    /// `GetFullPath` **自己**就把 `/` 归一成 `\`（`C:/repo/src` → `C:\repo\src`），
    /// `C:\repo\src/` → `C:\repo\src\`——所以实现里那行
    /// `Replace(AltDirectorySeparatorChar, DirectorySeparatorChar)` **是冗余的**，
    /// 这条测试对**那半句没有区分力**（删掉它这条照样绿）。
    /// 留着它是因为它钉的是**契约**（两种分隔符等价），而契约是对的。
    /// ⚠️ **要让它红得连 `Replace` 一起去掉**——实测过：只拿掉 `GetFullPath`
    /// （保留 `Replace`）这条**照样绿**，因为 `Replace` 仍把 `/` 统一成 `\`；
    /// 那时唯一红的是 `Normalize_ResolvesDotSegments`。**两处一起去才红。**
    /// </summary>
    [Fact]
    public void Normalize_TreatsBothSeparatorsTheSame()
    {
        Assert.Equal(Claims.Normalize(@"src/a.cs", Work), Claims.Normalize(@"src\a.cs", Work));
    }

    /// <summary>⚠️ 同样实测过：`GetFullPath` 顺手折叠 `.` / `..`，所以钉小写。</summary>
    [Fact]
    public void Normalize_ResolvesDotSegments() =>
        Assert.Equal(@"c:\repo\a.cs", Claims.Normalize(@"src\..\a.cs", Work));

    /// <summary>
    /// ⚠️ **末尾分隔符必须去掉**（`C:\repo\` 与 `C:\repo` 是同一处）。
    /// 实测 `GetFullPath` 在两种写法下**都保留**末尾分隔符
    /// （`C:\repo\src\` → `C:\repo\src\`；`C:\repo\src/` → `C:\repo\src\`）
    /// ——它只在遇到 `.` 段时才把它折掉（`C:\repo\src\.` → `C:\repo\src`）。
    /// 所以这条**有**区分力：删掉 `TrimEnd` 它就红——
    /// 且红的方式正是要害：`c:\repo\src\` 的前缀判据多一个字符，
    /// 于是 `src\` **永远命中不了** `src\a.cs`，冲突判据整体失效。
    /// </summary>
    [Fact]
    public void Normalize_DropsTheTrailingSeparator() =>
        Assert.Equal(Claims.Normalize(@"C:\repo\src", Work), Claims.Normalize(@"C:\repo\src\", Work));

    /// <summary>同一路径 ⇒ 冲突。</summary>
    [Fact]
    public void SamePath_Conflicts() =>
        Assert.True(Claims.Conflicts(@"C:\repo\src", @"C:\repo\src"));

    /// <summary>一个占父目录 ⇒ 冲突（`src` 覆盖 `src/a.cs`）。</summary>
    [Fact]
    public void ParentCoversChild_Conflicts()
    {
        Assert.True(Claims.Conflicts(@"C:\repo\src", @"C:\repo\src\a.cs"));
        Assert.True(Claims.Conflicts(@"C:\repo\src\a.cs", @"C:\repo\src"));
    }

    /// <summary>
    /// ⚠️ **这个陷阱值得单独一条**：朴素的前缀比较会让 `C:\repo\src` 命中 `C:\repo\src2`，
    /// 于是两个**毫不相干**的目录被判成冲突，声明功能从此没人敢用。
    /// 判据必须是「前缀 + 分隔符边界」。
    /// </summary>
    [Fact]
    public void SiblingWithASharedNamePrefix_DoesNotConflict()
    {
        Assert.False(Claims.Conflicts(@"C:\repo\src", @"C:\repo\src2"));
        Assert.False(Claims.Conflicts(@"C:\repo\a.cs", @"C:\repo\a.cs.bak"));
    }

    [Fact]
    public void UnrelatedPaths_DoNotConflict() =>
        Assert.False(Claims.Conflicts(@"C:\repo\src", @"C:\repo\tests"));

    /// <summary>
    /// ⚠️ **守卫上面那句 `TrimEnd` 的边界行为。** `GetFullPath(@"C:\")` 返回 `C:\`，去尾后是 `c:`
    /// ——一个**不是合法路径**的字符串，而它会出现在 `Normalize` 的输出里（声明 `C:\` 时）。
    /// 看上去像该"顺手修"的怪值，**其实修了就坏**：实测
    /// `IsUnder("c:", "c:\repo")` → True，而 `IsUnder("c:\", "c:\repo")` → False。
    /// 所以特例保留根的反斜杠会让**根路径与它下面的一切**都判不出冲突（静默失效）。
    /// 这条钉住那个方向，防止后来者把 `c:` 清理掉。
    /// </summary>
    [Fact]
    public void Normalize_RootPath_StillConflictsWithEverythingUnderIt()
    {
        var root = Claims.Normalize(@"C:\", Work);
        Assert.True(Claims.Conflicts(root, Claims.Normalize(@"C:\repo\a.cs", Work)));
        Assert.True(Claims.Conflicts(root, Claims.Normalize(@"C:\repo", Work)));
    }

    /// <summary>⚠️ 自己占的地方不算冲突，否则一个人无法重复声明同一片区域（幂等必需）。</summary>
    [Fact]
    public void FindConflict_IgnoresTheSameSession()
    {
        var existing = new[] { new Claim("s1", "pk", @"C:\repo\src", DateTimeOffset.UnixEpoch) };

        Assert.Null(Claims.FindConflict(existing, "s1", @"C:\repo\src\a.cs"));
        Assert.Equal("s1", Claims.FindConflict(existing, "s2", @"C:\repo\src\a.cs")!.SessionId);
    }

    [Fact]
    public void FindConflict_ReturnsNullWhenNothingOverlaps()
    {
        var existing = new[] { new Claim("s1", "pk", @"C:\repo\src", DateTimeOffset.UnixEpoch) };

        Assert.Null(Claims.FindConflict(existing, "s2", @"C:\repo\tests"));
    }
}
```

- [ ] **Step 2: 跑测试确认它失败**

Run: `dotnet test --filter "FullyQualifiedName~ClaimTests" -v q`
Expected: 编译失败，`Claims` / `Claim` 不存在

- [ ] **Step 3: 写实现**

```csharp
namespace Lilara.Core.Session;

/// <summary>一条声明（`01` §12.2）。</summary>
/// <param name="Path">**已规范化**的绝对路径。别把原始输入存进来——判据会当场失效。</param>
public sealed record Claim(string SessionId, string ProjectKey, string Path, DateTimeOffset At);

/// <summary>
/// 资源声明的冲突判据（`01` §12）。
///
/// <para>⚠️ **母题是文件系统，而单核心里它降级成一次路径比较**（§12.1）：
/// 核心是本设备唯一的 ⇒ 多 agent 的文件冲突问题不存在，只剩「两个声明是否重叠」。</para>
///
/// <para>⚠️ **本计划只做「声明主动」那一格**（§12.2 的两格之一）：
/// 「写入时自动检查」要求有写入路径（工具），而工具不存在（见计划的 D3）。
/// 所以声明现在是**纯可见性机制**——它不阻塞任何东西，它让别人**看见**。
/// 这不是缩水：§12.2 说声明保护的是「我的工作集不被作废」，
/// 而在没有工具的执行环境里，唯一会作废工作集的就是**另一个会话**——
/// 而那个会话看得见这里。</para>
///
/// <para>⚠️ **不做「同一派生树内不阻塞」那个例外**（§12.1）：它需要派生树概念，
/// 而 `child.spawned` 不存在 ⇒ 本计划里根本没有派生树，那个例外**没有输入可作用**。
/// ⚠️ **别写成「过阻塞」**：见上，本计划的声明**不阻塞任何东西**，所以漏做这个例外
/// 不可能多拦下一件事——它至多让同一棵树内的一对声明被判成冲突（一次**误报**，
/// 一个多余的可见信号）。等阻塞机制与派生树一并出现时，这个例外要一起补。见计划的 D4。</para>
/// </summary>
public static class Claims
{
    /// <summary>
    /// 规范化：绝对化 → 折叠 `.` / `..` → 统一分隔符 → 大小写不敏感。
    ///
    /// <para>⚠️ **大小写不敏感是无条件的**，不是「只在 Windows 上」。理由：
    /// 规范化结果**要落盘**，而同一份声明可能被 Windows 与（将来）别的平台的实例读到；
    /// 两边按不同规则规范化 ⇒ 同一个路径两个字符串 ⇒ 冲突判不出来。</para>
    ///
    /// <para>⚠️ 存成**规范形式（小写）**，而不是「保留原样 + 比较时忽略大小写」：
    /// 判据是前缀比较，而前缀比较每次都要走对比较器——存成规范形式，判据就是一句 <c>Ordinal</c>。
    /// 实现用 <see cref="string.ToLowerInvariant"/>，**不是** `StringComparison.OrdinalIgnoreCase`
    /// （后者是**比较**用的，存不成小写）。</para>
    ///
    /// <para>⚠️ **不做符号链接解析，这是 `01` §12.1 的一处显式缺口。**
    /// §12.1 写「Windows 下需处理大小写不敏感**与符号链接**」，本计划只做前半。
    /// 理由不是「不重要」，而是**做不到可验证**：本机实测
    /// <c>New-Item -ItemType SymbolicLink</c> 失败于「此操作需要管理员权限」，
    /// 于是任何符号链接测试**无法在开发环境跑**——而 G7 要求变异先确认落上再跑，
    /// 写一条跑不了的测试等于没有测试。
    /// ⇒ **记为已知偏离（本任务 D9）**，不是已知正确：两个路径经不同链接指向同一处时，
    /// 判据会**漏报**冲突（方向是漏，不是误报）。修法与判据改写见 T7 之后。</para>
    ///
    /// <para>⚠️ **上面那段「跨平台」论证只覆盖大小写这一半，别读成全覆盖。**
    /// 判据的另一半——分隔符边界——用的是**当次运行时的** <c>Path.DirectorySeparatorChar</c>
    /// （见 <see cref="Conflicts"/> 的 <c>IsUnder</c>）。所以 Windows 写下的
    /// <c>claims.json</c>（路径含 `\`）在别的平台上被规范化成 `/` 之后，
    /// 边界字符对不上，**冲突判不出**。⇒ **这是与 D9 同族的第二处缺口**，
    /// 不是「已解决」。今天不修：本机只跑 Windows，且写不出跨平台测试（同 D9 的理由）。</para>
    /// </summary>
    public static string Normalize(string path, string workingDirectory)
    {
        // ⚠️ 相对路径折叠成绝对：基址是 **`workingDirectory`**（经 `Path.Combine`），
        // 仅当 `workingDirectory` **本身**是相对路径时，进程当前目录才介入。
        var combined = Path.IsPathRooted(path) ? path : Path.Combine(workingDirectory, path);

        // ⚠️ `Path.GetFullPath` 顺手折叠掉 `.` 与 `..`——自己拼字符串做这件事会漏掉
        // `a/../../b` 这类多层回溯。它不碰文件系统（不要求路径存在），所以是纯函数。
        var full = Path.GetFullPath(combined);

        // ⚠️ 统一分隔符。**实测这行在 Windows 上是冗余的**：
        // `GetFullPath` 自己就把 `/` 归一成 `\`（`C:/repo/src` → `C:\repo\src`），
        // 删掉它没有任何测试会红。留着是因为它**不改变契约**、且在非 Windows
        // 上（`DirectorySeparatorChar` 是 `/` 而 `AltDirectorySeparatorChar` 是 `\`）方向正好相反。
        full = full.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

        // ⚠️ **根路径也一律去，这是刻意的，别"顺手修"。** `GetFullPath(@"C:\")` 返回 `C:\`，
        // 去尾后是 `c:`——那**不是**合法的绝对路径字符串，但本方法的结果**只用于比较与落盘**
        //（从不送回文件系统：T7 只把它存进 `claims.json` 并拿来判据），而 `c:` 恰好让前缀判据
        // **正确工作**。实测（`IsUnder` 逐字照抄本实现跑的）：
        //   `IsUnder("c:",  "c:\repo")`  → **True**   ← 去尾这一版，判对
        //   `IsUnder("c:\", "c:\repo")`  → **False**  ← 保留反斜杠那一版，判错
        // ⚠️ **即：保留根的反斜杠会让根路径与它下面的**一切**都判不出冲突——**静默失效**，
        // 正是 `01` §12 要防的。守卫在 `Normalize_RootPath_StillConflictsWithEverythingUnderIt`。
        //
        // ⚠️ **关于 `c:` 的一条事实——本机实测，别照抄别的环境的结论。**
        // `GetFullPath("c:")` 给的是**该盘自己的当前目录**（默认 `C:\`），
        // **不是**进程当前目录。实测：把进程 CWD 从 C 盘换到 `E:\` 之后，
        // `GetFullPath("c:")` **仍是 `C:\`**。
        // ⇒ `c:` 在默认情形下**是不动点**（`Normalize("c:")` 还是 `c:`）；
        // 但它依赖的「该盘当前目录」是**进程级可变状态**（谁都能 `SetCurrentDirectory`）。
        // ⇒ 既别**指望**它稳定，也别把它**回炉**。
        // ⚠️ 这段话是**改过的**：初版写成「`GetFullPath("c:")` 给的是进程当前目录所在的盘」
        // ＋「回炉会静默变成另一个目录」——两句都不对（前者错在语义，后者在本环境为假），
        // 而且证据是从一个 `%TEMP%`（在 C 盘）的临时工程里**转抄**的。
        // **别人的实测不是你的实测。**
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar);
        return (trimmed.Length == 0 ? full : trimmed).ToLowerInvariant();
    }

    /// <summary>
    /// 两个**已规范化**的路径是否冲突（`01` §12.1：一个是另一个的前缀）。
    ///
    /// <para>⚠️ **朴素前缀比较是错的**：`C:\repo\src` 会是 `C:\repo\src2` 的前缀，
    /// 于是两个毫不相干的目录被判成冲突。边界必须是**分隔符**——
    /// 相等，或者「前缀 + 分隔符」。这两条一起说完了所有情况。</para>
    /// </summary>
    public static bool Conflicts(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal)) return true;

        return IsUnder(a, b) || IsUnder(b, a);

        static bool IsUnder(string parent, string child) =>
            child.Length > parent.Length
            && child.StartsWith(parent, StringComparison.Ordinal)
            && child[parent.Length] == Path.DirectorySeparatorChar;
    }

    /// <summary>
    /// 在既有声明里找一条与它冲突的（**忽略自己的**——否则一个人无法幂等地重复声明）。
    /// 返回 null 表示没有冲突。
    /// </summary>
    public static Claim? FindConflict(IEnumerable<Claim> existing, string sessionId, string normalizedPath)
    {
        foreach (var c in existing)
        {
            if (string.Equals(c.SessionId, sessionId, StringComparison.Ordinal)) continue;
            if (Conflicts(c.Path, normalizedPath)) return c;
        }
        return null;
    }
}
```

⚠️ `Path.DirectorySeparatorChar` 在规范化后是 `\`（Windows）。上面 `IsUnder` 用它是对的，因为 `Normalize` 已经把两边都统一过了。

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test --filter "FullyQualifiedName~ClaimTests" -v q`
Expected: 通过 12，失败 0

⚠️ **这个数字按 `[Fact]` 实数过**（原计划写「通过 11」，而那时只有 **10** 个 `[Fact]`——
又一处没跑就写的数字）。派发前请数一遍 brief 里的 `[Fact]`；对不上就以**实测**为准。

- [ ] **Step 5: 确认变异被抓住（G7）**

把 `IsUnder` 里的 `child[parent.Length] == Path.DirectorySeparatorChar` 删掉（退化成朴素前缀）——`SiblingWithASharedNamePrefix_DoesNotConflict` **必须红**。跑之前先 grep 确认那半句真的没了。

⚠️ **第二条变异**（本次新增，钉根路径那条守卫）：在 `Normalize` 里把
`var trimmed = full.TrimEnd(Path.DirectorySeparatorChar);` 后面补上
`if (trimmed.EndsWith(':')) trimmed = full;`（即"顺手修"那个 `c:` 怪值）——
`Normalize_RootPath_StillConflictsWithEverythingUnderIt` **必须红**。
⚠️ 先 grep 确认那半句真落上了再跑（G7）——**没落上的变异和"变异存活"输出一模一样**。

- [ ] **Step 6: 提交**

```bash
git add src/Lilara.Core/Session/Claims.cs tests/Lilara.Core.Tests/ClaimTests.cs
git commit -m "feat(core): 声明判据（规范化大小写无关 / 前缀需分隔符边界 / 自己不算冲突）"
```

---

### Task 7: 声明落盘 + `claim.*` 命令 + `GET /claims`

**Files:**
- Create: `src/Lilara.Core/Persistence/ClaimStore.cs`
- Modify: `src/Lilara.Core/Hosting/CoreCommand.cs`
- Modify: `src/Lilara.Core/Control/WireEvent.cs`
- Modify: `src/Lilara.Core/Hosting/CommandDispatcher.cs`
- Modify: `src/Lilara.Core/Control/ControlServer.cs`
- Test: `tests/Lilara.Core.Tests/ClaimCommandTests.cs`（**新建**）· `tests/Lilara.Core.Tests/ControlServerTests.cs`（追加一条）

**Interfaces:**
- Consumes: `Claims` / `Claim`（T6）· `AtomicFile.Write` · `EventBus.ProjectTopic`
- Produces:
  - `ClaimStore(string workingDirectory)` —— `IReadOnlyList<Claim> All()` · `void Save(IEnumerable<Claim>)`
  - `CoreCommand.ClaimAcquire(string SessionId, IReadOnlyList<string> Scope)` · `CoreCommand.ClaimRelease(string SessionId, IReadOnlyList<string> Scope)`
  - `WireEvent.ClaimAcquired(string SessionId, string Path)` · `WireEvent.ClaimReleased(string SessionId, string Path)`
  - `GET /claims` → `Claim[]`

- [ ] **Step 1: 写失败的测试**

```csharp
/// <summary>
/// 声明的命令路径与持久化（`01` §12）。
/// ⚠️ 冲突判定的**对外行为**在这里钉：占用冲突回 `rejected` 并**点名占它的会话**——
/// 那是「别人的工作集会被作废」唯一的可见时刻。
/// </summary>
public class ClaimCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lilara-claim-" + Guid.NewGuid().ToString("N"));
    private readonly string _work = Path.Combine(Path.GetTempPath(), "lilara-claimwork-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
    }

    private (CoreHost Host, CommandDispatcher Dispatcher, EventBus Bus) Harness()
    {
        Directory.CreateDirectory(_work);
        var host = new CoreHost(_root, _work);
        host.Providers.Save(new ProviderDefinition
        {
            Id = "p", Protocol = Protocols.ClaudeMessages, ApiKey = "k",
            DefaultModel = "m", Modality = "vision",
        });
        var bus = new EventBus();
        var adapter = new Fakes.FakeAdapter();
        return (host, new CommandDispatcher(host, bus, h => new Stepper(host.Store, adapter)), bus);
    }

    private static string NewSession(CommandDispatcher d)
    {
        var r = d.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None)
            .GetAwaiter().GetResult();
        return ((Ack.Accepted)r.Ack).SessionId!;
    }

    /// <summary>⚠️ 声明写在**工作目录**（`01` §8：可分享的在工作目录，会话状态在数据根）。</summary>
    [Fact]
    public async Task Acquire_WritesIntoTheWorkingDirectorysLilaraFolder()
    {
        var (_, d, _) = Harness();
        var sid = NewSession(d);

        var r = await d.DispatchAsync(new CoreCommand.ClaimAcquire(sid, ["src"]), "c1", false, CancellationToken.None);

        Assert.IsType<Ack.Accepted>(r.Ack);
        Assert.True(File.Exists(Path.Combine(_work, ".lilara", "claims.json")),
            "声明是**可分享的项目信息**（01 §8），必须落在工作目录而不是数据根");
    }

    [Fact]
    public async Task Acquire_PublishesClaimAcquiredOnTheProjectTopic()
    {
        var (_, d, bus) = Harness();
        var sid = NewSession(d);
        var pk = Lilara.Core.Projects.ProjectKey.FromPath(_work);
        using var sub = bus.Subscribe(EventBus.ProjectTopic(pk));

        await d.DispatchAsync(new CoreCommand.ClaimAcquire(sid, ["src"]), "c1", false, CancellationToken.None);

        // ⚠️ **项目流，不是会话流**：声明是「谁在动哪些文件」，**跨会话**才有意义（03 §5）
        Assert.True(sub.Reader.TryRead(out var p), "声明必须发到项目流——会话流上别的人看不见");
        var e = Assert.IsType<WireEvent.ClaimAcquired>(p.Payload);
        Assert.Equal(sid, e.SessionId);
        Assert.EndsWith("src", e.Path);
    }

    /// <summary>
    /// ⚠️ **冲突 ⇒ `rejected` 且点名占它的会话**。
    /// 这条消息是「你的工作集会被作废」唯一能被看见的地方（`01` §12.2）。
    /// ⚠️ 它走 `rejected` 而不是 `error`：请求**本身**不合法（那块地方已经有主了），
    /// 不是「命令没错但这次没成」（G6）。
    /// </summary>
    [Fact]
    public async Task Acquire_OfAnOccupiedPath_IsRejected_AndNamesTheHolder()
    {
        var (_, d, _) = Harness();
        var a = NewSession(d);
        var b = NewSession(d);

        await d.DispatchAsync(new CoreCommand.ClaimAcquire(a, ["src"]), "c1", false, CancellationToken.None);
        var r = await d.DispatchAsync(new CoreCommand.ClaimAcquire(b, ["src\\a.cs"]), "c2", false, CancellationToken.None);

        var rejected = Assert.IsType<Ack.Rejected>(r.Ack);
        Assert.Contains(a, rejected.Reason);   // ⚠️ 点名，不是一句「已被占用」
    }

    [Fact]
    public async Task Acquire_TwiceByTheSameSession_IsIdempotent()
    {
        var (_, d, _) = Harness();
        var sid = NewSession(d);

        await d.DispatchAsync(new CoreCommand.ClaimAcquire(sid, ["src"]), "c1", false, CancellationToken.None);
        var r = await d.DispatchAsync(new CoreCommand.ClaimAcquire(sid, ["src"]), "c2", false, CancellationToken.None);

        Assert.IsType<Ack.Accepted>(r.Ack);
        // ⚠️ 第二次不该多出一条声明——否则右栏会列出两遍同一个区域。
        Assert.Single(ClaimsOf(_work));
    }

    [Fact]
    public async Task Release_RemovesIt_AndPublishes()
    {
        var (_, d, bus) = Harness();
        var sid = NewSession(d);
        await d.DispatchAsync(new CoreCommand.ClaimAcquire(sid, ["src"]), "c1", false, CancellationToken.None);

        using var sub = bus.Subscribe(EventBus.ProjectTopic(Lilara.Core.Projects.ProjectKey.FromPath(_work)));
        var r = await d.DispatchAsync(new CoreCommand.ClaimRelease(sid, ["src"]), "c2", false, CancellationToken.None);

        Assert.IsType<Ack.Accepted>(r.Ack);
        Assert.Empty(ClaimsOf(_work));
        Assert.True(sub.Reader.TryRead(out var p));
        Assert.IsType<WireEvent.ClaimReleased>(p.Payload);
    }

    /// <summary>⚠️ 释放一个**没占过**的路径：不算错，但也**不该发事件**（没有迁移）。</summary>
    [Fact]
    public async Task Release_OfSomethingNotHeld_IsANoOp()
    {
        var (_, d, bus) = Harness();
        var sid = NewSession(d);
        using var sub = bus.Subscribe(EventBus.ProjectTopic(Lilara.Core.Projects.ProjectKey.FromPath(_work)));

        var r = await d.DispatchAsync(new CoreCommand.ClaimRelease(sid, ["never"]), "c1", false, CancellationToken.None);

        Assert.IsType<Ack.Accepted>(r.Ack);
        Assert.False(sub.Reader.TryRead(out _));
    }

    private static List<Claim> ClaimsOf(string work) =>
        new ClaimStore(work).All().ToList();
}

// ⚠️ **下面两个方法在 `ClaimCommandTests` 类**内**——T7 执行时它们曾被排到类的 `}` 之外，
// 于是引用了 `Harness()` / `_root` / `_work` 这些实例成员却不在类里 ⇒ CS0116 编不过。
// 实现者把它俩移了进来（唯一自洽的读法）。**别再把它们挪出去。**

/// <summary>
/// ⚠️ **`claims.json` 是用户可写、可分享的输入，坏它不能打死核心。**
/// 两种**合法 JSON** 的坏形状，两种都会让 `Claims.Conflicts` 抛 `NullReferenceException`
/// （`child.Length` 对 null），而它们**都不进** `catch (JsonException)`：
/// <list type="bullet">
///   <item>`"path": null` —— 反序列化成 `Claim.Path = null`；</item>
///   <item>`[null]`（**数组元素**为 null）—— 列表里出现 null 项。</item>
/// </list>
/// ⚠️ 这两种是**同族**，不是同一种：只过滤 `Path is null` 关掉的是前者的**形状**，
/// 后者会在**过滤那一行本身**抛（`c.Path` 对 `c == null`）。⇒ 判据必须是
/// `c is not null && c.Path is not null`。
/// </summary>
[Theory]
[InlineData("""[{"sessionId":"s1","projectKey":"pk","path":null,"at":"2026-01-01T00:00:00+00:00"}]""")]
[InlineData("""[null]""")]
public async Task Acquire_WithAHandEditedBadClaimsFile_SurvivesInsteadOfKillingTheCore(string badJson)
{
    var (_, d, _) = Harness();
    var sid = NewSession(d);

    // 手写一份坏 claims.json（模拟「可分享、人手可编辑」那个真实来路）
    Directory.CreateDirectory(Path.Combine(_work, ".lilara"));
    File.WriteAllText(Path.Combine(_work, ".lilara", "claims.json"), badJson);

    var r = await d.DispatchAsync(new CoreCommand.ClaimAcquire(sid, ["src"]), "c1", false, CancellationToken.None);

    // 坏条目被丢掉 ⇒ 这条命令**照常成功**，而不是把核心抛死。
    Assert.IsType<Ack.Accepted>(r.Ack);
}

/// <summary>
/// ⚠️ **核心重启后，会话在盘上、不在内存激活表里。**
/// `claim` 走 `TryGetActivated` 时会回一句**假话**——「没有会话 '…'」，而它存在。
/// ⚠️ **这是 T3 那个缺陷的原样重演**（`9c3bd0d` 修过 `session.setAxis` 同一处），
/// 所以测试的形状照抄那条：**让会话只存在于磁盘上**，再调。
///
/// <para>⚠️ **"重启"用一个新 `CoreHost` 表达**（指向同一个数据根），
/// 因为新宿主天然有一张空的激活表。**别**用别的办法模拟——
/// 激活表是私有的，而"新宿主"正是真实的重启语义。</para>
/// </summary>
[Fact]
public async Task Acquire_OnASessionThatIsOnlyOnDisk_IsFoundNotReportedMissing()
{
    var (_, d, _) = Harness();
    var sid = NewSession(d);                // 用一个宿主建会话……

    // ……然后换一个**新宿主**指向同一个数据根：它的激活表是空的，
    // 于是这个会话此刻**只存在于磁盘上**——核心重启后的真实形态。
    var restarted = new CoreHost(_root, _work);
    var d2 = new CommandDispatcher(restarted, new EventBus(),
        h => new Stepper(restarted.Store, new Fakes.FakeAdapter()));

    var r = await d2.DispatchAsync(new CoreCommand.ClaimAcquire(sid, ["src"]), "c1", false, CancellationToken.None);

    // ⚠️ 缺陷下这里是 `Rejected`（「没有会话 '…'」）——而会话明明在盘上。
    Assert.IsType<Ack.Accepted>(r.Ack);
}
```

在 `ControlServerTests.cs` 追加：

```csharp
/// <summary>
/// ⚠️ **没有 `claims.json` 时返回 `[]`，不是 404 也不是 500**——
/// 「这个项目没人声明任何东西」是合法且**最常见**的形态。
/// </summary>
[Fact]
public async Task Claims_WithNoClaimsFile_IsAnEmptyArray()
{
    var r = await Client.GetAsync(Http("/claims"));

    Assert.Equal(System.Net.HttpStatusCode.OK, r.StatusCode);
    Assert.Equal("[]", (await r.Content.ReadAsStringAsync()).Trim());
}
```

- [ ] **Step 2: 跑测试确认它失败**

Run: `dotnet test --filter "FullyQualifiedName~ClaimCommandTests|FullyQualifiedName~Claims_WithNo" -v q`
Expected: 编译失败（`ClaimStore` / `ClaimAcquire` / `WireEvent.ClaimAcquired` 不存在）

- [ ] **Step 3: 写实现**

`ClaimStore.cs`：

```csharp
using Lilara.Core.Session;

namespace Lilara.Core.Persistence;

/// <summary>
/// 声明的落盘（`01` §8：`&lt;工作目录&gt;/.lilara/claims.json`）。
///
/// <para>⚠️ **声明进工作目录，不进数据根**——它是**可分享的项目信息**
/// （「这个仓库的这部分在做工程」团队里别人也该知道），而会话状态是私有的。
/// 这是 `01` §8 那条判据「可分享 vs 私有」的直接应用。</para>
///
/// <para>⚠️ **没有文件 = 空列表，不是错误**。大多数项目、大多数时候没有任何声明。</para>
/// </summary>
public sealed class ClaimStore(string workingDirectory)
{
    private string Path_ => System.IO.Path.Combine(workingDirectory, ".lilara", "claims.json");

    public IReadOnlyList<Claim> All()
    {
        if (!File.Exists(Path_)) return [];

        try
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<List<Claim>>(
                File.ReadAllText(Path_), Canonical.Json.Options) ?? [];

            // ⚠️ **丢掉 `Path` 为 null 的条目**——这是**回读边界**，不是多余的守卫。
            // `claims.json` 在**工作目录**里、可分享、**人手可编辑**，而
            // `Canonical.Json.Options` 没开 `RespectNullableAnnotations` ⇒
            // `"path": null` 是**合法 JSON**、能反序列化成 `Claim.Path = null`，
            // **不会**进下面的 `catch (JsonException)`。
            // ⇒ 不丢的话，`Claims.Conflicts` 里 `child.Length` 直接 **NRE**，
            // 冒泡出 `HandleClaimAcquire`（T7 没包 try/catch）⇒ **打死核心**——
            // 与本类注释「坏文件不让它打死核心」**正面冲突**。
            // ⚠️ 顺带：`WhenWritingNull` 会把 null 的 `path` **整个省略**，
            // 所以这种坏条目是**稳定**的（写回再读还是它），不会自愈。
            //
            // ⚠️ **`c is not null` 那半句是必须的，别删。** `[null]`（**数组元素**为 null）
            // 也是合法 JSON、也能手写进文件，而 `List<Claim>` 反序列化会给出**含 null 的列表**
            // ⇒ `c.Path` 对 `c == null` 直接 **NRE**，**且抛在这一行本身**，
            // 同样逃出下面那个 `catch (JsonException)`。只过滤 `Path is null` 关掉的是
            // F2 的**形状**，关不掉**同族**。
            return parsed.Where(c => c is not null && c.Path is not null).ToList();
        }
        catch (System.Text.Json.JsonException)
        {
            // ⚠️ 坏文件不让它打死核心：声明是**辅助**信息（可见性），
            // 而核心的职责是推进会话。宁可这一处暂时瞎，也不能整个进程起不来。
            //
            // ⚠️ **这里是静默的，而且这是已知的缺口，不是疏忽。** 初版注释写「别静默：
            // 写一条核心告警」——**那句描述的是本类做不到的事**：`ClaimStore` 只拿得到
            // 一个工作目录字符串，**没有 `EventBus`、没有 logger**，它没有任何告警出口。
            // ⇒ 真要做可见性告警，得把出口**注入进来**（改构造函数签名 ⇒ 改 `ClaimStore`
            // 的全部调用点）。**代价与收益不成比例**：声明是辅助信息，而"坏文件被忽略了"
            // 这件事本身不阻塞任何动作。⇒ 记为**已知偏离**，留待将来需要时连告警出口一起加。
            return [];
        }
    }

    public void Save(IEnumerable<Claim> claims) =>
        AtomicFile.Write(Path_, System.Text.Json.JsonSerializer.Serialize(claims.ToList(), Canonical.Json.Options));
}
```

`CoreCommand.cs`：

```csharp
    /// <summary>占用一片区域（`01` §12.2）。路径可以是相对的，按会话的工作目录解析。</summary>
    public sealed record ClaimAcquire(string SessionId, IReadOnlyList<string> Scope) : CoreCommand;

    /// <summary>释放。⚠️ 这是「模型失控时的逃生口」（`03` §3）——所以它**不检查**占用者是不是自己。</summary>
    public sealed record ClaimRelease(string SessionId, IReadOnlyList<string> Scope) : CoreCommand;
```

`WireEvent.cs`：

```csharp
[JsonDerivedType(typeof(ClaimAcquired), "claim.acquired")]
[JsonDerivedType(typeof(ClaimReleased), "claim.released")]
```

```csharp
    /// <summary>⚠️ 发到**项目流**（`03` §5）——「谁在动哪些文件」跨会话才有意义。</summary>
    public sealed record ClaimAcquired(string SessionId, string Path) : WireEvent;

    public sealed record ClaimReleased(string SessionId, string Path) : WireEvent;
```

`CommandDispatcher.cs` —— case + 两个处理器：

```csharp
                case CoreCommand.ClaimAcquire a: return HandleClaimAcquire(a, commandId);
                case CoreCommand.ClaimRelease r: return HandleClaimRelease(r, commandId);
```

```csharp
    private DispatchResult HandleClaimAcquire(CoreCommand.ClaimAcquire c, string id)
    {
        // ⚠️ **找会话走 `TryFind`，不是只看激活表。** 核心重启之后会话**在盘上、不在内存里**，
        // 只看激活表会回一句**假话**——「没有会话 '…'」，而它存在，只是没被激活。
        // ⚠️ **这是 T3 那个缺陷的原样重演**：`HandleSessionSetAxis` 修过一次
        // （`9c3bd0d`，`TryGetActivated` → `TryFind`），这里是两个**新**处理器又写回去了。
        // ⇒ 纪律：`CommandDispatcher` 里**任何**「按 id 取会话」都必须走 `TryFind`。
        if (!TryFind(c.SessionId, out var handle, out var notFound)) return Reject(id, notFound!);

        var store = new ClaimStore(handle.Spec.WorkingDirectory);
        var paths = c.Scope.Select(p => Claims.Normalize(p, handle.Spec.WorkingDirectory)).ToList();

        // ⚠️ **冲突 ⇒ rejected 且点名持有者**（`01` §12.2）。这句话是「你的工作集会被作废」
        // 在界面上唯一能被看见的地方——只回一句「已被占用」，用户不知道去找谁。
        var clash = Claims.FindConflict(store.All(), c.SessionId, path);
        if (clash is not null)
            return Reject(id, $"'{path}' 已被会话 {clash.SessionId} 占用。");

        var all = store.All().ToList();
        if (all.Any(x => x.SessionId == c.SessionId && x.Path == path))
            return new DispatchResult(new Ack.Accepted { CommandId = id });   // 幂等

        all.Add(new Claim(c.SessionId, handle.ProjectKey, path, DateTimeOffset.UtcNow));
        store.Save(all);

        // ⚠️ **项目流**：声明是跨会话的（`03` §5）
        _bus.Publish(EventBus.ProjectTopic(handle.ProjectKey),
            new WireEvent.ClaimAcquired(c.SessionId, path));

        return new DispatchResult(new Ack.Accepted { CommandId = id });
    }

    private DispatchResult HandleClaimRelease(CoreCommand.ClaimRelease c, string id)
    {
        // ⚠️ 同上：`TryFind`，不是 `TryGetActivated`。
        if (!TryFind(c.SessionId, out var handle, out var notFound)) return Reject(id, notFound!);

        var store = new ClaimStore(handle.Spec.WorkingDirectory);
        var paths = c.Scope.Select(p => Claims.Normalize(p, handle.Spec.WorkingDirectory)).ToList();

        var all = store.All().ToList();
        // ⚠️ **逃生口**（`03` §3）：不检查占用者是不是自己——模型失控时，
        // 用户要能从一个**别的**会话里把锁摘掉。
        var removed = all.RemoveAll(x => x.Path == path);
        if (removed == 0) return new DispatchResult(new Ack.Accepted { CommandId = id });

        store.Save(all);
        _bus.Publish(EventBus.ProjectTopic(handle.ProjectKey),
            new WireEvent.ClaimReleased(c.SessionId, path));

        return new DispatchResult(new Ack.Accepted { CommandId = id });
    }
```

`ControlServer.cs` —— 加端点（放在 `MapSnapshots` 里）：

```csharp
        // ⚠️ **没有声明时返回 `[]`**（`03` §6）。「这个项目没人声明任何东西」
        // 是合法且最常见的形态——回 404 会让头以为端点写错了。
        //
        // ⚠️ **声明的落点是工作目录**（`01` §8：可分享的项目信息），而数据根里
        // 只存了会话——所以得先找一个会话问它的工作目录。没有会话时无从问起，如实回空数组。
        //
        // ⚠️ **读 spec，不用 `TryFind`——因为 `ControlServer.TryFind` 会 `Activate` 会话！**
        // （见它的回落分支：`host.Activate(pk, sessionId)`。）
        // 那意味着：**每拉一次 `/claims` 就把数据根下所有会话激活进内存**——
        // 一个 **GET 端点产生了状态副作用**，而且**随会话数无限增长**。
        // 与 `01` §8.3 的懒激活取向（「激活即缓存」，按需才有）相悖。
        // ⇒ 直接 `TryReadSpec` 取 `WorkingDirectory`：**不激活、更简单、无副作用**。
        // ⚠️ 而且**没有陈旧问题**：spec 的 `WorkingDirectory` 建会话时定下、之后不变
        //（`SessionSpec.WorkingDirectory` 是 `init`），所以读盘那份与内存那份**必然一致**。
        app.MapGet("/claims", () =>
        {
            var claims = ProjectKeys()
                .SelectMany(pk => host.Store.ListSessions(pk)
                    .Select(id => host.Store.TryReadSpec(pk, id)))
                .Where(spec => spec is not null)
                .SelectMany(spec => new ClaimStore(spec!.WorkingDirectory).All())
                .Distinct()
                .ToList();

            return Results.Json(claims, WireJson.Options);
        });
```

- [ ] **Step 4: 先写接线测试，跑一次确认它红（此时接线还不存在）**

⚠️ **顺序是「测试 → 红 → 接线 → 绿」，别倒过来。** T7 执行时计划把这两步写成
「Step 4 接线、Step 4b 确认先红」——**自相矛盾**（接完线还怎么"先"红）。
实现者按目的执行（先红、后接线），判据是对的；这里把顺序写正。

先加测试（不接线）：

```csharp
    /// <summary>
    /// ⚠️ **走真 WS，不是直接调 dispatcher。** 后者在接线缺失时会**照样绿**——
    /// 它绕过的正是本条要钉的那一层。
    /// </summary>
    [Fact]
    public async Task WebSocket_ClaimRelease_ReachesTheDispatcher()
    {
        // …照 WebSocket_SetAxis_ReachesTheDispatcher 的脚手架：
        // 建会话（直接调 dispatcher）、连 WS、发 {"kind":"claim.release", …}、读帧
        Assert.True(ack.GetProperty("accepted").GetBoolean());
        // ⚠️ `reason` 用 TryGetProperty 惰性取，别无条件 GetProperty——
        // 成功回执的 `reason` 被 `WhenWritingNull` **整个省略**，
        // 无条件求值会在**成功路径**上抛 `KeyNotFoundException`（修好了反而红）。
    }
```

Run: `dotnet test --filter "FullyQualifiedName~WebSocket_ClaimRelease_ReachesTheDispatcher" -v q`
Expected: **失败**，消息含「**认不出的命令 'claim.release'**」——
若它**通过**了，说明测试没走真 WS（或接线已经在了），去改测试。

- [ ] **Step 5: 接上 `ParseCommand`（只加 `claim.release`）**

⚠️ **只加 `claim.release` 一条——不是 `claim.acquire`。** 依据是 `03` §3 的
**命令表**（协议是权威）：表里资源那节**只有** `claim.release`，
并明写「清单查询走 `GET /claims`（§6），**不做 `claim.list` 命令**」。
`claim.acquire` **不在**线上命令表里。

⚠️ 理由是**使用场景**，不是「不重要」。`03` §8.2 把 `claim.release` 归入**止血**
（与 `interrupt` / `approval.respond` 并列），原文：

> 「它存在，是因为头不该是唯一入口：止血（`interrupt` / `claim.release` /
> `approval.respond`）与引导（`provider.upsert` / `session.create`）恰恰发生在
> **头最指望不上**的时候。」

⇒ **头需要能在核心/控制台不响应时强制释放声明**——所以这条必须在线。

⚠️ **而 `claim.acquire` 相反**：头**从不发**它（T9 只 `GET /claims` 读、渲染；
T10 的冲突验收是**在控制台**跑的，见 T10 Step「`session activate <B>` → `claim src`」）。
「声明主动」是**模型/用户**的动作，由控制台发起。
⚠️ 且**不加**它的理由压过「对称性」：`03` §3 说命令表只有 `claim.release`，
而**线上多一条不在协议表里的命令**是一种**未经同意的协议扩展**——
T3 漏接线是「该有的没接」，这里加 `claim.acquire` 会变成「不该有的加了」，**方向相反**。

⚠️ 不过接线这一步本身**不是可选的润色**：`ParseCommand` 是命令的**第三条线**
——核心语义在 `CommandDispatcher`、控制台解析在 `CoreCommandParser`、
**线上解析在 `ControlServer.ParseCommand`**。T3（`session.setAxis`）漏过它，
代价是那条命令**在线上整整不可达**（后来由 Task 4b 补，`b1696b4`）。

在 `src/Lilara.Core/Control/ControlServer.cs` 的 `ParseCommand` switch 里，
紧跟 `"session.setAxis"` 那条之后加：

```csharp
            // ⚠️ 与 `session.setAxis` 同一条纪律：线上解析是命令的**第三条线**。
            // ⚠️ **只有 release，没有 acquire**——`03` §3 的命令表里资源那节只有 release，
            // 且 §8.2 把它归入「止血」（头在核心/控制台不响应时的逃生口）。
            // 头的认领动作走控制台（T8），头自己**从不发** `claim.acquire`。
            "claim.release" => new CoreCommand.ClaimRelease(
                payload.GetProperty("sessionId").GetString()!,
                payload.GetProperty("path").GetString()!),
```

（⚠️ **接线测试已在 Step 4 里先写好并跑红过了**——本步只加这四行 case，然后进 Step 6。）

- [ ] **Step 6: 跑测试确认通过**

Run: `dotnet test -v q`
Expected: 全绿。**本任务新增 10 个测试方法 / 11 个用例**，外加接线 1 条：
- `ClaimCommandTests`（8 个方法 / 9 个用例）：`Acquire_Writes…` · `Acquire_Publishes…` ·
  `Acquire_OfAnOccupied…` · `Acquire_Twice…` · `Release_Removes…` · `Release_OfSomethingNotHeld…` ·
  **`Acquire_OnASessionThatIsOnlyOnDisk_…`**（控制器补：`TryFind` 那条） ·
  **`Acquire_WithAHandEditedBadClaimsFile_…`**（控制器补：`[Theory]` 2 个坏文件形状）
- `ControlServerTests`（2 个方法）：`Claims_WithNoClaimsFile_…` · **`WebSocket_ClaimRelease_…`**（接线）

⚠️ **这个数按 brief 里实测 `[Fact]` / `[Theory]` 得到的**（原计划写「新增 7 条 + 接线 1 条」，
那是我加了两条测试之后**没更新的旧数**）。派发前数一遍；对不上以**实测**为准。
⚠️ **分母是"执行时的既有基线"（当前 435），不是任何写死的数字。**

- [ ] **Step 6b: 补上跨会话的短临界区（`claims.json` 的读-改-写）**

⚠️ **这一条是控制器在 T7 派发后自己发现的洞，属于本任务的范围**（`ClaimStore` 是 T7 的文件）。

`HandleClaimAcquire` / `HandleClaimRelease` 做的是
`store.All()` → 改列表 → `store.Save()`，**而 `claim.*` 不拿会话闸**
（正确：闸是**会话粒度**的，而声明是**跨会话**的操作）。
⚠️ `AtomicFile.Write` 只保证**文件不半截**与**并发写不撞名**——
**它不保证读-改-写的原子性**。⇒ 两个会话同时 acquire：
各自读到同一份旧列表、各自写回 ⇒ **后写的盖掉前写的，丢一条声明**，而且**丢得静默**
（两边都收到 `Accepted`，盘上只剩一条）。
⚠️ 这比 `setAxis` 不拿闸更严重：`setAxis` 写的是**各自**的 `state.json`，
claim 写的是**共享的** `claims.json`。

修法照 `HistoryLog` 的既有形态（`HistoryLog.cs:30,42`）——**按路径一把静态锁**：

```csharp
    // ⚠️ 按路径的锁：`claims.json` 是**跨会话共享**的，而 `claim.*` 不拿会话闸。
    // 形态照 `HistoryLog.PathLocks`（同一处代码库的既有做法）。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lock> PathLocks = new();

    /// <summary>
    /// ⚠️ **读-改-写必须在同一把锁里**——这是本方法与 `All()`/`Save()` 分开调用的区别。
    /// 分开调用会让两个会话各自 `All()` → 各自 `Save()` ⇒ **丢更新**（静默）。
    /// </summary>
    public void Update(Func<IReadOnlyList<Claim>, IEnumerable<Claim>> change)
    {
        lock (PathLocks.GetOrAdd(Path_, _ => new Lock()))
        {
            // ⚠️ 在这里重新 `All()`（**锁内**），不是把锁外的快照传进来——
            // 传进来就等于锁没起作用。
            Save(change(All()));
        }
    }
```

`HandleClaimAcquire` / `HandleClaimRelease` 改成用 `store.Update(...)`，
**并把 `FindConflict` 挪进 `Update` 的委托里**（冲突判定与写入必须在同一个临界区，
否则「查了没冲突 → 别人占了 → 我写进去」照样丢/超占）。

⚠️ **`Save` 保持公开**（端点不用它，但将来可能要），**`All` 保持公开**（读路径无需锁）。

- [ ] **Step 6c: 加一条真的并发测试**

⚠️ **不能用 `Task.Delay` 制造时序**（那是在测试里编造并发，而不是产生并发）。
用 `Barrier`/`Task.WhenAll` 让两个**真**任务同时进 `DispatchAsync`：

```csharp
    /// <summary>
    /// ⚠️ **两个会话并发 acquire 两条不同的路径 ⇒ 两条都必须留下。**
    /// 没有临界区时这条会**间歇性**红（丢一条），所以断言要跑在一个
    /// **真的并发**上（`Barrier` 对齐两个任务），不是 `Task.Delay` 错开。
    /// ⚠️ 这类测试天然间歇——**跑 20 次确认它稳定绿**，别只跑一次。
    /// </summary>
    [Fact]
    public async Task Acquire_FromTwoSessionsConcurrently_KeepsBothClaims()
    {
        // 两个会话各占一条**不重叠**的路径，用 Barrier 让两个 DispatchAsync 同时进
        // ⇒ 断言 claims.json 里有**两条**（缺陷下会出现只有一条）
    }
```

- [ ] **Step 7: 提交**

```bash
git add src/Lilara.Core/ tests/Lilara.Core.Tests/ClaimCommandTests.cs tests/Lilara.Core.Tests/ControlServerTests.cs
git commit -m "feat(core): 声明落盘（工作目录，可分享）+ claim.acquire/release 命令 + GET /claims（无文件即空数组）+ claim.release 接上 ParseCommand（原来线上不可达）+ claims.json 的读-改-写进临界区（并发 acquire 会丢更新）"
```

⚠️ **提交消息里的「`claim.acquire/release`」指 `CoreCommand` 的**两个类型**（dispatcher 与 T8 的控制台都要用），
**不表示两条都在线上**——线上只有 `claim.release`（见 Step 5）。

---

### Task 8: 控制台的 `claim`

**Files:**
- Modify: `src/Lilara.Core/Console/CoreCommandParser.cs`
- Modify: `src/Lilara.Core/Console/Repl.cs`（回执格式 switch + `PrintStatus` + `claim list`）
- Modify: `src/Lilara.Core/Program.cs:108`
- Test: `tests/Lilara.Core.Tests/ParserTests.cs`（**追加**——本任务 7 条里的 6 条都是
  `CoreCommandParser.Parse` 的测试，那里是它们的归属地；它现在有 T5 的 3 条）
- Test: `tests/Lilara.Core.Tests/ReplTests.cs`（追加 **1 条**：`claim list` 的显示——
  它是 `Repl` 的行为，`ParserTests` 测不到）

⚠️ **别把解析器的测试塞进 `ReplTests.cs`**（初版就是这么写的，与 T5 同形）：
`ReplTests` 要起一个 `Repl` + 宿主，而解析器测试是**纯函数**测试，放那儿是错位置。

**Interfaces:**
- Consumes: `CoreCommand.ClaimAcquire` / `.ClaimRelease`（T7，**字段是 `IReadOnlyList<string> Scope`**）
- Produces: `claim <path>`（占）· `claim release <path>`（放）· `claim list`（列）
  ⚠️ **控制台一次只收一个路径**，但命令的字段是**列表**（`03` §3 的 `scope`）——
  ⇒ 解析器把单个路径**包成单元素列表**（`new ClaimAcquire(sid, [path])`）。
  ⚠️ **别把命令的字段改回 `string`**：那是线上协议的字段名与形状（T7 已按 `scope` 落地）。

- [ ] **Step 1: 写失败的测试**

```csharp
[Fact]
public void Claim_WithAPath_AcquiresOnTheCurrentSession()
{
    var (cmd, err) = CoreCommandParser.Parse("claim src/foo");

    Assert.Null(err);
    Assert.Equal(["src/foo"], Assert.IsType<CoreCommand.ClaimAcquire>(cmd).Scope);
}

[Fact]
public void ClaimRelease_WithAPath_Releases()
{
    var (cmd, err) = CoreCommandParser.Parse("claim release src/foo");

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
```

- [ ] **Step 2: 跑测试确认它失败**

Run: `dotnet test -v q`
Expected: **编译失败**——`CoreCommand.ClaimAcquire` 存在（T7），但
`CoreCommandParser.Parse` 还**没有 `currentSessionId` 那个重载**，
而本步的测试传了两个实参 ⇒ `CS1501`（没有那个参数的重载）。

⚠️ **这一步不要用 `--filter`**：编译不过时 filter 没有意义（没有测试能跑）。
⚠️ 顺带记下：初版这里写的是 `--filter "FullyQualifiedName~Claim_"`，
**那个表达式会漏掉 3 条**——`ClaimRelease_WithAPath_…` / `ClaimRelease_WithNothing_…`
（`Claim` 后面是 `R` 不是 `_`）与 `ClaimList_IsNotACoreCommand`。
⇒ 本任务的 7 条测试**没有一条共有的筛选子串**能覆盖全部；**用全量跑**。

- [ ] **Step 3: 写实现**

⚠️ 解析器需要知道「当前会话」——而 `claim` 命令的 `sessionId` 从哪来？**解析器不知道会话**。
⇒ 与 `session send` 同一条路：解析器产出**不含会话**的形状，由 `Repl` 补上当前会话。

改 `CoreCommand` 的形状会牵连线上协议。**更省的做法**：解析器返回一个**占位**，`Repl` 替换。
但那是两处语义。⇒ 正解：让解析器接受一个 `currentSessionId` 参数（默认 null），
`Repl` 调用时传当前会话。

`CoreCommandParser.cs`：

```csharp
    /// <summary>
    /// ⚠️ <paramref name="currentSessionId"/> 是**控制台专有**的输入：
    /// `claim src/foo` 里没有会话 id，而核心命令**必须**有（线上协议的 `claim.acquire` 带它）。
    /// ⇒ 由控制台补上「当前会话」。头走线上协议，它自己知道是哪个会话。
    /// </summary>
    public static (CoreCommand? Command, string? Error) Parse(string line, string? currentSessionId = null)
```

在 `Parse` 里加（放在 `axis set` 之后）：

```csharp
        // ⚠️ `claim list` 是**控制台的查询**，不是核心命令——返回 (null,null) 让 Repl 拦截。
        if (trimmed is "claim list") return (null, null);

        // ⚠️ **`claim release` 少了路径必须单独拦，而且要排在 acquire 之前。**
        // 不拦的话 `trimmed.StartsWith("claim ")` 会把它接住、`trimmed[6..]` = `"release"`,
        // 于是用户**静默占了一个叫 `release` 的路径**——不报错、看得出在动，看不出在动错东西。
        // 与 `axis set`（T5）是**同一个形状**：前缀判断放行半个命令，剩下半截被当参数吃掉。
        if (trimmed == "claim release")
            return (null, "用法：claim <路径> | claim release <路径> | claim list");

        if (trimmed.StartsWith("claim release ", StringComparison.Ordinal))
            return WithSession(currentSessionId, trimmed["claim release ".Length..].Trim(),
                (sid, path) => new CoreCommand.ClaimRelease(sid, [path]));

        if (trimmed.StartsWith("claim ", StringComparison.Ordinal) && trimmed != "claim")
            return WithSession(currentSessionId, trimmed["claim ".Length..].Trim(),
                (sid, path) => new CoreCommand.ClaimAcquire(sid, [path]));

        // ⚠️ 光一个 `claim` ⇒ **报错**，不落回「当成消息」——
        // 用户以为占了地方，实际是把它当消息发给了会话。
        if (trimmed == "claim") return (null, "用法：claim <路径> | claim release <路径> | claim list");

        return (null, null);

        static (CoreCommand?, string?) WithSession(
            string? sid, string path, Func<string, string, CoreCommand> make) =>
            sid is null
                ? (null, "还没有选中会话。先 `session create` 或 `session activate <id>`。")
                : (make(sid, path), null);
```

⚠️ 上面那个 `return (null, null);` 是 `Parse` 原本的兜底，`WithSession` 局部函数要放在它**之后**（C# 局部函数可以后置声明）。注意 `ParseProviderSet` 的调用在它之前。

`Repl.cs` —— 调用点改传当前会话，并加 `claim list`：

```csharp
        if (trimmed == "claim list") { PrintClaims(); return true; }
...
        var (parsed, parseError) = CoreCommandParser.Parse(trimmed, Current?.SessionId);
```

```csharp
    private void PrintClaims()
    {
        // ⚠️ 声明落在**工作目录**（`01` §8），所以要有一个会话来问它的工作目录。
        if (Current is null) { _out.WriteLine("还没有选中会话。"); return; }

        var claims = new ClaimStore(Current.Spec.WorkingDirectory).All();
        if (claims.Count == 0) { _out.WriteLine("（没有声明）"); return; }

        foreach (var c in claims) _out.WriteLine($"{c.SessionId}  {c.Path}");
    }
```

`Program.cs:108` 横幅补一句：

```csharp
Console.WriteLine("  `axis set <会话> <轴> <值>` 调行为；`claim <路径>` 占区域；`history` 回放；`quit` 退出。");
```

- [ ] **Step 4: 加 `claim list` 的显示测试（放 `ReplTests.cs`）**

⚠️ **本任务唯一一条 `Repl` 行为**（`claim list` 不走命令通道，是控制台的查询），
所以它在 `ReplTests.cs`；其余 6 条解析器测试在 `ParserTests.cs`。

```csharp
    /// <summary>
    /// ⚠️ **`claim list` 是控制台的查询**（`03` §3：不做 `claim.list` 命令），
    /// 所以它必须**自己**读 `claims.json` 并印出来——而这条钉的就是那个显示。
    ///
    /// <para>⚠️ 用 `NewHarness()`（已有脚手架）：`repl.ExecuteAsync` + `_out`。
    /// 声明用 `ClaimStore` 直接写（`claim` 命令的认领走 dispatcher，这里只测显示）。</para>
    /// </summary>
    [Fact]
    public async Task ClaimList_PrintsTheClaimsInTheWorkingDirectory()
    {
        var (repl, _, session) = NewHarness();
        new Lilara.Core.Persistence.ClaimStore(session.Spec.WorkingDirectory)
            .Save([new Lilara.Core.Session.Claim("other", "pk", @"c:\repo\src", DateTimeOffset.UnixEpoch)]);

        await repl.ExecuteAsync("claim list", CancellationToken.None);

        // ⚠️ 断言用**钉专属措辞**（不是"有输出就行"）：会话 id 与路径都要看得见，
        // 否则「印了一行空表」也算过。
        Assert.Contains("other", _out.ToString());
        Assert.Contains("src", _out.ToString());
    }

    /// <summary>⚠️ 没有声明时印「（没有声明）」，不是空白——空白让用户分不清「没有」与「坏了」。</summary>
    [Fact]
    public async Task ClaimList_WithNoClaims_SaysSo()
    {
        var (repl, _, _) = NewHarness();

        await repl.ExecuteAsync("claim list", CancellationToken.None);

        Assert.Contains("没有声明", _out.ToString());
    }
```

- [ ] **Step 5: 跑测试确认通过**

Run: `dotnet test -v q`
Expected: 全绿。**本任务新增 6 条 `ParserTests` + 2 条 `ReplTests` = 8 条**
（⚠️ 这个数按 brief 实测；初版说「7 条」而 `Files` 却写 `ReplTests`——
解析器那 6 条放错了文件，且 `claim list` 的显示**根本没有测试**）。
⚠️ 分母是执行时的既有基线（**454**），别写死。

- [ ] **Step 6: 提交**

```bash
git add src/Lilara.Core/Console/ src/Lilara.Core/Program.cs tests/Lilara.Core.Tests/ParserTests.cs tests/Lilara.Core.Tests/ReplTests.cs
git commit -m "feat(console): claim/claim release/claim list（光一个 claim 报错，不落回当成消息；claim release 少了路径也报错）"
```

---

### Task 9: 头 —— 三栏骨架 + 引导 + 五轴

**Files:**
- Modify: `head/wwwroot/index.html`（⚠️ **全量替换**——见下面的「全量替换」条）
- Modify: `head/wwwroot/app.js`（⚠️ **全量替换**——见下面的「全量替换」条）
- Modify: `src/Lilara.Core/Control/ControlServer.cs`（**新增 `GET /providers`**，见下面的 ⚠️）

⚠️ **「全量替换」不是「Modify」——这两份文件现在是控制面分支留下的两栏版本。**

| 现在的形状（`20663ba` 那版） | 本任务之后 |
|---|---|
| `index.html`：`#side` + `#main`（两栏）、`#compose` | `#left` / `#center` / `#right`（三栏） |
| `app.js`：`loadSessions` / `renderSessions` / `openSession` / `renderTurn` / `renderMeta` / `renderSlots` … | **全部重写**：`loadProjects` / `loadAxes` / `loadClaims` / `renderAxes` / `renderAlert` / `renderOnboarding` / `bootstrap` … |

⚠️ **Step 1 的标题就是「换掉 `index.html`」，而下面的代码块是**完整文件**——
所以这两步是**整份替换**，不是往现有文件里插。**
⇒ **旧的两栏元素（`#side` / `#main` / `#compose`）不要留在文件里**：
它们没有任何 JS 引用，是**死代码**，而"页面上还有一块看不见的东西"在调试时极具误导性。
⇒ **旧的 `app.js` 函数名与本任务的新函数可能重名**（`loadSessions`、`subscribe`、`command`…）——
函数声明会**后来者覆盖先前的**，于是旧的那份成了**永远不被调用的死代码**。
**同名的旧函数要删掉**，别让两份实现并存在同一个文件里。

**判据（可执行）**：替换后跑
```
cd E:/Workspace/AgentLilaraCode && grep -nE 'id="(side|main|compose)"' head/wwwroot/index.html
```
**应当无输出**（一个都不剩）。

**Interfaces:**
- Consumes: `GET /projects` · `GET /projects/{pk}/sessions` · `GET /sessions/{id}/state`（含 `axes` / 三槽）· `GET /sessions/{id}/history` · **`GET /axes`**（T4）· `GET /claims` · **`GET /providers`**（本任务新增）· WS `subscribe` / `command` / `resync_required`
- Produces: 页面（无对外接口）· `GET /providers` → `[{ id, protocol, defaultModel }]`（**不含 `apiKey`**）

- [ ] **Step 1: 换掉 `index.html`**

```html
<!DOCTYPE html>
<html lang="zh">
<head>
<meta charset="utf-8">
<title>LILARA 头</title>
<style>
  /* ⚠️ **三栏是 `05` §5 的选择**（不是义务）。它按「阅读 vs 并行」分工：
     中栏保证日常阅读，右栏偿还并行债。
     ⚠️ **右栏恒常占位、不收起、不浮出**（§5 明说）：浮出的东西你要先注意到它浮出了，
     而那正好抵消了它存在的意义。 */
  * { box-sizing: border-box; }
  body { margin: 0; font: 14px/1.6 system-ui, "Segoe UI", sans-serif; display: flex; height: 100vh; }

  /* 左栏：项目 / 会话树。⛔ 不做声明树内 ⚠——§7.2 说树里的 ⚠ 会被忽略，冲突要上浮到右栏。 */
  #left { width: 240px; border-right: 1px solid #ddd; overflow: auto; padding: 8px; background: #fafafa; }

  /* 中栏：专注会话。时间线是**要读**的东西，所以它拿最大的地方。 */
  #center { flex: 1; display: flex; flex-direction: column; min-width: 0; }

  /* 右栏：注意力。⚠️ **恒常占位**——不收起（§5）。 */
  #right { width: 320px; border-left: 1px solid #ddd; overflow: auto; padding: 8px; background: #fafafa; }

  #top { padding: 8px 12px; border-bottom: 1px solid #ddd; display: flex; gap: 12px; align-items: center; }
  #timeline { flex: 1; overflow: auto; padding: 12px; min-height: 0; }
  #compose { border-top: 1px solid #ddd; padding: 8px; display: flex; gap: 8px; }
  #text { flex: 1; padding: 8px; font: inherit; border: 1px solid #ccc; border-radius: 4px; }
  button { padding: 6px 12px; font: inherit; cursor: pointer; }
  .session { padding: 6px 8px; cursor: pointer; border-radius: 4px; }
  .session:hover { background: #eee; }
  .session.active { background: #dce8ff; }
  .turn { margin-bottom: 10px; white-space: pre-wrap; }
  .turn.user { color: #0a5; }
  .turn.model { color: #222; }
  .turn.error { color: #b00; }
  .turn.meta { color: #999; font-size: 12px; }
  .muted { color: #aaa; }
  #status { margin-left: auto; font-size: 12px; color: #666; }
  h4 { margin: 12px 0 4px; font-size: 12px; color: #666; text-transform: uppercase; letter-spacing: .05em; }

  /* ⚠️ 义务 1：两个头槽位**并排**（`05` §4 明说「不是折叠在一个详情里」）——
     归位错误（人设同时出现在系统与首要）**只在并排时看得出来**。 */
  #slots { border-top: 1px solid #ddd; padding: 8px 12px; font-size: 12px; display: flex; gap: 16px; }
  .slot { flex: 1; min-width: 0; }
  .slot b { display: block; color: #333; margin-bottom: 2px; }
  .slot .body { color: #666; white-space: pre-wrap; max-height: 4.8em; overflow: auto; }

  /* ⚠️ 义务 4：安全轴**不许收进「更多设置」**（`05` §4）——它的全部价值是「用户忘了
     说这是生产环境」时能提醒，藏起来等于取消它。所以它就在右栏最上面，加粗。 */
  #safety-row { padding: 6px; border-radius: 4px; }
  #safety-row.alert { background: #ffecec; border: 1px solid #e88; }
  #alert { color: #b00; font-size: 12px; margin-top: 4px; }
  .axis { display: flex; align-items: center; gap: 6px; margin: 3px 0; font-size: 12px; }
  .axis label { width: 68px; color: #555; }
  .axis select { flex: 1; font: inherit; font-size: 12px; }
  .axis.effort { opacity: .55; }   /* ⚠️ effort 不进上下文（01 §3.5）——视觉上标出来 */

  /* ⚠️ 浮动槽（`01` §3.2）：它是**头槽位之后的那个槽**，五轴的显示落点。
     ⚠️ 有界是它的硬约束——这里用 max-height 兑现，别让它长成第二个时间线。 */
  #floating { border-top: 1px solid #ddd; padding: 6px 12px; font-size: 12px; color: #666; max-height: 6em; overflow: auto; }
  .claim { font-size: 12px; padding: 3px 0; border-bottom: 1px dotted #eee; }
  .claim .who { color: #888; }
  .banner { padding: 8px; background: #fffbe6; border: 1px solid #e5d27a; border-radius: 4px; margin-bottom: 8px; font-size: 12px; }
</style>
</head>
<body>
<div id="left">
  <div style="margin-bottom:6px"><b>会话</b> <button id="new">+ 新建</button></div>
  <div id="sessions"></div>
</div>

<div id="center">
  <div id="top">
    <span id="title" class="muted">（未选会话）</span>
    <button id="interrupt">中断</button>
    <span id="status">未连接</span>
  </div>

  <!-- ⚠️ **引导页**（`05` §2.2）：核心启动后没有任何会话与提供商，而
       「配置提供商 → 建初始会话」**是头的行为**（策略归头，机制归核心）。
       ⚠️ 此时**不能做别的**——所以它挡住输入框（见 `showOnboarding`）。
       ⚠️ **这个元素必须在 T9 里就存在**：T9 的验收第 1 条测的就是「引导页出现」，
       而 `$("onboarding")` 为 null 时 `showOnboarding()` 会抛
       `Cannot set properties of null` ⇒ **引导页根本不显示**。
       （原本它在 T10 才加，而 T10 的验收是 T9 的验收，顺序上倒置了。）
       ⚠️ 头的「引导」只到「提示你在哪配」为止——不做配置表单，见计划 D5。 -->
  <div id="onboarding" style="display:none" class="banner">
    <b>先配置一个提供商。</b>核心启动后是空的——没有提供商不能推进任何会话。<br>
    在**核心的终端**里执行：<br>
    <code>provider set &lt;id&gt; --key &lt;key&gt; --protocol claude-messages</code><br>
    <span class="muted">（头的「引导」目前只到「提示你在哪配」为止——见计划 D5：不做配置表单。）</span>
  </div>

  <div id="timeline"></div>

  <!-- ⚠️ **义务 1**：两个头槽位**并排**，且**空的也要显示**（§7.1）。
       裸核心连系统提示词都没有（`04` §1.3）是**合法形态**，
       但用户要能看见「它真的是空的」，而不是以为头坏了。 -->
  <div id="slots">
    <div class="slot"><b>系统提示词（义务 1）</b><div class="body muted" id="slot-system">（空）</div></div>
    <div class="slot"><b>首要提示词（义务 1）</b><div class="body muted" id="slot-premier">（空）</div></div>
  </div>

  <!-- ⚠️ **浮动槽**（`01` §3.2 的**尾槽位**）：义务 4 的落点。
       ⚠️ 它的内容是**头拼的**（`05` §3 的分工：轴的值核心给，怎么排是显示）。
       核心的 `slots.floating` 现在是空的——核心还没有装配器。 -->
  <div id="floating"><b>浮动</b> <span id="floating-body">（核心侧装配器尚未实现）</span></div>

  <div id="compose">
    <input id="text" placeholder="说点什么（回车发送）" autocomplete="off">
    <button id="send">发送</button>
  </div>
</div>

<div id="right">
  <!-- ⚠️ **义务 4**：安全轴在最上面，且跨轴告警就长在它下面。 -->
  <h4>安全轴（义务 4）</h4>
  <div id="safety-row">
    <!-- ⚠️ 标签**留空**：由 `renderAxes` 从核心交的 `label` 填（`05` §3：渲染归核心）。 -->
    <div class="axis"><label id="ax-safety-label"></label><select id="ax-safety"></select></div>
    <div id="alert"></div>
  </div>

  <h4>行为控制轴</h4>
  <div id="axes"></div>

  <!-- ⚠️ **义务 3 明说阻塞**：与其显示一个永远为空的「待决策」让用户分不清
       「没有待审批」与「闸门坏了」，不如**明说核心侧还没有闸门**。
       闸门的前提是工具，而工具集从插件宿主派生、插件宿主不存在（`01` §9.1）。 -->
  <h4>待决策（义务 3）</h4>
  <div class="banner" id="approvals-blocked">
    ⛔ <b>核心侧尚未实现。</b>审批流需要权限闸门，而闸门的前提是「工具」——
    本核心还没有工具（<code>01</code> §9.1：无插件 ⇒ 工具表为空）。
    这里**不是**「暂时没有待审批」，是**通道还不存在**。
  </div>

  <h4>声明（义务 2）</h4>
  <!-- ⚠️ 冲突在这里，**不在树里**（§7.2：树里的 ⚠ 会被忽略）。 -->
  <div id="claim-conflicts"></div>
  <div id="claims"></div>
</div>

<script src="app.js"></script>
</body>
</html>
```

- [ ] **Step 2: 换掉 `app.js`**

```javascript
// ⚠️ **零构建**（用户裁定）：原生 JS，没有打包、没有依赖、没有类型。
// 页面**直连核心**（`03` §1 的 WS + HTTP）——头进程不代理。
//
// ⚠️ **头的第一原则是反话**（`05` §1）：「核心越克制，头越要喧哗。」
// 核心为缓存优化，头为**人的注意力**优化。所以这里默认「多显示」而不是「省着显示」。
//
// ⚠️ 按 `05` §4 的四条义务逐条对：
//   ① 两个槽位原样显示 —— **做了**（并排，且空的也显示）
//   ② 声明可见       —— **做了**（保守：只做「声明主动」那一格，不做写入时自动检查）
//   ③ 审批走独立流   —— **阻塞**：核心侧闸门不存在，页面**明说**而不是显示空壳
//   ④ 安全轴显眼     —— **做了**（右栏最上 + 跨轴告警）
// ⚠️ 头仍是 `05` §1.1 的**可替换件**——义务必须履行，其余全是实现自由。

const $ = (id) => document.getElementById(id);

let core = "http://127.0.0.1:7788";
let ws = null;
let current = null;                 // 当前会话 id（**视图**，不是真相）
let currentProject = null;
const sessions = new Map();         // sessionId -> { projectKey }  ← §6.1：派生索引
const cursors = new Map();          // topic -> lastSeq           ← §7：每流一个游标
const claims = new Map();           // sessionId -> Set<path>     ← 声明（义务 2）
let axes = {};                      // 当前会话的轴值

// ---- 引导（`05` §2.2：**这是头的行为**，核心不做策略） ----
//
// ⚠️ 核心启动后**没有任何会话**（`01` §8.1），也没有任何提供商。
// 「配置提供商 → 建初始会话」是**头的策略**——它正是头比控制台强的地方
// （控制台没有视图，做不了这个判断）。
//
// ⚠️ **幂等是硬要求**：这段每次开页面都跑，所以「项目下有会话就打开它」——
// 少了它，每开一次浏览器就多一个会话（`01` §8.1）。
async function bootstrap() {
  // ⚠️ **轴表先拉**（G8：头不自带副本）。少了这一步，`AXES` 是空数组
  // ⇒ 右栏一个控件都画不出来，而用户看到的「没有轴」与「头坏了」分不清。
  await loadAxes();
  await loadSessions();

  // ① 有没有提供商？——没有就不能做别的（§2.2：引导页此时**挡住**其余操作）
  const providers = await (await fetch(core + "/providers")).json();
  if (!providers || providers.length === 0) { showOnboarding(); return; }
  hideOnboarding();

  // ② 当前项目下有会话？
  if (sessions.size === 0) {
    // ⇒ **由头调** session.create（核心只认命令，谁来调是头的事）
    command("boot-create", { kind: "session.create" });
    return;
  }

  // ③ 有 ⇒ 打开最近的（**不新建**——见上面的幂等）
  openSession([...sessions.keys()][sessions.size - 1]);
}

function showOnboarding() {
  $("onboarding").style.display = "block";
  $("text").disabled = true;
  $("send").disabled = true;
}

function hideOnboarding() {
  $("onboarding").style.display = "none";
  $("text").disabled = false;
  $("send").disabled = false;
}

async function loadSessions() {
  // ⚠️ §6.1：**全局会话列表是派生的**——对每个 pk 取 sessions 的并集。头自己拼。
  const projects = await (await fetch(core + "/projects")).json();
  sessions.clear();
  for (const pk of projects) {
    if (currentProject === null) currentProject = pk;
    const ids = await (await fetch(`${core}/projects/${pk}/sessions`)).json();
    for (const id of ids) sessions.set(id, { projectKey: pk });
  }
  renderSessions();
}

function renderSessions() {
  const box = $("sessions");
  box.innerHTML = "";
  if (sessions.size === 0) { box.innerHTML = '<div class="muted">还没有会话</div>'; return; }
  for (const id of sessions.keys()) {
    const el = document.createElement("div");
    el.className = "session" + (id === current ? " active" : "");
    el.textContent = id;
    el.onclick = () => openSession(id);
    box.appendChild(el);
  }
}

// ---- 打开会话：先快照、再带高水位订阅（`03` §7 的原子性） ----

async function openSession(id) {
  current = id;
  renderSessions();
  $("title").textContent = id;
  $("timeline").innerHTML = "";

  const state = await (await fetch(`${core}/sessions/${id}/state`)).json();
  const history = await (await fetch(`${core}/sessions/${id}/history`)).json();

  renderSlots(state.slots);
  renderAxes(state.axes);
  setStatus(state.state.status, state.state.turn);
  for (const turn of history.rendered) renderTurn(turn.from, turn.blocks);

  await loadClaims();

  // ⚠️ **用快照带回来的高水位当起点**——「拉快照 → 订阅」之间那段窗口不漏不重的唯一办法。
  subscribe(`session:${id}`, history.highWater);
}

function subscribe(topic, since) {
  if (!ws || ws.readyState !== WebSocket.OPEN) return;
  ws.send(JSON.stringify({ kind: "subscribe", topic, since }));
}

// ---- 渲染：**核心渲染，头只显示**（`05` §3） ----

function textOf(blocks) {
  return (blocks || []).filter((b) => b.kind === "text").map((b) => b.text).join("");
}

function renderTurn(from, blocks, cls) {
  const el = document.createElement("div");
  el.className = "turn " + (cls || from);
  el.textContent = (from === "user" ? "你  " : "⏺  ") + (textOf(blocks) || "（这一步没有正文输出）");
  $("timeline").appendChild(el);
  $("timeline").scrollTop = $("timeline").scrollHeight;
}

function renderMeta(text, cls) {
  const el = document.createElement("div");
  el.className = "turn meta " + (cls || "");
  el.textContent = text;
  $("timeline").appendChild(el);
  $("timeline").scrollTop = $("timeline").scrollHeight;
}

function renderSlots(slots) {
  // ⚠️ **空的也要显示**（§7.1）：`（空）` 与「有这个槽位但没内容」是两回事，
  // 而用户要能分清「核心没配提示词」与「头坏了」。
  const fmt = (b) => (b && b.length ? b.map(textOf).join("") : "（空）");
  $("slot-system").textContent = slots ? fmt(slots.system) : "（空）";
  $("slot-premier").textContent = slots ? fmt(slots.premier) : "（空）";
  // ⚠️ 浮动槽的正文由**核心**给（`05` §3：核心渲染）。核心还没有装配器 ⇒ 它恒空，
  // 而页面**如实显示「尚未实现」**——不假装它是空的提示词。
  $("floating-body").textContent = slots && slots.floating && slots.floating.length
    ? slots.floating.map(textOf).join("")
    : "（核心侧装配器尚未实现）";
}

function setStatus(status, turn) {
  $("status").textContent = `${status} · 第 ${turn} 步`;
}

// ---- 五轴（义务 4 的落点） ----
//
// ⚠️ **轴表从核心拉，头不自带副本**（G8 / `Axes.cs:8`：「任何别处（头的下拉…）
// 都从这张表读，**不许手抄**」）。加一条轴、改一个档，只改核心 `Axes.Table` 一处。
//
// ⚠️ 标签（`主动性` / `安全` …）与 `inContext` 也**归核心**——`05` §3「渲染归核心，头只显示」，
// 而下拉的标签就是渲染。核心交 `{ name, label, values, inContext }`，头只画控件。
// ⚠️ 由此 `GET /axes` 端点在 **T4**，而 `Axes` 要补上 `01` §3.5 的中文名（那是唯一真相来源，不是头）。
let AXES = [];
async function loadAxes() {
  // ⚠️ **一次拉取拿到三样**：轴表本身 + 告警线的两端。
  // ⚠️ **告警两端必须被真的覆盖**——只声明 `let ALERT_SAFETY = "prod"` 而不赋值，
  // 那两个字面量就还是硬编码的副本（「静默失效」的入口），白写了 `GET /axes` 的那两个字段。
  const cfg = await (await fetch(core + "/axes")).json();
  AXES = cfg.axes;
  ALERT_SAFETY = cfg.alertSafety;
  ALERT_FROM = cfg.alertFrom;
}

// ⚠️ **告警线的两端也归核心**：`安全=生产` 的档名与「权限≥授权」的起始档，
// 头里各写一个字面量就是**两份副本**——改一个档名就静默失效（告警不再响，而用户以为它在守）。
//
// ⚠️ 下面的初始值是**占位**，`loadAxes()` 会用核心交的值覆盖它。
// ⚠️ 但「有初始值」这件事本身有代价：**如果 `loadAxes()` 失败或没跑**，
// 这两个占位值就成了真的在用——那时告警按一份**没人维护的副本**判断。
// ⇒ 占位值取**永不触发告警**的那个（空串），宁可漏报也不误报：
// 漏报的表现是「该响没响」，而误报会让用户学会无视这个告警。
// ⚠️ 这也解释了为什么 `bootstrap()` 的第一句必须是 `await loadAxes()`。
let ALERT_SAFETY = "";
let ALERT_FROM   = "";

function renderAxes(values) {
  axes = values || {};
  $("ax-safety").innerHTML = "";
  $("axes").innerHTML = "";

  for (const a of AXES) {
    const row = document.createElement("div");
    // ⚠️ `inContext` 由**核心**给（`01` §3.5：`effort` 不进上下文）——
    // 头不再靠手抄的 `effort: true` 标记，那又是一份副本。
    row.className = "axis" + (a.inContext ? "" : " effort");

    const sel = document.createElement("select");
    sel.id = "ax-" + a.name;
    for (const v of a.values) {
      const o = document.createElement("option");
      o.value = v; o.textContent = v;
      if (axes[a.name] === v) o.selected = true;
      sel.appendChild(o);
    }
    sel.onchange = () => {
      if (!current) return;
      command(crypto.randomUUID().slice(0, 8),
        { kind: "session.setAxis", sessionId: current, axis: a.name, value: sel.value });
    };

    const lab = document.createElement("label");
    lab.textContent = a.label;

    // ⚠️ **安全轴放在右栏最上面单独一份**（义务 4），这里跳过它免得出现两个控件。
    // ⚠️ 但**标签也要填**——它是从核心来的（`05` §3：渲染归核心），不能因为换了位置就丢掉。
    if (a.name === "safety") {
      $("ax-safety-label").textContent = a.label;
      $("ax-safety").appendChild(sel);
      continue;
    }

    row.appendChild(lab); row.appendChild(sel);
    $("axes").appendChild(row);
  }

  renderAlert();
}

/// ⚠️ **跨轴告警**（`01` §3.5 定的那条）：`安全=生产` + `权限≥授权` → 告警。
/// ⚠️ 它是**纯提示、不联动**——不设「生产 ⇒ 权限封顶」，那是家长式干预且会掩盖
/// 「安全是建议」这个诚实的定位。
/// ⚠️ **跨轴告警**（`01` §3.5 定的那条）：`安全=生产` + `权限≥授权` → 告警。
/// ⚠️ 它是**纯提示、不联动**——不设「生产 ⇒ 权限封顶」，那是家长式干预且会掩盖
/// 「安全是建议」这个诚实的定位。
///
/// ⚠️ **两头都读核心交的值，不写字面量**：`ALERT_SAFETY` 是安全轴里代表生产的**档名**，
/// `ALERT_FROM` 是权限轴里「授权及以上」的**起始档**。写字面量的话，
/// 轴里改一个档名就会让告警**静默失效**——用户以为它在守，而它再也不响。
///
/// ⚠️ **判据是「档位序」而不是「是不是这两个值之一」**：权限是**单调阶梯**（`01` §3.5，
/// 0 禁用 … 5 信任），所以 `≥授权` 的正确写法是「在表里的下标 ≥ `ALERT_FROM` 的下标」。
/// 今天 `AT_OR_ABOVE` 恰好等于 {authorized, trusted} 两个，但那是**巧合**——
/// 权限轴加第 6 档时，枚举写法会漏掉它，而序数写法自动覆盖。
function alertFromIndex() {
  const perm = AXES.find((a) => a.name === "permission");
  return perm ? perm.values.indexOf(ALERT_FROM) : -1;
}

function renderAlert() {
  const perm = AXES.find((a) => a.name === "permission");
  const at = perm ? perm.values.indexOf(axes.permission) : -1;

  // ⚠️ **每一端都必须先「解析出来」才算数。**
  // `indexOf` 找不到时返回 `-1`，而 `at >= -1` **恒真** ⇒ 少了 `from >= 0` 这道守卫，
  // 只要安全档恰好相符就会**误报**（而误报会让用户学会无视这个告警）。
  // 同理 `ALERT_SAFETY === ""` 表示「核心还没把告警线交过来」（见 `loadAxes` 的注释），
  // 那时**不该**按一个占位值判断。
  const from = alertFromIndex();
  const ready = ALERT_SAFETY !== "" && from >= 0;
  const risky = ready && axes.safety === ALERT_SAFETY && at >= from;

  $("safety-row").className = risky ? "alert" : "";
  $("alert").textContent = risky
    ? "⚠️ 生产环境 + 权限 ≥ 授权：高危操作会被**自动放行**。确认这是你要的。"
    : "";
}

// ---- 声明（义务 2） ----

async function loadClaims() {
  const all = await (await fetch(core + "/claims")).json();
  claims.clear();
  for (const c of all) {
    if (!claims.has(c.sessionId)) claims.set(c.sessionId, new Set());
    claims.get(c.sessionId).add(c.path);
  }
  renderClaims();
}

function renderClaims() {
  // ⚠️ §7.2：**呈现的关键不是列表，是「冲突」**。
  // 列表画在下面，冲突**顶到最上面**——因为树里（或者列表里）的 ⚠ 会被忽略。
  const paths = [...claims.entries()].flatMap(([sid, set]) => [...set].map((p) => ({ sid, p })));
  const conflicts = [];
  for (let i = 0; i < paths.length; i++) {
    for (let j = i + 1; j < paths.length; j++) {
      if (paths[i].sid === paths[j].sid) continue;
      const a = paths[i].p.toLowerCase(), b = paths[j].p.toLowerCase();
      if (a === b || a.startsWith(b + "\\") || b.startsWith(a + "\\"))
        conflicts.push(`${paths[i].sid} ✕ ${paths[j].sid}：${paths[i].p}`);
    }
  }

  $("claim-conflicts").innerHTML = "";
  for (const line of conflicts) {
    const el = document.createElement("div");
    el.className = "banner";
    el.textContent = "⚠️ 声明冲突 — " + line;
    $("claim-conflicts").appendChild(el);
  }

  const box = $("claims");
  box.innerHTML = claims.size === 0 ? '<div class="muted">（没有声明）</div>' : "";
  for (const [sid, set] of claims) {
    for (const p of set) {
      const el = document.createElement("div");
      el.className = "claim";
      el.innerHTML = `<span class="who">${sid}</span> ${p}`;
      box.appendChild(el);
    }
  }
}

// ---- WS ----

function connect() {
  ws = new WebSocket(core.replace("http", "ws") + "/ws");

  ws.onopen = () => {
    $("status").textContent = "已连接";
    // ⚠️ **项目流**：会话列表是派生的（§6.1），靠 `session.created` 更新；
    // 声明也走这条流（`03` §5）。不订它的话，在**控制台**建会话或占区域，
    // 这个页面看不见，且不会自愈。
    if (currentProject) ws.send(JSON.stringify({ kind: "subscribe", topic: `project:${currentProject}` }));
    if (current) openSession(current);   // 重连一律拉快照重建（§6.2 唯一的降级路径）
  };

  ws.onclose = () => {
    $("status").textContent = "已断开（3 秒后重连）";
    setTimeout(connect, 3000);
  };

  ws.onmessage = (e) => {
    const frame = JSON.parse(e.data);

    if (frame.kind === "ack") {
      if (frame.accepted && frame.sessionId && !sessions.has(frame.sessionId)) loadSessions();
      if (!frame.accepted) renderMeta("✗ " + frame.reason, "error");
      return;
    }

    if (frame.kind === "resync_required") {
      // ⚠️ §6.2：降级路径**只有一条**——拉快照重建。
      // **不发明「部分合并」**：那是另一套状态机，且必然与快照对不齐。
      renderMeta("（事件跟不上，已重新拉取快照）");
      cursors.delete(frame.topic);
      if (current && frame.topic === `session:${current}`) openSession(current);
      if (frame.topic.startsWith("project:")) loadClaims();
      return;
    }

    if (frame.kind !== "event") return;

    // ⚠️ **每流独立的游标**（§7）。只丢弃重复，**不排序**——排序是核心的保证。
    if (frame.seq <= (cursors.get(frame.topic) ?? 0)) return;
    cursors.set(frame.topic, frame.seq);

    if (frame.topic.startsWith("project:")) { handleProjectEvent(frame.payload); return; }
    handleEvent(frame.payload);
  };
}

function handleProjectEvent(p) {
  switch (p.kind) {
    case "session.created": loadSessions(); break;
    // ⚠️ 义务 2 的实时性靠这两条——声明变化**必须**刷新右栏，
    // 否则「谁在动哪些文件」只在开页面那一刻是对的。
    case "claim.acquired":
    case "claim.released": loadClaims(); break;
  }
}

function handleEvent(p) {
  switch (p.kind) {
    case "message.appended":
      if (p.sessionId === current) renderTurn(p.from, p.blocks);
      break;

    case "session.step.begin":
      // ⚠️ 「在跑」的信号源是它，不是 `session.state`——后者是磁盘的镜像，而磁盘**从不存 Running**。
      if (p.sessionId === current) setStatus("Running", p.turn - 1);
      break;

    case "session.step.end":
      if (p.sessionId === current) {
        setStatus("Idle", p.turn);
        if (p.refused) renderMeta("⚠️ 模型拒绝了这次请求");
        else if (["max_tokens", "length", "incomplete"].includes(p.stopReason))
          renderMeta(`（输出被截断了：${p.stopReason}）`);
      }
      break;

    case "session.state":
      if (p.sessionId === current) setStatus(p.status, p.turn);
      break;

    case "axis.changed":
      // ⚠️ 只在**真的迁移**时来（核心侧已保证），所以这里直接重渲染即可。
      if (p.sessionId === current) { axes[p.axis] = p.value; renderAxes(axes); }
      break;

    case "error":
      // ⚠️ **业务失败走这里**（`03` §3 的分界线）——它不是 `rejected`（命令没写错）。
      if (p.sessionId === current) renderMeta("✗ " + p.message, "error");
      break;
  }
}

// ---- 命令（`03` §3） ----

function command(id, payload) {
  if (!ws || ws.readyState !== WebSocket.OPEN) { renderMeta("✗ 没连上核心", "error"); return; }
  ws.send(JSON.stringify({ kind: "command", id, payload }));
}

function send() {
  const text = $("text").value.trim();
  if (!text || !current) return;
  command(crypto.randomUUID().slice(0, 8),
    { kind: "session.send", sessionId: current, blocks: [{ kind: "text", text }] });
  $("text").value = "";
}

// ---- 接线 ----

$("send").onclick = send;
$("text").onkeydown = (e) => { if (e.key === "Enter") send(); };
$("new").onclick = () => command(crypto.randomUUID().slice(0, 8), { kind: "session.create" });
$("interrupt").onclick = () => {
  if (current) command(crypto.randomUUID().slice(0, 8), { kind: "session.interrupt", sessionId: current });
};

// ⚠️ **`GET /providers` 是头的引导要用的**（`05` §2.2）：没有提供商时**不能做别的**。
// 端点不存在的话 `bootstrap()` 会在 `fetch(...).json()` 上抛，
// 而那是**第一条**要跑的异步动作 ⇒ 整个页面停在空白，控制台里一条错。
// ⇒ 它在 `ControlServer.MapSnapshots` 里加：

//         // ⚠️ 头的引导（`05` §2.2）要判断「有没有提供商」——没有就得挡住其余操作。
//         // ⚠️ **`apiKey` 绝不进响应**：它是本机秘密，而头的页面在浏览器里、还开着 CORS（`*`）。
//         // 只回「有没有」，不回「是什么」。
//         app.MapGet("/providers", () =>
//             Results.Json(host.Providers.List().Select(p => new { p.Id, p.Protocol, p.DefaultModel }),
//                 WireJson.Options));

(async () => {
  try { core = (await (await fetch("/config.json")).json()).core; } catch { /* 用默认值 */ }
  connect();
  await bootstrap();
})();
```

⚠️ **引导页需要 `GET /providers`**——而那个端点**不存在**。在 `ControlServer.cs` 的 `MapSnapshots` 里加：

```csharp
        // ⚠️ 头的引导（`05` §2.2）要判断「有没有提供商」——没有就得挡住其余操作。
        // ⚠️ **`apiKey` 绝不进响应**：它是本机秘密，而头的页面在浏览器里、
        // 还开着 CORS（`*`）。只回「有没有」，不回「是什么」。
        app.MapGet("/providers", () =>
            Results.Json(host.Providers.List().Select(p => new { p.Id, p.Protocol, p.DefaultModel }), WireJson.Options));
```

- [ ] **Step 3: 起核心与头，手工走一遍（浏览器）**

Run:
```bash
dotnet run --project src/Lilara.Core   # 终端 A
dotnet run --project head              # 终端 B
```
Expected（逐条对，**每条都要真的看到**）：
1. 打开 `http://127.0.0.1:7790` → **引导页出现**（没有提供商时）
2. 控制台 A 里 `provider set p --key k --protocol claude-messages` → 刷新页面 → 引导页消失
3. 页面**自动建了一个会话**（`01` §8.1 由头调）——**刷新第二次不再多建**（幂等）
4. 右栏五个轴都有值，安全轴在最上面
5. 控制台 A 里 `axis set <会话> safety prod` → 页面右栏**立刻**变成高亮（`axis.changed` 到达）
6. 控制台 A 里 `claim src` → 页面右栏**出现**这条声明
7. 控制台 A 里 `axis set <会话> permission authorized` → **跨轴告警出现**
8. 底部两个头槽位**并排**、都显示「（空）」；浮动槽显示「核心侧装配器尚未实现」
9. 右栏「待决策」区显示**阻塞说明**，不是空列表

- [ ] **Step 4: 提交**

```bash
git add head/ src/Lilara.Core/Control/ControlServer.cs
git commit -m "feat(head): 三栏骨架 + 引导流程 + 五轴面板（义务 4）；义务 3 明说阻塞而不是空壳；GET /providers"
```

---

### Task 10: 头 —— 声明视图与冲突上浮 + 收尾

**Files:**
- Modify: `head/wwwroot/app.js`（⚠️ **增量**——T9 已把整份换成三栏版，
  本任务只往里**加**声明视图的那几段，**不是**再替换一次）
- Modify: `head/Program.cs`（加一条注释，代码不动）

**Interfaces:**
- Consumes: T9 的全部
- Produces: 页面（无对外接口）

- [ ] **Step 1: 核 `#onboarding` 元素在（T9 已加，别再加一遍）**

⚠️ 原先这个元素排在 T10，而 **T9 的 `app.js` 就引用了它** ——
`$("onboarding")` 为 null 时 `showOnboarding()` 抛 `Cannot set properties of null`，
于是**引导页根本不显示**，而 T9 的验收第 1 条测的正是「引导页出现」。
⇒ 已挪进 T9 的 `index.html`（见那条 `⚠️ 这个元素必须在 T9 里就存在`）。

**这一步现在只是核对**，不是新增：

```bash
# 两处都必须命中
grep -n 'id="onboarding"' head/wwwroot/index.html
grep -n 'onboarding' head/wwwroot/app.js
```

Expected：两条都有输出。**若 `index.html` 那条没有**，说明 T9 没照计划做，补上它
（内容见 T9 段的 `index.html` 块）。**别在这里重新设计它。**

- [ ] **Step 2: 起核心与头，手工验声明冲突上浮**

Run（两个终端，同 T9），然后：
1. 控制台 A 建第二个会话：`session create`
2. `session activate <A>` → `claim src`
3. `session activate <B>` → 尝试 `claim src` → **核心回 `rejected` 并点名 A**
4. 在新会话**发一条消息**（页面点一下那个会话再发）确认核心照常工作

Expected：第 3 步的拒绝消息里有 A 的 id。

⚠️ **但这不证明头侧的上浮可达。** 头侧上浮是「**一条声明落在两个会话**」的渲染，
而同一项目内的会话由 `HandleClaimAcquire` 挡住了 ⇒ **同项目内它确实不可达**。
它是**防御性渲染**：留给将来「声明可以不互斥」的形态（如派生树例外）。
⚠️ **不要为了让它可达而放开核心的检查。**

⚠️ **不过「同一个核心只会有一个项目」这句话是错的，别照着写。** `/claims`
按 `ProjectKeys()` **跨所有项目**枚举，而每个会话的声明读自**它自己的工作目录**
（`ClaimStore(h.Spec.WorkingDirectory)`）。`projectKey` 确实按工作目录算，
但**盘上载入的会话可以来自别的工作目录**（`CommandDispatcher.TryFind` 会扫所有项目目录）。
⇒ 两个工作目录不同的会话各存一份 `claims.json`、**互不检查**，
于是「一条声明出现在两个会话下」在**跨项目**时是**可达**的——那正是头上浮的用武之地。
（只有在「本机只有一个工作目录」的常见情形里它才不可达。）

- [ ] **Step 3: 给 `head/Program.cs` 加一条注释**

在文件顶部注释里追加：

```csharp
// ⚠️ **头进程只做一件事：托管静态资源。** 它**不代理**、不缓存、不理解协议——
// 页面自己连核心（`05` §1.1：头是可替换件，不该背协议的语义）。
// ⚠️ 所以这个文件**不该长大**。要加东西时先问：那是头的事，还是核心的事？
// （`05` §3：渲染归核心。头只显示。）
```

- [ ] **Step 4: 全量验证**

Run: `dotnet test -v q`
Expected: 全绿。

Run: `dotnet build -warnaserror -v q`
Expected: 0 警告 0 错误。

⚠️ **再手工走一遍 T9 的 9 条**——尤其第 3/5/8 条（它们跨了核心与头的边界，是单测覆盖不到的那类）。

- [ ] **Step 5: 提交**

```bash
git add head/
git commit -m "feat(head): 声明视图与冲突上浮的渲染 + 头部进程职责注释"
```

---

## 验证

1. `dotnet test` —— **全绿**；**既有金样一条都不许改断言值**（只能加新的）。
   ⚠️ **别写死「N 基线」**：这个数每完成一个任务就变（执行中从 371 一路涨到 435），
   **写死的数字必然腐**——一处写死的基线在派发时是错的，就有两个后果：
   实现者拿它当验收标准（会以为"少了几条"），或直接无视所有数字（连带无视真信号）。
   ⇒ 以**当前 HEAD 的全量绿**为准。
2. `dotnet build -warnaserror` —— 0 警告（本项目 `TreatWarningsAsErrors`）
3. **端到端手工**：T9 Step 3 的九条 + T10 Step 2 的四条，**逐条真的看到**。
   ⚠️ **逐条先写下预期、再跑、再记下实际看到的**（只看"没崩"不算看到）。
   其中 T10 的冲突那条**三个可观测量都要记**，缺一不算跑过：
   ① 核心回 `rejected`；② 头在**两个会话下都显示对方的声明**
   （`GET /claims` 的同一份渲染）；③ 用户自己那条**没有被别人删掉**。
   ⚠️ ②是**要害**：真阻塞的实现会让双方的声明**互相看不见**——
   那不是"更安全"，那是**记下了一份用户看不见的状态**，正是 `01` §12 要避免的。
4. **变异确认**（G7）：T1 Step 5 与 T6 Step 5 各两条，**跑之前先 grep 确认变异落上了**
5. `git`：动手前记下 `master` 的 SHA 当回滚点

## 明确不做（写进代码注释的偏离）

见开头的 D1–D9。**其中 D1 是用户裁定**，D2/D3/D4/D5/D9 是本计划的判断，各自在对应文件的注释里点名。
⚠️ D9 是**派 T6 之前**由控制器实测补上的（原表里没有，而 `01` §12.1 明确要求）；D8 是执行中评审发现后追加。
