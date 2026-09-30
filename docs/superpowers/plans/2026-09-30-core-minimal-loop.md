# 核心最小闭环 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让核心在终端里真的能跟 Claude 聊一轮，并且重启后历史还在。

**Architecture:** 核心是常驻的控制台程序（`04` §1.1）——本计划只做**最小闭环**：数据根 → provider 配置 → 建会话 → 渲染 → 一次模型调用 → 步边界落盘 → 回放。**不起 HTTP/WS 服务，不做头，不做插件，不做工具。**

⚠️ **核心启动后不建任何会话**（`01` §8.1）——「配置完提供商自动建初始会话」是**头的行为**。本计划里由**控制台**代劳：它发现没有会话时显式敲 `session create`（与头调 `session.create` 是同一条命令，只是入口不同）。

⚠️ **因为没有插件，核心不带任何工具、不带任何提示词**（`04` §1.3）——所以首版闭环是**纯 chat**，连系统提示词都没有。这不是妥协，这正是「最小」的定义。

**Tech Stack:** .NET 10 · C# 13 · xunit · `System.Text.Json` · **`Anthropic`（官方 C# SDK，NuGet）**

**Spec:** [`docs/README.md`](../../README.md) 为索引。本计划直接实现：
- [`01-会话设计.md`](../../01-会话设计.md) — §3 记录形状 · §4 步 · §6.1 队列 · §7 配置分层 · §8 持久化布局
- [`04-插件与内部结构.md`](../../04-插件与内部结构.md) — §1.1 控制台 · §1.2 适配器焊死 · §1.3 核心是空壳
- [`03-控制面协议.md`](../../03-控制面协议.md) — §3 命令集（控制台的命令名从这来）

## Global Constraints

- **目标框架 `net10.0`**，语言版本默认（C# 13）。
- **命名空间根是 `Lilara.Core`**，程序集名 `Lilara.Core`，解决方案名 `AgentLilaraCode`。
- **所有落盘文件 UTF-8 且不带 BOM**（`new UTF8Encoding(false)`）。带 BOM 会让 `history.jsonl` 第一行解析失败。
- **JSON 不转义非 ASCII**：`Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping`（`Unsafe` 是相对 HTML 注入说的，本地文件存储正是它的正确用途）。否则中文被写成 `\u4f60\u597d`，人不可读。
- **`history.jsonl` 只追加，永不重写**（`01` §12.4）。
- **`state.json` 每步原子重写**（先写 `.tmp` 再 replace）。
- **`spec.json` 创建时写一次，此后不可变**（`01` §8）。
- **步是持久化的原子单位**（`01` §4）：模型输出在内存里合并完，落盘只落一次。
- **核心不带工具、不带提示词**（`04` §1.3）。本计划中系统块**恒为空**。
- **控制台不是头**（`04` §1.1）：不订阅、不流式渲染、非流式调用。
- ⚠️ **模型调用用非流式**（`max_tokens` 固定 16000，低于 SDK 的 HTTP 超时阈值）。
- ⚠️ **`claude-opus-5` 的 thinking 默认开启**，`max_tokens` 同时封顶「思考 + 正文」。16000 对聊天足够；不要往下调。
- ⚠️ **`stop_reason` 可能是 `"refusal"`**（安全分类器拒答，HTTP 仍是 200）。**读 `content` 之前必须先查它**——拒答时 `content` 可能是空的。

## Review Focus

按「最可能咬到人」排序。每条都要落到某个任务的测试里。

1. **首轮空时间线** —— 全新会话没有任何历史。空 `messages` 数组会被 API 以 400 拒绝。期望行为：首轮必须至少有一条用户消息；`Stepper` 在没有用户输入时不得发起调用。
2. **`stop_reason: "refusal"`** —— 拒答时 `content` 可能是空数组。期望行为：核心不崩、不写空模型消息、把拒答如实告诉用户。
3. **落盘崩溃点** —— `history.jsonl` 追加之后、`state.json` 重写之前崩溃。期望行为：重启后历史完整（步已落盘），`turn` 计数偏小但**不损坏**；不得出现半个 JSON 行。
4. **会话选择** —— 核心启动时没有任何会话（`01` §8.1）。期望行为：控制台**不自动建**；没有会话时发消息给人话；敲过 `session create` 之后，**再启动要能自动选回那一条**。
5. **中文往返** —— 用户输入中文，落盘再读回来。期望行为：逐字节相同，且文件里是明文中文不是 `\uXXXX`。
6. **凭证缺失/无效** —— provider 没配，或 key 是错的（401）。期望行为：给一句人话，不吐栈。

---

### Task 1: 解决方案骨架 + 数据根 + 原子写

**Files:**
- Create: `AgentLilaraCode.sln`（`dotnet new sln` 生成）
- Create: `src/Lilara.Core/Lilara.Core.csproj`
- Create: `src/Lilara.Core/Persistence/DataRoot.cs`
- Create: `src/Lilara.Core/Persistence/AtomicFile.cs`
- Create: `tests/Lilara.Core.Tests/Lilara.Core.Tests.csproj`
- Test: `tests/Lilara.Core.Tests/DataRootTests.cs`
- Test: `tests/Lilara.Core.Tests/AtomicFileTests.cs`

**Interfaces:**
- Consumes: 无（第一个任务）
- Produces:
  - `Lilara.Core.Persistence.DataRoot.Resolve(Func<string,string?>? getEnv = null) → string`
  - `Lilara.Core.Persistence.DataRoot.EnvVar` （`const string = "LILARA_HOME"`）
  - `Lilara.Core.Persistence.AtomicFile.Write(string path, string content) → void`

- [ ] **Step 1: 初始化仓库（⚠️ 只做一次）**

⚠️ **当前仓库还不是 git 仓库**（只有 `docs/`，无 `.git`），而本计划每个任务都以 commit 收尾——所以这一步必须最先做。

```bash
git init
```

`.gitignore`：

```gitignore
bin/
obj/
*.user
.vs/
.idea/
*.tmp
```

然后**先把已有的设计文档存进去**（它们目前完全没被版本控制）：

```bash
git add docs/ .gitignore
git commit -m "docs: 设计文档（00-05 + 实现计划）"
```

- [ ] **Step 2: 建解决方案骨架**

在仓库根 `E:\Workspace\AgentLilaraCode` 执行：

```bash
dotnet new sln -n AgentLilaraCode
dotnet new classlib -o src/Lilara.Core -f net10.0
dotnet new xunit -o tests/Lilara.Core.Tests -f net10.0
dotnet sln add src/Lilara.Core/Lilara.Core.csproj
dotnet sln add tests/Lilara.Core.Tests/Lilara.Core.Tests.csproj
dotnet add tests/Lilara.Core.Tests/Lilara.Core.Tests.csproj reference src/Lilara.Core/Lilara.Core.csproj
rm src/Lilara.Core/Class1.cs
```

把 `src/Lilara.Core/Lilara.Core.csproj` 改成（`RootNamespace` 必须显式设，否则默认取 `Lilara.Core`——但显式写出来更稳）：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>Lilara.Core</RootNamespace>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

- [ ] **Step 3: 写失败的测试**

`tests/Lilara.Core.Tests/DataRootTests.cs`：

```csharp
using Lilara.Core.Persistence;

namespace Lilara.Core.Tests;

public class DataRootTests
{
    [Fact]
    public void Resolve_UsesEnvVarWhenSet()
    {
        var actual = DataRoot.Resolve(name =>
            name == DataRoot.EnvVar ? @"C:\tmp\lilara-home" : null);

        Assert.Equal(Path.GetFullPath(@"C:\tmp\lilara-home"), actual);
    }

    [Fact]
    public void Resolve_FallsBackToUserProfile()
    {
        var actual = DataRoot.Resolve(_ => null);

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(profile, ".lilara"), actual);
    }

    [Fact]
    public void Resolve_IgnoresBlankEnvVar()
    {
        var actual = DataRoot.Resolve(name =>
            name == DataRoot.EnvVar ? "   " : null);

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(profile, ".lilara"), actual);
    }
}
```

`tests/Lilara.Core.Tests/AtomicFileTests.cs`：

```csharp
using System.Text;
using Lilara.Core.Persistence;

namespace Lilara.Core.Tests;

public class AtomicFileTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "lilara-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Write_CreatesMissingDirectories()
    {
        var path = Path.Combine(_dir, "nested", "deep", "state.json");

        AtomicFile.Write(path, "{}");

        Assert.Equal("{}", File.ReadAllText(path));
    }

    [Fact]
    public void Write_LeavesNoTempFileBehind()
    {
        var path = Path.Combine(_dir, "state.json");

        AtomicFile.Write(path, "{}");

        Assert.False(File.Exists(path + ".tmp"));
        Assert.Single(Directory.GetFiles(_dir), "state.json");
    }

    [Fact]
    public void Write_WritesUtf8WithoutBom()
    {
        var path = Path.Combine(_dir, "state.json");

        AtomicFile.Write(path, "你好");

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(Encoding.UTF8.GetBytes("你好"), bytes);
        Assert.NotEqual(0xEF, bytes[0]);
    }

    [Fact]
    public void Write_OverwritesExistingContent()
    {
        var path = Path.Combine(_dir, "state.json");
        AtomicFile.Write(path, "old");

        AtomicFile.Write(path, "new");

        Assert.Equal("new", File.ReadAllText(path));
    }
}
```

- [ ] **Step 4: 跑测试，确认失败**

```bash
dotnet test --filter "FullyQualifiedName~DataRootTests|FullyQualifiedName~AtomicFileTests"
```

Expected: FAIL — `CS0246: The type or namespace name 'DataRoot' could not be found`

- [ ] **Step 5: 写实现**

`src/Lilara.Core/Persistence/DataRoot.cs`：

```csharp
namespace Lilara.Core.Persistence;

/// <summary>
/// 全局数据根的解析（01 §8）。
/// LILARA_HOME ?? ~/.lilara（Windows 下 ~ = %USERPROFILE%）。
/// 多用户天然隔离：每个 OS 用户一份数据根，不需要额外的换用户机制。
/// </summary>
public static class DataRoot
{
    public const string EnvVar = "LILARA_HOME";

    public static string Resolve(Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;

        var configured = getEnv(EnvVar);
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(profile, ".lilara");
    }
}
```

`src/Lilara.Core/Persistence/AtomicFile.cs`：

```csharp
using System.Text;

namespace Lilara.Core.Persistence;

/// <summary>
/// 原子重写：先写 .tmp，再替换。
/// 崩溃在两步之间只会留下一个孤儿 .tmp，目标文件永远不是半截的。
/// </summary>
public static class AtomicFile
{
    public static void Write(string path, string content)
    {
        var dir = Path.GetDirectoryName(path)
                  ?? throw new ArgumentException($"路径没有目录部分: {path}", nameof(path));
        Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tmp, path, overwrite: true);
    }
}
```

- [ ] **Step 6: 跑测试，确认通过**

```bash
dotnet test --filter "FullyQualifiedName~DataRootTests|FullyQualifiedName~AtomicFileTests"
```

Expected: PASS，7 个测试全绿。

- [ ] **Step 7: 提交**

```bash
git add -A
git commit -m "feat(core): 解决方案骨架 + 数据根解析 + 原子写"
```

---

### Task 2: canonical 块模型

**Files:**
- Create: `src/Lilara.Core/Canonical/Block.cs`
- Create: `src/Lilara.Core/Canonical/Json.cs`
- Test: `tests/Lilara.Core.Tests/BlockTests.cs`

**Interfaces:**
- Consumes: 无
- Produces:
  - `Lilara.Core.Canonical.Block`（抽象基类，`[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]`）
  - `Lilara.Core.Canonical.Block.TextBlock(string Text)`（派生，判别值 `"text"`）
  - `Lilara.Core.Canonical.Json.Options`（`JsonSerializerOptions` 单例）

- [ ] **Step 1: 写失败的测试**

