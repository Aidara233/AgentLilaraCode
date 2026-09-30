# 控制面 + 简略版头 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 给核心开一个 WS + HTTP 控制面，并写一个能用的简略版头，让核心从「只能坐在终端前用」变成「浏览器能看能说话」。

**Architecture:** 核心侧新增 `CoreHost`（持激活会话表 + 每会话信号量 + 每会话 CTS + stepper 缓存）与 `CommandDispatcher`（命令的唯一执行点，两个前端都走它，事件全部从它发）。事件经**单消费者泵**分配 `seq` 后 fan-out 到每流有界队列。线格式另开一份 `JsonSerializerOptions`，wire 类型**独立成根**。头是独立进程，托管静态文件，页面 JS 直连核心。

**Tech Stack:** .NET 10；`FrameworkReference Microsoft.AspNetCore.App`（Kestrel + WebSockets，**不引 NuGet 包**）；前端零构建（原生 HTML + JS）。

**Spec:** `docs/03-控制面协议.md`（头核接口）、`docs/05-头设计.md`（头）、`docs/01-会话设计.md` §6.1（队列与推进）、`docs/02-多会话调度.md`（调度）、`docs/04-插件与内部结构.md` §1.1（控制台是控制面的客户端）

---

## Global Constraints

以下每一条都**实测或逐行核对过**，不是推测。每个任务的要求都隐含包含本节。

### G1. 这是本仓库**第一次引入并发**

`src/` 与 `tests/` 全量 grep `lock` / `SemaphoreSlim` / `Interlocked` / `volatile` / `Mutex` / `Monitor` / `Concurrent*` / `Channel<` / `async void` / `Task.Run` / `Parallel.` / `ConfigureAwait` —— **零命中**。
`CancellationToken` 管道通到底（`Repl` → `Stepper` → `CallAsync` → HTTP），但 `Program.cs:85` **永远传 `None`**。

⇒ 任何并发原语都要**自带理由与测试**，不能靠「以前也这么写」。

### G2. 存储层三个并发洞——**实测**，必须先修

```
并发 File.AppendAllText 同一文件  → IOException      ← 硬失败，不是脏数据
读句柄打开时 append               → IOException      ← 读挡写
AtomicFile 的 tmp 名是固定的 path + ".tmp"（AtomicFile.cs:17） → 并发写必撞名
```

⚠️ **两个修法各修一个洞，不可互替**：`FileShare.ReadWrite` 解决**句柄冲突**；**每会话逻辑串行**解决**字节交错**。

⚠️ **真实时延**：控制台敲 `history` 读历史的同时，头发的消息 append 会抛——**那条用户消息彻底消失**（append 在调模型之前）。

### G3. 落盘格式**已冻结**，`Json.Options` 不许动

`Canonical/Json.cs` 的 `Options` 是 `public static readonly` 共享实例，`history.jsonl` / `state.json` / `spec.json` 全用它。
- ⚠️ 往它的 `Converters`（可变 `List`）里加东西会**污染落盘路径**
- ⚠️ 它的 camelCase + 枚举按名让磁盘契约与线上契约**耦合**：改 `ReasoningKind` 成员名，线上协议跟着变，而头是**独立发布节奏**
- ⇒ **wire 另开一份 `JsonSerializerOptions` 实例**（策略值照抄）

### G4. 多态取自**静态声明类型**——这个仓库最贵的一类缺陷

`Json.cs:26-34` 记着：`Serialize<Block>(b)` 带 `"kind"`；泛型推断成 `TextBlock` 时**判别式整个丢掉**，而**错误只在反序列化时才炸**。
- `Block` 有 `StableSerialize`（钉死基类型）；**`TimelineEvent` 没有等价入口** ⇒ 广播它会**静默丢 `kind`**
- ⚠️ wire 类型**不许继承** `TimelineEvent` / `Block`——继承会拿到**基类**的判别式表，派生类的 `[JsonDerivedType]` **不被读**
- 要复用块就**包含** `IReadOnlyList<Block>`（按 `Block` 声明类型序列化，判别式正常）

