# 管理面 API 实现计划（批次 0 + 1）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把核心的管理面补到「头能配 provider、能看会话全貌」——并把两处安全前提先修掉。

**Architecture:** 沿用既有两条通道，**不发明第三条**：
- **HTTP 只读快照**（`ControlServer.MapSnapshots`）——读一律走它
- **WS 命令**（`CoreCommand` → `CommandDispatcher`）——**改**一律走它

**Tech Stack:** .NET 10 · Kestrel · System.Text.Json · xUnit

**Spec:** `docs/03-控制面协议.md` §3/§4/§6 · `docs/01-会话设计.md` §7/§12 · `docs/04-核心设计.md` §1.1

## 用户裁定（本计划的约束来源，2026-10-03）

| # | 裁定 | 对计划的影响 |
|---|---|---|
| R-A | **「绝对充足的访问权限，想改啥几乎都行——怎么用那是头的事」** | 覆盖面要全；但**充足的是覆盖面，不是任意性**（不做 `raw.eval` 之类的万能口子） |
| R-B | **key：能改不能看** | `apiKey` 变成「可写不可读」：`provider.upsert` 的 `apiKey` 改**可选**；`/providers` **永不回传 key**，只回 `hasApiKey` 布尔 |
| R-C | **改历史：整份原子重写（路径锁内）**，删轮时**重算 `Turn`** | 见批次 2（本计划不含） |
| R-D | **`claim.acquire` 暂不上线** | 本计划**不**加它；声明仍是「头只读只释放」 |

## ⚠️ 本计划发现的一条**连锁**（它决定了批次 0 的必须性）

> **`apiKey` 可选 + WS 无 Origin 校验 = 任何网页都能把你的流量转走。**

攻击链：任意网页连 `ws://127.0.0.1:7788/ws` → 发 `provider.upsert(id, endpoint:"http://evil/")`
（**不需要知道 key**）→ 用户下一次发消息时，**他的 key 与整段对话一起发到 `evil`**。

⇒ **Origin 校验从「顺手做的卫生」升级为 `apiKey` 可选的硬前置。** 顺序**不能反**：
先 Task 2（Origin），再 Task 5（`apiKey` 可选）。

## Global Constraints

继承上一份计划（`2026-09-30-head-obligations.md`）的 G1–G11，逐条仍然有效。本计划新增：

- **G12 密钥单向：可写不可读。** 任何 HTTP 响应不得包含 `ApiKey`；`hasApiKey` 这种**布尔**可以。
  ⚠️ 判据要能执行：对真实数据根打一遍所有端点，断言响应字节里**不含** key 的前 16 个字符。
- **G13 命令是**改**的唯一入口。** 新加的编辑能力一律做成 `CoreCommand` + dispatcher 分支 +
  控制台语法（可选）；**不许**在 HTTP 上加写端点（`03` §1：HTTP 一次性读 / WS 双向）。
- **G14 四条线一个都不能少。** 加一条命令要同时落四处，**缺一处就是静默失效**：
  ① `CoreCommand` 记录 ② `CommandDispatcher` 分支 ③ `ControlServer.ParseCommand`（线上）
  ④ `CoreCommandParser`（控制台，**若该命令该有控制台语法**）。
  ⚠️ 缺口形状：T3 加 `session.setAxis` 时**漏了 ③**，而计划里没有任何任务认领它。
- **G15 落盘格式变更必须显式且可迁移。** 加字段要先量它是否改既有金样的字节
  （`DefaultIgnoreCondition = WhenWritingNull` 是否兜住），把答案写进注释；金样要同步。
- **G16 校验在 dispatcher，不在前端。** 控制台与头是两个前端；校验放前端 ⇒ 另一个前端绕过。
  （`Protocol` / `Auth` 已经在 dispatcher 校验——`id` 那次**漏了**，见 Task 1。）

## Review Focus

本计划里**没有任何任务自己的测试会碰**、而最可能咬人的五类输入/条件（按可能性排序）：

1. **`provider id` 里含 `..` / `/` / `\` / 绝对路径 / 空串** —— 用户会手打 provider id。
   合理预期：**拒绝**并说明合法形状，而不是写到一个奇怪的地方（今天实测：`../escaped` 写进了**数据根**）。
2. **不带 `Origin` 的 WS 连接**（curl / 测试驱动 / 未来的 CLI 头）—— 合理预期：**放行**（它不是网页）。
   带 `http://evil.example` 的——**拒绝**。⚠️ 浏览器永远带 `Origin`，所以放行「无 Origin」不削弱这道闸。
3. **`/state` 的 `spec` 里含盘上写死的怪值**（provider 被手改成未知协议、`modelId` 是空串）——
   合理预期：**原样交出**（是事实），头的渲染出错是头的事。
4. **`apiKey` 省略时的 `provider.upsert`**（R-B 的核心用法：只想改协议）——
   合理预期：**保留旧 key**；而**新 provider 且没给 key** ⇒ 拒绝并说清。
5. **`GET /providers` 的响应里混进 key**（手滑把 `ApiKey` 加回投影）——
   合理预期：**测试要红**。这是 G12 的那条可执行判据。

---

## 批次 0 + 1 的落地顺序（依赖决定，不能跳）

```
T1 provider id 校验        ← 独立；但必须在 T5/T6 之前（它们让配置面开始被头用）
T2 WS Origin 白名单        ← 独立；但必须在 T5 之前（那条连锁，见上）
T3 /state 交全 spec        ← 独立
T4 GET /provider-schema    ← 依赖 T1 的「合法 id 形状」定下来（schema 要把形状交出去）
T5 apiKey 可选 + hasApiKey ← 依赖 T2；且与 T6 同一次改（同一个端点的两半）
T6 /providers 补全         ← 依赖 T5（两个改动落在同一处投影上，别分两次动它）
T7 provider.delete         ← 独立
```

---

### Task 1: `provider.upsert` 的 `id` 校验（路径穿越）

**Files:**
- Modify: `src/Lilara.Core/Hosting/CommandDispatcher.cs`（`HandleProviderUpsert`）
- Test: `tests/Lilara.Core.Tests/ProviderStoreTests.cs` 或新建 `ProviderIdTests.cs`