`tests/Lilara.Core.Tests/BlockTests.cs`：

```csharp
using System.Text.Json;
using Lilara.Core.Canonical;

namespace Lilara.Core.Tests;

public class BlockTests
{
    [Fact]
    public void RoundTrip_PreservesDiscriminator()
    {
        Block original = new Block.TextBlock("看看这个报错");

        var json = JsonSerializer.Serialize(original, Json.Options);
        var back = JsonSerializer.Deserialize<Block>(json, Json.Options);

        Assert.Equal(original, back);
        Assert.Contains("\"kind\":\"text\"", json);
    }

    [Fact]
    public void Serialize_DoesNotEscapeNonAscii()
    {
        Block original = new Block.TextBlock("你好");

        var json = JsonSerializer.Serialize(original, Json.Options);

        Assert.Contains("你好", json);
        Assert.DoesNotContain("\\u4f60", json);
    }

    [Fact]
    public void RoundTrip_ThroughBlockList()
    {
        var original = new List<Block> { new Block.TextBlock("a"), new Block.TextBlock("b") };

        var json = JsonSerializer.Serialize(original, Json.Options);
        var back = JsonSerializer.Deserialize<List<Block>>(json, Json.Options);

        Assert.Equal(original, back);
    }
}
```

- [ ] **Step 2: 跑测试，确认失败**

```bash
dotnet test --filter "FullyQualifiedName~BlockTests"
```

Expected: FAIL — `CS0246: The type or namespace name 'Block' could not be found`

- [ ] **Step 3: 写实现**

`src/Lilara.Core/Canonical/Block.cs`：

```csharp
using System.Text.Json.Serialization;

namespace Lilara.Core.Canonical;

/// <summary>
/// 时间线上每个事件的载荷是一串块（01 §3.4）。
/// ⚠️ 最小闭环只有文本块——因为核心不带任何工具、不带任何提示词（04 §1.3）。
/// 图片 / 工具调用 / 工具结果 / 推理块按同样方式追加 [JsonDerivedType] 即可。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(TextBlock), "text")]
public abstract record Block
{
    /// <summary>具体载荷：内容即渲染结果，不可变。</summary>
    public sealed record TextBlock(string Text) : Block;
}
```

`src/Lilara.Core/Canonical/Json.cs`：

```csharp
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lilara.Core.Canonical;

/// <summary>落盘与回读共用的 JSON 设置。</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // ⚠️ 不转义非 ASCII。Unsafe 是相对「注入 HTML」说的；
        // 本地文件存储正是它的正确用途，否则中文会被写成 \u4f60\u597d。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
```

- [ ] **Step 4: 跑测试，确认通过**

```bash
dotnet test --filter "FullyQualifiedName~BlockTests"
```

Expected: PASS，3 个测试全绿。

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "feat(core): canonical 块模型（判别式联合 + 不转义非 ASCII）"
```

---

### Task 3: 时间线记录

**Files:**
- Create: `src/Lilara.Core/Records/TimelineEvent.cs`
- Test: `tests/Lilara.Core.Tests/TimelineEventTests.cs`

**Interfaces:**
- Consumes: `Block`（Task 2）、`Json.Options`（Task 2）
- Produces:
  - `Lilara.Core.Records.Speaker`（枚举：`User` / `Model`）
  - `Lilara.Core.Records.TimelineEvent`（抽象基类，判别键 `"kind"`，含 `DateTimeOffset At`）
  - `Lilara.Core.Records.TimelineEvent.MessageEvent`（派生，判别值 `"message"`，含 `Speaker From` + `IReadOnlyList<Block> Blocks`）

- [ ] **Step 1: 写失败的测试**

`tests/Lilara.Core.Tests/TimelineEventTests.cs`：

```csharp
using System.Text.Json;
using Lilara.Core.Canonical;
using Lilara.Core.Records;

namespace Lilara.Core.Tests;

public class TimelineEventTests
{
    [Fact]
    public void RoundTrip_PreservesEverything()
    {
        var at = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        TimelineEvent original = new TimelineEvent.MessageEvent
        {
            At = at,
            From = Speaker.User,
            Blocks = [new Block.TextBlock("你好")],
        };

        var json = JsonSerializer.Serialize(original, Json.Options);
        var back = JsonSerializer.Deserialize<TimelineEvent>(json, Json.Options);

        Assert.Equal(original, back);
    }

    [Fact]
    public void RoundTrip_PreservesModelSpeaker()
    {
        TimelineEvent original = new TimelineEvent.MessageEvent
        {
            At = DateTimeOffset.UnixEpoch,
            From = Speaker.Model,
            Blocks = [new Block.TextBlock("hi")],
        };

        var json = JsonSerializer.Serialize(original, Json.Options);
        var back = (TimelineEvent.MessageEvent)JsonSerializer
            .Deserialize<TimelineEvent>(json, Json.Options)!;

        Assert.Equal(Speaker.Model, back.From);
    }

    [Fact]
    public void Serialize_UsesStableDiscriminator()
    {
        TimelineEvent original = new TimelineEvent.MessageEvent
        {
            At = DateTimeOffset.UnixEpoch,
            From = Speaker.User,
            Blocks = [],
        };

        var json = JsonSerializer.Serialize(original, Json.Options);

        Assert.Contains("\"kind\":\"message\"", json);
    }
}
```

- [ ] **Step 2: 跑测试，确认失败**

```bash
dotnet test --filter "FullyQualifiedName~TimelineEventTests"
```

Expected: FAIL — `CS0246: The type or namespace name 'TimelineEvent' could not be found`

- [ ] **Step 3: 写实现**

`src/Lilara.Core/Records/TimelineEvent.cs`：

```csharp
using System.Text.Json.Serialization;
using Lilara.Core.Canonical;

namespace Lilara.Core.Records;

/// <summary>发言者。最小闭环只有这两种。</summary>
public enum Speaker
{
    User,
    Model,
}

/// <summary>
/// 记录 = 事件时间线 + 两个固定槽位（头、尾）（01 §3）。
/// 本计划只实现时间线——头尾两个槽位要等插件系统。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(MessageEvent), "message")]
public abstract record TimelineEvent
{
    public required DateTimeOffset At { get; init; }

    /// <summary>用户 / 模型的发言。工具调用记录将来也走这里（一块的两面，00 §5）。</summary>
    public sealed record MessageEvent : TimelineEvent
    {
        public required Speaker From { get; init; }
        public required IReadOnlyList<Block> Blocks { get; init; }
    }
}
```

- [ ] **Step 4: 跑测试，确认通过**

```bash
dotnet test --filter "FullyQualifiedName~TimelineEventTests"
```

Expected: PASS，3 个测试全绿。

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "feat(core): 时间线记录类型"
```

---

### Task 4: 会话持久化三件套

**Files:**
- Create: `src/Lilara.Core/Persistence/SessionSpec.cs`
- Create: `src/Lilara.Core/Persistence/SessionState.cs`
- Create: `src/Lilara.Core/Persistence/HistoryLog.cs`
- Create: `src/Lilara.Core/Persistence/SessionStore.cs`
- Test: `tests/Lilara.Core.Tests/SessionStoreTests.cs`

**Interfaces:**
- Consumes: `TimelineEvent`（Task 3）、`Block`/`Json`（Task 2）、`AtomicFile`（Task 1）
- Produces:
  - `Lilara.Core.Persistence.ModelSelection`（record：`ProviderId` `ModelId` `Protocol` `Effort` `MaxTokens`）
  - `Lilara.Core.Persistence.SessionSpec`（record：`SessionId` `ProjectKey` `WorkingDirectory` `CreatedAt` `Model`）
  - `Lilara.Core.Persistence.SessionState`（record：`SessionId` `Status` `Turn` `Pending`）
  - `Lilara.Core.Persistence.SessionStore`：
    - `SessionStore(string dataRoot)`
    - `string SessionDirectory(string projectKey, string sessionId)`
    - `void WriteSpec(string projectKey, SessionSpec spec)`
    - `SessionSpec ReadSpec(string projectKey, string sessionId)`
    - `void WriteState(string projectKey, SessionState state)`
    - `SessionState ReadState(string projectKey, string sessionId)`
    - `void AppendEvent(string projectKey, string sessionId, TimelineEvent evt)`
    - `IReadOnlyList<TimelineEvent> ReadTimeline(string projectKey, string sessionId)`

- [ ] **Step 1: 写失败的测试**

`tests/Lilara.Core.Tests/SessionStoreTests.cs`：

```csharp
using Lilara.Core.Canonical;
using Lilara.Core.Persistence;
using Lilara.Core.Records;

namespace Lilara.Core.Tests;

public class SessionStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-store-" + Guid.NewGuid().ToString("N"));
    private readonly SessionStore _store;

    public SessionStoreTests() => _store = new SessionStore(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static SessionSpec Spec() => new()
    {
        SessionId = "s1",
        ProjectKey = "p1",
        WorkingDirectory = @"C:\work",
        CreatedAt = DateTimeOffset.UnixEpoch,
        Model = new ModelSelection
        {
            ProviderId = "claude",
            ModelId = "claude-opus-5",
            Protocol = "claude-messages",
            Effort = "high",
            MaxTokens = 16000,
        },
    };

    private static SessionState State(int turn) => new()
    {
        SessionId = "s1",
        Status = "Idle",
        Turn = turn,
        Pending = [],
    };

    private static TimelineEvent Msg(string text) => new TimelineEvent.MessageEvent
    {
        At = DateTimeOffset.UnixEpoch,
        From = Speaker.User,
        Blocks = [new Block.TextBlock(text)],
    };

    [Fact]
    public void Spec_RoundTrips()
    {
        _store.WriteSpec("p1", Spec());

        Assert.Equal(Spec(), _store.ReadSpec("p1", "s1"));
    }

    [Fact]
    public void State_RoundTripsAndOverwrites()
    {
        _store.WriteState("p1", State(1));
        _store.WriteState("p1", State(2));

        Assert.Equal(2, _store.ReadState("p1", "s1").Turn);
    }

    [Fact]
    public void Timeline_AppendsInOrder()
    {
        _store.AppendEvent("p1", "s1", Msg("一"));
        _store.AppendEvent("p1", "s1", Msg("二"));

        var timeline = _store.ReadTimeline("p1", "s1");

        Assert.Equal(2, timeline.Count);
        Assert.Equal("一", ((Block.TextBlock)((TimelineEvent.MessageEvent)timeline[0]).Blocks[0]).Text);
        Assert.Equal("二", ((Block.TextBlock)((TimelineEvent.MessageEvent)timeline[1]).Blocks[0]).Text);
    }

    [Fact]
    public void Timeline_IsOneJsonObjectPerLine()
    {
        _store.AppendEvent("p1", "s1", Msg("a"));
        _store.AppendEvent("p1", "s1", Msg("b"));

        var lines = File.ReadAllLines(
            Path.Combine(_store.SessionDirectory("p1", "s1"), "history.jsonl"));

        Assert.Equal(2, lines.Length);
        Assert.All(lines, l => Assert.StartsWith("{", l));
    }

    [Fact]
    public void Timeline_ChineseSurvivesRoundTripByteForByte()
    {
        _store.AppendEvent("p1", "s1", Msg("看看这个报错"));

        var raw = File.ReadAllText(
            Path.Combine(_store.SessionDirectory("p1", "s1"), "history.jsonl"));

        Assert.Contains("看看这个报错", raw);
        var back = _store.ReadTimeline("p1", "s1");
        Assert.Equal("看看这个报错",
            ((Block.TextBlock)((TimelineEvent.MessageEvent)back[0]).Blocks[0]).Text);
    }

    [Fact]
    public void Timeline_EmptyFileReadsAsEmptyList()
    {
        Directory.CreateDirectory(_store.SessionDirectory("p1", "s1"));

        Assert.Empty(_store.ReadTimeline("p1", "s1"));
    }
}
```

- [ ] **Step 2: 跑测试，确认失败**

