# 工具容量与框架 实现计划（批次 1）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让工具**撑不爆上下文**，并且结果能**自己说清自己是什么**——同时把两个被 dll 锁定的契约（`ITool` / `ToolResultEvent`）一次改到位。

**Architecture:** 三条都属于「工具结果这条路」，所以**同批**：
① 权限类别挂 `ITool`（**执行期**闸门，不动工具表）
② `read` 的 `offset`/`limit`/上限
③ `ls` 的元数据 + 截断
④ 两者的**表达层** = `ToolResultEvent` 上新增的**框架**那一格

**Tech Stack:** .NET 10 · xUnit · System.Text.Json

**Spec:** [`01` §3.7](../../01-会话设计.md)（框架与内容）· §9.2（闸门）· §9.3（上限/分页/元数据）· [`04` §9](../../04-插件与内部结构.md)（加载与物理形态）

## 用户裁定（本计划的约束来源，2026-10-07）

| # | 裁定（原话） | 对计划的影响 |
|---|---|---|
| R-A | 「工具限制**不在于模型是否可以调用，而是调用后能不能生效，保住工具列表**」 | 闸门在**执行期**；工具表**一个字不动** |
| R-B | 「read 文件大小…**必须有所限制**，尤其是大文件一个 read 下去直接给自己上下文撑爆了」 | 上限是**硬约束**，不是建议 |
| R-C | 「**偏移、限制长度我觉得都是必要的**」 | `offset`/`limit` 进 schema |
| R-D | 「ls 也许可以顺带包含一些文件或文件夹的元数据…**防止模型一不小心 read 了一个大文件**」 | 元数据是**软信号**——`00` §7 判据 5：**意图靠工具表表达，不靠请求** |
| R-E | 「现在是**开发阶段**，你不需要担心任何会导致当前缓存失效的风险，**只考虑上线后是否安全和友好**」 | 全程**不拿"破前缀"当改动的否决理由**；而**上线后**的运行期性质照常算 |
| R-F | 「先做普通工具，**再考虑征求意见等行为控制**…接口得预先考虑到」 | 审批那套（`问` / `WaitingForInput`）**本批不做**，只留钩子 |

## Global Constraints

继承 `2026-10-03-admin-apis.md` 的 G1–G16，逐条仍然有效。本批新增：

- **G17 上限只能有一个源。** `read` 的单次上限与 `ls` 里那句「这个文件太大」**必须从同一个常量读**。
  ⚠️ 两个源 ⇒ 改一处则 `ls` **当场开始说谎，而没有任何东西会红**（同族：`01` §8.0.2「两层判据必须共用一个谓词」）。
- **G18 框架与内容不许共用字面量。** 模型只能靠「**这是一个独立的块**」区分二者
  ⇒ 必须有一条测试喂「**首行伪装成框架**」的内容，断言它没被当成框架。
- **G19 拒绝/超限理由**必须是**那一刻的事实**。`ToolResultEvent` 是记录（只追加）
  ⇒ 任何「档位 / 上限」都不能在**渲染期**重算——重算会让同一条历史因改档位而改变说法。
- **G20 写盘格式的变化必须显式。** `Json.FormatVersion` 今天 = **4**。
  ⚠️ **加可空字段不改老文件字节**（`Json.Options` 开了 `WhenWritingNull`，没设的键整个不出现）
  ⇒ **本批大概率不需要动版本号**；但**每个新字段都要按这条量一次**，把答案写进注释（`Ref` 字段就是先例）。
- **G21 四钩子只有「执行」落地。** 本批**不做**渲染 / 优化 / 取消钩子，也不做 `tool.setEnabled`。

## Review Focus

本计划里**没有任务自己的测试会碰**、而最可能咬人的五类输入/条件（按可能性排序）：

1. **`read` 的 `offset` 落在多字节字符中间**（UTF-8 续字节）—— 合理预期：**不产生半个字符**。
   实测过的先例：`File.ReadAllBytes` 之后整体 `GetString`（替换非法序列），而分页会把「切点」交给用户。