### G5. 文档已定的硬约束

- **`03` §1**：核心不关心头在不在 · 命令**投递即返回**、结果走事件 · 头无状态
- ⚠️ **唯一例外**：分配型命令（`session.create`）**必须**回传新身份（`sessionId`）；回执仍是 `ack(id)`，只是带上分配的 id
- **`03` §3**：回执只有 `ack(id)` / `rejected(id, reason)`；⚠️ **`rejected` 只用于「命令本身不合法」**（会话不存在、轴取值非法），**不用于业务结果** ⇒「提供商没了」「模型 400」「上游 503」**一个都不能是 `rejected`**，只能是 `error` 事件
- **`03` §4**：每个事件带 `seq`（**流内单调增**）
- **`03` §7**：每流各自一个游标；`hello(lastSeq)` → 补发；漏太多 → `resync_required` → 头拉快照重建（**唯一**降级路径）
- **`03` §4.1**：流式是**转发不是记录**；**不支持断线续传**；步结束才合并成一条完整模型消息落盘
- **`03` §8.1**：控制台**不订阅任何流**，但 ⚠️ **它的命令照样产生事件**
- **`04` §1.1**：控制台与头**走同一条命令通道、同一个命令集、同一个回执**；「命令投递即返回」在本地**退化成同步拿回执**
- **`05` §2.1**：默认 `http://127.0.0.1:7788`；⚠️ **端口被占必须显式失败并报告，不许静默换端口**
- **`README`**：⚠️ **「无头也能自跑」是硬约束** ⇒ stdin 命令面必须活着；⚠️ stdin 那条结构叫「**命令面**」，**不与「控制面」并列**
- **`02` §1**：⚠️ **不许引入 per-session 活对象**——「没有循环、没有定时器、没有订阅、没有端口」⇒ 订阅挂**核心级**
- **`01` §6.1**：`入队(x)` = 若 x 是事件**立即追加进记录** + 安排一次「启动」（去重）；`启动(s)` = Running 则 no-op、队列空则 no-op、否则**取空队列**推进。**「合并是队列的副产品」**

### G6. 报错要说准

- Windows 上端口落在 **excluded port range** 时 bind 失败是 **`WSAEACCES`(10013)**，**不是** `WSAEADDRINUSE` ⇒ 别把原因写死成「端口被占」（会把用户引向「去杀另一个核心」，而其实没有）
- ⚠️ **端口守卫保护的是端口，不是数据根**：`--port 7789` 就绕过了。**真正的「一设备一核心」锁放在数据根**（`FileShare.None` 独占 `<dataRoot>/.lilara.lock`）

### G7. 注释纪律（本项目付过 19 次代价）

- **没实际跑过，不许写 `实测` / `观测到` / `验证过`**
- **不许写 `唯一` / `全部` / `无一例外` 而不逐个枚举**被量化的东西
- **不许写会腐的数字**（测试总数、守卫条数）——改说「见 X」
- **观测不到的写「由…推得」**，不许写「实测」
- ⚠️ **钉住一个行为要跑两次**：变异版必须红 **且** 正确版必须绿。只跑正确版会得出「它没区分力」的**相反**结论

---

### Task 1: 存储层并发加固

**为什么**：G2 的三个洞是**实测**的。不先修，后面每条都会以间歇性 `IOException` 复现，而那种 bug 最难查。

**Files:**
- Modify: `src/Lilara.Core/Persistence/HistoryLog.cs`
- Modify: `src/Lilara.Core/Persistence/AtomicFile.cs`
- Test: `tests/Lilara.Core.Tests/HistoryLogTests.cs`（新建）、`tests/Lilara.Core.Tests/AtomicFileTests.cs`（加）