```bash
dotnet test --filter "FullyQualifiedName~SessionStoreTests"
```

Expected: FAIL — `CS0246: The type or namespace name 'SessionStore' could not be found`

- [ ] **Step 3: 写实现**

`src/Lilara.Core/Persistence/SessionSpec.cs`：

```csharp
namespace Lilara.Core.Persistence;

/// <summary>
/// 会话持有的模型选择：解析结果的快照 + 指向定义的引用（01 §7.2）。
/// 会话必须能回答「这一步是哪个模型跑的」，否则重启后历史来路不明。
/// </summary>
public sealed record ModelSelection
{
    /// <summary>指向 <c>providers/</c> 里的定义。</summary>
    public required string ProviderId { get; init; }

    /// <summary>「换模型 = 改会话配置」改的是这个。</summary>
    public required string ModelId { get; init; }

    /// <summary>快照：建会话时协议是什么。老会话遇到新适配器也不慌。</summary>
    public required string Protocol { get; init; }

    /// <summary>思考强度轴（01 §3.5）。纯请求参数，绝不进上下文。</summary>
    public required string Effort { get; init; }

    public required int MaxTokens { get; init; }
}

/// <summary>创建时写一次，此后不可变（01 §8）。</summary>
public sealed record SessionSpec
{
    public required string SessionId { get; init; }
    public required string ProjectKey { get; init; }
    public required string WorkingDirectory { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required ModelSelection Model { get; init; }
}
```

`src/Lilara.Core/Persistence/SessionState.cs`：

```csharp
namespace Lilara.Core.Persistence;

/// <summary>每步原子重写（01 §8）。</summary>
public sealed record SessionState
{
    public required string SessionId { get; init; }

    /// <summary>Idle / Running / WaitingForInput / WaitingExternal / Blocked（01 §5）。</summary>
    public required string Status { get; init; }

    /// <summary>已完成的步数。</summary>
    public required int Turn { get; init; }

    /// <summary>
    /// 待办集合 = 消息队列（01 §6.1）。进 state.json，崩溃重启后待办不丢。
    /// ⚠️ 最小闭环是交互式的（入队即启动），队列表恒为空。
    /// </summary>
    public required IReadOnlyList<string> Pending { get; init; }
}
```

`src/Lilara.Core/Persistence/HistoryLog.cs`：

```csharp
using System.Text;
using System.Text.Json;
using Lilara.Core.Canonical;
using Lilara.Core.Records;

namespace Lilara.Core.Persistence;

/// <summary>
/// 每步追加的骨架记录（01 §8）。
/// ⚠️ 只追加，永不重写（01 §12.4）——「简化」是加载期解释，不是落盘时改写。
/// </summary>
public static class HistoryLog
{
    public static void Append(string path, TimelineEvent evt)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        var line = JsonSerializer.Serialize(evt, Json.Options);
        File.AppendAllText(path, line + "\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public static IReadOnlyList<TimelineEvent> ReadAll(string path)
    {
        if (!File.Exists(path)) return [];

        var result = new List<TimelineEvent>();
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            result.Add(JsonSerializer.Deserialize<TimelineEvent>(line, Json.Options)!);
        }
        return result;
    }
}
```

`src/Lilara.Core/Persistence/SessionStore.cs`：

```csharp
using System.Text.Json;
using Lilara.Core.Canonical;
using Lilara.Core.Records;

namespace Lilara.Core.Persistence;

/// <summary>
/// 会话在磁盘上的落点（01 §8）：
///   &lt;数据根&gt;/projects/&lt;projectKey&gt;/sessions/&lt;sessionId&gt;/{spec.json, state.json, history.jsonl}
/// </summary>
public sealed class SessionStore(string dataRoot)
{
    private readonly string _dataRoot = dataRoot;

    public string SessionDirectory(string projectKey, string sessionId) =>
        Path.Combine(_dataRoot, "projects", projectKey, "sessions", sessionId);

    public string ProjectsRoot => Path.Combine(_dataRoot, "projects");

    public void WriteSpec(string projectKey, SessionSpec spec) =>
        AtomicFile.Write(Path.Combine(SessionDirectory(projectKey, spec.SessionId), "spec.json"),
            JsonSerializer.Serialize(spec, Json.Options));

    public SessionSpec ReadSpec(string projectKey, string sessionId) =>
        JsonSerializer.Deserialize<SessionSpec>(
            File.ReadAllText(Path.Combine(SessionDirectory(projectKey, sessionId), "spec.json")),
            Json.Options)!;

    public void WriteState(string projectKey, SessionState state) =>
        AtomicFile.Write(Path.Combine(SessionDirectory(projectKey, state.SessionId), "state.json"),
            JsonSerializer.Serialize(state, Json.Options));

    public SessionState ReadState(string projectKey, string sessionId) =>
        JsonSerializer.Deserialize<SessionState>(
            File.ReadAllText(Path.Combine(SessionDirectory(projectKey, sessionId), "state.json")),
            Json.Options)!;

    public void AppendEvent(string projectKey, string sessionId, TimelineEvent evt) =>
        HistoryLog.Append(Path.Combine(SessionDirectory(projectKey, sessionId), "history.jsonl"), evt);

    public IReadOnlyList<TimelineEvent> ReadTimeline(string projectKey, string sessionId) =>
        HistoryLog.ReadAll(Path.Combine(SessionDirectory(projectKey, sessionId), "history.jsonl"));

    /// <summary>该项目下已有的全部会话 id。启动时「选最近的会话」用它（01 §8.1）。</summary>
    public IReadOnlyList<string> ListSessions(string projectKey)
    {
        var dir = Path.Combine(ProjectsRoot, projectKey, "sessions");
        if (!Directory.Exists(dir)) return [];
        return Directory.GetDirectories(dir).Select(Path.GetFileName).OfType<string>().ToList();
    }
}
```

- [ ] **Step 4: 跑测试，确认通过**

```bash
dotnet test --filter "FullyQualifiedName~SessionStoreTests"
```

Expected: PASS，6 个测试全绿。

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "feat(core): 会话持久化三件套（spec / state / history）"
```

---

### Task 5: 项目键 + 建会话

**Files:**
- Create: `src/Lilara.Core/Projects/ProjectKey.cs`
- Create: `src/Lilara.Core/Session/SessionHandle.cs`
- Test: `tests/Lilara.Core.Tests/ProjectKeyTests.cs`
- Test: `tests/Lilara.Core.Tests/SessionHandleTests.cs`

**Interfaces:**
- Consumes: `SessionStore`/`SessionSpec`/`SessionState`/`ModelSelection`（Task 4）
- Produces:
  - `Lilara.Core.Projects.ProjectKey.FromPath(string workingDirectory) → string`（16 位小写十六进制）
  - `Lilara.Core.Session.SessionHandle`（record：`ModelSelection Model` `ProjectKey` `Spec` `State`；提供 `SessionId`、`HistoryPath`、`StatePath`）
  - `Lilara.Core.Session.SessionHandle.Create(SessionStore store, string workingDirectory, ModelSelection model) → SessionHandle`
  - `Lilara.Core.Session.SessionHandle.Load(SessionStore store, string projectKey, string sessionId) → SessionHandle`
  - `Lilara.Core.Session.SessionHandle.SaveState(SessionStore store) → void`

- [ ] **Step 1: 写失败的测试**

`tests/Lilara.Core.Tests/ProjectKeyTests.cs`：

```csharp
using Lilara.Core.Projects;

namespace Lilara.Core.Tests;

public class ProjectKeyTests
{
    [Fact]
    public void FromPath_IsStable()
    {
        var a = ProjectKey.FromPath(@"C:\work\proj");
        var b = ProjectKey.FromPath(@"C:\work\proj");

        Assert.Equal(a, b);
        Assert.Equal(16, a.Length);
    }

    [Fact]
    public void FromPath_IsCaseInsensitive()
    {
        // Windows 路径大小写不敏感（01 §12.1 要求规范化）
        Assert.Equal(ProjectKey.FromPath(@"C:\Work\Proj"),
                     ProjectKey.FromPath(@"c:\work\proj"));
    }