2. **`limit` 给 0 / 负数 / 巨大值 / 非数字**—— 合理预期：**说人话的拒绝**，不是异常（异常会让那一对只落一半 ⇒ 上游 400）。
3. **`ls` 一个 5 万条目的目录**—— 合理预期：**截断并明说**「前 N 条，共 M 条」，不是静默截断。
4. **一条结果被回收之后，框架那一格**—— 合理预期：**框架原样**（仍然是 `[read a.log …]`），只有内容变成注记。
   ⚠️ 这条是本批最容易做错的：回收今天是【整个 `Content` 换掉】。
5. **内容首行长得像框架**（`[read a.log 1-2/3]` 之类）—— 合理预期：**不被当成框架**（G18）。

---

## 落地顺序（依赖决定，不能跳）

```
T1 上限的唯一来源 + ToolArgs 补全      ← 独立；T2/T3 都要它
T2 read: offset / limit / 上限          ← 依赖 T1
T3 ls: 元数据 + 截断                    ← 依赖 T1（G17：读同一个上限）
T4 ToolResultEvent 的 Framing 那一格    ← 依赖 T2/T3 的形状定下来（它要装的就是那两样）
T5 ITool.PermissionClass + Gate.Decide  ← 独立；但必须与 T4 同批（同改契约）
T6 反向证明与变异验证                    ← 收尾，见文末
```

⚠️ **T4 与 T5 必须在同一批**：它们改的是**被 dll 锁定的两个契约**（`ToolResultEvent` / `ITool`），
分开做等于改两遍契约（`04` §9.3：`pluginId` 在 dll 内声明 ⇒ 契约先定）。

---

### Task 1: 上限的唯一来源 + `ToolArgs` 补全

**Files:**
- Create: `src/Lilara.Core/Session/Tools/ToolLimits.cs`
- Modify: `src/Lilara.Core/Session/Tools/ListTool.cs`（`ToolArgs` 那个 `internal static class`）
- Test: `tests/Lilara.Core.Tests/ToolArgsTests.cs`（新建）

**Interfaces:**
- Produces: `ToolLimits.MaxResultBytes`（**唯一**的上限来源）· `ToolLimits.MaxDirectoryEntries` ·
  `ToolArgs.Number(argumentsJson, key)` → `long?`
- Consumes: 无

⚠️ **两个常量都要按 `Reclaim.ProtectedRecentResults = 4` 的纪律写**：
**取一个保守值 + 注释里明写「这是占位」**——上限那个数是**策略值**，`01` §9.3 只说了「必须有」，
**没说多少**（同 §11.3 的「最近 N 步」而 N 是未定项）。

- [ ] **Step 1: 写失败的测试**

```csharp
public class ToolLimitsTests
{
    // ⚠️ 这两条钉的是「唯一来源」本身：改了常量，两处一起变。
    [Fact]
    public void ReadAndList_ShareTheSameSource() { /* 见 T2/T3：两边都引用 ToolLimits，不写自己的字面量 */ }
}

public class ToolArgsTests
{
    [Theory]
    [InlineData("{\"offset\":42}", 42L)]
    [InlineData("{\"offset\":-1}", -1L)]        // ⚠️ 负数**这里**不判合法，交给调用方（它知道语义）
    [InlineData("{\"offset\":1.5}", null)]      // 小数 ⇒ null（偏移不是小数量）
    [InlineData("{\"offset\":\"42\"}", null)]   // 字符串 ⇒ null（**不**宽容地解析）
    [InlineData("{\"other\":1}", null)]         // 缺键
    [InlineData("not json", null)]              // 整段不是 JSON
    public void Number_ReadsWhatItSaysOrReturnsNull(string json, long? expected) { … }
}
```

- [ ] **Step 2: 跑测试确认红**
- [ ] **Step 3: 实现**

⚠️ `ToolArgs.Number` 的三条判据（与既有的 `String` 同一条纪律）：
1. **缺键 / 类型不符 / 整段不是 JSON ⇒ `null`，不抛**——模型是不可信输入源（`ListTool.cs` 那段注释）
2. **数值**走 `TryGetInt64`（**不是** `GetDouble` 再截断：`1.5` 不是偏移）
3. **只读、不重写参数**——解析出来的是**值**，记录里存的仍是原文（逐字那条铁律在记录层）

- [ ] **Step 4: 跑测试确认绿**

---

### Task 2: `read` 的 `offset` / `limit` / 上限

**Files:**
- Modify: `src/Lilara.Core/Session/Tools/ListTool.cs`（`ReadTool`）
- Test: `tests/Lilara.Core.Tests/ToolTests.cs`