**Interfaces:**
- Consumes: 无
- Produces: `HistoryLog.Append` / `ReadAll`、`AtomicFile.Write` —— **签名不变**，只改内部的文件共享模式与 tmp 命名

- [ ] **Step 1: 写失败的并发测试**（这是「写路径无锁」论断的**唯一**可执行证据，现在没有）

```csharp
// HistoryLogTests.cs
using Lilara.Core.Canonical;
using Lilara.Core.Persistence;
using Lilara.Core.Records;

namespace Lilara.Core.Tests;

public class HistoryLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lilara-hist-" + Guid.NewGuid().ToString("N"));
    private string Path_ => System.IO.Path.Combine(_root, "history.jsonl");

    public HistoryLogTests() => Directory.CreateDirectory(_root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    private static TimelineEvent Evt(string text) => new TimelineEvent.MessageEvent
    {
        At = DateTimeOffset.UnixEpoch,
        From = Speaker.User,
        Blocks = [new Block.TextBlock(text)],
    };

    /// <summary>
    /// ⚠️ **实测过的真缺陷**：`File.AppendAllText` 默认 `FileShare.Read`，
    /// 并发两个 append 会在开句柄那一刻抛 `IOException`（Windows：文件正被另一进程使用）。
    /// 这不是「脏数据」，是**硬失败**——而调用方看到的是「消息发丢了」。
    /// ⚠️ 这条也钉住「读挡写」：`ReadLines` 开着句柄时 append 同样抛。
    /// </summary>
    [Fact]
    public void Append_ToleratesConcurrentWriters()
    {
        var errors = new List<Exception>();

        var a = Task.Run(() => { for (var i = 0; i < 200; i++) try { HistoryLog.Append(Path_, Evt("a")); } catch (Exception e) { lock (errors) errors.Add(e); } });
        var b = Task.Run(() => { for (var i = 0; i < 200; i++) try { HistoryLog.Append(Path_, Evt("b")); } catch (Exception e) { lock (errors) errors.Add(e); } });
        Task.WaitAll(a, b);

        Assert.Empty(errors);
    }

    /// <summary>
    /// ⚠️ 读句柄开着时也必须能 append（实测旧实现抛 `IOException`）。
    /// 真实时延：控制台敲 `history` 读历史，同时头发的消息 append。
    /// </summary>
    [Fact]
    public void Append_SucceedsWhileAReadIsInFlight()
    {
        HistoryLog.Append(Path_, Evt("一"));

        using var reader = new StreamReader(
            new FileStream(Path_, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));

        var ex = Record.Exception(() => HistoryLog.Append(Path_, Evt("二")));

        Assert.Null(ex);
    }
}
```

```csharp
// AtomicFileTests.cs —— 追加
    /// <summary>
    /// ⚠️ **实测过的真缺陷**：旧实现的 tmp 名是**固定的** `path + ".tmp"`，
    /// 两个并发写会写同一个 tmp 再各自 `Move`，第二个 `Move` 抛。
    /// 真实时延：`SaveState` 与 HTTP 快照端点同时碰同一个 `state.json`。
    /// </summary>
    [Fact]
    public void Write_ToleratesConcurrentWritersToTheSamePath()
    {
        var target = Path.Combine(_root, "state.json");
        var errors = new List<Exception>();

        var a = Task.Run(() => { for (var i = 0; i < 100; i++) try { AtomicFile.Write(target, $"{{\"n\":{i}}}"); } catch (Exception e) { lock (errors) errors.Add(e); } });
        var b = Task.Run(() => { for (var i = 0; i < 100; i++) try { AtomicFile.Write(target, $"{{\"n\":{i + 1000}}}"); } catch (Exception e) { lock (errors) errors.Add(e); } });
        Task.WaitAll(a, b);

        Assert.Empty(errors);
        Assert.True(File.Exists(target));
        Assert.False(File.Exists(target + ".tmp"));   // 不留残渣
    }
```

- [ ] **Step 2: 跑测试，确认失败**