    [Fact]
    public void FromPath_IgnoresTrailingSeparator()
    {
        Assert.Equal(ProjectKey.FromPath(@"C:\work\proj"),
                     ProjectKey.FromPath(@"C:\work\proj\"));
    }

    [Fact]
    public void FromPath_DiffersForDifferentDirectories()
    {
        Assert.NotEqual(ProjectKey.FromPath(@"C:\work\a"),
                        ProjectKey.FromPath(@"C:\work\b"));
    }
}
```

`tests/Lilara.Core.Tests/SessionHandleTests.cs`：

```csharp
using Lilara.Core.Persistence;
using Lilara.Core.Session;

namespace Lilara.Core.Tests;

public class SessionHandleTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-handle-" + Guid.NewGuid().ToString("N"));
    private readonly SessionStore _store;

    public SessionHandleTests() => _store = new SessionStore(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static ModelSelection Model() => new()
    {
        ProviderId = "claude",
        ModelId = "claude-opus-5",
        Protocol = "claude-messages",
        Effort = "high",
        MaxTokens = 16000,
    };

    [Fact]
    public void Create_WritesSpecAndStateToDisk()
    {
        var handle = SessionHandle.Create(_store, @"C:\work", Model());

        Assert.True(File.Exists(handle.SpecPath));
        Assert.True(File.Exists(handle.StatePath));
        Assert.Equal(0, handle.State.Turn);
        Assert.Equal("Idle", handle.State.Status);
    }

    [Fact]
    public void Create_RecordsTheWorkingDirectoryInTheSpec()
    {
        var handle = SessionHandle.Create(_store, @"C:\work", Model());

        Assert.Equal(Path.GetFullPath(@"C:\work"), handle.Spec.WorkingDirectory);
    }

    [Fact]
    public void Load_RestoresSpecAndState()
    {
        var created = SessionHandle.Create(_store, @"C:\work", Model());

        var loaded = SessionHandle.Load(_store, created.ProjectKey, created.SessionId);

        Assert.Equal(created.Spec, loaded.Spec);
        Assert.Equal(created.State, loaded.State);
    }

    [Fact]
    public void SaveState_PersistsTurnCounter()
    {
        var handle = SessionHandle.Create(_store, @"C:\work", Model());
        handle.State = handle.State with { Turn = 3 };

        handle.SaveState(_store);

        Assert.Equal(3, _store.ReadState(handle.ProjectKey, handle.SessionId).Turn);
    }

    [Fact]
    public void Create_ProducesDistinctSessionIdsPerCall()
    {
        var a = SessionHandle.Create(_store, @"C:\work", Model());
        var b = SessionHandle.Create(_store, @"C:\work", Model());

        Assert.NotEqual(a.SessionId, b.SessionId);
        Assert.Equal(2, _store.ListSessions(a.ProjectKey).Count);
    }
}
```

- [ ] **Step 2: 跑测试，确认失败**

```bash
dotnet test --filter "FullyQualifiedName~ProjectKeyTests|FullyQualifiedName~SessionHandleTests"
```

Expected: FAIL — `CS0246: The type or namespace name 'ProjectKey' could not be found`

- [ ] **Step 3: 写实现**

`src/Lilara.Core/Projects/ProjectKey.cs`：

```csharp
using System.Security.Cryptography;
using System.Text;

namespace Lilara.Core.Projects;

/// <summary>
/// projectKey = 工作目录规范化后的指纹（01 §8）。
/// Project 是涌现的、不是声明的：一个文件夹天然就是一个 project（01 §8.1）。
/// </summary>
public static class ProjectKey
{
    public static string FromPath(string workingDirectory)
    {
        var normalized = Path.GetFullPath(workingDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToLowerInvariant(); // Windows 下大小写不敏感（01 §12.1）

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(hash)[..16];
    }
}
```

`src/Lilara.Core/Session/SessionHandle.cs`：

```csharp
using Lilara.Core.Persistence;

namespace Lilara.Core.Session;

/// <summary>
/// 一个已激活会话在内存里的把手：数据 + 目录位置。
/// ⚠️ 它会变，但它是**数据**——没有循环、没有定时器（02 §5「激活即缓存」）。
/// </summary>
public sealed record SessionHandle
{
    public required string ProjectKey { get; init; }
    public required SessionSpec Spec { get; init; }
    public required SessionState State { get; set; }
    public required string Directory { get; init; }

    public string SessionId => Spec.SessionId;
    public string SpecPath => Path.Combine(Directory, "spec.json");
    public string StatePath => Path.Combine(Directory, "state.json");
    public string HistoryPath => Path.Combine(Directory, "history.jsonl");

    public static SessionHandle Create(SessionStore store, string workingDirectory, ModelSelection model)
    {
        var projectKey = Projects.ProjectKey.FromPath(workingDirectory);
        var spec = new SessionSpec
        {
            SessionId = Guid.NewGuid().ToString("N")[..12],
            ProjectKey = projectKey,
            WorkingDirectory = Path.GetFullPath(workingDirectory),
            CreatedAt = DateTimeOffset.UtcNow,
            Model = model,
        };
        var state = new SessionState
        {
            SessionId = spec.SessionId,
            Status = "Idle",
            Turn = 0,
            Pending = [],
        };

        store.WriteSpec(projectKey, spec);
        store.WriteState(projectKey, state);

        return new SessionHandle
        {
            ProjectKey = projectKey,
            Spec = spec,
            State = state,
            Directory = store.SessionDirectory(projectKey, spec.SessionId),
        };
    }

    public static SessionHandle Load(SessionStore store, string projectKey, string sessionId) => new()
    {
        ProjectKey = projectKey,
        Spec = store.ReadSpec(projectKey, sessionId),
        State = store.ReadState(projectKey, sessionId),
        Directory = store.SessionDirectory(projectKey, sessionId),
    };

    public void SaveState(SessionStore store) => store.WriteState(ProjectKey, State);
}
```

- [ ] **Step 4: 跑测试，确认通过**

```bash
dotnet test --filter "FullyQualifiedName~ProjectKeyTests|FullyQualifiedName~SessionHandleTests"
```

Expected: PASS，9 个测试全绿。

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "feat(core): 项目键指纹 + 会话把手"
```

---

### Task 6: 提供商配置存储

**Files:**
- Create: `src/Lilara.Core/Providers/ProviderDefinition.cs`
- Create: `src/Lilara.Core/Providers/ProviderStore.cs`
- Test: `tests/Lilara.Core.Tests/ProviderStoreTests.cs`

**Interfaces:**
- Consumes: `AtomicFile`（Task 1）、`ModelSelection`（Task 4）
- Produces:
  - `Lilara.Core.Providers.ProviderDefinition`（record：`Id` `Protocol` `ApiKey` `DefaultModel` `Modality`）
  - `Lilara.Core.Providers.ProviderStore`：
    - `ProviderStore(string dataRoot)`
    - `void Save(ProviderDefinition def)`
    - `ProviderDefinition? Load(string id)`
    - `IReadOnlyList<ProviderDefinition> List()`
    - `ModelSelection ToSelection(ProviderDefinition def, string? modelOverride, string effort, int maxTokens)`

- [ ] **Step 1: 写失败的测试**

`tests/Lilara.Core.Tests/ProviderStoreTests.cs`：

```csharp
using Lilara.Core.Providers;

namespace Lilara.Core.Tests;

public class ProviderStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-prov-" + Guid.NewGuid().ToString("N"));
    private readonly ProviderStore _store;

    public ProviderStoreTests() => _store = new ProviderStore(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static ProviderDefinition Def(string id = "claude") => new()
    {
        Id = id,
        Protocol = "claude-messages",
        ApiKey = "sk-test-not-a-real-key",
        DefaultModel = "claude-opus-5",
        Modality = "vision",
    };

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        _store.Save(Def());

        Assert.Equal(Def(), _store.Load("claude"));
    }

    [Fact]
    public void Load_ReturnsNullWhenAbsent()
    {
        Assert.Null(_store.Load("nope"));
    }

    [Fact]
    public void Save_OverwritesExistingDefinition()
    {
        _store.Save(Def());
        _store.Save(Def() with { DefaultModel = "claude-sonnet-5" });

        Assert.Equal("claude-sonnet-5", _store.Load("claude")!.DefaultModel);
        Assert.Single(_store.List());
    }

    [Fact]
    public void List_ReturnsAllSaved()
    {
        _store.Save(Def("a"));
        _store.Save(Def("b"));

        Assert.Equal(2, _store.List().Count);
    }

    [Fact]
    public void ToSelection_UsesDefaultModelWhenNoOverride()
    {
        var selection = _store.ToSelection(Def(), null, "high", 16000);

        Assert.Equal("claude-opus-5", selection.ModelId);
        Assert.Equal("claude", selection.ProviderId);
        Assert.Equal("claude-messages", selection.Protocol);
        Assert.Equal("high", selection.Effort);
        Assert.Equal(16000, selection.MaxTokens);
    }

    [Fact]
    public void ToSelection_HonoursOverride()
    {
        var selection = _store.ToSelection(Def(), "claude-sonnet-5", "medium", 8000);

        Assert.Equal("claude-sonnet-5", selection.ModelId);
        Assert.Equal("medium", selection.Effort);
        Assert.Equal(8000, selection.MaxTokens);
    }
}
```

- [ ] **Step 2: 跑测试，确认失败**

```bash
dotnet test --filter "FullyQualifiedName~ProviderStoreTests"
```

Expected: FAIL — `CS0246: The type or namespace name 'ProviderStore' could not be found`

- [ ] **Step 3: 写实现**

`src/Lilara.Core/Providers/ProviderDefinition.cs`：

```csharp
namespace Lilara.Core.Providers;

/// <summary>
/// 提供商定义，存全局（含 key）（01 §7 配置分层的全局层）。
/// 会话只存「引用 + 选择」，不存 key。
/// </summary>
public sealed record ProviderDefinition
{
    public required string Id { get; init; }

    /// <summary>协议类型。适配器按它分派（04 §1.2：内建，焊死）。</summary>
    public required string Protocol { get; init; }

    /// <summary>⚠️ 明文。数据根是本用户私有的（01 §8）。</summary>
    public required string ApiKey { get; init; }

    public required string DefaultModel { get; init; }

    /// <summary>
    /// 模态能力声明（03 §9.2）：判据是**提供商配置的一部分**，不是适配器的运行时契约。
    /// 最小闭环用不到（没有附件），但字段现在就留着。
    /// </summary>
    public required string Modality { get; init; }
}
```

`src/Lilara.Core/Providers/ProviderStore.cs`：

```csharp
using System.Text.Json;
using Lilara.Core.Canonical;
using Lilara.Core.Persistence;

namespace Lilara.Core.Providers;

/// <summary>提供商定义的落点：&lt;数据根&gt;/providers/&lt;id&gt;.json。</summary>
public sealed class ProviderStore(string dataRoot)
{
    private readonly string _root = Path.Combine(dataRoot, "providers");

    private string PathFor(string id) => Path.Combine(_root, $"{id}.json");

    public void Save(ProviderDefinition def) =>
        AtomicFile.Write(PathFor(def.Id), JsonSerializer.Serialize(def, Json.Options));

    public ProviderDefinition? Load(string id)
    {
        var path = PathFor(id);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<ProviderDefinition>(File.ReadAllText(path), Json.Options)
            : null;
    }

    public IReadOnlyList<ProviderDefinition> List()
    {
        if (!Directory.Exists(_root)) return [];
        return Directory.GetFiles(_root, "*.json")
            .Select(f => JsonSerializer.Deserialize<ProviderDefinition>(
                File.ReadAllText(f), Json.Options)!)
            .OrderBy(d => d.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 组装会话持有的模型选择：解析结果的快照 + 指向定义的引用（01 §7.2）。
    /// </summary>
    public ModelSelection ToSelection(
        ProviderDefinition def, string? modelOverride, string effort, int maxTokens) => new()
    {
        ProviderId = def.Id,
        ModelId = modelOverride ?? def.DefaultModel,
        Protocol = def.Protocol,
        Effort = effort,
        MaxTokens = maxTokens,
    };
}
```

- [ ] **Step 4: 跑测试，确认通过**

```bash
dotnet test --filter "FullyQualifiedName~ProviderStoreTests"
```

Expected: PASS，6 个测试全绿。

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "feat(core): 提供商定义与存储"
```

---

### Task 7: 模型调用契约 + Claude 请求构造

**Files:**
- Create: `src/Lilara.Core/Adapters/ModelCall.cs`
- Create: `src/Lilara.Core/Adapters/Claude/ClaudeRequestBuilder.cs`
- Modify: `src/Lilara.Core/Lilara.Core.csproj`（加 `Anthropic` 包引用）
- Test: `tests/Lilara.Core.Tests/ClaudeRequestBuilderTests.cs`

**Interfaces:**
- Consumes: `TimelineEvent`/`Speaker`（Task 3）、`Block`（Task 2）、`ModelSelection`（Task 4）
- Produces:
  - `Lilara.Core.Adapters.ModelRequest`（record：`ModelId` `MaxTokens` `Effort` `SystemBlocks` `Timeline`）
  - `Lilara.Core.Adapters.ModelReply`（record：`Blocks` `StopReason` `Refused`）
  - `Lilara.Core.Adapters.Adapters.ModelCallFailedException`（异常）
  - `Lilara.Core.Adapters.Claude.ClaudeRequestBuilder.Build(ModelRequest) → Anthropic.Models.Messages.MessageCreateParams`

- [ ] **Step 1: 加包引用**

```bash
dotnet add src/Lilara.Core/Lilara.Core.csproj package Anthropic
```

- [ ] **Step 2: 写失败的测试**

`tests/Lilara.Core.Tests/ClaudeRequestBuilderTests.cs`：

```csharp
using Lilara.Core.Adapters;
using Lilara.Core.Adapters.Claude;
using Lilara.Core.Canonical;
using Lilara.Core.Records;
using Anthropic.Models.Messages;
using AnthropicRole = Anthropic.Models.Messages.Role;
using CanonicalBlock = Lilara.Core.Canonical.Block;

namespace Lilara.Core.Tests;

public class ClaudeRequestBuilderTests
{
    private static TimelineEvent Msg(Speaker from, string text) => new TimelineEvent.MessageEvent
    {
        At = DateTimeOffset.UnixEpoch,
        From = from,
        Blocks = [new CanonicalBlock.TextBlock(text)],
    };

    private static ModelRequest Request(params TimelineEvent[] timeline) => new()
    {
        ModelId = "claude-opus-5",
        MaxTokens = 16000,
        Effort = "high",
        SystemBlocks = [],
        Timeline = timeline,
    };

    [Fact]
    public void Build_PassesModelAndMaxTokens()
    {
        var p = ClaudeRequestBuilder.Build(Request(Msg(Speaker.User, "hi")));

        Assert.Equal("claude-opus-5", p.Model.ToString());
        Assert.Equal(16000, p.MaxTokens);
    }

    [Fact]
    public void Build_MapsUserToUserRole()
    {
        var p = ClaudeRequestBuilder.Build(Request(Msg(Speaker.User, "你好")));

        Assert.Single(p.Messages);
        Assert.Equal(AnthropicRole.User, p.Messages[0].Role);
    }

    [Fact]
    public void Build_MapsModelToAssistantRole()
    {
        var p = ClaudeRequestBuilder.Build(Request(
            Msg(Speaker.User, "你好"),
            Msg(Speaker.Model, "你好！")));

        Assert.Equal(2, p.Messages.Count);
        Assert.Equal(AnthropicRole.Assistant, p.Messages[1].Role);
    }

    [Fact]
    public void Build_PreservesOrder()
    {
        var p = ClaudeRequestBuilder.Build(Request(
            Msg(Speaker.User, "一"),
            Msg(Speaker.Model, "二"),
            Msg(Speaker.User, "三")));

        var texts = p.Messages
            .Select(m => m.Content.Value as string)
            .ToList();

        Assert.Equal(["一", "二", "三"], texts);
    }

    [Fact]
    public void Build_OmitsSystemWhenThereAreNoSystemBlocks()
    {
        // 04 §1.3：裸核心连系统提示词都没有
        var p = ClaudeRequestBuilder.Build(Request(Msg(Speaker.User, "hi")));

        Assert.Null(p.System);
    }

    [Fact]
    public void Build_EmitsSystemBlocksWhenPresent()
    {
        var request = Request(Msg(Speaker.User, "hi")) with
        {
            SystemBlocks = [new CanonicalBlock.TextBlock("你是助手")],
        };

        var p = ClaudeRequestBuilder.Build(request);

        Assert.NotNull(p.System);
    }

    [Fact]
    public void Build_SetsEffortOnOutputConfig()
    {
        var p = ClaudeRequestBuilder.Build(Request(Msg(Speaker.User, "hi")));

        Assert.NotNull(p.OutputConfig);
    }

    [Fact]
    public void Build_ThrowsOnEmptyTimeline()
    {
        // Review Focus #1：空 messages 会被 API 以 400 拒绝，必须早失败
        var ex = Assert.Throws<ArgumentException>(
            () => ClaudeRequestBuilder.Build(Request()));

        Assert.Contains("时间线为空", ex.Message);
    }
}
```

- [ ] **Step 3: 跑测试，确认失败**

```bash
dotnet test --filter "FullyQualifiedName~ClaudeRequestBuilderTests"
```

Expected: FAIL — `CS0246: The type or namespace name 'ClaudeRequestBuilder' could not be found`

- [ ] **Step 4: 写实现**

`src/Lilara.Core/Adapters/ModelCall.cs`：

```csharp
using Lilara.Core.Canonical;
using Lilara.Core.Records;

namespace Lilara.Core.Adapters;

/// <summary>
/// 一次模型调用要的全部东西。核心发全量 canonical（00 §6），
/// 适配器负责翻译成具体协议。
/// </summary>
public sealed record ModelRequest
{
    public required string ModelId { get; init; }
    public required int MaxTokens { get; init; }

    /// <summary>思考强度轴，映射到协议参数——**绝不进上下文**（01 §3.5）。</summary>
    public required string Effort { get; init; }

    /// <summary>
    /// 系统块。⚠️ 最小闭环恒为空——核心不带提示词（04 §1.3）。
    /// 「块数恒定」说的是「最多各 1 块」，不是非有不可。
    /// </summary>
    public required IReadOnlyList<Block> SystemBlocks { get; init; }

    public required IReadOnlyList<TimelineEvent> Timeline { get; init; }
}

/// <summary>模型回话的产物。</summary>
public sealed record ModelReply
{
    public required IReadOnlyList<Block> Blocks { get; init; }

    /// <summary>协议原样透传的停止原因，用于诊断。</summary>
    public required string StopReason { get; init; }

    /// <summary>
    /// ⚠️ 安全分类器拒答（Review Focus #2）。拒答时 Blocks 通常为空，
    /// 核心必须当**一等结果**处理，不能当成「模型说了空话」。
    /// </summary>
    public required bool Refused { get; init; }
}

/// <summary>调用失败，带一句人话。</summary>
public sealed class ModelCallFailedException(string message, Exception? inner = null)
    : Exception(message, inner);
```

`src/Lilara.Core/Adapters/Claude/ClaudeRequestBuilder.cs`：

```csharp
using Anthropic.Models.Messages;
using Lilara.Core.Canonical;
using Lilara.Core.Records;

namespace Lilara.Core.Adapters.Claude;

/// <summary>
/// canonical → Claude Messages 请求。
/// ⚠️ 纯函数：同一个 ModelRequest 必须构造出逐字节相同的请求（00 §5 铁律 1）。
/// 这里是前缀稳定性的第一道闸——顺序、块数、无时间戳，全都不能漂。
/// </summary>
public static class ClaudeRequestBuilder
{
    public static MessageCreateParams Build(ModelRequest request)
    {
        // Review Focus #1：空 messages 会被 API 400 拒绝，早失败
        if (request.Timeline.Count == 0)
            throw new ArgumentException("时间线为空，无法构造请求", nameof(request));

        return new MessageCreateParams
        {
            Model = request.ModelId,
            MaxTokens = request.MaxTokens,
            Messages = request.Timeline.Select(ToMessage).ToList(),
            System = BuildSystem(request.SystemBlocks),
            OutputConfig = new OutputConfig { Effort = ToEffort(request.Effort) },
        };
    }

    private static MessageParam ToMessage(TimelineEvent evt)
    {
        var message = (TimelineEvent.MessageEvent)evt;
        var role = message.From == Speaker.User ? Role.User : Role.Assistant;

        var texts = message.Blocks.OfType<Block.TextBlock>().ToList();

        // 单块走字符串形式（协议原生形状，最省），多块走块列表
        if (texts.Count == 1)
            return new MessageParam { Role = role, Content = texts[0].Text };

        return new MessageParam
        {
            Role = role,
            Content = texts
                .Select(t => (ContentBlockParam)new TextBlockParam { Text = t.Text })
                .ToList(),
        };
    }

    /// <summary>
    /// ⚠️ 没有系统块时**不设 System**，而不是设成空串——
    /// 空串会变成一个真实的空 system 块，白白改变前缀。
    /// </summary>
    private static MessageCreateParamsSystem? BuildSystem(IReadOnlyList<Block> blocks)
    {
        if (blocks.Count == 0) return null;

        return new List<TextBlockParam>(
            blocks.OfType<Block.TextBlock>()
                  .Select(t => new TextBlockParam { Text = t.Text }));
    }

    /// <summary>思考强度轴 → 协议参数。低/中/高 → low/medium/high。</summary>
    private static Effort ToEffort(string effort) => effort switch
    {
        "low" => Effort.Low,
        "medium" => Effort.Medium,
        "high" => Effort.High,
        "xhigh" => Effort.XHigh,
        "max" => Effort.Max,
        _ => Effort.High,
    };
}
```

- [ ] **Step 5: 跑测试，确认通过**

```bash
dotnet test --filter "FullyQualifiedName~ClaudeRequestBuilderTests"
```

Expected: PASS，8 个测试全绿。

⚠️ **已知编译风险点**（技能建议的 compile-fix 循环，改完再跑）：
- `p.Model.ToString()` —— 若 `Model` 是普通 `string`，改成 `p.Model`。
- `Effort.XHigh` / `Effort.Max` —— 若枚举缺这两个成员，删掉对应分支，`_ =>` 兜底仍成立。
- `MessageCreateParamsSystem?` —— 若该类型名不同，用编译器报的名字。
- `m.Content.Value as string` —— 若 `Content` 不是 union，改成直接 `m.Content as string`。

- [ ] **Step 6: 提交**

```bash
git add -A
git commit -m "feat(core): 模型调用契约 + Claude 请求构造（纯函数）"
```

---

### Task 8: Claude 响应映射

**Files:**
- Create: `src/Lilara.Core/Adapters/Claude/ClaudeResponseMapper.cs`
- Test: `tests/Lilara.Core.Tests/ClaudeResponseMapperTests.cs`

**Interfaces:**
- Consumes: `ModelReply`（Task 7）、`Block`（Task 2）
- Produces: `Lilara.Core.Adapters.Claude.ClaudeResponseMapper.Map(Anthropic.Models.Messages.Message) → ModelReply`

- [ ] **Step 1: 写失败的测试**

`tests/Lilara.Core.Tests/ClaudeResponseMapperTests.cs`：

```csharp
using Lilara.Core.Adapters.Claude;
using Lilara.Core.Canonical;
using Anthropic.Models.Messages;

namespace Lilara.Core.Tests;

public class ClaudeResponseMapperTests
{
    private static Message Response(string text, string stopReason) => new()
    {
        Id = "msg_test",
        Type = "message",
        Role = Role.Assistant,
        Model = "claude-opus-5",
        StopReason = stopReason,
        Content = [new ContentBlock(new TextBlock { Text = text })],
        Usage = new Usage { InputTokens = 1, OutputTokens = 1 },
    };

    [Fact]
    public void Map_ExtractsTextBlocks()
    {
        var reply = ClaudeResponseMapper.Map(Response("你好！", "end_turn"));

        Assert.Single(reply.Blocks);
        Assert.Equal("你好！", ((Block.TextBlock)reply.Blocks[0]).Text);
        Assert.False(reply.Refused);
    }

    [Fact]
    public void Map_SurfacesStopReason()
    {
        var reply = ClaudeResponseMapper.Map(Response("hi", "max_tokens"));

        Assert.Equal("max_tokens", reply.StopReason);
    }

    [Fact]
    public void Map_FlagsRefusalAndToleratesEmptyContent()
    {
        // Review Focus #2：拒答时 content 可能为空，绝不能在这里炸
        var refusal = new Message
        {
            Id = "msg_test",
            Type = "message",
            Role = Role.Assistant,
            Model = "claude-opus-5",
            StopReason = "refusal",
            Content = [],
            Usage = new Usage { InputTokens = 1, OutputTokens = 0 },
        };

        var reply = ClaudeResponseMapper.Map(refusal);

        Assert.True(reply.Refused);
        Assert.Empty(reply.Blocks);
    }

    [Fact]
    public void Map_IgnoresNonTextBlocks()
    {
        var withThinking = new Message
        {
            Id = "msg_test",
            Type = "message",
            Role = Role.Assistant,
            Model = "claude-opus-5",
            StopReason = "end_turn",
            Content =
            [
                new ContentBlock(new ThinkingBlock { Thinking = "想想", Signature = "sig" }),
                new ContentBlock(new TextBlock { Text = "答案" }),
            ],
            Usage = new Usage { InputTokens = 1, OutputTokens = 1 },
        };

        var reply = ClaudeResponseMapper.Map(withThinking);

        Assert.Single(reply.Blocks);
        Assert.Equal("答案", ((Block.TextBlock)reply.Blocks[0]).Text);
    }
}
```

- [ ] **Step 2: 跑测试，确认失败**

```bash
dotnet test --filter "FullyQualifiedName~ClaudeResponseMapperTests"
```

Expected: FAIL — `CS0246: The type or namespace name 'ClaudeResponseMapper' could not be found`

- [ ] **Step 3: 写实现**

`src/Lilara.Core/Adapters/Claude/ClaudeResponseMapper.cs`：

```csharp
using Anthropic.Models.Messages;
using Lilara.Core.Canonical;

namespace Lilara.Core.Adapters.Claude;

/// <summary>
/// Claude Messages 响应 → canonical。
/// ⚠️ 纯函数。只取文本块：最小闭环没有工具，推理块**永不进上下文**
/// （01 §11.5：推理块逐字回传，永不可优化——那是多轮 tool use 的活儿，本计划不做）。
/// </summary>
public static class ClaudeResponseMapper
{
    public static ModelReply Map(Message response)
    {
        var stopReason = response.StopReason?.ToString() ?? "unknown";
        var refused = stopReason == "refusal";

        // ⚠️ 拒答时 content 可能是空数组，OfType 天然容忍，不要提前索引
        var blocks = response.Content
            .Select(b => b.Value)
            .OfType<TextBlock>()
            .Select(t => (Block)new Block.TextBlock(t.Text))
            .ToList();

        return new ModelReply
        {
            Blocks = blocks,
            StopReason = stopReason,
            Refused = refused,
        };
    }
}
```

- [ ] **Step 4: 跑测试，确认通过**

```bash
dotnet test --filter "FullyQualifiedName~ClaudeResponseMapperTests"
```

Expected: PASS，4 个测试全绿。

⚠️ **已知编译风险点**：
- `response.StopReason?.ToString()` —— 若 `StopReason` 是非空结构体，去掉 `?`。
- `b.Value` —— 若 `Content` 元素不是 union 包装，改成 `response.Content.OfType<TextBlock>()`。
- `new ContentBlock(new TextBlock { ... })` —— 若 `ContentBlock` 没有接受具体块的构造器，用编译器提示的隐式转换写法。

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "feat(core): Claude 响应映射（拒答是一等结果）"
```

---

### Task 9: 适配器接口 + Claude 实现

**Files:**
- Create: `src/Lilara.Core/Adapters/IModelAdapter.cs`
- Create: `src/Lilara.Core/Adapters/Claude/ClaudeAdapter.cs`
- Create: `tests/Lilara.Core.Tests/Fakes/FakeAdapter.cs`
- Test: `tests/Lilara.Core.Tests/ClaudeAdapterTests.cs`

**Interfaces:**
- Consumes: `ModelRequest`/`ModelReply`/`ModelCallFailedException`（Task 7）、`ClaudeRequestBuilder`（Task 7）、`ClaudeResponseMapper`（Task 8）、`ProviderDefinition`（Task 6）
- Produces:
  - `Lilara.Core.Adapters.IModelAdapter`（`Task<ModelReply> CallAsync(ModelRequest, CancellationToken)`）
  - `Lilara.Core.Adapters.Claude.ClaudeAdapter`（构造器 `ClaudeAdapter(ProviderDefinition)`）
  - `Lilara.Core.Tests.Fakes.FakeAdapter`（测试替身，可预置回复/异常）

- [ ] **Step 1: 写失败的测试**

`tests/Lilara.Core.Tests/Fakes/FakeAdapter.cs`：

```csharp
using Lilara.Core.Adapters;

namespace Lilara.Core.Tests.Fakes;

/// <summary>
/// 假适配器：不联网、确定性强。
/// Stepper 的全部测试都跑在它上面——真适配器只做「构造请求 + 发出去 + 映射回来」。
/// </summary>
public sealed class FakeAdapter : IModelAdapter
{
    private readonly Queue<Func<ModelRequest, ModelReply>> _scripted = new();

    public List<ModelRequest> Calls { get; } = [];

    public FakeAdapter Reply(string text, string stopReason = "end_turn") =>
        ReplyWith(_ => new ModelReply
        {
            Blocks = [new Canonical.Block.TextBlock(text)],
            StopReason = stopReason,
            Refused = stopReason == "refusal",
        });

    public FakeAdapter ReplyWith(Func<ModelRequest, ModelReply> factory)
    {
        _scripted.Enqueue(factory);
        return this;
    }

    public FakeAdapter Throws(Exception ex)
    {
        _scripted.Enqueue(_ => throw ex);
        return this;
    }

    public Task<ModelReply> CallAsync(ModelRequest request, CancellationToken ct)
    {
        Calls.Add(request);
        if (_scripted.Count == 0)
            throw new InvalidOperationException("FakeAdapter 没有预置回复了");

        return Task.FromResult(_scripted.Dequeue()(request));
    }
}
```

`tests/Lilara.Core.Tests/ClaudeAdapterTests.cs`：

```csharp
using Lilara.Core.Adapters.Claude;
using Lilara.Core.Providers;

namespace Lilara.Core.Tests;

public class ClaudeAdapterTests
{
    [Fact]
    public void Constructor_RejectsWrongProtocol()
    {
        var wrong = new ProviderDefinition
        {
            Id = "openai",
            Protocol = "openai-chat",
            ApiKey = "x",
            DefaultModel = "gpt",
            Modality = "vision",
        };

        var ex = Assert.Throws<ArgumentException>(() => new ClaudeAdapter(wrong));

        Assert.Contains("claude-messages", ex.Message);
    }

    [Fact]
    public void Constructor_AcceptsClaudeProtocol()
    {
        var def = new ProviderDefinition
        {
            Id = "claude",
            Protocol = "claude-messages",
            ApiKey = "sk-test-not-a-real-key",
            DefaultModel = "claude-opus-5",
            Modality = "vision",
        };

        var adapter = new ClaudeAdapter(def);

        Assert.NotNull(adapter);
    }
}
```

> ⚠️ 这两个测试**不发起网络请求**——它们只验证构造期的守卫。真实调用是手动验收项（Task 12 末尾）。

- [ ] **Step 2: 跑测试，确认失败**

```bash
dotnet test --filter "FullyQualifiedName~ClaudeAdapterTests"
```

Expected: FAIL — `CS0246: The type or namespace name 'ClaudeAdapter' could not be found`

- [ ] **Step 3: 写实现**

`src/Lilara.Core/Adapters/IModelAdapter.cs`：

```csharp
namespace Lilara.Core.Adapters;

/// <summary>
/// 上游协议适配器。⚠️ 内建、焊死在代码里，不做插件（04 §1.2）——
/// 出去的协议是产品的形状，不该可换；进来的能力（MCP）才是用户的形状。
/// </summary>
public interface IModelAdapter
{
    Task<ModelReply> CallAsync(ModelRequest request, CancellationToken ct);
}
```

`src/Lilara.Core/Adapters/Claude/ClaudeAdapter.cs`：

```csharp
using Anthropic;
using Lilara.Core.Providers;

namespace Lilara.Core.Adapters.Claude;

/// <summary>
/// Claude Messages 适配器。走**官方 C# SDK**，不手搓 HTTP。
/// 职责薄得可怜：构造请求 → 发出去 → 映射回来。翻译逻辑全在两个纯函数里。
/// </summary>
public sealed class ClaudeAdapter : IModelAdapter
{
    public const string ProtocolId = "claude-messages";

    private readonly AnthropicClient _client;

    public ClaudeAdapter(ProviderDefinition provider)
    {
        if (provider.Protocol != ProtocolId)
            throw new ArgumentException(
                $"ClaudeAdapter 只认协议 '{ProtocolId}'，收到 '{provider.Protocol}'",
                nameof(provider));

        _client = new AnthropicClient { ApiKey = provider.ApiKey };
    }

    public async Task<ModelReply> CallAsync(ModelRequest request, CancellationToken ct)
    {
        var parameters = ClaudeRequestBuilder.Build(request);

        try
        {
            var response = await _client.Messages.Create(parameters, cancellationToken: ct);
            return ClaudeResponseMapper.Map(response);
        }
        catch (Exception ex)
        {
            throw new ModelCallFailedException(Describe(ex), ex);
        }
    }

    /// <summary>把 SDK 的异常翻译成一句人话（Review Focus #6）。</summary>
    private static string Describe(Exception ex) => ex switch
    {
        Anthropic.Exceptions.AnthropicUnauthorizedException =>
            "API key 无效或已吊销（401）。检查 `provider set` 里的 --key。",
        Anthropic.Exceptions.AnthropicForbiddenException =>
            "这个 key 没有访问该模型的权限（403）。",
        Anthropic.Exceptions.AnthropicNotFoundException =>
            "模型 id 不存在（404）。检查 --model 拼写。",
        Anthropic.Exceptions.AnthropicRateLimitException =>
            "被限流了（429）。等一会儿再试。",
        Anthropic.Exceptions.Anthropic5xxException =>
            "上游 5xx。这是临时的，重试即可。",
        _ => $"模型调用失败：{ex.Message}",
    };
}
```

- [ ] **Step 4: 跑测试，确认通过**

```bash
dotnet test --filter "FullyQualifiedName~ClaudeAdapterTests"
```

Expected: PASS，2 个测试全绿。

⚠️ **已知编译风险点**：
- `Anthropic.Exceptions.*` 命名空间/类名 —— 技能文档列了 `AnthropicApiException` / `AnthropicRateLimitException` / `Anthropic5xxException` 等，但没有给全每个基类的确切名字。若某个不匹配，**删掉那个 case**，保留 `_ =>` 兜底（功能不受影响，只是错误信息退化成通用文案）。
- `_client.Messages.Create(parameters, cancellationToken: ct)` —— 若签名不同，改成 `Create(parameters)`（`CancellationToken` 参数名以编译器为准）。

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "feat(core): 适配器接口 + Claude 实现（官方 SDK）"
```

---

### Task 10: 推进器（一步）

**Files:**
- Create: `src/Lilara.Core/Session/Stepper.cs`
- Test: `tests/Lilara.Core.Tests/StepperTests.cs`

**Interfaces:**
- Consumes: `SessionHandle`（Task 5）、`SessionStore`（Task 4）、`IModelAdapter`/`ModelRequest`/`ModelReply`（Task 7、9）、`TimelineEvent`（Task 3）
- Produces: `Lilara.Core.Session.Stepper`：
  - `Stepper(SessionStore store, IModelAdapter adapter)`
  - `Task<ModelReply> StepAsync(SessionHandle handle, string userText, CancellationToken ct)`

- [ ] **Step 1: 写失败的测试**

`tests/Lilara.Core.Tests/StepperTests.cs`：

```csharp
using Lilara.Core.Adapters;
using Lilara.Core.Canonical;
using Lilara.Core.Persistence;
using Lilara.Core.Records;
using Lilara.Core.Session;
using Lilara.Core.Tests.Fakes;

namespace Lilara.Core.Tests;

public class StepperTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-step-" + Guid.NewGuid().ToString("N"));
    private readonly SessionStore _store;
    private readonly FakeAdapter _adapter = new();
    private readonly Stepper _stepper;

    public StepperTests()
    {
        _store = new SessionStore(_root);
        _stepper = new Stepper(_store, _adapter);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static ModelSelection Model() => new()
    {
        ProviderId = "claude",
        ModelId = "claude-opus-5",
        Protocol = "claude-messages",
        Effort = "high",
        MaxTokens = 16000,
    };

    private SessionHandle NewSession() => SessionHandle.Create(_store, @"C:\work", Model());

    private static string TextOf(TimelineEvent e) =>
        ((Block.TextBlock)((TimelineEvent.MessageEvent)e).Blocks[0]).Text;

    [Fact]
    public async Task Step_AppendsBothTurnsToDisk()
    {
        var handle = NewSession();
        _adapter.Reply("你好！");

        await _stepper.StepAsync(handle, "你好", CancellationToken.None);

        var timeline = _store.ReadTimeline(handle.ProjectKey, handle.SessionId);
        Assert.Equal(2, timeline.Count);
        Assert.Equal(Speaker.User, ((TimelineEvent.MessageEvent)timeline[0]).From);
        Assert.Equal(Speaker.Model, ((TimelineEvent.MessageEvent)timeline[1]).From);
        Assert.Equal("你好", TextOf(timeline[0]));
        Assert.Equal("你好！", TextOf(timeline[1]));
    }

    [Fact]
    public async Task Step_SendsFullCanonicalTimelineEachTime()
    {
        var handle = NewSession();
        _adapter.Reply("一").Reply("二");

        await _stepper.StepAsync(handle, "问一", CancellationToken.None);
        await _stepper.StepAsync(handle, "问二", CancellationToken.None);

        // 第二次调用必须带上完整历史（00 §6：核心永远发全量 canonical）
        Assert.Equal(1, _adapter.Calls[0].Timeline.Count);
        Assert.Equal(3, _adapter.Calls[1].Timeline.Count);
    }

    [Fact]
    public async Task Step_AdvancesTurnCounter()
    {
        var handle = NewSession();
        _adapter.Reply("ok");

        await _stepper.StepAsync(handle, "hi", CancellationToken.None);

        Assert.Equal(1, handle.State.Turn);
        Assert.Equal(1, _store.ReadState(handle.ProjectKey, handle.SessionId).Turn);
    }

    [Fact]
    public async Task Step_DoesNotWriteModelTurnWhenRefused()
    {
        // Review Focus #2：拒答不落空模型消息
        var handle = NewSession();
        _adapter.Reply("", stopReason: "refusal");

        var reply = await _stepper.StepAsync(handle, "hi", CancellationToken.None);

        Assert.True(reply.Refused);
        var timeline = _store.ReadTimeline(handle.ProjectKey, handle.SessionId);
        Assert.Single(timeline); // 只有用户那条
        Assert.Equal(Speaker.User, ((TimelineEvent.MessageEvent)timeline[0]).From);
    }

    [Fact]
    public async Task Step_LeavesHistoryIntactWhenCallFails()
    {
        // Review Focus #3：调用失败时历史保持可解析
        var handle = NewSession();
        _adapter.Throws(new ModelCallFailedException("上游 503"));

        await Assert.ThrowsAsync<ModelCallFailedException>(
            () => _stepper.StepAsync(handle, "hi", CancellationToken.None));

        var timeline = _store.ReadTimeline(handle.ProjectKey, handle.SessionId);
        Assert.Single(timeline);
        Assert.Equal(Speaker.User, ((TimelineEvent.MessageEvent)timeline[0]).From);
        Assert.Equal(0, handle.State.Turn);
    }

    [Fact]
    public async Task Step_PassesEffortAndMaxTokensFromSession()
    {
        var handle = NewSession();
        _adapter.Reply("ok");

        await _stepper.StepAsync(handle, "hi", CancellationToken.None);

        Assert.Equal("high", _adapter.Calls[0].Effort);
        Assert.Equal(16000, _adapter.Calls[0].MaxTokens);
        Assert.Equal("claude-opus-5", _adapter.Calls[0].ModelId);
    }

    [Fact]
    public async Task Step_KeepsSystemBlocksEmpty()
    {
        // 04 §1.3：裸核心没有系统提示词
        var handle = NewSession();
        _adapter.Reply("ok");

        await _stepper.StepAsync(handle, "hi", CancellationToken.None);

        Assert.Empty(_adapter.Calls[0].SystemBlocks);
    }

    [Fact]
    public async Task Step_FlipsStatusToIdleWhenDone()
    {
        var handle = NewSession();
        _adapter.Reply("ok");

        await _stepper.StepAsync(handle, "hi", CancellationToken.None);

        Assert.Equal("Idle", handle.State.Status);
    }
}
```

- [ ] **Step 2: 跑测试，确认失败**

```bash
dotnet test --filter "FullyQualifiedName~StepperTests"
```

Expected: FAIL — `CS0246: The type or namespace name 'Stepper' could not be found`

- [ ] **Step 3: 写实现**

`src/Lilara.Core/Session/Stepper.cs`：

```csharp
using Lilara.Core.Adapters;
using Lilara.Core.Persistence;
using Lilara.Core.Records;

namespace Lilara.Core.Session;

/// <summary>
/// 推进器：跑一步 = 渲染 → 调用 → 执行 → 落盘（02 §2）。
///
/// ⚠️ 步是持久化的原子单位（01 §4）。这里的次序是刻意的：
///   1. 用户消息**先落盘**——它是事实，模型调用失败也抹不掉
///   2. 模型输出在内存里合并完，**落盘只落一次**
///   3. state.json 最后写——崩在 2 和 3 之间只会让 turn 计数偏小，历史不损坏
/// </summary>
public sealed class Stepper(SessionStore store, IModelAdapter adapter)
{
    private readonly SessionStore _store = store;
    private readonly IModelAdapter _adapter = adapter;

    public async Task<ModelReply> StepAsync(
        SessionHandle handle, string userText, CancellationToken ct)
    {
        // 「now 冻结在步开始」（00 §2.5）——同一步内重复渲染必须逐字节相同
        var at = DateTimeOffset.UtcNow;

        handle.State = handle.State with { Status = "Running" };

        // 1. 用户消息先落盘
        _store.AppendEvent(handle.ProjectKey, handle.SessionId, new TimelineEvent.MessageEvent
        {
            At = at,
            From = Speaker.User,
            Blocks = [new Canonical.Block.TextBlock(userText)],
        });

        // 2. 渲染：读回完整时间线 → canonical（核心永远发全量，00 §6）
        var request = new ModelRequest
        {
            ModelId = handle.Spec.Model.ModelId,
            MaxTokens = handle.Spec.Model.MaxTokens,
            Effort = handle.Spec.Model.Effort,
            SystemBlocks = [], // 04 §1.3：核心自带零内容
            Timeline = _store.ReadTimeline(handle.ProjectKey, handle.SessionId),
        };

        // 3. 调用
        var reply = await _adapter.CallAsync(request, ct);

        // 4. 落盘：拒答不写空模型消息（Review Focus #2）
        if (!reply.Refused && reply.Blocks.Count > 0)
        {
            _store.AppendEvent(handle.ProjectKey, handle.SessionId, new TimelineEvent.MessageEvent
            {
                At = at,
                From = Speaker.Model,
                Blocks = reply.Blocks,
            });
        }

        handle.State = handle.State with
        {
            Status = "Idle",
            Turn = handle.State.Turn + 1,
        };
        handle.SaveState(_store);

        return reply;
    }
}
```

- [ ] **Step 4: 跑测试，确认通过**

```bash
dotnet test --filter "FullyQualifiedName~StepperTests"
```

Expected: PASS，8 个测试全绿。

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "feat(core): 推进器（一步 = 渲染 → 调用 → 落盘）"
```

---

---

### Task 11: 控制台 REPL + 回放

**Files:**
- Create: `src/Lilara.Core/Console/Repl.cs`
- Create: `src/Lilara.Core/Adapters/AdapterFactory.cs`
- Modify: `src/Lilara.Core/Lilara.Core.csproj`（`OutputType` 改成 `Exe`）
- Create: `src/Lilara.Core/Program.cs`
- Test: `tests/Lilara.Core.Tests/ReplTests.cs`

**Interfaces:**
- Consumes: 前面全部
- Produces:
  - `Lilara.Core.Adapters.AdapterFactory.Create(ProviderDefinition) → IModelAdapter`
  - `Lilara.Core.Console.Repl`：
    - `Repl(SessionStore store, ProviderStore providers, Stepper stepper, string workingDirectory, TextWriter output)`
    - `SessionHandle? Current { get; set; }` —— 当前会话。**核心不自动建**，由 `session create` 或启动时"选最近的"填上
    - `Task<bool> ExecuteAsync(string line, CancellationToken ct)`（返回 `false` 表示退出）

- [ ] **Step 1: 让核心变成可执行**

把 `src/Lilara.Core/Lilara.Core.csproj` 的 `PropertyGroup` 改成：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>Lilara.Core</RootNamespace>
    <AssemblyName>lilara</AssemblyName>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <!-- ⚠️ 版本号保留 Task 7 里 `dotnet add package Anthropic` 写进去的那个，
         不要改成浮动的 "*"。 -->
  </ItemGroup>
</Project>
```

- [ ] **Step 2: 写失败的测试**

`tests/Lilara.Core.Tests/ReplTests.cs`：

```csharp
using Lilara.Core.Console;
using Lilara.Core.Persistence;
using Lilara.Core.Providers;
using Lilara.Core.Session;
using Lilara.Core.Tests.Fakes;

namespace Lilara.Core.Tests;

public class ReplTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-repl-" + Guid.NewGuid().ToString("N"));
    private readonly string _work =
        Path.Combine(Path.GetTempPath(), "lilara-replwork-" + Guid.NewGuid().ToString("N"));
    private readonly SessionStore _store;
    private readonly ProviderStore _providers;
    private readonly FakeAdapter _adapter = new();
    private readonly StringWriter _out = new();

    public ReplTests()
    {
        _store = new SessionStore(_root);
        _providers = new ProviderStore(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
    }

    private Repl NewRepl() => new(_store, _providers, new Stepper(_store, _adapter), _work, _out);

    /// <summary>配好提供商，并**显式**建一个会话——核心不自动建（01 §8.1）。</summary>
    private Repl ReplWithSession()
    {
        _providers.Save(new ProviderDefinition
        {
            Id = "claude",
            Protocol = "claude-messages",
            ApiKey = "sk-test-not-a-real-key",
            DefaultModel = "claude-opus-5",
            Modality = "vision",
        });

        var repl = NewRepl();
        var projectKey = Lilara.Core.Projects.ProjectKey.FromPath(_work);
        repl.Current = SessionHandle.Create(
            _store, _work, _providers.ToSelection(_providers.Load("claude")!, null, "high", 16000));
        return repl;
    }

    [Fact]
    public async Task ProviderSet_PersistsDefinition()
    {
        var repl = NewRepl();

        await repl.ExecuteAsync(
            "provider set claude --key sk-abc --model claude-opus-5", CancellationToken.None);

        var saved = _providers.Load("claude");
        Assert.NotNull(saved);
        Assert.Equal("sk-abc", saved!.ApiKey);
        Assert.Equal("claude-opus-5", saved.DefaultModel);
        Assert.Equal("claude-messages", saved.Protocol);
    }

    [Fact]
    public async Task NoSession_CoreDoesNotCreateOneOnItsOwn()
    {
        // ⚠️ 01 §8.1：核心启动后不建任何会话
        var repl = NewRepl();

        await repl.ExecuteAsync("你好", CancellationToken.None);

        Assert.Null(repl.Current);
        Assert.Empty(_adapter.Calls);
        Assert.Contains("session create", _out.ToString());
    }

    [Fact]
    public async Task SessionCreate_MakesTheSessionCurrent()
    {
        _providers.Save(new ProviderDefinition
        {
            Id = "claude",
            Protocol = "claude-messages",
            ApiKey = "sk-test-not-a-real-key",
            DefaultModel = "claude-opus-5",
            Modality = "vision",
        });
        var repl = NewRepl();

        await repl.ExecuteAsync("session create", CancellationToken.None);

        Assert.NotNull(repl.Current);
        Assert.Single(_store.ListSessions(repl.Current!.ProjectKey));
    }

    [Fact]
    public async Task PlainText_IsSentToTheModel()
    {
        _adapter.Reply("你好！");
        var repl = ReplWithSession();

        await repl.ExecuteAsync("你好", CancellationToken.None);

        Assert.Contains("你好！", _out.ToString());
    }

    [Fact]
    public async Task History_ReplaysPersistedTimeline()
    {
        _adapter.Reply("你好！");
        var repl = ReplWithSession();
        await repl.ExecuteAsync("你好", CancellationToken.None);
        _out.GetStringBuilder().Clear();

        await repl.ExecuteAsync("history", CancellationToken.None);

        var text = _out.ToString();
        Assert.Contains("你好", text);
        Assert.Contains("你好！", text);
    }

    [Fact]
    public async Task Quit_ReturnsFalse()
    {
        var repl = NewRepl();

        Assert.False(await repl.ExecuteAsync("quit", CancellationToken.None));
    }

    [Fact]
    public async Task SendBeforeProvider_PrintsGuidanceInsteadOfCrashing()
    {
        // Review Focus #6：没配 provider 时给人话，不吐栈
        var repl = NewRepl();

        await repl.ExecuteAsync("你好", CancellationToken.None);

        Assert.Contains("provider", _out.ToString());
        Assert.Empty(_adapter.Calls);
    }

    [Fact]
    public async Task ModelFailure_PrintsOneLineAndKeepsReplAlive()
    {
        _adapter.Throws(new Lilara.Core.Adapters.ModelCallFailedException("上游 503。这是临时的，重试即可。"));
        var repl = ReplWithSession();

        var keepGoing = await repl.ExecuteAsync("你好", CancellationToken.None);

        Assert.True(keepGoing);
        Assert.Contains("上游 503", _out.ToString());
    }
}
```

- [ ] **Step 3: 跑测试，确认失败**

```bash
dotnet test --filter "FullyQualifiedName~ReplTests"
```

Expected: FAIL — `CS0246: The type or namespace name 'Repl' could not be found`

- [ ] **Step 4: 写实现**

`src/Lilara.Core/Adapters/AdapterFactory.cs`：

```csharp
using Lilara.Core.Adapters.Claude;
using Lilara.Core.Providers;

namespace Lilara.Core.Adapters;

/// <summary>
/// 协议 → 适配器。⚠️ 焊死的 switch（04 §1.2），不做插件注册表。
/// 加协议 = 加一个 case + 一个适配器类。
/// </summary>
public static class AdapterFactory
{
    public static IModelAdapter Create(ProviderDefinition provider) => provider.Protocol switch
    {
        ClaudeAdapter.ProtocolId => new ClaudeAdapter(provider),
        _ => throw new ModelCallFailedException(
            $"未知协议 '{provider.Protocol}'。目前只支持 '{ClaudeAdapter.ProtocolId}'。"),
    };
}
```

`src/Lilara.Core/Console/Repl.cs`：

```csharp
using Lilara.Core.Adapters;
using Lilara.Core.Canonical;
using Lilara.Core.Persistence;
using Lilara.Core.Providers;
using Lilara.Core.Records;
using Lilara.Core.Session;

namespace Lilara.Core.Console;

/// <summary>
/// 命令面（04 §1.1）。⚠️ **它不是头**：同步收发、没有视图、不订阅、不流式渲染。
/// 命令名沿用控制面协议（03 §3），只是换了个本地入口。
/// </summary>
public sealed class Repl(
    SessionStore store, ProviderStore providers, Stepper stepper, string workingDirectory, TextWriter output)
{
    private readonly SessionStore _store = store;
    private readonly ProviderStore _providers = providers;
    private readonly Stepper _stepper = stepper;
    private readonly string _workingDirectory = workingDirectory;
    private readonly TextWriter _out = output;

    /// <summary>
    /// ⚠️ 核心**不自动建会话**（01 §8.1）。这个字段由 `session create` 填，
    /// 或者由 Program.cs 在启动时「选最近的一条」填。
    /// </summary>
    public SessionHandle? Current { get; set; }

    public async Task<bool> ExecuteAsync(string line, CancellationToken ct)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0) return true;

        try
        {
            if (trimmed is "quit" or "exit") return false;

            if (trimmed.StartsWith("provider set ", StringComparison.Ordinal))
            {
                HandleProviderSet(trimmed);
                return true;
            }

            if (trimmed == "session create")
            {
                HandleSessionCreate();
                return true;
            }

            if (trimmed == "history")
            {
                HandleHistory();
                return true;
            }

            if (trimmed == "status")
            {
                HandleStatus();
                return true;
            }

            await HandleMessageAsync(trimmed, ct);
            return true;
        }
        catch (ModelCallFailedException ex)
        {
            // 一行人话，REPL 继续活着（Review Focus #6）
            _out.WriteLine($"✗ {ex.Message}");
            return true;
        }
    }

    private void HandleProviderSet(string line)
    {
        // provider set <id> --key <key> --model <model>
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var id = parts[2];

        string? key = null, model = null;
        for (var i = 3; i < parts.Length - 1; i++)
        {
            if (parts[i] == "--key") key = parts[i + 1];
            if (parts[i] == "--model") model = parts[i + 1];
        }

        if (key is null)
        {
            _out.WriteLine("用法：provider set <id> --key <key> [--model <model>]");
            return;
        }

        _providers.Save(new ProviderDefinition
        {
            Id = id,
            Protocol = "claude-messages",
            ApiKey = key,
            DefaultModel = model ?? "claude-opus-5",
            Modality = "vision",
        });

        _out.WriteLine($"✓ 提供商 '{id}' 已保存。");
    }

    /// <summary>
    /// 控制面 `session.create` 的本地入口（03 §3）。
    /// ⚠️ **这是显式动作**——核心不会替你决定该有个会话。
    /// </summary>
    private void HandleSessionCreate()
    {
        var provider = _providers.List().FirstOrDefault();
        if (provider is null)
        {
            _out.WriteLine("先配提供商：provider set <id> --key <key>");
            return;
        }

        var model = _providers.ToSelection(provider, null, "high", 16000);
        Current = SessionHandle.Create(_store, _workingDirectory, model);
        _out.WriteLine($"✓ 会话 {Current.SessionId} 已创建。");
    }

    private void HandleHistory()
    {
        if (Current is null)
        {
            _out.WriteLine("还没有会话。用 `session create` 建一个。");
            return;
        }

        var timeline = _store.ReadTimeline(Current.ProjectKey, Current.SessionId);
        if (timeline.Count == 0)
        {
            _out.WriteLine("（历史为空）");
            return;
        }

        foreach (var evt in timeline)
        {
            var message = (TimelineEvent.MessageEvent)evt;
            var who = message.From == Speaker.User ? "你" : "⏺";
            var text = string.Concat(
                message.Blocks.OfType<Block.TextBlock>().Select(b => b.Text));
            _out.WriteLine($"{who}  {text}");
        }
    }

    private void HandleStatus()
    {
        if (Current is null)
        {
            _out.WriteLine("还没有会话。用 `session create` 建一个。");
            return;
        }

        _out.WriteLine($"会话 {Current.SessionId} · {Current.State.Status} · 第 {Current.State.Turn} 步");
        _out.WriteLine($"模型 {Current.Spec.Model.ModelId} · effort {Current.Spec.Model.Effort}");
        _out.WriteLine($"目录 {Current.Spec.WorkingDirectory}");
    }

    private async Task HandleMessageAsync(string text, CancellationToken ct)
    {
        if (Current is null)
        {
            // 01 §8.1：核心不替你建会话——先要提供商，再要一条显式命令
            _out.WriteLine(_providers.List().Count == 0
                ? "还没有配置提供商。先执行 `provider set <id> --key <key>`。"
                : "还没有会话。执行 `session create` 建一个。");
            return;
        }

        var reply = await _stepper.StepAsync(Current, text, ct);

        if (reply.Refused)
        {
            _out.WriteLine("⚠️ 模型拒绝了这次请求（stop_reason: refusal）。");
            return;
        }

        var body = string.Concat(reply.Blocks.OfType<Block.TextBlock>().Select(b => b.Text));
        _out.WriteLine($"⏺  {body}");

        if (reply.StopReason == "max_tokens")
            _out.WriteLine("（输出被 max_tokens 截断了）");
    }
}
```

`src/Lilara.Core/Program.cs`：

```csharp
using Lilara.Core.Adapters;
using Lilara.Core.Console;
using Lilara.Core.Persistence;
using Lilara.Core.Projects;
using Lilara.Core.Providers;
using Lilara.Core.Session;

var dataRoot = DataRoot.Resolve();
Directory.CreateDirectory(dataRoot);

var store = new SessionStore(dataRoot);
var providers = new ProviderStore(dataRoot);
var workingDirectory = Directory.GetCurrentDirectory();

Console.WriteLine($"LILARA — 数据根 {dataRoot}");

// ⚠️ 核心启动时**不建任何会话**（01 §8.1）。
// 这里只做一件事：把当前项目下最近的会话选出来当"当前会话"。
// 「配置完提供商自动建会话」是头的行为——控制台要建，得显式敲 `session create`。
var projectKey = ProjectKey.FromPath(workingDirectory);
var existing = store.ListSessions(projectKey);

var repl = new Repl(
    store,
    providers,
    new Stepper(store, new UnconfiguredAdapter()),
    workingDirectory,
    Console.Out);

if (existing.Count > 0)
{
    var handle = SessionHandle.Load(store, projectKey, existing[0]);
    var provider = providers.Load(handle.Spec.Model.ProviderId);

    if (provider is null)
    {
        Console.WriteLine("  会话引用的提供商已不存在。用 `provider set` 重新配一个。");
    }
    else
    {
        repl = new Repl(
            store,
            providers,
            new Stepper(store, AdapterFactory.Create(provider)),
            workingDirectory,
            Console.Out) { Current = handle };
        Console.WriteLine($"  当前会话 {handle.SessionId}（第 {handle.State.Turn} 步）");
    }
}
else
{
    Console.WriteLine("  还没有会话。");
}

Console.WriteLine("  `provider set <id> --key <key>` 配置；`session create` 建会话；`history` 回放；`quit` 退出。");

while (true)
{
    Console.Write("lilara> ");
    var line = Console.ReadLine();
    if (line is null) break;
    if (!await repl.ExecuteAsync(line, CancellationToken.None)) break;
}

/// <summary>
/// 没有可用适配器时的占位。
/// ⚠️ 它存在是为了让 REPL **能起来**——没配提供商时你仍然要能收
/// `provider set` 和 `session create`，否则引导会自锁。
/// </summary>
file sealed class UnconfiguredAdapter : IModelAdapter
{
    public Task<ModelReply> CallAsync(ModelRequest request, CancellationToken ct) =>
        throw new ModelCallFailedException("还没有配置提供商。");
}
```

- [ ] **Step 5: 跑全部测试**

```bash
dotnet test
```

Expected: PASS，全部测试绿（约 60 个）。

- [ ] **Step 6: 手动验收——真的聊一轮**

⚠️ 这一步需要真的 API key。**没有 key 就跳过，但要在报告里说明跳过。**

```bash
export ANTHROPIC_API_KEY=sk-ant-...
dotnet run --project src/Lilara.Core
```

在 `lilara>` 提示符下依次输入：

```
provider set claude --key sk-ant-... --model claude-opus-5
session create
你好，用一句话介绍你自己
history
quit
```

Expected:
- 启动时打印 `还没有会话。` —— ⚠️ **核心不自动建会话**（01 §8.1）
- `✓ 提供商 'claude' 已保存。`
- `✓ 会话 <id> 已创建。`
- `⏺  ...`（模型的真实回复）
- `history` 打印两行：`你  你好，...` 与 `⏺  ...`
- 数据根下出现 `providers/claude.json` 和 `projects/<key>/sessions/<id>/{spec,state,history}`

- [ ] **Step 7: 手动验收——重启后历史还在**

```bash
dotnet run --project src/Lilara.Core
```

在 `lilara>` 下输入：

```
history
```

Expected: 上一次的两行原样回来；且控制台打印的是 `当前会话 <id>（第 1 步）`——**"选最近的"，不是"新建一个"**（Review Focus #4）。

- [ ] **Step 8: 提交**

```bash
git add -A
git commit -m "feat(core): 控制台 REPL + 回放（最小闭环打通）"
```

---

## 完成判据

跑完 11 个任务后，下面这些**全部**要成立：

1. `dotnet test` 全绿。
2. `dotnet run --project src/Lilara.Core` 能起，打印数据根，且**不自动建会话**（`01` §8.1）。
3. 配了 provider、敲过 `session create` 后，能在终端里跟 Claude 真的聊一轮。
4. `quit` 后重启，自动选回那条会话，`history` 原样回放（Review Focus #4）。
5. 中文在 `history.jsonl` 里是明文（Review Focus #5）。
6. 拔掉 key 再发消息，打印一行人话而不是栈（Review Focus #6）。

## 范围：这只是**第一份**

⚠️ **头与核心同步开始**——本来就是两个独立程序，都从零起步。本计划产出的**第一个可用东西是控制台**；头随后跟上（`05`）。

```
第一份（本计划）      第二份              第三份
核心 + 控制台   →   控制面协议   →   头
纯 chat 可用         WS/HTTP + 载荷     三栏 Web UI
                    schema（05 §11）
```

⚠️ **为此，本计划刻意只碰「核心自己的形状」**——`Block` / `TimelineEvent` / `SessionStore` / `Stepper` 都设计成**能直接被协议层序列化**，不掺任何控制台专有的东西。头不需要读控制台的代码，它只需要 `03` 的载荷 schema——而那份 schema 会直接复用本计划的 canonical 类型。

## 明确不在本计划内

- 控制面（WS/HTTP）、头 —— **下一份**，见 `03` / `05`
- 插件系统、任何工具、任何提示词 —— 见 `04`
- 压缩 / release / GC / 声明锁 —— 见 `01` §11–§12
- 流式输出 —— 见 `01` §11.9
- 子会话、后台事项、定时器 —— 见 `01` §6.2 / §10
- 多会话调度 —— 本计划里**同一时刻只有一个会话**