**Interfaces:**
- Consumes: `ToolLimits.MaxResultBytes` · `ToolArgs.Number`
- Produces: `read` 的三个新参数语义（T4 的框架要写它们的落点）

**三个参数的语义（写进 schema 的 `description`，因为那是唯一教模型的地方）**：

| 参数 | 语义 | 省略时 |
|---|---|---|
| `path` | 相对工作目录（**不变**） | 必填 |
| `offset` | **起始字节**（0 基） | 0 |
| `limit` | **最多读多少字节** | 到文件尾，**但不超过 `MaxResultBytes`** |

- [ ] **Step 1: 写失败的测试**

```csharp
[Fact] public void Read_WithNoLimit_IsCappedAtTheLimit() { … }          // 上限生效
[Fact] public void Read_WithOffset_ReturnsTheTail() { … }              // 偏移生效
[Fact] public void Read_WithOffsetPastTheEnd_SaysSo() { … }            // 不是空结果
[Fact] public void Read_SplittingAMultiByteChar_DoesNotEmitHalfAChar() { … }  // ⚠️ Review Focus 1
[Fact] public void Read_WithANegativeLimit_IsRejectedInPlainWords() { … }     // 不是异常
[Fact] public void Read_WithANonNumericOffset_SaysWhatItWanted() { … }
[Fact] public void Read_DoesNotSilentlyTruncate() { … }   // ⚠️ 返回的东西必须**说清**它是一段
```

⚠️ **最要紧的那条判据**：**不静默截断**。`01` §9.3：

> **宁可明着少，不可暗着假。**

⇒ 返回里必须带「这是第 X–Y 字节，共 Z 字节」。**本批它先进内容**（T4 会把它挪进框架）。

- [ ] **Step 2: 跑测试确认红**
- [ ] **Step 3: 实现**

⚠️ **三个实现要点**：
1. **先看文件长度再决定读多少**（`FileInfo.Length`），**不要** `ReadAllBytes` 之后再切——
   后者在 20 MB 文件上仍然是 20 MB 的分配（上限防的是**上下文**，不是内存，但没必要白花）
2. **多字节字符的切点**：`offset`/`limit` 是**字节**，而结果要是**文本**。
   ⇒ 与既有那条一致（`new UTF8Encoding(false, throwOnInvalidBytes: false).GetString(bytes)`）——
   **非法序列变替换字符**，不是异常。⚠️ 但要**避免把切点落在字符中间**：宁可**多读几个字节**到下一个字符边界。
3. **失败仍是内容**（`ToolText.Of(...)`），不抛——异常会让那一对只落一半 ⇒ 上游 400

- [ ] **Step 4: 跑测试确认绿**

---

### Task 3: `ls` 的元数据 + 截断

**Files:**
- Modify: `src/Lilara.Core/Session/Tools/ListTool.cs`（`ListTool` + 私有的格式函数）
- Test: `tests/Lilara.Core.Tests/ToolTests.cs`

**Interfaces:**
- Consumes: `ToolLimits.MaxDirectoryEntries` · `ToolLimits.MaxResultBytes`（G17）
- Produces: `ls` 的行格式（T4 的框架要写表头）

**两件必须一起做**（`01` §9.3：加元数据会让无上限**更严重**）：

| 加什么 | 形状 |
|---|---|
| **元数据** | 每行：名字（目录带 `/`）· **大小**（目录为空）· 修改时间 |
| **截断** | 超过 `MaxDirectoryEntries` ⇒ 截断，**并在框架里明说** |

⚠️ **排序与格式化都不能漂**（工具输出进上下文，`ListTool` 那段注释已有此判据）：
- 排序 **`StringComparer.Ordinal`**（**已经是**，别动）
- 大小显示**用固定宽度**，否则列对不齐、模型读起来更费劲

- [ ] **Step 1: 写失败的测试**

```csharp
[Fact] public void List_ShowsSizeForFiles() { … }
[Fact] public void List_ShowsNoSizeForDirectories() { … }     // 目录没有"大小"这件事
[Fact] public void List_TruncatesAndSaysHowMany() { … }        // ⚠️ Review Focus 3
[Fact] public void List_OfAnEmptyDirectory_IsUnchanged() { … } // 既有行为不许回归
```