**Interfaces:**
- Consumes: 既有 `CommandDispatcher.HandleProviderUpsert`
- Produces: 一条**可复用的**合法 id 判据（T4 的 schema 要把它交出去）

⚠️ **实测过的缺陷**（不是推得）：控制台敲 `provider set ../escaped --key k`
⇒ `✓ 提供商 '../escaped' 已保存`，而文件落在 **`<数据根>/escaped.json`**——
逃出了 `providers/`。成因：`ProviderStore.PathFor(id) => Path.Combine(_root, $"{id}.json")`，
而 `HandleProviderUpsert` 只校验了 `Protocol` 与 `Auth`，**没校验 `id`**。
⚠️ 今天只有本机能触发（声明只能从控制台进）；**Task 5 一落地，任何网页都能触发**。

- [ ] **Step 1: 写失败的测试**

```csharp
[Theory]
[InlineData("../escaped")]        // 实测过：写到数据根
[InlineData("..\\escaped")]       // Windows 分隔符
[InlineData("a/b")]               // 正斜杠
[InlineData("a\\b")]              // 反斜杠
[InlineData("/abs")]              // 绝对路径
[InlineData("C:\\abs")]           // 盘符
[InlineData("")]                  // 空
[InlineData("   ")]               // 全空白
[InlineData(".")]
[InlineData("..")]
[InlineData("a\0b")]              // 内嵌 NUL
[InlineData("NUL")]               // ⚠️ Windows 设备名：`ProviderStore.Save` 抛异常 ⇒ 掀翻常驻进程
[InlineData("nul")]               // ⚠️ 设备名判定必须**大小写不敏感**
[InlineData("CON.json")]          // ⚠️ 带扩展名一样是设备
[InlineData("NUL.txt.txt.json")]  // ⚠️ 同样是设备；`GetFileNameWithoutExtension` 会【放行】它
[InlineData("NUL  .json")]        // ⚠️ 尾随空格再带扩展名 ⇒ 判据要 TrimEnd
[InlineData("COM1.json")]         // ⚠️ 同族，抛 FileNotFoundException('\\.\COM1')
[InlineData("PRN")]               // ⚠️ 覆盖缺口：原先没有任何用例碰 PRN / AUX 分支 ⇒ 删掉那两行**变异存活**
[InlineData("AUX")]
[InlineData("LPT9")]              // ⚠️ 下边界那一族也要有一条
[InlineData("COM\u00B9")]         // ⚠️ 上标 ¹（U+00B9）也算数字——实测抛异常
[InlineData("LPT\u00B3")]         // ⚠️ 上标 ³（U+00B3）
[InlineData("CONIN$")]            // ⚠️ 带 `$` 的伪设备；**裸 `CONIN` 是正常文件，别加**
[InlineData("CONOUT$")]
public void ProviderUpsert_WithAnUnsafeId_IsRejected(string id)
{
    var cmd = new CoreCommand.ProviderUpsert(id, "k", null, null, null, null);
    var result = _dispatcher.DispatchAsync(cmd, "t1", awaitCompletion: true, CancellationToken.None)
        .GetAwaiter().GetResult();

    Assert.IsType<Ack.Rejected>(result.Ack);
}

[Theory]
[InlineData("local")]
[InlineData("my-provider")]
[InlineData("p1")]
[InlineData("本地")]                // 非 ASCII 合法——别用字符白名单把它挡了
public void ProviderUpsert_WithASafeId_IsAccepted(string id) { /* Assert.IsType<Ack.Accepted> */ }
```

- [ ] **Step 2: 跑测试确认红**
- [ ] **Step 3: 实现**

⚠️ **判据的形状**（写进注释，因为它是 T4 要交出去的那份）：
**「id 是一个文件名，不是一个路径」**——所以拒绝的是**路径语法**，不是**字符**：

```csharp
/// <summary>
/// ⚠️ **id 会变成文件名**（`ProviderStore.PathFor`）⇒ 它必须**不含路径语法**。
/// 判据是「**它是不是一个单纯的文件名**」，不是「它像不像 ASCII 标识符」——
/// 后者会把 `本地` 这类合法 id 一起挡掉（实测的教训方向：白名单型判据总会漏掉用户正当的输入）。
/// ⚠️ 实测坑：`../escaped` 让文件落在**数据根**（`providers/` 之外）。
/// </summary>
internal static bool IsSafeProviderId(string id) =>
    !string.IsNullOrWhiteSpace(id)
    && id == Path.GetFileName(id)          // ⚠️ 挡的是**路径语法**：`a/b` / `a\b` / `/abs` / `C:\abs` / `../escaped`
    && id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
    && id is not "." and not ".."          // ⚠️ **`.` 与 `..` 由这一句挡**，不是上一句——见下
    && !IsWindowsDeviceName(id);           // ⚠️ `NUL` / `CON` / `COM1`…：未捕获异常，见下
```

⚠️ **`id == Path.GetFileName(id)` 挡的是路径语法，`..` 不归它管——这两句别搞混。**
`Path.GetFileName("../escaped")` 返回 `escaped` ⇒ 不相等 ⇒ 拒绝 ✓；
但 `Path.GetFileName("..")` 返回 `..`、`Path.GetFileName(".")` 返回 `.` ⇒ **相等 ⇒ 放行**。
挡掉 `.` / `..` 的是**最后那句**。⚠️ 计划初版把 `..` 记在了这一句名下，
而实现逐字照抄 ⇒ **注释与代码说的不是一件事**。
⛔ **别把最后那句当冗余删掉**（它的注释已经这样误导过一次）。
⚠️ 代价实测：`PathFor("..")` 得到 `...json`，仍在 `providers/` 内 ⇒ **今天不构成穿越**；
但 `PathFor` 一旦不拼 `.json` 后缀，它立刻就变成穿越。**这是纵深防御，不是修复。**

⚠️ **`Path.GetFileName` 是平台相关的**（.NET 的既有性质）：Windows 上它把
`a/b` 也当分隔符。**这是我们要的**（数据根可能被同步到别的平台），**但要在注释里写明这一点**，
并为此补一条针对 `/` 的 `InlineData`（已在上面）。

⚠️ **Windows 设备名必须挡——它不属于上面任何一类，而后果是「未捕获异常掀翻常驻进程」。**
判据「是不是一个单纯的文件名」**抓不到它**：`NUL` 词法上完全合法。