```bash
dotnet test --filter "FullyQualifiedName~Append_ToleratesConcurrentWriters|FullyQualifiedName~Append_SucceedsWhileAReadIsInFlight|FullyQualifiedName~Write_ToleratesConcurrentWritersToTheSamePath"
```
Expected: FAIL —— 三条都抛 `IOException`

- [ ] **Step 3: 改实现**

`HistoryLog.cs`：`Append` 与 `ReadAll` 都改成**显式开 `FileStream`**，共享模式 `FileShare.ReadWrite`。
```csharp
// Append：把 File.AppendAllText 换成
using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
{
    w.Write(line);
    w.Write('\n');
}

// ReadAll：把 File.ReadLines 换成
using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
using var reader = new StreamReader(fs, Encoding.UTF8);
while (reader.ReadLine() is { } line) { ... }
```
⚠️ **注释要写清两个洞各修一个**：`ReadWrite` 只解决**句柄冲突**，**不解决字节交错**——两个 append 交错写仍会写出坏行，那靠**每会话逻辑串行**（Task 2）解决。

`AtomicFile.cs`：tmp 名带 `Guid`；`File.Move` 短重试。
```csharp
var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
File.WriteAllText(tmp, content, new UTF8Encoding(false));

// ⚠️ Windows 上目标被读句柄占着时 Move 会 IOException（读没带 FILE_SHARE_DELETE）。
// 短重试；失败则清 tmp 再抛——**不许留残渣**。
for (var attempt = 0; ; attempt++)
{
    try { File.Move(tmp, path, overwrite: true); break; }
    catch (IOException) when (attempt < 3) { Thread.Sleep(10 * (attempt + 1)); }
    catch { TryDelete(tmp); throw; }
}
```

- [ ] **Step 4: 跑测试，确认通过**

```bash
dotnet test
```
Expected: PASS（全量 295 + 新增 3）

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "fix(core): 存储层并发加固（FileShare.ReadWrite / Guid tmp + 重试）"
```

---

### Task 2: `CoreHost` + 数据根锁

**为什么**：`store` / `providers` / `stepper` / `repl` 全是 `Program.cs` 顶层局部量，**没有任何可挂载的宿主**。控制面需要一个。

**Files:**
- Create: `src/Lilara.Core/Hosting/CoreHost.cs`
- Create: `src/Lilara.Core/Hosting/DataRootLock.cs`
- Test: `tests/Lilara.Core.Tests/CoreHostTests.cs`、`CoreHostLockTests.cs`

**Interfaces:**
- Consumes: `SessionStore`、`ProviderStore`、`StepperResolver`
- Produces:
  - `CoreHost(string dataRoot, string workingDirectory)`；`SessionStore Store`；`ProviderStore Providers`；`string WorkingDirectory`
  - `SessionHandle Activate(string projectKey, string sessionId)` —— 载入并登记；已激活则返回同一个实例
  - `bool TryGetActivated(string sessionId, out SessionHandle)` —— 快照端点先查这个
  - `SemaphoreSlim GateFor(string sessionId)` —— **每会话一把**
  - `CancellationTokenSource? CtsFor(string sessionId)` / `void SetCts(string sessionId, CancellationTokenSource?)`
  - `Stepper StepperFor(SessionHandle)` —— 按 handle 的 `Spec.Model.ProviderId` 解析并缓存
  - `void InvalidateSteppers()` —— `provider.upsert` 后调
  - `DataRootLock.Acquire(string dataRoot) → IDisposable`

- [ ] **Step 1: 写失败的测试**

```csharp
    [Fact]
    public void Activate_ReturnsTheSameInstanceForTheSameSession()
    {
        var host = NewHost();
        var spec = ...;   // 建一个会话
        var a = host.Activate(pk, id);
        var b = host.Activate(pk, id);
        Assert.Same(a, b);
    }

    [Fact]
    public void GateFor_IsStablePerSession()
    {
        var host = NewHost();
        Assert.Same(host.GateFor("s1"), host.GateFor("s1"));
        Assert.NotSame(host.GateFor("s1"), host.GateFor("s2"));
    }

    /// <summary>
    /// ⚠️ stepper 必须按会话的 provider 解析——共用一把会把 A 的历史发到 B 的端点。
    /// </summary>
    [Fact]
    public void StepperFor_ResolvesPerSessionsOwnProvider() { ... }

    [Fact]
    public void DataRootLock_SecondAcquireOnTheSameRootFails() { ... }
    [Fact]
    public void DataRootLock_DifferentRootsBothSucceed() { ... }