- [ ] **Step 2: 跑测试确认红**
- [ ] **Step 3: 实现**

⚠️ **`Directory.Exists` 在循环里 = 每个条目一次 stat**。用 `DirectoryInfo.EnumerateFileSystemInfos()`
一次拿到 `FileSystemInfo`（它有 `Attributes` / `Length` / `LastWriteTime`），**别为每个条目再问一次盘**。

- [ ] **Step 4: 跑测试确认绿**

---

### Task 4: `ToolResultEvent` 的 `Framing` 那一格

**Files:**
- Modify: `src/Lilara.Core/Kernel/Tool.cs`（`ToolOutcome` 加 `Framing`）
- Modify: `src/Lilara.Core/Kernel/TimelineEvent.cs`（`ToolResultEvent` 加 `Framing`）
- Modify: `src/Lilara.Core/Session/Tools/ToolExecutor.cs`（搬过去）
- Modify: `src/Lilara.Core/Session/TimelineRender.cs`（**第三步**：框架 → 一个块，恒定在内容之前）
- Test: `tests/Lilara.Core.Tests/DiskFormatGoldenTests.cs` · 新建 `FramingTests.cs`

**Interfaces:**
- Consumes: T2/T3 定下来的那两样（段号 / 条数）
- Produces: `ToolFraming` / `ToolFramingField`（**结构化**，不是一段文本）

⚠️ **为什么结构化而不是一段字符串**：核心要能**取用**其中的事实（比如判「这条结果被截断过」），
而不是去 `StartsWith("[read")` 解析自己刚拼的字符串。

```csharp
/// <summary>框架——工具对**自己那次调用**的结构化描述。⚠️ 它是**事实**（那次调用返回了什么）⇒ **落盘**。</summary>
/// <param name="Fields">⚠️ **顺序由工具定**（它知道哪个字段重要）——渲染器**不排序**（排序会漂）。</param>
public sealed record ToolFraming(string Tool, IReadOnlyList<ToolFramingField> Fields);

public sealed record ToolFramingField(string Key, string Value);
```

- [ ] **Step 1: 写失败的测试**

```csharp
[Fact] public void Frame_IsRenderedBeforeTheContent() { … }
[Fact] public void Frame_IsItsOwnBlock_NotMergedIntoText() { … }      // G18：独立的块
[Fact] public void Reclaim_ReplacesTheContent_ButNotTheFrame() { … }   // ⚠️ 本批**最要紧**的一条
[Fact] public void Fetch_ReturnsTheContent_WithoutTheFrame() { … }     // fetch 取的是"当时那串块"
[Fact] public void Framing_DoesNotChangeTheDiskGoldenBytes() { … }     // G20
```

⚠️ **第三条测试就是本批存在的理由**：回收今天是【整个 `Content` 换掉】，
**它必须变成只换 `Content`、`Framing` 一个字不动**。变异：把 `Framing` 也一起换掉 ⇒ 这条必须红。

⚠️ **第四条测试是一条容易做错的边界**：`SnapshotOf` 读的是**记录**（未经渲染），
所以 `fetch` **天然**取不到框架。⚠️ **别把它"修"成从渲染后的形态算**——
那样算出来的会是**那段注记**，`fetch` 当场永久取不回（`FetchTool` 的注释里有完整理由）。

- [ ] **Step 2: 跑测试确认红**
- [ ] **Step 3: 实现**

**落点与次序**（`TimelineRender` 从两步变三步）：

```
① 解库引用（Dereference）
② 换回收渲染（Reclaim.Apply）—— **只换 Content**
③ 把 Framing 渲染成一块、插到 Content 之前   ← 新增
```

⚠️ **两个实现要点**：
1. **框架文本的拼法只应有一处**（`ToolFraming.Render()` 之类），
   否则「定长、位置固定」这条会因为两处各拼一遍而漂
2. ⚠️ **盘上老记录没有 `Framing` ⇒ `null` ⇒ 不插块 ⇒ 逐字节与今天相同**。
   这就是 G20 的答案（`WhenWritingNull` 兜住，**不需要动 `FormatVersion`**），
   **把它写进注释 + 用金样钉住**

- [ ] **Step 4: 跑测试确认绿**

---