⚠️ **别用裸 `File.WriteAllText` 探它的后果——那不是被测物。** 实测两条路结局不同：

| 探针 | `NUL` | `COM1` |
|---|---|---|
| 裸 `File.WriteAllText(<dir>/"NUL.json", …)` | 不抛，但 `GetFiles`/`File.Exists` 都看不见 ⇒ 像「静默消失」 | 抛 `FileNotFoundException` |
| **`ProviderStore.Save`（`AtomicFile`：写 `.tmp` 再 `Move`）** | 抛 `UnauthorizedAccessException('\\.\NUL')` | 抛 `FileNotFoundException('\\.\COM1')` |

⇒ **真正会发生的是后者**：命令从 dispatcher 抛出**未捕获异常**，
而 `DispatchAsync` 没有 try/catch、`Repl.DispatchAsync` 只接 `DispatchException` / `ModelCallFailedException`
⇒ **常驻进程直接死，后续命令不再执行**。
（这是**真二进制实测**的，实现者量的：敲 `provider set NUL --key k` ⇒ 进程掀翻、退出非 0。)
⚠️ **这一条本身是本计划的教训样本**：我第一版处方里把它写成了「回 ✓ 而盘上无文件」——
**探针 ≠ 被测物，要验证就调那个函数。**

⚠️ **判据要严过系统的怪癖，不是刚好合上它。** 三个坑：

1. **取「第一个点之前」那一段，不是 `Path.GetFileNameWithoutExtension`**：
   后者把 `NUL.txt.txt.json` 变成 `NUL.txt.txt` ⇒ **放行**，而它同样是设备名。
2. **`TrimEnd()`**：`NUL ` / `NUL  .json` 一样是设备名（实测）。
3. ⚠️ **相等，不是前缀**——实测 `NULx` / `COM1x` / `COM10` / `LPT10` / `console` / `connect`
   **全是正常文件**，前缀型会把这一大片正当 id 一起误杀。
4. ⚠️ **表本身还得补两处**（复审点名风险 + 控制器复现）：
   - **`CONIN$` / `CONOUT$`** —— 带 `$`，不在任何常见名单里。⚠️ 裸 `CONIN` / `CONOUT` 是**正常文件**，别顺手加。
   - **`¹²³`（U+00B9 / U+00B2 / U+00B3）也算数字** —— Windows 认这一组上标：
     实测 `COM¹` 抛异常，而 `COM⁴` / `COM⁵` / `COM⁹`（U+2074…）与全角 `COM１`（U+FF11）、
     阿拉伯-印度 `COM١` **都是正常文件** ⇒ **只加上标那三个，别扩到别的「数字」写法**。

```csharp
private static bool IsWindowsDeviceName(string id)
{
    var dot = id.IndexOf('.');
    var stem = (dot < 0 ? id : id[..dot]).TrimEnd();

    foreach (var d in new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" })
        if (stem.Equals(d, StringComparison.OrdinalIgnoreCase)) return true;

    // ⚠️ 相等、不是前缀；只有 **1–9 与 ¹²³**（COM0 / COM10 / COM⁴ 实测都是正常文件）
    if (stem.Length != 4) return false;
    var head = stem[..3];
    if (!head.Equals("COM", StringComparison.OrdinalIgnoreCase)
     && !head.Equals("LPT", StringComparison.OrdinalIgnoreCase)) return false;
    return stem[3] is (>= '1' and <= '9') or '¹' or '²' or '³';
}
```

⚠️ **平台后果要写明**：Linux 上 `NUL` 是完全正常的文件名，这里**照样拒绝**——
与 `/` 那条同一套理（数据根可能被同步到别的平台），**这是刻意的**。

- [ ] **Step 4: 跑测试确认绿 + 补一条守卫**：把 `IsSafeProviderId` 的调用**去掉**跑一次，
  确认那 **24** 条 `InlineData` **全红**（G7：变异必须先确认落上）。
  ⚠️ 还要跑**第二次变异**：只删 `IsWindowsDeviceName` 里 `PRN` / `AUX` 那两行 ——
  补上的 `PRN` / `AUX` 用例必须红（复审发现：原先那两条分支**零覆盖**，删掉是**变异存活**）。
- [ ] **Step 5: 提交** `fix(core): provider.upsert 校验 id 是「文件名」而非「路径」——实测 ../escaped 曾写到数据根`

#### ⚠️ 补丁轮 T1b（同一处文件，紧随 T1）

评审抓出一条**注释与代码说的不是一件事**：那句 `id == Path.GetFileName(id)` 的注释把 `..`
记在它名下，而实测 `Path.GetFileName("..") == ".."` ⇒ 挡掉 `..` 的是**最后那句**。
⛔ 后果：下一个「清理者」会把它当冗余删掉，而**那正是纵深防御那一层**。
⇒ **注释改对**（`..` 归最后一句；这一句只说路径语法）。
⇒ **顺带把设备名并进谓词**（上面 Step 1/Step 3 已改）：`NUL` 会被 `ProviderStore.Save`
   抛成**未捕获异常、掀翻常驻进程**，
   而「静默的 ✓」是本项目一直在收的那一族。
⇒ 计划自己的文本也要改（实现者照抄了它）——**已在本文件里改掉**。

---

### Task 2: WS Origin 白名单（`apiKey` 可选的硬前置）

**Files:**
- Modify: `src/Lilara.Core/Control/ControlServer.cs`（`MapWebSocket`）
- Modify: `src/Lilara.Core/Control/ControlServer.cs`（构造参数：允许的 origin）
- Modify: `src/Lilara.Core/Program.cs`（注入，带默认值）
- Test: `tests/Lilara.Core.Tests/ControlServerTests.cs`

**Interfaces:**
- Produces: `ControlServer(..., string allowedOrigin = "http://127.0.0.1:7790")` ——
  ⚠️ **必须有默认值**：`ControlServer` 有**两个**构造点（`src/Lilara.Core/Program.cs:74` 与
  `tests/Lilara.Core.Tests/ControlServerTests.cs:57`），没默认值就会**直接打断既有的那批 WS 测试**
  （而它们正是这条改动的回归网——打断它们等于自己拆掉安全网）。
  测试要覆盖**非默认值**时**显式传参**，别去改默认值。