```

- [ ] **Step 2: 跑测试确认失败** → `CS0246`
- [ ] **Step 3: 写实现**
- [ ] **Step 4: 跑全量确认通过**
- [ ] **Step 5: 提交** —— `feat(core): CoreHost（激活表 / 每会话信号量 / CTS / stepper 缓存）+ 数据根独占锁`

⚠️ `DataRootLock` 用 `FileShare.None` 独占 `<dataRoot>/.lilara.lock`；**错误信息要说准**：「数据根 X 已被另一个核心持有」——**不要**说成端口问题（G6）。

---

### Task 3: `CommandDispatcher` + 控制台改走它

**为什么**：`04` §1.1 明说控制台与头**走同一条路、同一套命令集、同一个回执**。今天 `Repl` 直连 handler，这层抽取**不是可选项**。

**Files:**
- Create: `src/Lilara.Core/Hosting/CommandDispatcher.cs`
- Modify: `src/Lilara.Core/Console/Repl.cs`（改为调 dispatcher）
- Test: `tests/Lilara.Core.Tests/CommandDispatcherTests.cs`

**Interfaces:**
- Produces:
  - `Task<Ack> DispatchAsync(CoreCommand cmd, bool awaitCompletion, CancellationToken ct)`
  - `Ack` = `ack(id, sessionId?)` | `rejected(id, reason)`
  - 命令集（`03` §3 的子集）：`session.create` / `session.activate` / `session.send` / `session.start` / `session.interrupt` / `provider.upsert`

⚠️ **分工**：文本形态的守卫（末尾缺值 / 值形如 `--` / 未知标志）**留在控制台适配器**——那是控制台专属语法，搬进 dispatcher 会把它泄漏到线上协议。**语义校验**（未知协议 / 未知 auth）**搬进 dispatcher**，否则线上 `provider.upsert` 能写进一个控制台写不进的坏值。

⚠️ **`rejected` vs `error` 的分界**（G5）：「提供商没了」「模型 400」**不是** `rejected`，是 `error` 事件。

- [ ] **Step 1-5**: TDD；既有 `ReplTests` 必须**全绿**（它们断言的行为不变，只是现在多走一层）

---

### Task 4: `session.send` 保文档语义

**为什么**：用户裁定「保文档」。`01` §6.1：`入队(x)` 立即追加进记录 + 安排启动（去重）；`启动(s)` 若 Running 则 no-op、否则**取空队列**推进 ⇒ 连发 3 条 = **1 次模型调用**。
今天 `StepAsync` 是「一条消息一步、且自己 append」。

**Files:**
- Modify: `src/Lilara.Core/Session/Stepper.cs`（**不再 append**，收队列）
- Modify: `src/Lilara.Core/Hosting/CommandDispatcher.cs`（`session.send` = append + 入队 + 启动）
- Test: `tests/Lilara.Core.Tests/StepperTests.cs`（三条断言**移位置**）、`CommandDispatcherTests.cs`（加）

⚠️ **`StepAsync(handle, string, ct)` 留成薄兼容壳**，转调 `StepAsync(handle, IReadOnlyList<Block>, ct)`。
⚠️ 三条会红的测试：`Step_AppendsBothTurnsToDisk` / `Step_LeavesHistoryIntactWhenCallFails` / `Step_DoesNotWriteModelTurnWhenRefused` —— **断言值不变，只是断言对象从 `StepAsync` 移到 dispatcher。改位置不是删测试。**
⚠️ **`Pending` 恒 `[]`** 写成显式偏离（它的类型装不下 blocks）。

---

### Task 5: 线格式 + 金样测试

**Files:**
- Create: `src/Lilara.Core/Control/WireMessage.cs`、`WireJson.cs`
- Modify: `src/Lilara.Core/Records/TimelineEvent.cs`（补 `StableSerialize` 等价入口）
- Test: `tests/Lilara.Core.Tests/WireJsonTests.cs`、`DiskFormatGoldenTests.cs`

⚠️ 按 G3/G4：**wire 另开 `JsonSerializerOptions`**；wire 类型**独立成根不继承**；给 `TimelineEvent` 补稳定序列化入口。
⚠️ **金样测试**：对固定 `SessionSpec`/`SessionState`/`TimelineEvent` 断言**精确字符串**——这是「磁盘格式已冻结」唯一可执行的证据。

---

### Task 6: 事件泵（seq / 订阅 / 游标）

**Files:**
- Create: `src/Lilara.Core/Control/EventBus.cs`、`EventPump.cs`
- Test: `tests/Lilara.Core.Tests/EventPumpTests.cs`

⚠️ 按 G5 `03` §4/§7：
- 所有生产者 → **一个 `Channel<Event>`** → **单消费者泵**分配 `seq` → fan-out **每流有界队列**
- ⚠️ **不许「先 `Interlocked` 再各自写 socket」**：编号唯一 ≠ **投递有序**。甲拿 5、乙拿 6，乙先写 ⇒ 头收 6 再收 5 ⇒ `lastSeq` 前进到 6，**5 永久跳过且无从发现**——违反 `03` §4 硬约束
- **ack 也走同一个泵**（头才能保证「收到 `ack(id)` 之后不会收到 `id` 之前产生的事件」）
- ⚠️ **不许全局环形缓冲**（A 流式会挤掉 B 的事件 ⇒ 抖动）。**每流各自**有界队列
- `resync_required` 判据一句话：**「该流被要求补发的起点 seq 已不在该流的待补发缓冲里」**
- ⚠️ **首次订阅原子性**：快照响应带该流 **seq 高水位**（与 seq 分配同临界区取），头 `subscribe(topic, since:)`
- **最小事件集**：`session.created` / **`session.step.begin`** / **`session.step.end`** / **`message.appended`** / `session.state`
- ⚠️ **`step.begin/end` 才是在跑的信号源**——`session.state` 是磁盘镜像，而**磁盘从不存 Running**（`Stepper.cs:39` 只改内存）
- ⚠️ **拒答路径 `Turn++` 仍执行** ⇒ `step.end` **不能**挂在 append 之后（会漏掉拒答）

---

### Task 7: 中断

**Files:**
- Modify: `src/Lilara.Core/Hosting/CommandDispatcher.cs`
- Create: `src/Lilara.Core/Adapters/Cancellation.cs`（抽出 `ShouldWrap`）
- Test: `tests/Lilara.Core.Tests/InterruptTests.cs`

⚠️ **CTS 每步一个**，`interrupt` 只在「Running 且 CTS 非空」时 Cancel **并置空**。
⚠️ 长命 CTS 会让 interrupt 落在 Idle 期时把**下一次**的 token 变成已取消 ⇒ `ShouldWrap` 判「调用方取消」⇒ **不包装** ⇒ 裸 `OperationCanceledException` 冒到 `Repl.cs:76`（不接）⇒ **打死进程**。
⚠️ **后台任务必须把「取消」和「失败」分开**：`catch (OperationCanceledException) when (ct.IsCancellationRequested)` → `session.state(Idle)`；真失败才 `error`。否则**每次正常中断都变成一条红色 error**。
⚠️ **`ShouldWrap` 已有两份**（`ClaudeAdapter.cs:83`、`OpenAiHttp.cs:91`），两处注释都说「出现第三份再抽」——**现在就是那个时刻**：抽到一处。
⚠️ 协议里写明 **`interrupt` 是 best-effort**：步已过模型调用则取消无效，头应收到 `step.end`。

---

### Task 8: Kestrel 宿主 + 端点

**Files:**
- Modify: `src/Lilara.Core/Lilara.Core.csproj`（`FrameworkReference`）
- Create: `src/Lilara.Core/Control/ControlServer.cs`
- Modify: `src/Lilara.Core/Program.cs`
- Test: `tests/Lilara.Core.Tests/ControlServerTests.cs`

⚠️ 按 G5/G6：
- ⚠️ **ASP.NET Core 的 analyzer 会进编译**，`TreatWarningsAsErrors` 一视同仁 ⇒ **预期第一把 build 冒新警告**；用**点 ID 的 `NoWarn` + 理由**，别整体关 analyzer
- ⚠️ `Microsoft.NET.Sdk` 下 `WebApplication.CreateBuilder` 的 content root = **当前工作目录**（与 `Program.cs:14` 的 `workingDirectory` 是两回事）⇒ 显式 `UseContentRoot`
- ⚠️ **`Console.Write("lilara> ")` 不带换行，Kestrel 默认日志写 stdout，两者交错会搅烂正在敲的那行** ⇒ `builder.Logging.ClearProviders()`
- **`await app.StartAsync()` 并 catch，别等 `RunAsync`**（否则提示符先打出来、进程才死）
- **端口可注入**（构造参数/环境变量），只在默认值上不许静默换——否则测试里两个 host 并行必撞
- ⚠️ **快照端点先查内存激活表再回落磁盘**：读磁盘会在步进行中报 **Idle** ⇒ 头渲染「空闲」⇒ 用户再点一次 ⇒ **烧钱**
- 头是独立 origin ⇒ **放开 CORS**

---

### Task 9: 头（**糊弄**）

**Files:**
- Create: `head/index.html`、`head/app.js`、`head/head.csproj`

独立进程，Kestrel 托管静态文件；页面 JS **直连核心** `127.0.0.1:7788`（不做代理）。
一个页面：会话列表 / 时间线 / 输入框 / 发送 / 中断。
⚠️ **如实说明它只履行 `05` §4 四条义务里的 1 条**（两个槽位原样显示——而系统块恒空，正好对应「空的也要显示」）。

---

## 验证

1. `dotnet test` —— 295 基线 + 新增；被移动断言的测试必须**仍然断言同一件事**
2. **并发金样**（Task 1）：两 `Task` 同时 append 同一路径不抛
3. **金样序列化**（Task 5）：固定对象的**精确字符串**；再对真实 `~/.lilara` 做前后字节 diff
4. **seq 有序性**（Task 6）：多生产者并发发事件，订阅者收到的 `seq` **严格递增**
5. **端到端**：起核心 → 起头 → 浏览器 → 建会话 → 发消息 → 看时间线 → 中断
6. 每阶段一个 commit，便于回滚

## 明确不做（写进文档的偏离）

- **队列只在内存**，`Pending` 恒 `[]`（`01` §6.1/§12.8 要求进 `state.json`，但 `Pending` 是 `IReadOnlyList<string>`，**装不下 blocks**；趁磁盘上还没有第二份数据，将来可改成块列表——**这次不改**）
- **`term.delta` 不做**（`03` §4.1 已声明不支持续传，最容易被砍）
- **`session.state` 的 `Running` 不落盘**（既有设计）⇒「在跑」只能靠 `step.begin/end`
- 附件 / 库 / 五轴 / 槽位面板 / 审批 / 声明 / 控制点 —— 全不做
- **全局 in-flight 上限**（`02` §6）：不引入，每会话一把信号量而已