### Task 5: `ITool.PermissionClass` + `Gate.Decide`

**Files:**
- Modify: `src/Lilara.Core/Kernel/Tool.cs`（`ITool` 加成员 + `PermissionClass` 枚举）
- Create: `src/Lilara.Core/Session/Tools/Gate.cs`
- Modify: `src/Lilara.Core/Session/Tools/ToolExecutor.cs`（三路结果）
- Modify: `src/Lilara.Core/Session/Tools/FetchTool.cs` · `ListTool.cs`（三个内建各自声明）
- Modify: `src/Lilara.Core/Session/Stepper.cs`（把权限轴传进执行相位）
- Test: 新建 `GateTests.cs`

**Interfaces:**
- Consumes: `Axes.Permission` 的当前值 · `ITool.PermissionClass`
- Produces: `Gate.Decide(PermissionClass, string permissionAxis)` → `Verdict`（`Allow` / `Deny`；`Ask` **本批不产生**）

⚠️ **成员没有默认值**（与 `ITool` 上已有的那几个一致的形状）——
**编译器逼每个工具作者表态**：MCP 那条路拿不到声明时，由**适配层**补 `Writing`
（`04` §5 已经这么写了，而**那个默认只在适配层**，不在接口上）。

⚠️ **`01` §3.5 那张 `档位 × 类别` 表是唯一来源**，`Gate` 里**照抄那 6×4 格**，别自己推。
⚠️ **`Verdict` 里留着 `Ask` 这个值，但本批没有生产者**——`01` §9.2 的裁定是
「工具限制走执行期闸门」，而 `Ask` 要转 `WaitingForInput`，**那是单独一批**（R-F）。

- [ ] **Step 1: 写失败的测试**

```csharp
[Theory]
[InlineData("off")]          // 0 禁用 ⇒ **四类全拒**
[InlineData("readonly")]     // 1 ⇒ 只有 ReadOnly 通
[InlineData("investigate")]  // 2 ⇒ ReadOnly + Consuming 通
[InlineData("standard")]     // 3 ⇒ 前三通（Writing 本该"问"，本批**降级为拒**，见下）
[InlineData("authorized")]   // 4
[InlineData("trusted")]      // 5 ⇒ 四类全通
public void Gate_MatchesTheTableInSection3_5(string axis, PermissionClass cls, bool expected) { … }
```

⚠️ **必须有一个 `Writing` 的测试替身**，否则**这张表根本测不到**——
今天三个内建**全是只读**，唯一的可观测效果只有 `off` 档那一格。
⚠️ 替身是**诚实的**：产品代码里**不该**有 `Writing` 工具（没有写盘的手），
而测闸门**必须**有一个 ⇒ 放在测试工程里，**别为了测试往产品里塞一个假工具**。

> **判据**：探针（替身）**不是**被测物（闸门），但这里被测物**就是**闸门本身 ⇒ 成立。
> （区别在于：测试断言的是「闸门怎么判」，不是「替身干了什么」。）

- [ ] **Step 2: 跑测试确认红**
- [ ] **Step 3: 实现**

```csharp
// ToolExecutor 里那一行拆成三路
var verdict = tool is null ? Verdict.Unknown : Gate.Decide(tool.PermissionClass, permissionAxis);

var outcome = verdict switch
{
    Verdict.Allow  => await tool!.RunAsync(new ToolCall(call.CallId, call.Name, call.Arguments), context, ct),
    Verdict.Deny   => ToolText.Of(Gate.DenyReason(tool!.PermissionClass, permissionAxis)),
    Verdict.Unknown => new ToolOutcome { Content = [new Block.TextBlock($"没有名为 '{call.Name}' 的工具。")] },
    // ⚠️ Verdict.Ask 本批到不了（没有生产者）；它到的那天，这里要变成"合成一条待批准的结果"
    _ => throw new InvalidOperationException("Ask 尚无生产者（见 01 §9.2）"),
};
```

⚠️ **三条硬要求**：
1. **被拒的调用绝不 `RunAsync`**（否则"拒了但还是删了库"）
2. **拒绝理由要说清是"档位不够"还是"本该问你但我还不会问"**——
   后者是**临时的**，必须让用户看得见它临时（否则就是把「问」静默实现成「拒」，
   而那正是本仓反复栽的「两层判据漂了而没人看得出来」）