⚠️ **判据必须是「放行无 Origin，拒绝别的 Origin」**，不是「要求 Origin」：
- **浏览器永远带 `Origin`** ⇒ 挡住坏 origin 就达到了目的
- **curl / 测试驱动 / 未来的 CLI 头不带** ⇒ 要求它就等于把它们一起挡掉
  （既有 `ControlServerTests` 全是这种客户端——要求 Origin 会让它们**全部**失败，
  而那**不是**「测试在保护我们」，是「测试被误伤」）

- [ ] **Step 1: 写失败的测试**

```csharp
[Theory]
[InlineData(null)]                              // 无 Origin（curl / 测试驱动）⇒ 放行
[InlineData("http://127.0.0.1:7790")]           // 头自己的 origin ⇒ 放行
[InlineData("http://evil.example")]             // ⇒ 拒绝
[InlineData("http://localhost:7790")]           // ⚠️ 同机不同写法：**拒绝**——见下
[InlineData("null")]                            // `file://` 页面与沙箱 iframe 的 Origin 字面量是 "null"
public async Task WebSocket_OriginPolicy(string? origin)
{
    using var socket = new ClientWebSocket();
    // ⚠️ **设备端没有「带 Origin 的握手」这个 API，只能手设头**——
    // `SetRequestHeader` 这条路**实测过**（.NET 10）：设了服务端 `ctx.Request.Headers["Origin"]`
    // 收得到；**不设则该头根本不存在**（读出来是空串，不是 null）⇒ 判据里「放行无 Origin」就是这条。
    if (origin is not null) socket.Options.SetRequestHeader("Origin", origin);

    var connect = socket.ConnectAsync(new Uri(Ws()), CancellationToken.None);
    if (origin is null or "http://127.0.0.1:7790")
    {
        await connect;                                    // 放行 ⇒ 握上手
        Assert.Equal(WebSocketState.Open, socket.State);
    }
    else
    {
        // ⚠️ 握手被拒时 `ConnectAsync` 抛 `WebSocketException`（服务端回了 403）。
        await Assert.ThrowsAnyAsync<WebSocketException>(() => connect);
    }
}
```

⚠️ **`"null"` 那条要单独想**：`Origin: null` 是**沙箱 iframe 与 `file://` 页面**发的，
它们**不是**我们的头 ⇒ **拒绝**。（写成 `InlineData("null")` 并断言拒绝。）

- [ ] **Step 2–4: 红 → 实现 → 绿**

```csharp
// MapWebSocket 里，AcceptWebSocketAsync 之前：
// ⚠️ **这是 `apiKey` 可选的硬前置**：网页能改 `endpoint` 就能把流量（连同 key）转走。
// ⚠️ **放行「无 Origin」，只拒绝「别的 Origin」**——浏览器永远带 Origin（挡坏的就够了），
//    而 curl / 测试驱动不带（要求它会把它们全部误伤）。
// ⚠️ 用**索引器**取头（`ctx.Request.Headers["Origin"]`），不用 .NET 10 的强类型属性——
//    索引器两种写法都吃（大小写不敏感），且**能被 grep 到**。
var origin = ctx.Request.Headers["Origin"].ToString();
if (!string.IsNullOrEmpty(origin) && !string.Equals(origin, allowedOrigin, StringComparison.OrdinalIgnoreCase))
{
    ctx.Response.StatusCode = (int)HttpStatusCode.Forbidden;
    return;
}
```

⚠️ **`localhost` vs `127.0.0.1` 的裁定要写进注释**（上面的测试用例里有这一条）：
它们**不是**同一个 origin（浏览器就是这么判的）。头的 `config.json` 交的是
`http://127.0.0.1:7788`，页面在 `http://127.0.0.1:7790` ⇒ **默认只放行 `127.0.0.1:7790`**。
⚠️ 若用户改成用 `localhost:7790` 打开页面，**会被拒**——**这是刻意的**：
放行 `localhost` 等于放行一个**任何本机程序都能抢占的名字**（hosts 文件、DNS）。
⇒ **失败要能自解释**：拒绝时在响应体里写一句「本核心只接受来自 <allowedOrigin> 的页面」，
并在核心启动横幅里带上这个值。

- [ ] **Step 5: 提交** `fix(core): WS 校验 Origin——放行无 Origin（curl/测试），拒绝别的网页 origin；apiKey 可选的前置`

---

### Task 3: `/state` 交全 `spec`

**Files:** `src/Lilara.Core/Control/ControlServer.cs`（`/sessions/{id}/state`）· 其测试

⚠️ **控制台 `status` 印四样**（会话 id · `Status`/`Turn` · **模型** · **目录** · 策略），
而**头只拿得到前两样与策略**——`modelId` / `providerId` / `workingDirectory` / `createdAt` **一个都没有**。
后果实测过：用户问「怎么切换提供商」，头**答不出来**。

- [ ] **Step 1: 测试**（写进 `ControlServerTests`，它已有随机端口 + `Client` + `Http()` + `_dispatcher` 的脚手架）