3. **工具表一个字不动**（R-A）——`Stepper` 那行 `Tools = [...]` **不改**

- [ ] **Step 4: 跑测试确认绿**
- [ ] **Step 5: 把「降级」写进文档**

⚠️ **`01` §3.5 那张表要就地标一句**：`standard(3)` 的写入格今天实现成**拒**（不是**问**），
**理由是 `WaitingForInput` 还没有生产者**。**不标 = 文档说一套、代码做一套**。

---

### Task 6: 反向证明与变异验证

⚠️ **本仓栽过五次「变异存活」（见记忆 `mutation-must-be-confirmed`），所以变异必须
「变异 → 跑 → 恢复」在同一段连续动作里**，且**先确认变异真的落上了**（判据是**语义**，不是文本）。

- [ ] **V1：`Reclaim.Apply` 连 `Framing` 一起换掉** ⇒ `Reclaim_ReplacesTheContent_ButNotTheFrame` **必须红**
- [ ] **V2：`Gate` 表里把 `standard` 的写入格从「拒」改成「通」** ⇒ `Gate_MatchesTheTableInSection3_5` **必须红**
- [ ] **V3：`read` 去掉上限、改回 `ReadAllBytes`** ⇒ 上限那条**必须红**
- [ ] **V4：`ls` 改成静默截断（不写"共 M 条"）** ⇒ 截断那条**必须红**
- [ ] **V5：让 `ls` 的"太大"阈值自己写一个数**（不读 `ToolLimits`）⇒ G17 那条**必须红**
- [ ] **V6：把 `ToolArgs.Number` 改成宽容解析（`"42"` 也认）** ⇒ `ToolArgsTests` **必须红**
- [ ] **V7：⚠️「伪装成框架的内容」** —— 给 `read` 喂一个首行是 `[read a.log 1-2/3]` 的文件
      ⇒ 断言**只有一块框架**（G18）。⚠️ 变异：把框架与内容**合并成一个块** ⇒ 这条必须红

⚠️ **每条变异都要留痕**：跑之前确认它落上了（`grep` 出变异后的那一行），跑完**立刻**恢复
（重置写在跑之后，不是之前——本仓栽过的第五种就是「自己那一步先把变异还原了」）。

---

## 明确不做

| 不做 | 为什么 |
|---|---|
| **`Ask` / `WaitingForInput` / `approval.respond`** | R-F：用户裁定**单独一批**；本批只留 `Verdict.Ask` 这个值 |
| **工具结果的 `Attribution`（归属）那一格** | 它的生产者是**子会话**（`01` §10 整章零命中）⇒ **没有生产者** |
| **`ls` 的 mtime 当机制用** | 它只是**软信息**；`ls` 今天**没有指纹**，没有任何东西告诉模型这份清单过期了 |
| **写盘的手（`IToolContext` 扩展）** | 第一个 `Writing` 工具才需要；⚠️ 那时要守住「只读是结构性的」那条 |
| **取消 / 渲染 / 优化三个钩子** | G21 |
| **`tool.setEnabled`** | 它是**错的杠杆**（改工具表 = 破前缀最前端）——`实现现状` 已标 |

## 验证

1. `dotnet build -c Debug --no-incremental --warnaserror` → **0 警告**
   ⚠️ **必须 `--no-incremental`**：增量时分析器不重跑，「0 警告」与真跑过**逐字相同**
2. `dotnet build head/head.csproj`（**头不在 sln 里**）
3. `dotnet test` 全绿 + 本批新增
4. ⚠️ **金样**：`DiskFormatGoldenTests` 必须**逐字节不变**（G20 的答案）——若红了，说明
   「加可空字段不改老字节」那条前提**不成立**，那就得**改 `FormatVersion` 并迁移**，别硬改金样
5. ⚠️ **E2E（真核心 + 隔离临时数据根，绝不用 `~/.lilara`）**：
   `ls` 看大小 → `read` 一个超上限的文件（应看到段号）→ `read` 带 `offset` 接着读 → `session.gc`
   → 断言**框架仍在、内容变成注记**
6. ⚠️ **G17 的可执行核对**：把 `ToolLimits` 的上限常量改一个数，
   `grep` 全 `src/` 确认**那个数只出现一次**（`read` 与 `ls` 都从它读）