```csharp
[Fact]
public async Task State_CarriesTheWholeSpec()
{
    var created = await _dispatcher.DispatchAsync(new CoreCommand.SessionCreate(), "c", false, CancellationToken.None);
    var sid = ((Ack.Accepted)created.Ack).SessionId!;      // ⚠️ 分派型命令只在这个回执里给身份

    using var s = JsonDocument.Parse(await Client.GetStringAsync(Http($"/sessions/{sid}/state")));
    var spec = s.RootElement.GetProperty("spec");

    // ⚠️ **整份 `SessionSpec` 交出去**（R-A：头要什么由头决定，核心不做子集裁剪）。
    // ⚠️ 线上是 **camelCase**（`WireJson.Options`）——键名按它写，别按 C# 属性名。
    Assert.Equal(sid, spec.GetProperty("sessionId").GetString());
    Assert.False(string.IsNullOrEmpty(spec.GetProperty("projectKey").GetString()));
    Assert.False(string.IsNullOrEmpty(spec.GetProperty("workingDirectory").GetString()));
    Assert.NotEqual(default, spec.GetProperty("createdAt").GetDateTimeOffset());

    var model = spec.GetProperty("model");
    // ⚠️ 值来自测试**预置的那个 provider**（`"p"` / `DefaultModel = "m"`）——
    // 写死它们同时钉住了「spec 里的模型就是该会话实际绑的那份」。
    Assert.Equal("p", model.GetProperty("providerId").GetString());
    Assert.Equal("m", model.GetProperty("modelId").GetString());
    Assert.Equal(Protocols.ClaudeMessages, model.GetProperty("protocol").GetString());
    Assert.False(string.IsNullOrEmpty(model.GetProperty("effort").GetString()));
    Assert.True(model.GetProperty("maxTokens").GetInt32() > 0);

    // ⚠️ **G12**：key 不在 `SessionSpec` 里（它住 `providers/*.json`）
    // ⇒ 「整份交出去」这件事**天生不泄 key**——这条断言把那句话钉成可执行的。
    Assert.False(spec.TryGetProperty("apiKey", out _));
}
```
- [ ] **Step 2–4: 红 → 实现 → 绿**

```csharp
// 加在匿名对象里（与 Axes / Slots 同级）：
// ⚠️ **整个 spec 交出去**（`01` §7 的配置分层本就把它当可分享信息）。
// ⚠️ **不含 key**：key 在 providers/*.json 里，不在 spec 里——所以这里天然安全（G12）。
Spec = handle.Spec,
```

⚠️ **别只交一个子集**——「头要什么」是头的事（R-A）。整份 `SessionSpec` 序列化即可。
⚠️ `WorkingDirectory` 是**本机绝对路径**，交出去意味着它在开放的 CORS 下可读（终审 §Declined #2 记过）。
**本计划接受**（R-A 的代价），但**要在注释里点名**。

- [ ] **Step 5: 提交** `feat(core): /state 交全 spec（model/provider/目录/时间）——控制台 status 的四个字段头一个都拿不到`

---

### Task 4: `GET /provider-schema`

**Files:** `src/Lilara.Core/Control/ControlServer.cs` · 其测试

⚠️ **它不是「可选的美化」，是「不许手抄」的执行面**：头要渲染配置表单就必须知道
「合法协议 / 合法 auth / 各协议默认模型」，而这些的真相来源在核心
（`Protocols.All` / `AuthSchemes` / **`AdapterFactory` 的默认模型**）。
⚠️ **头已经抄错一次**：`head/wwwroot/index.html` 的引导文案写死 `--protocol claude-messages`
——而 `claude-messages` 只是**默认值**，不是「唯一能用的」，这正是用户第一次配置时踩的坑。

- [ ] **Step 1: 测试**（写进 `ControlServerTests`）

```csharp
[Fact]
public async Task ProviderSchema_CarriesTheTruthItOwns()
{
    using var s = JsonDocument.Parse(await Client.GetStringAsync(Http("/provider-schema")));

    // ⚠️ **与真相来源对拍，不写死字面量**——写死的话，这个端点与 `Protocols.All`
    //    分叉了测试也不会红，而「不许手抄」正是这个端点存在的理由。
    var protocols = s.RootElement.GetProperty("protocols").EnumerateArray()
        .Select(e => e.GetString()).ToList();
    Assert.Equal(Protocols.All, protocols);

    var auth = s.RootElement.GetProperty("authSchemes").EnumerateArray()
        .Select(e => e.GetString()).ToList();
    Assert.Equal([AuthSchemes.ApiKey, AuthSchemes.Bearer], auth);

    // ⚠️ **人话规则要含设备名那一档**——它是 T1c 补上的、最容易被漏掉的一类，
    //    而头要靠这句话**预先**挡掉用户手打的 `NUL`（不然用户撞的是「核心崩了」）。
    Assert.Contains("设备名", s.RootElement.GetProperty("providerIdRule").GetString());
}

/// <summary>
/// ⚠️ **`defaultModel` 有意**不交——它今天**没有可信来源**（核过：`claude-opus-5` 只出现在
/// `CommandDispatcher` 的兜底那一处，而 `IModelAdapter` 不交默认模型）。
/// ⇒ 交它 = 要么复制一份（G10），要么**发明**一个来源。
/// ⚠️ 这条断言是**反向的**，因为「下一个人顺手补上它」是最可能发生的事：
/// 他觉得 schema 里少个默认模型是缺陷，而**它是有意缺席的**。
/// </summary>
[Fact]
public async Task ProviderSchema_DeliberatelyOmitsTheDefaultModel()
{
    using var s = JsonDocument.Parse(await Client.GetStringAsync(Http("/provider-schema")));

    Assert.False(s.RootElement.TryGetProperty("defaultModel", out _));
    Assert.False(s.RootElement.TryGetProperty("model", out _));
}
```
- [ ] **Step 2–4: 红 → 实现 → 绿**

```csharp
// ⚠️ **不交 `defaultModel`——因为它今天没有可信来源。**
// 核到的事实：`claude-opus-5` 这个字面量**只出现在 `CommandDispatcher.cs:100` 一处**
//（`ProviderUpsert` 的兜底），而 `IModelAdapter` **不交**默认模型。
// ⇒ 它不是「第二份副本」（G10 那种），它是**唯一那份**，住在命令的兜底里。
// ⇒ 在 schema 里交它 = 要么复制一份（G10），要么**发明**一个来源。
// **两条都不做**：如实不交。头要预填模型，从 `/providers` 拿**已有那个 provider 的** `defaultModel`——
// 那是**用户实际在用的值**，比任何全局默认都准。
app.MapGet("/provider-schema", () => Results.Json(new
{
    Protocols = Protocols.All,
    AuthSchemes = new[] { AuthSchemes.ApiKey, AuthSchemes.Bearer },
    ProviderIdRule = "单个文件名：不含路径分隔符，不能是 . 或 ..，也不是 CON/NUL/COM1/CONIN$ 这类 Windows 设备名",
}, WireJson.Options));
```

- [ ] **Step 5: 提交** `feat(core): GET /provider-schema——头渲染配置表单的真相来源（头已抄错一次协议默认值）`

---

### Task 5: `apiKey` 可选 + `hasApiKey`（**先读那条连锁**）

**Files:**
- Modify: `src/Lilara.Core/Hosting/CommandDispatcher.cs`（`HandleProviderUpsert`）
- Modify: `src/Lilara.Core/Hosting/CoreCommand.cs`（`ProviderUpsert.ApiKey` → `string?`）
- Modify: `src/Lilara.Core/Control/ControlServer.cs`（`ParseCommand` 的 `apiKey` 改可选）
- Modify: `src/Lilara.Core/Console/CoreCommandParser.cs`（`--key` 不再是必填）
- Test: 上述各自的测试文件

⚠️ **用户裁定 R-B**：能改不能看。今天 `apiKey` **必填** ⇒ 头改协议必须重发 key，
而头**读不到 key**（`/providers` 有意不给）⇒ **头永远改不了已有 provider**。

- [ ] **Step 1: 测试**

```csharp
/// ⚠️ **省略 key = 保留旧 key**（R-B：头读不到 key，不该逼它重发）。
/// 头改协议时**必须**能不带 key 走通——不然它永远改不了已有 provider。
[Fact]
public async Task ProviderUpsert_WithoutApiKey_KeepsTheStoredOne()
{
    var (host, _) = NewHarness();
    SaveProvider(host);                                       // 既有 provider（key="k"）
    var dispatcher = new CommandDispatcher(host, new EventBus(), host.StepperFor);

    await dispatcher.DispatchAsync(
        new CoreCommand.ProviderUpsert("p", null, null, Protocols.OpenAiChat, null, null),
        "c", true, CancellationToken.None);

    var after = host.Providers.Load("p")!;
    Assert.Equal("k", after.ApiKey);                          // ⚠️ 盘上**仍是旧 key**
    Assert.Equal(Protocols.OpenAiChat, after.Protocol);       // 而这一项**真的改了**
}

/// ⚠️ 新 provider **必须**给 key：否则会存下一个**发不出去的配置**，比拒绝更糟
/// （用户以为配好了，第一次发消息才炸）。
[Fact]
public async Task ProviderUpsert_WithoutApiKey_ForANewProvider_IsRejected()
{
    var (host, _) = NewHarness();
    SaveProvider(host);
    var dispatcher = new CommandDispatcher(host, new EventBus(), host.StepperFor);

    var r = await dispatcher.DispatchAsync(
        new CoreCommand.ProviderUpsert("brand-new", null, null, null, null, null),
        "c", true, CancellationToken.None);

    var rejected = Assert.IsType<Ack.Rejected>(r.Ack);
    Assert.Contains("brand-new", rejected.Reason);            // ⚠️ 理由要**点名那个 id**——
                                                              // 用户手打了三个 provider 时，光说「必须给 key」不够
    Assert.Null(host.Providers.Load("brand-new"));            // ⚠️ 且**没有半成品落盘**
}

/// ⚠️ **第三条不是凑数**：`InvalidateSteppers()` 挂在 `HandleProviderUpsert` 的**末尾**，
/// 而「省略 key」这条路径最自然的写法就是**提前 return** ⇒ **改了协议但缓存没失效**
/// = 用旧适配器发新协议（正是本文件里 `ProviderUpsert_InvalidatesTheStepperCache` 防的那件事，
/// 换了个入口回来）。
/// ⇒ 范本就在 `CommandDispatcherTests.cs:224`，**唯一的区别是这次不带 key**。
[Fact]
public async Task ProviderUpsert_WithoutApiKey_StillInvalidatesSteppers()
{
    var (host, _) = NewHarness();
    SaveProvider(host);
    var dispatcher = new CommandDispatcher(host, new EventBus(), host.StepperFor);
    var sessionId = await CreateSessionAsync(dispatcher);
    host.TryGetActivated(sessionId, out var handle);

    var before = host.StepperFor(handle);
    await dispatcher.DispatchAsync(
        new CoreCommand.ProviderUpsert("p", null, null, Protocols.OpenAiChat, null, null),
        "c", true, CancellationToken.None);

    Assert.NotSame(before, host.StepperFor(handle));          // ⚠️ 缓存必须换掉
}
```

- [ ] **Step 2–4: 红 → 实现 → 绿**

```csharp
// ProviderUpsert 的 ApiKey 改 string?；dispatcher 里：
var existing = _host.Providers.Load(p.Id);
// ⚠️ 新 provider **必须**给 key（否则存一个发不出去的配置，比拒绝更糟）；
//    已有 provider **省略即保留**（R-B：头读不到 key，不该逼它重发）。
if (p.ApiKey is null && existing is null)
    return Reject(id, $"新提供商 '{p.Id}' 必须给 --key。");
// ... Save 时：ApiKey = p.ApiKey ?? existing!.ApiKey,
```

⚠️ `ApiKey` 在 `ProviderDefinition` 上是 `required`——改成 `required` 保留但**构造时必给值**
（用 `p.ApiKey ?? existing!.ApiKey`），别把 `required` 去掉（那会让别的构造点静默漏填）。

⚠️ **`ControlServer.ParseCommand` 那一处是个陷阱，别漏。** 现在它是：

```csharp
"provider.upsert" => new CoreCommand.ProviderUpsert(
    payload.GetProperty("id").GetString()!,
    payload.GetProperty("apiKey").GetString()!,     // ⚠️ 键【缺失】时 GetProperty 会**抛**，
    ...                                              //    不是返回 null
```

⇒ 键缺失时 `JsonElement.GetProperty` 抛 `KeyNotFoundException`（实测的 .NET 行为），
而**它会被 `HandleCommandAsync` 的 `catch (Exception ex)` 接住**、回一句
`命令形状不对：…` ⇒ 头收到的是**「形状不对」而不是「省略 key 就是保留旧的」**。
⇒ **必须把 `apiKey` 并进它旁边那个 `Opt(...)` 辅助**（同一次改，别分两次动这个臂）：

```csharp
    payload.GetProperty("id").GetString()!,
    Opt(payload, "apiKey"),                         // ⚠️ 缺失 ⇒ null ⇒ 语义是「保留旧 key」，
    Opt(payload, "model"), Opt(payload, "protocol"), Opt(payload, "endpoint"), Opt(payload, "auth")),
```
⚠️ **测试要覆盖「帧里根本没有 `apiKey` 键」这一条**（不是「`"apiKey": null`」）——
两个前端里只有控制台走文本解析，**线上是 JSON**，而 JSON 的「省略」就是**键不存在**。
只测控制台会漏掉这个臂（这正是 G16 那条「两个前端」的形状）。

- [ ] **Step 5: 提交** `feat(core): provider.upsert 的 apiKey 可选（省略=保留旧 key）——头读不到 key，不然永远改不了已有 provider`

---

### Task 6: `GET /providers` 补全（**仍不给 key**）

**Files:** `src/Lilara.Core/Control/ControlServer.cs` · 其测试

⚠️ 与 Task 5 **一起做**：两个改动落在同一个投影上，分两次动它等于两次评审同一个小区域。

- [ ] **Step 1: 测试**（⚠️ 两条都是 G12 的可执行判据）

⚠️ **先说一个会让测试「假红」的坑**：`WireJson.Options` 带
`DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull`（`WireJson.cs:28`），
而 `Endpoint` / `AuthScheme` 是 `string?`、默认 **null** ⇒ **键根本不会出现**。
⇒ 断言「这些字段都在」时，**必须先把它们设上**，否则红的是测法、不是端点。

```csharp
[Fact]
public async Task Providers_CarriesEverythingButTheKey()
{
    // ⚠️ 端点与认证方式**显式设上**——它们是可空的，而 `WhenWritingNull` 会把 null 的键整个省掉。
    _host.Providers.Save(new ProviderDefinition
    {
        Id = "full", Protocol = Protocols.ClaudeMessages, ApiKey = "k",
        DefaultModel = "m", Modality = "vision",
        Endpoint = "http://127.0.0.1:1/v1", AuthScheme = AuthSchemes.Bearer,
    });

    using var a = JsonDocument.Parse(await Client.GetStringAsync(Http("/providers")));
    var p = a.RootElement.EnumerateArray().Single(e => e.GetProperty("id").GetString() == "full");

    Assert.Equal(Protocols.ClaudeMessages, p.GetProperty("protocol").GetString());
    Assert.Equal("m", p.GetProperty("defaultModel").GetString());
    Assert.Equal("http://127.0.0.1:1/v1", p.GetProperty("endpoint").GetString());
    Assert.Equal(AuthSchemes.Bearer, p.GetProperty("authScheme").GetString());
    Assert.Equal("vision", p.GetProperty("modality").GetString());
    Assert.True(p.GetProperty("hasApiKey").GetBoolean());
}

[Fact]
public async Task Providers_NeverLeaksTheKey()
{
    // ⚠️ **哨兵串**：一个不可能偶然出现的值。**别用「不含 apiKey 字样」那种弱判据**——
    //    字段一改名它就失效了。
    const string sentinel = "SENTINEL-8f3a2b1c-DO-NOT-LEAK";
    _host.Providers.Save(new ProviderDefinition
    {
        Id = "leaky", Protocol = Protocols.ClaudeMessages, ApiKey = sentinel,
        DefaultModel = "m", Modality = "vision",
    });

    var body = await Client.GetStringAsync(Http("/providers"));

    // ⚠️ 断言**字节里**不含它——这是 G12「可写不可读」唯一可执行的判据。
    Assert.DoesNotContain(sentinel, body);
    Assert.DoesNotContain(sentinel[..16], body);

    // ⚠️ **反向那半不能省**：只断言「不含哨兵」的话，「这个端点什么都没交」也能过。
    using var a = JsonDocument.Parse(body);
    var p = a.RootElement.EnumerateArray().Single(e => e.GetProperty("id").GetString() == "leaky");
    Assert.True(p.GetProperty("hasApiKey").GetBoolean());   // 「有 key」这件事要看得见
    Assert.False(p.TryGetProperty("apiKey", out _));        // 而「是什么」不行
}
```

- [ ] **Step 2–4: 红 → 实现 → 绿**

```csharp
app.MapGet("/providers", () =>
    Results.Json(host.Providers.List().Select(p => new
    {
        p.Id, p.Protocol, p.DefaultModel,
        // ⚠️ **端点与认证方式要交**——端点决定数据发去哪（核心自己每次 `provider set` 都印一行），
        //    头看不到就无从核对。
        p.Endpoint, p.AuthScheme, p.Modality,
        // ⚠️ **key 只回「有没有」，不回「是什么」**（用户裁定 R-B：能改不能看）。
        //    有了它，表单能显示「已配置」而不是一个空框（空框会让人以为没配、然后重填一遍）。
        HasApiKey = !string.IsNullOrEmpty(p.ApiKey),
    }), WireJson.Options));
```

- [ ] **Step 5: 提交** `feat(core): /providers 补 endpoint/auth/modality/hasApiKey——端点决定数据发去哪，头得能核对；key 仍只回有无`

---

### Task 7: `provider.delete`

**Files:** `CoreCommand.cs` · `CommandDispatcher.cs` · `ControlServer.ParseCommand` · `ProviderStore.cs` · 测试

⚠️ **G14 四条线**：① 记录 ② dispatcher 分支 ③ 线上解析 ④ **要不要控制台语法**？

- [ ] **Step 1: 先裁定 ④ 并在测试里体现**

⚠️ **我的裁定**：**加**控制台语法 `provider delete <id>`，理由是**对称性**——
`provider set` 有控制台语法，而 `provider delete` 没有会造出「配得进、删不掉」的形状
（今天就是这样：只能手删文件）。⚠️ **代价（若错）**：`04` §1.1 的「控制台不多一条命令」
若被判定为连对称的删也算，则应撤掉——但那样 `provider set` 也该只在线上，而它是既有的。

```csharp
// ---- 线上那条（ControlServerTests 里，真起 Kestrel 真连 WS）----
// ⚠️ 用既有那套 WS 脚手架；断言的是**两端**：ack 与盘上的后果。
var upsert = new CoreCommand.ProviderUpsert("p", "k", "m", null, null, null);
await _dispatcher.DispatchAsync(upsert, "u", false, CancellationToken.None);
Assert.NotNull(_host.Providers.Load("p"));

// 发 provider.delete（**要四条线都通**：记录 → dispatcher → 线上解析 → 控制台语法）
var del = await SendCommand(Id(), new { kind = "provider.delete", id = "p" });   // 用既有 WS 收发 helper
Assert.True(del.Ack.Accepted);
Assert.Null(_host.Providers.Load("p"));      // ⚠️ **盘上真没了**——只断言 ack 是半条判据

// ---- 控制台那条（ProviderIdTests 或 ReplTests 里）----
// ⚠️ G14 的 ④：`provider delete p` 必须**被解析成命令**，不能落回「当成消息发出去」
var (cmd, err) = CoreCommandParser.Parse("provider delete p");
Assert.Null(err);
Assert.Equal("p", Assert.IsType<CoreCommand.ProviderDelete>(cmd).Id);
Assert.Null(CoreCommandParser.Parse("provider delete").Command);   // 少了 id ⇒ 报错，不落回消息
```

⚠️ **删的测试必须断言「文件真没了」**（上面 `Load` 那句）——**只断言 ack 是半条判据**：
ack 对了而文件还在的那种错，用户以为删了、下次发消息还在用那个凭据。
⚠️ **反向也要一条**：删一个**不存在**的 id ⇒ `Rejected`、且理由点名那个 id
（判据：本项目一路在收「静默的成功」——`provider delet local`（拼错）回 ✓ 的话，
用户以为删了、其实没删）。

- [ ] **Step 2–4: 红 → 实现 → 绿**

⚠️ **`id` 必须过同一个闸 `IsSafeProviderId`——这条比 T1 更要紧，因为删是破坏性的。**
`Delete(id)` 会走同一个 `PathFor(id)`：`delete ../escaped` ⇒ `<数据根>/escaped.json` **被删掉**
（`providers/` 之外的东西）；`delete NUL` ⇒ `File.Delete` 同样抛未捕获异常掀翻进程。
⇒ **复用那个谓词**（它已经是 `internal static`）：判据一处、两处调用（G10）。
⇒ 测试必须**断言「那个文件没被删掉」**，不能只断言 ack——
**删是不可逆的**，而 ack 对了文件已经没了的那种错，用户挽不回来。

⚠️ **删一个不存在的 provider**：**拒绝**（`Rejected`，理由点名那个 id），**不是幂等成功**。
理由：本项目一路在收「静默的成功」——`provider delet local`（拼错）若回 ✓，
用户以为删掉了、其实没删，而下一次发消息用的还是那个凭据。

⚠️ **删一个**正被会话引用的** provider 会怎样**？——**不阻止**（R-A：想改啥都行），
但**要让后果可见**：删完 `InvalidateSteppers()`；被引用的会话下次发消息会走
`StepperResolver` 的「绑定的提供商已不存在」那条路（**已经实现了**，`Program.cs` 也会在启动时报）。
⚠️ **别在这里加「先检查有没有会话在用」的闸**——那是家长式干预，
且 `SessionSpec` 的存在不构成「不能删」的理由（用户可能就是要把一个坏配置删掉）。

- [ ] **Step 5: 提交** `feat(core): provider.delete（命令 + 线上解析 + 控制台语法 + 缓存失效）`

---

## 验证（本批次）

1. `dotnet test` —— **全绿**；既有金样一条不许改断言值（只能加新的）。
2. `dotnet build -warnaserror **--no-incremental**` —— **0 警告 0 错误、exit 0**。
   ⚠️ **`--no-incremental` 不能省**：增量构建**不重跑分析器**，于是它打印的「0 个警告」
   与「真跑过且干净」**逐字相同**——实测量过同一条命令增量绿、全量红。
   ⚠️ 除了 `src`（`TreatWarningsAsErrors=true`），**测试项目也要干净**：
   它没有那个属性，诊断平时只是 warning，**靠这条命令行开关才升格**。
3. **手工端到端（四个可观测量，逐条记「预期 / 实际」）**：
   - ① 控制台 `provider set ../escaped --key k` ⇒ **拒绝**，且**数据根下不出现 `escaped.json`**
   - ② 用带 `Origin: http://evil.example` 的 WS 客户端连 ⇒ **403**；不带 Origin 的 ⇒ **连上**
   - ③ `GET /sessions/{id}/state` ⇒ 含 `spec.model.modelId` 与 `spec.workingDirectory`
   - ④ 对**真实数据根**打 `GET /providers`，用 `grep` 断言响应里**没有**那个 key 的片段
4. **变异确认**（G7）：T1 的 `IsSafeProviderId` 调用（含 `IsWindowsDeviceName` 的 **`PRN`/`AUX` 那条单独杀一次**）、
   T2 的 origin 分支、T6 的 key 投影、**T7 的 id 闸**，各去掉一次 ⇒ **必须各有一条红**；
   跑之前先 `grep` 确认变异落上了，跑完再 grep 确认还原。
5. `git`：动手前记下 `master` 的 SHA 当回滚点。

## 明确不做（写进代码注释的偏离）

- **`claim.acquire` 不上线**（用户裁定 R-D）
- **改历史 / 会话编辑**（R-C 的落点）：**批次 2**，且它需要一个**前提**——
  `TimelineEvent` 今天**没有稳定 id**（只有 `At`），「编辑某一段」无从指涉
  ⇒ 那是一个**磁盘格式变更**（落盘加字段 + 金样要动 + 旧文件要迁移），单独立任务
- **改会话的 provider/model**：批次 2（今天只能重建会话）
- **`session.delete`**：批次 2
- **`/projects` 排序、会话列表端点、provider 变更事件、`renderLevel` 可设**：批次 3
- **原始文件读写（`raw.eval` 之类的万能口子）**：**不做**——充足的是**覆盖面**，不是**任意性**
  （R-A 的边界：每条命令一个明确语义，才答得出「谁改的、改完还自洽吗」）

## ⚠️ 批次 2 之前必须知道的一条设计后果

**改历史会让前缀缓存整段失效。** 本机网关的响应里**真的带 `prompt_cache_hit_tokens`**
（实测探到的字段），说明前缀缓存是实的。而**改中间/靠前的轮次** ⇒ 后面所有轮的前缀指纹变
⇒ **缓存全部失效**、下一轮按未缓存计价。
⇒ 这是**功能上正确、账上很贵**的操作。核心该把它**说清**（至少注释里点名），
头要不要在 UI 上提示是头的事。**「截断」（删尾部）比「改中间」便宜得多**——
若批次 2 只做一件事，做截断。
