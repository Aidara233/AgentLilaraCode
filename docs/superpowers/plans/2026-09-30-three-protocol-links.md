# 三条协议链路补齐 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让核心能通过 Claude Messages / OpenAI Chat / OpenAI Responses 三种协议跟模型对话，且推理块的往返语义在三种协议下都正确。

**Architecture:** 核心发全量 canonical（`00` §6），三个适配器各自做减法。每个适配器都是**薄壳**——翻译逻辑全在两个**纯函数**里（请求构造器 / 响应映射器），适配器本体只做「构造 → 发 → 映射」。

⚠️ **规则重写，不移植 ApiPlan 代码。** 差异是本质的：
> ApiPlan 的 canonical 是**传输形状**（进 → 规范化 → 出，转瞬即逝）。
> 我们的 canonical 是**骨架**（落盘的事实），请求时要**展开**成聊天历史。

所以我们的块模型必须容得下**引用**（`01` §3.4 的「托管」形态），而 ApiPlan 的形状不需要。ApiPlan 当**规格**读——映射表照抄（那是知识），形状自己定（那是我们的）。

**Tech Stack:** .NET 10 · C# 13 · xunit · `System.Text.Json` · `Anthropic`（Claude 一条用官方 SDK；OpenAI 两条**手写 HTTP**，无官方 SDK 依赖）

**Spec:**
- [`docs/00-设计哲学.md`](../../00-设计哲学.md) — §2.1 canonical 定位 · §5 工具观与两条铁律 · §6 协议适配
- [`docs/01-会话设计.md`](../../01-会话设计.md) — §3.4 载荷两形态（具体/托管）· §7 配置分层
- [`docs/04-插件与内部结构.md`](../../04-插件与内部结构.md) — §1.2 适配器焊死 · §1.3 核心是空壳
- ApiPlan 作为**规格**（只读）：`E:\Workspace\ApiPlan\docs\superpowers\specs\2026-07-27-openai-responses-compatibility-design.md` §6、§10.2

## Global Constraints

- **目标框架 `net10.0`**；`TreatWarningsAsErrors` 仅 `src` 项目。
- ⚠️ **未使用的 using 不会导致构建失败**（C# 编译器不报告它；那是 IDE 分析器）。仍请避免，但别为它改写代码。
- **命名空间根 `Lilara.Core`**；程序集名 `lilara`。
- **所有落盘文件 UTF-8 不带 BOM**。
- ⚠️ **单一命名约定 = camelCase**（`Json.Options` 已强制：`PropertyNamingPolicy` + `JsonStringEnumConverter(CamelCase)`）。
- ⚠️ **`history.jsonl` 只追加，永不重写**。**本计划会改 canonical 的落盘形状**（加 `kind` 与可空字段）——分支未合并、无真实数据，这是唯一免费的窗口；改完就是迁移。
- ⚠️ **核心不带任何工具**（`04` §1.3）→ 请求里**永不声明 tools** → `tool_call` / `tool_result` 两种块**本计划不做**。
- ⚠️ **核心自带零提示词**：本计划里 system 恒为空。适配器要**支持** system，但调用方传空。
- ⚠️ **渲染必须逐字节稳定**（`00` §5 铁律 1）：同一 canonical → 同一 JSON。**JSON 键顺序、数组顺序一律显式确定，绝不依赖字典枚举序。**
- ⚠️ **推理块逐字回传**（`00` §5 铁律 2）：能回传的协议必须原样回传；不能的必须**不伪造**（`API 计划 spec §10.2`）。
- **适配器焊死在代码里**（`04` §1.2）：`AdapterFactory` 是 `switch`，不做插件注册表。
- ⚠️ **本计划新增的两条 OpenAI 链路要自己发 HTTP**。**HTTP 只在适配器里出现**，纯函数里绝不出现——纯函数必须可离线测。

## 三个协议的实测形状（控制器已对 `localhost:5129` 逐一验证，勿重新推导）

```
端点认证：Authorization: Bearer <key>    （不是 x-api-key；见 Task 1）
```

| | Claude Messages | OpenAI Chat | OpenAI Responses |
|---|---|---|---|
| 路径 | `/v1/messages` | `/v1/chat/completions` | `/v1/responses` |
| 系统提示 | `system` | `messages[0].role="system"` | `instructions` |
| 消息 | `messages:[{role,content}]` | `messages:[{role,content}]` | `input:[{**type:"message"**,role,content:[{type,text}]}]` |
| 用户内容块类型 | `text` | 字符串或数组 | `input_text` |
| 助手内容块类型 | `text` | 字符串 | `output_text` |
| 输出上限 | `max_tokens` | `max_tokens` | `max_output_tokens` |
| 结束原因 | `stop_reason` | `choices[0].finish_reason` | `status` |
| **推理** | `content:[{type:"thinking",thinking}]` **无 signature** | ❌ 不暴露 | ❌ 不暴露 |

⚠️ **Responses 的输入项必须带 `"type":"message"`**——少了它上游报 `upstream_error`（实测：A 形状失败、B 形状成功）。

---

## Review Focus

按「最可能咬到人」排序。每条都要落到某个任务的测试里。

1. **推理块在三种协议下的往返** —— 这是本计划的核心。Claude 能回传（有 signature 时必须逐字）；Chat / Responses 不能回传，**必须不伪造**。期望行为：`ReasoningBlock` 只有在**目标协议确实能承载**时才进请求；否则**静默丢弃而非伪造**，且不得因此产生空内容的消息。
2. **空的模型回复** —— 推理模型会把 token 全烧在思考上，`content` 为空、`finish_reason: "length"`（实测见过）。期望行为：核心不崩、不写空的模型消息、如实告诉用户被截断。
3. **system 为空 vs 非空** —— 核心现在恒传空。期望行为：空时**不产生 system 字段**（而不是发一个空串）；非空时三种协议的落点各不相同且都对。
4. **JSON 键顺序稳定性** —— 前缀稳定性的地基。期望行为：同一 canonical 两次渲染**逐字节相同**；不得依赖 `Dictionary` 枚举序。
5. **HTTP 失败的人话** —— 401 / 404 / 5xx / 超时。期望行为：一句人话，不吐栈，REPL 继续活着。
6. **协议不认识 `endpoint` 为空** —— 官方地址兜底，且**不静默**（沿用既有 `EndpointResolver` 的三档来源与打印）。

---

### Task 1: 认证方式 = 端点怪癖（先修 Claude 链路）

**为什么先做这个**：控制器的既有 `ClaudeAdapter` **打不通这个网关**——它设 `ApiKey`（发 `x-api-key`），而网关只认 `Authorization: Bearer`。实测：

```
ApiKey     → 失败（SDK 解析不了错误体）
AuthToken  → 成功，拿回 ThinkingBlock
```

这就是 `00` §1.2「协议怪癖归适配器」的又一个实例——和端点一样，**认证方式也是后端属性**。

**Files:**
- Modify: `src/Lilara.Core/Providers/ProviderDefinition.cs`
- Modify: `src/Lilara.Core/Adapters/Claude/ClaudeAdapter.cs`
- Test: `tests/Lilara.Core.Tests/ProviderStoreTests.cs`（加一条）
- Test: `tests/Lilara.Core.Tests/ClaudeAdapterTests.cs`（加一条）

**Interfaces:**
- Consumes: 既有的 `ProviderDefinition`（`Endpoint` / `ApiKey` 等）
- Produces:
  - `ProviderDefinition.AuthScheme`（`string?`，`"api-key"` | `"bearer"`，空 = `"api-key"`）
  - `Lilara.Core.Providers.AuthSchemes`（静态类：`const string ApiKey = "api-key"` / `Bearer = "bearer"` / `Default = ApiKey`）

- [ ] **Step 1: 写失败的测试**

`ProviderStoreTests.cs` 加：

```csharp
    [Fact]
    public void SaveThenLoad_RoundTripsAuthScheme()
    {
        // ⚠️ 认证方式和端点一样，是**后端怪癖**——归提供商配置，不归代码。
        // 本机网关只认 Authorization: Bearer，官方只认 x-api-key。
        _store.Save(Def() with { AuthScheme = AuthSchemes.Bearer });

        Assert.Equal(AuthSchemes.Bearer, _store.Load("claude")!.AuthScheme);
    }

    [Fact]
    public void AuthScheme_IsOptional_OldFilesStillLoad()
    {
        // ⚠️ 老 providers/*.json 没有这个字段，必须照常反序列化（可空 + 不 required）
        File.WriteAllText(Path.Combine(_root, "providers", "old.json"),
            """{"id":"old","protocol":"claude-messages","apiKey":"k","defaultModel":"m","modality":"vision"}""");

        Assert.Null(_store.Load("old")!.AuthScheme);
    }
```

`ClaudeAdapterTests.cs` 加：

```csharp
    [Fact]
    public void Constructor_UsesBearerWhenAuthSchemeSaysSo()
    {
        var provider = new ProviderDefinition
        {
            Id = "gw", Protocol = ClaudeAdapter.ProtocolId,
            ApiKey = "sk-x", DefaultModel = "m", Modality = "vision",
            AuthScheme = AuthSchemes.Bearer,   // ← 网关要 Bearer
        };

        // 构造不触网；断言的是「解析出的方案被选中」
        var adapter = new ClaudeAdapter(provider);

        Assert.Equal(AuthSchemes.Bearer, adapter.AuthScheme);
    }

    [Fact]
    public void Constructor_DefaultsToApiKeyWhenUnset()
    {
        var provider = new ProviderDefinition
        {
            Id = "official", Protocol = ClaudeAdapter.ProtocolId,
            ApiKey = "sk-x", DefaultModel = "m", Modality = "vision",
        };

        Assert.Equal(AuthSchemes.ApiKey, new ClaudeAdapter(provider).AuthScheme);
    }
```

- [ ] **Step 2: 跑测试，确认失败**

```bash
dotnet test --filter "FullyQualifiedName~ProviderStoreTests|FullyQualifiedName~ClaudeAdapterTests"
```

Expected: FAIL — `CS0117: 'ProviderDefinition' 未包含 'AuthScheme' 的定义`

- [ ] **Step 3: 写实现**

`src/Lilara.Core/Providers/AuthSchemes.cs`（新文件）：

```csharp
namespace Lilara.Core.Providers;

/// <summary>
/// 认证方式。⚠️ 和端点一样是**后端怪癖**——官方 Anthropic 只认 `x-api-key`，
/// 而本机网关（ApiPlan 中转站）只认 `Authorization: Bearer`（实测：ApiKey 打不通，AuthToken 通）。
/// 归提供商配置，不写死在适配器里。
/// </summary>
public static class AuthSchemes
{
    public const string ApiKey = "api-key";
    public const string Bearer = "bearer";

    /// <summary>缺省 = 官方那套。</summary>
    public const string Default = ApiKey;
}
```

`ProviderDefinition.cs` 在 `Endpoint` 之后加：

```csharp
    /// <summary>
    /// 认证方式：<see cref="AuthSchemes.ApiKey"/>（x-api-key，官方）或
    /// <see cref="AuthSchemes.Bearer"/>（Authorization: Bearer，本机网关）。
    /// ⚠️ 可空——老 providers/*.json 没这个字段，缺失按 <see cref="AuthSchemes.Default"/> 处理。
    /// </summary>
    public string? AuthScheme { get; init; }
```

`ClaudeAdapter.cs` —— **不要整类重写**。现有的协议守卫、`ArgumentNullException.ThrowIfNull`、`Endpoint` 属性与其注释**全部保留**。只做三处改动：

**① 加一个属性**（放在既有 `Endpoint` 属性旁边）：

```csharp
    /// <summary>实际采用的认证方式（解析后的，不是原始配置）。</summary>
    public string AuthScheme { get; }
```

**② 构造器里，在 `ArgumentNullException.ThrowIfNull(provider);` 之后、协议守卫之前**，解析认证方式：

```csharp
        // ⚠️ 两种认证方式走不同属性：ApiKey → x-api-key，AuthToken → Authorization: Bearer。
        // 本机网关只认后者（实测：ApiKey 打不通、AuthToken 通）。这是协议怪癖，不是可选优化。
        AuthScheme = provider.AuthScheme ?? AuthSchemes.Default;
```

**③ 把既有的 `_client = new AnthropicClient { ApiKey = ..., BaseUrl = ... };` 换成按方案二选一**（`Endpoint` 的解析与赋值**保持原样不动**）：

```csharp
        _client = AuthScheme == AuthSchemes.Bearer
            ? new AnthropicClient { AuthToken = provider.ApiKey, BaseUrl = endpoint.Url }
            : new AnthropicClient { ApiKey = provider.ApiKey, BaseUrl = endpoint.Url };
```

⚠️ `AuthSchemes` 在 `Lilara.Core.Providers`，文件已经 `using Lilara.Core.Providers;`，无需加 using。
⚠️ `CallAsync` 与 `ShouldWrap` / `Describe` **一个字都不改**。

- [ ] **Step 4: 跑测试，确认通过**

```bash
dotnet test --filter "FullyQualifiedName~ProviderStoreTests|FullyQualifiedName~ClaudeAdapterTests"
```

Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "feat(core): 认证方式归提供商配置（网关要 Bearer，官方要 x-api-key）"
```

---

### Task 2: canonical 形状——`ReasoningKind` 与显式键序

**为什么**：`ReasoningBlock(Signature, Text)` 假设了 signature 一定存在，而**实测这个网关的 `deepseek-flash` 不返回 signature**。同时要为本计划后面三个协议的稳定渲染打地基。

**Files:**
- Modify: `src/Lilara.Core/Canonical/Block.cs`
- Modify: `src/Lilara.Core/Canonical/Json.cs`
- Test: `tests/Lilara.Core.Tests/BlockTests.cs`（加）

**Interfaces:**
- Consumes: 既有 `Block` / `Block.TextBlock` / `Json.Options`
- Produces:
  - `Block.ReasoningBlock { string? Signature; string Text; string Kind }`（`Kind` ∈ `"text"` | `"redacted"`，缺省 `"text"`）
  - `Json.Options` 增加 `PropertyNamingPolicy` 已存在；**新增** `Json.StableSerialize<T>(T) → string`（键序显式确定的序列化入口）

- [ ] **Step 1: 写失败的测试**

`BlockTests.cs` 加：

```csharp
    [Fact]
    public void ReasoningBlock_SignatureIsOptional()
    {
        // ⚠️ 实测：本机网关的 deepseek-flash 返回 {"type":"thinking","thinking":"..."}
        // **没有 signature**。把它当必填，整条推理就存不下来。
        Block original = new Block.ReasoningBlock(Signature: null, Text: "想想", Kind: "text");

        var json = JsonSerializer.Serialize(original, Json.Options);
        var back = JsonSerializer.Deserialize<Block>(json, Json.Options);

        Assert.Equal(original, back);
        Assert.Contains("\"kind\":\"reasoning\"", json);
    }

    [Fact]
    public void ReasoningBlock_PreservesSignatureByteForByte()
    {
        // ⚠️ signature 是不透明凭据，上游会校验——绝不能 trim 或重编码
        const string sig = "ErUBCkYIBRgCIkAxY±+/= 空格 也保留";
        Block original = new Block.ReasoningBlock(sig, "想", "text");

        var back = JsonSerializer.Deserialize<Block>(
            JsonSerializer.Serialize(original, Json.Options), Json.Options);

        Assert.Equal(sig, ((Block.ReasoningBlock)back!).Signature);
    }

    [Fact]
    public void RedactedReasoning_RoundTrips()
    {
        // redacted 是加密载荷：原样保留、不解释（ApiPlan spec §10.2）
        Block original = new Block.ReasoningBlock(null, "ZW5jcnlwdGVk", "redacted");

        var back = JsonSerializer.Deserialize<Block>(
            JsonSerializer.Serialize(original, Json.Options), Json.Options);

        Assert.Equal("redacted", ((Block.ReasoningBlock)back!).Kind);
    }

    [Fact]
    public void StableSerialize_ProducesIdenticalBytesForIdenticalInput()
    {
        // ⚠️ 前缀稳定性的地基：同一内容两次渲染必须逐字节相同。
        // 这条测试是「不依赖字典枚举序」的守卫。
        var a = new Block.TextBlock("你好");
        var b = new Block.TextBlock("你好");

        Assert.Equal(Json.StableSerialize(a), Json.StableSerialize(b));
    }
```

- [ ] **Step 2: 跑测试，确认失败**

```bash
dotnet test --filter "FullyQualifiedName~BlockTests"
```

Expected: FAIL — `CS7036: 没有为必需参数 'Signature' 提供参数` / `CS0117: 'Json' 未包含 'StableSerialize'`

- [ ] **Step 3: 写实现**

`Block.cs` 替换 `ReasoningBlock`：

```csharp
    /// <summary>
    /// 模型的推理块。
    /// ⚠️ `00` §5 铁律：**能回传的协议必须逐字回传**；不能回传的（Chat / Responses）
    /// **必须不伪造**——见 ApiPlan spec §10.2。
    /// </summary>
    /// <param name="Signature">
    /// 不透明凭据，上游校验用。**原样存、原样传，绝不修改**。
    /// ⚠️ **可以为 null**：实测本机网关的 deepseek-flash 返回的 thinking 块没有 signature。
    /// </param>
    /// <param name="Text">可读推理文本；`Kind = "redacted"` 时是加密载荷的 base64。</param>
    /// <param name="Kind">
    /// `"text"`（明文推理，可回传）或 `"redacted"`（加密载荷，原样保留不解释）。
    /// </param>
    public sealed record ReasoningBlock(string? Signature, string Text, string Kind = "text") : Block;
```

`Json.cs` 加：

```csharp
    /// <summary>
    /// 稳定序列化入口。**所有出站渲染都走这里**。
    /// ⚠️ 存在的理由：`JsonSerializer` 对同一类型的键序虽然目前稳定，
    /// 但那是实现细节；而前缀稳定性要求**逐字节可预期**。
    /// 这里把「稳定」变成一个被测试钉住的**承诺**，而不是对 BCL 的信任。
    /// </summary>
    public static string StableSerialize<T>(T value) =>
        JsonSerializer.Serialize(value, Options);
```

- [ ] **Step 4: 跑测试，确认通过**

```bash
dotnet test --filter "FullyQualifiedName~BlockTests"
```

Expected: PASS

⚠️ **回归检查**：`ClaudeResponseMapper` / `ClaudeRequestBuilder` 现在构造 `ReasoningBlock(sig, text)`——三参构造器的第三个有默认值，**调用点不用改**。

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "feat(core): 推理块加 Kind、Signature 可空；加稳定序列化入口"
```

---

### Task 3: 推理块的**协议能力表**（纯逻辑，无 HTTP）

**为什么**：三条链路对推理块的**能力不同**——Claude 能承载（且要求逐字回传），Chat / Responses 不能。这个判断必须是**一个显式的地方**，而不是散在三个适配器里的 `if`。

**Files:**
- Create: `src/Lilara.Core/Canonical/ReasoningPolicy.cs`
- Test: `tests/Lilara.Core.Tests/ReasoningPolicyTests.cs`

**Interfaces:**
- Consumes: `Block.ReasoningBlock`（Task 2）
- Produces:
  - `Lilara.Core.Canonical.ReasoningPolicy.CanReplay(string protocol) → bool`
  - `Lilara.Core.Canonical.ReasoningPolicy.FilterFor(IReadOnlyList<Block>, string protocol) → IReadOnlyList<Block>`

- [ ] **Step 1: 写失败的测试**

```csharp
using Lilara.Core.Canonical;

namespace Lilara.Core.Tests;

public class ReasoningPolicyTests
{
    [Theory]
    [InlineData("claude-messages", true)]
    [InlineData("openai-chat", false)]
    [InlineData("openai-responses", false)]
    public void CanReplay_MatchesWhatEachProtocolActuallyCarries(string protocol, bool expected)
    {
        // ⚠️ 实测：Claude 的 content 里有 thinking 块；Chat 只有 content；
        // Responses 的 output 只有 message/output_text。后两者**不暴露推理**。
        Assert.Equal(expected, ReasoningPolicy.CanReplay(protocol));
    }

    [Fact]
    public void FilterFor_DropsReasoningWhenTheProtocolCannotCarryIt()
    {
        var blocks = new List<Block>
        {
            new Block.ReasoningBlock("sig", "想想"),
            new Block.TextBlock("答案"),
        };

        var filtered = ReasoningPolicy.FilterFor(blocks, "openai-chat");

        Assert.Single(filtered);
        Assert.IsType<Block.TextBlock>(filtered[0]);
    }

    [Fact]
    public void FilterFor_KeepsReasoningForClaude()
    {
        var blocks = new List<Block>
        {
            new Block.ReasoningBlock("sig", "想想"),
            new Block.TextBlock("答案"),
        };

        Assert.Equal(2, ReasoningPolicy.FilterFor(blocks, "claude-messages").Count);
    }

    [Fact]
    public void FilterFor_PreservesOrder()
    {
        var blocks = new List<Block>
        {
            new Block.ReasoningBlock("s", "想"),
            new Block.TextBlock("正文"),
            new Block.ReasoningBlock("s2", "再想"),
        };

        var filtered = ReasoningPolicy.FilterFor(blocks, "claude-messages");

        Assert.Equal(["reasoning", "text", "reasoning"],
            filtered.Select(b => b is Block.ReasoningBlock ? "reasoning" : "text"));
    }

    [Fact]
    public void FilterFor_DoesNotFabricateReasoningForProtocolsThatLackIt()
    {
        // ⚠️ ApiPlan spec §10.2：不能回传的协议**不伪造**——
        // 丢就是丢，不能把推理文本塞进 text 块冒充正文。
        var blocks = new List<Block> { new Block.ReasoningBlock(null, "悄悄想的话") };

        Assert.Empty(ReasoningPolicy.FilterFor(blocks, "openai-chat"));
    }
}
```

- [ ] **Step 2: 跑测试，确认失败**

```bash
dotnet test --filter "FullyQualifiedName~ReasoningPolicyTests"
```

Expected: FAIL — `CS0246: 找不到类型 'ReasoningPolicy'`

- [ ] **Step 3: 写实现**

```csharp
namespace Lilara.Core.Canonical;

/// <summary>
/// 各协议**能不能承载推理**——一个显式的地方，而不是散在三个适配器里的 if。
///
/// ⚠️ 这是「协议怪癖」的一种（`00` §1.2），但它是**纯数据**，所以放在 canonical 旁边，
/// 让三个适配器读同一份判断。
///
/// 规则来源（ApiPlan spec §10.2，实测印证）：
/// - Claude：content 支持 thinking 块 → **能承载，且要求逐字回传**。
/// - Chat / Responses：**不暴露推理**（实测两者返回体里都没有推理字段）
///   → 不能承载，**丢就是不丢，绝不伪造**。
/// </summary>
public static class ReasoningPolicy
{
    public static bool CanReplay(string protocol) => protocol switch
    {
        "claude-messages" => true,
        "openai-chat" => false,
        "openai-responses" => false,
        _ => false,   // 未知协议：保守选择「不能」，宁可丢也不伪造
    };

    /// <summary>
    /// 按目标协议过滤块列表。**保序**。
    /// ⚠️ 过滤后可能为空——调用方必须能处理空列表（见 Review Focus #2）。
    /// </summary>
    public static IReadOnlyList<Block> FilterFor(IReadOnlyList<Block> blocks, string protocol)
    {
        if (CanReplay(protocol)) return blocks;
        return blocks.Where(b => b is not Block.ReasoningBlock).ToList();
    }
}
```

- [ ] **Step 4: 跑测试，确认通过**

```bash
dotnet test --filter "FullyQualifiedName~ReasoningPolicyTests"
```

Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "feat(core): 推理块的协议能力表（能承载才回传，不能则不伪造）"
```

---

### Task 4: 协议常量 + 适配器工厂三个 case

**为什么**：先把**分派**搭好，三条链路各自的实现随后填。这样中间态始终可编译、可测。

**Files:**
- Create: `src/Lilara.Core/Adapters/Protocols.cs`
- Modify: `src/Lilara.Core/Adapters/AdapterFactory.cs`
- Modify: `src/Lilara.Core/Providers/ProviderStore.cs`（`ToSelection` 不变——协议从 `ProviderDefinition.Protocol` 来）
- Test: `tests/Lilara.Core.Tests/AdapterFactoryTests.cs`

**Interfaces:**
- Consumes: `ClaudeAdapter.ProtocolId`、`ProviderDefinition`
- Produces:
  - `Lilara.Core.Adapters.Protocols`（`const string ClaudeMessages = "claude-messages"` / `OpenAiChat = "openai-chat"` / `OpenAiResponses = "openai-responses"`）
  - `AdapterFactory.Create(ProviderDefinition) → IModelAdapter`——三个 case

- [ ] **Step 1: 写失败的测试**

```csharp
using Lilara.Core.Adapters;
using Lilara.Core.Adapters.Claude;
using Lilara.Core.Providers;

namespace Lilara.Core.Tests;

public class AdapterFactoryTests
{
    private static ProviderDefinition Def(string protocol) => new()
    {
        Id = "p", Protocol = protocol, ApiKey = "k",
        DefaultModel = "m", Modality = "vision",
    };

    [Fact]
    public void Create_ClaudeMessages() =>
        Assert.IsType<ClaudeAdapter>(AdapterFactory.Create(Def(Protocols.ClaudeMessages)));

    [Fact]
    public void Create_OpenAiChat() =>
        Assert.IsType<Adapters.OpenAi.OpenAiChatAdapter>(AdapterFactory.Create(Def(Protocols.OpenAiChat)));

    [Fact]
    public void Create_OpenAiResponses() =>
        Assert.IsType<Adapters.OpenAi.OpenAiResponsesAdapter>(AdapterFactory.Create(Def(Protocols.OpenAiResponses)));

    [Fact]
    public void Create_UnknownProtocol_ThrowsWithAHumanMessage()
    {
        var ex = Assert.Throws<ModelCallFailedException>(() => AdapterFactory.Create(Def("gemini")));

        Assert.Contains("gemini", ex.Message);
        Assert.Contains(Protocols.ClaudeMessages, ex.Message);   // 列出支持的
    }

    [Fact]
    public void Protocols_AreDistinctStrings()
    {
        // ⚠️ 三个常量必须互不相同，且与 ClaudeAdapter.ProtocolId 一致——
        // 否则 AdapterFactory 的分派与适配器自身的守卫会对不上（这正是之前
        // 「Repl 硬编码 claude-messages」那类缺陷的同一个坑）。
        Assert.Equal(ClaudeAdapter.ProtocolId, Protocols.ClaudeMessages);
        Assert.Equal(3, new[] { Protocols.ClaudeMessages, Protocols.OpenAiChat, Protocols.OpenAiResponses }
            .Distinct().Count());
    }
}
```

- [ ] **Step 2: 跑测试，确认失败**

```bash
dotnet test --filter "FullyQualifiedName~AdapterFactoryTests"
```

Expected: FAIL — `CS0246: 找不到 'Protocols'` / `找不到 'OpenAiChatAdapter'`

- [ ] **Step 3: 写实现**

`src/Lilara.Core/Adapters/Protocols.cs`：

```csharp
namespace Lilara.Core.Adapters;

/// <summary>
/// 协议标识。⚠️ **单一来源**——`AdapterFactory` 的分派、适配器自身的守卫、
/// `ReasoningPolicy` 的能力表，全读这里。
/// 之前踩过「Repl 硬编码协议字符串」的坑（同一事实两处各写一份），不要重蹈。
/// ⚠️ 这些字符串**会落盘**（`spec.json` / `providers/*.json`），改动即数据迁移。
/// </summary>
public static class Protocols
{
    public const string ClaudeMessages = "claude-messages";
    public const string OpenAiChat = "openai-chat";
    public const string OpenAiResponses = "openai-responses";

    public static readonly IReadOnlyList<string> All =
        [ClaudeMessages, OpenAiChat, OpenAiResponses];
}
```

`AdapterFactory.cs`：

```csharp
using Lilara.Core.Adapters.Claude;
using Lilara.Core.Adapters.OpenAi;
using Lilara.Core.Providers;

namespace Lilara.Core.Adapters;

/// <summary>
/// 协议 → 适配器。⚠️ 焊死的 switch（04 §1.2），不做插件注册表。
/// 加协议 = 加一个 case + 一个适配器类 + Protocols 里一个常量。
/// </summary>
public static class AdapterFactory
{
    public static IModelAdapter Create(ProviderDefinition provider) => provider.Protocol switch
    {
        Protocols.ClaudeMessages => new ClaudeAdapter(provider),
        Protocols.OpenAiChat => new OpenAiChatAdapter(provider),
        Protocols.OpenAiResponses => new OpenAiResponsesAdapter(provider),
        _ => throw new ModelCallFailedException(
            $"未知协议 '{provider.Protocol}'。目前支持：{string.Join(" / ", Protocols.All)}。"),
    };
}
```

⚠️ 让 `ClaudeAdapter.ProtocolId` 改成引用常量，避免两份真相：

```csharp
    public const string ProtocolId = Protocols.ClaudeMessages;
```

- [ ] **Step 4: 本任务只做骨架——两个 OpenAI 适配器先写**空壳**

⚠️ 为了让本任务自洽可编译，先落两个**最小可编译**的适配器（Task 5 / 7 会填实）。空壳**必须抛异常而不是返回假数据**：

`src/Lilara.Core/Adapters/OpenAi/OpenAiChatAdapter.cs`：

```csharp
using Lilara.Core.Providers;

namespace Lilara.Core.Adapters.OpenAi;

/// <summary>OpenAI Chat 适配器。⚠️ 骨架——Task 5 填入真实实现。</summary>
public sealed class OpenAiChatAdapter(ProviderDefinition provider) : IModelAdapter
{
    public Task<ModelReply> CallAsync(ModelRequest request, CancellationToken ct) =>
        throw new ModelCallFailedException("OpenAI Chat 适配器尚未实现。");
}
```

`OpenAiResponsesAdapter.cs` 同形（Task 7 填实）。

- [ ] **Step 5: 跑测试，确认通过**

```bash
dotnet test
```

Expected: PASS（全量；新增 5 条）

- [ ] **Step 6: 提交**

```bash
git add -A
git commit -m "feat(core): 协议常量 + 适配器工厂三分派（OpenAI 两条留骨架）"
```

---

### Task 5: OpenAI Chat——请求构造（纯函数）

**Files:**
- Create: `src/Lilara.Core/Adapters/OpenAi/OpenAiChatRequest.cs`
- Modify: `src/Lilara.Core/Adapters/ModelCall.cs`（`ModelRequest` 加 `Protocol`）
- Modify: `src/Lilara.Core/Session/Stepper.cs`（构造 `ModelRequest` 时补 `Protocol`）
- Test: `tests/Lilara.Core.Tests/OpenAiChatRequestTests.cs`

**Interfaces:**
- Consumes: `ModelRequest`、`Block`、`ReasoningPolicy`（Task 3）
- Produces:
  - `Lilara.Core.Adapters.OpenAi.OpenAiChatRequest.Build(ModelRequest) → string`（**JSON 文本**）
  - `Lilara.Core.Adapters.OpenAi.OpenAiChatRequest.Path`（`const string = "/v1/chat/completions"`）

- [ ] **Step 1: 写失败的测试**

```csharp
using System.Text.Json;
using Lilara.Core.Adapters;
using Lilara.Core.Adapters.OpenAi;
using Lilara.Core.Canonical;
using Lilara.Core.Records;

namespace Lilara.Core.Tests;

public class OpenAiChatRequestTests
{
    private static TimelineEvent Msg(Speaker from, params Block[] blocks) =>
        new TimelineEvent.MessageEvent
        {
            At = DateTimeOffset.UnixEpoch, From = from, Blocks = blocks,
        };

    private static ModelRequest Req(params TimelineEvent[] tl) => new()
    {
        Protocol = Protocols.OpenAiChat,
        ModelId = "deepseek-flash", MaxTokens = 16000, Effort = "high",
        SystemBlocks = [], Timeline = tl,
    };

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Build_SetsModelAndMaxTokens()
    {
        var root = Parse(OpenAiChatRequest.Build(Req(Msg(Speaker.User, new Block.TextBlock("hi")))));

        Assert.Equal("deepseek-flash", root.GetProperty("model").GetString());
        Assert.Equal(16000, root.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public void Build_MapsRoles()
    {
        var root = Parse(OpenAiChatRequest.Build(Req(
            Msg(Speaker.User, new Block.TextBlock("问")),
            Msg(Speaker.Model, new Block.TextBlock("答")))));

        var messages = root.GetProperty("messages");
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
    }

    [Fact]
    public void Build_SingleTextBlock_UsesThePlainStringForm()
    {
        // 单文本走字符串形式（协议原生形状，最省字节）
        var root = Parse(OpenAiChatRequest.Build(Req(Msg(Speaker.User, new Block.TextBlock("你好")))));

        Assert.Equal("你好", root.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public void Build_DropsReasoningBecauseChatCannotCarryIt()
    {
        // ⚠️ 实测：Chat 返回体里没有推理字段 → 不能承载 → 丢，且不伪造
        var root = Parse(OpenAiChatRequest.Build(Req(Msg(Speaker.Model,
            new Block.ReasoningBlock("sig", "想想"), new Block.TextBlock("答案")))));

        Assert.Equal("答案", root.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public void Build_OmitsSystemWhenThereAreNoSystemBlocks()
    {
        // ⚠️ 核心自带零提示词——空时不发 system 消息，而不是发个空串
        var root = Parse(OpenAiChatRequest.Build(Req(Msg(Speaker.User, new Block.TextBlock("hi")))));

        Assert.Equal(1, root.GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public void Build_PutsSystemFirstWhenPresent()
    {
        var request = Req(Msg(Speaker.User, new Block.TextBlock("hi"))) with
        {
            SystemBlocks = [new Block.TextBlock("你是助手")],
        };

        var messages = Parse(OpenAiChatRequest.Build(request)).GetProperty("messages");

        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("你是助手", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
    }

    [Fact]
    public void Build_ThrowsOnEmptyTimeline()
    {
        var ex = Assert.Throws<ArgumentException>(() => OpenAiChatRequest.Build(Req()));
        Assert.Contains("时间线为空", ex.Message);
    }

    [Fact]
    public void Build_ThrowsWhenAMessageHasNoReplayableBlocks()
    {
        // ⚠️ 全是推理块的消息，过滤后为空——协议不接受空 content
        var ex = Assert.Throws<ArgumentException>(() =>
            OpenAiChatRequest.Build(Req(Msg(Speaker.Model, new Block.ReasoningBlock(null, "只想不说")))));

        Assert.Contains("空消息", ex.Message);
    }

    [Fact]
    public void Build_IsByteStable()
    {
        // ⚠️ 前缀稳定性的地基：同一输入两次构造必须逐字节相同（不依赖字典枚举序）
        var request = Req(Msg(Speaker.User, new Block.TextBlock("hi")));

        Assert.Equal(OpenAiChatRequest.Build(request), OpenAiChatRequest.Build(request));
    }
}
```

⚠️ 这个测试用到 `ModelRequest.Protocol`——**Task 4 之后 `ModelRequest` 需要加这个字段**。若编译报缺字段，在 `ModelCall.cs` 的 `ModelRequest` 上加：

```csharp
    /// <summary>目标协议——决定推理块能否回传（ReasoningPolicy），以及适配器分派。</summary>
    public required string Protocol { get; init; }
```

并在 `Stepper.cs` 构造 `ModelRequest` 处补 `Protocol = handle.Spec.Model.Protocol,`。
**这是本计划对既有代码的唯一结构性改动**，做完跑全量确认没回归。

- [ ] **Step 2: 跑测试，确认失败**

```bash
dotnet test --filter "FullyQualifiedName~OpenAiChatRequestTests"
```

Expected: FAIL — `CS0246: 找不到 'OpenAiChatRequest'`

- [ ] **Step 3: 写实现**

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using Lilara.Core.Canonical;
using Lilara.Core.Records;

namespace Lilara.Core.Adapters.OpenAi;

/// <summary>
/// canonical → OpenAI Chat 请求体。
/// ⚠️ 纯函数：同一 `ModelRequest` 必须构造出**逐字节相同**的 JSON（00 §5 铁律 1）。
/// ⚠️ 用 `JsonObject` 显式按序插入——**绝不依赖字典枚举序**。
/// </summary>
public static class OpenAiChatRequest
{
    public const string Path = "/v1/chat/completions";

    public static string Build(ModelRequest request)
    {
        if (request.Timeline.Count == 0)
            throw new ArgumentException("时间线为空，无法构造请求", nameof(request));

        var messages = new JsonArray();

        // ⚠️ 系统提示放第一条消息（Chat 协议没有独立 system 字段）。
        // 核心自带零提示词，所以通常走不到这里——但适配器要支持。
        var systemText = string.Concat(
            request.SystemBlocks.OfType<Block.TextBlock>().Select(b => b.Text));
        if (systemText.Length > 0)
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = systemText });

        foreach (var evt in request.Timeline)
            messages.Add(ToMessage(evt, request.Protocol));

        var body = new JsonObject
        {
            ["model"] = request.ModelId,
            ["max_tokens"] = request.MaxTokens,
            ["messages"] = messages,
        };

        return body.ToJsonString(Json.Options);
    }

    private static JsonObject ToMessage(TimelineEvent evt, string protocol)
    {
        var message = (TimelineEvent.MessageEvent)evt;
        var role = message.From == Speaker.User ? "user" : "assistant";

        // ⚠️ 先按协议能力过滤推理块——Chat 不承载，丢且不伪造（Task 3）
        var blocks = ReasoningPolicy.FilterFor(message.Blocks, protocol);
        var texts = blocks.OfType<Block.TextBlock>().Select(b => b.Text).ToList();

        if (texts.Count == 0)
            throw new ArgumentException(
                $"时间线里有空消息（speaker={message.From}），无法构造请求", nameof(evt));

        // 单文本走字符串形式（协议原生形状，最省）
        return texts.Count == 1
            ? new JsonObject { ["role"] = role, ["content"] = texts[0] }
            : new JsonObject
            {
                ["role"] = role,
                ["content"] = new JsonArray(texts.Select(t => (JsonNode)t!).ToArray()),
            };
    }
}
```

- [ ] **Step 4: 跑测试，确认通过**

```bash
dotnet test --filter "FullyQualifiedName~OpenAiChatRequestTests"
```

Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "feat(core): OpenAI Chat 请求构造（纯函数，键序显式）"
```

---

### Task 6: OpenAI Chat——响应映射（纯函数）

**Files:**
- Create: `src/Lilara.Core/Adapters/OpenAi/OpenAiChatResponse.cs`
- Test: `tests/Lilara.Core.Tests/OpenAiChatResponseTests.cs`

**Interfaces:**
- Consumes: `ModelReply`、`Block`
- Produces: `Lilara.Core.Adapters.OpenAi.OpenAiChatResponse.Map(string json) → ModelReply`

- [ ] **Step 1: 写失败的测试**

```csharp
using Lilara.Core.Adapters.OpenAi;
using Lilara.Core.Canonical;

namespace Lilara.Core.Tests;

public class OpenAiChatResponseTests
{
    private static string Body(string content, string finish = "stop") =>
        $$"""
        {"id":"chatcmpl-1","object":"chat.completion","model":"deepseek-flash",
         "choices":[{"index":0,"message":{"role":"assistant","content":{{Json(content)}}},"finish_reason":"{{finish}}"}],
         "usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
        """;

    private static string Json(string? s) =>
        s is null ? "null" : System.Text.Json.JsonSerializer.Serialize(s);

    [Fact]
    public void Map_ExtractsTheText()
    {
        var reply = OpenAiChatResponse.Map(Body("你好！"));

        Assert.Equal("你好！", ((Block.TextBlock)reply.Blocks[0]).Text);
        Assert.False(reply.Refused);
    }

    [Fact]
    public void Map_SurfacesFinishReason()
    {
        Assert.Equal("length", OpenAiChatResponse.Map(Body("x", "length")).StopReason);
    }

    [Fact]
    public void Map_TreatsContentFilterAsRefused()
    {
        // ⚠️ Chat 的拒答信号是 finish_reason: "content_filter"
        var reply = OpenAiChatResponse.Map(Body("", "content_filter"));

        Assert.True(reply.Refused);
        Assert.Empty(reply.Blocks);
    }

    [Fact]
    public void Map_HandlesNullContentWithoutThrowing()
    {
        // ⚠️ 实测见过：推理模型把 token 烧在思考上，content 为 **null**、
        // finish_reason 为 "length"。绝不能在这里 NRE。
        var reply = OpenAiChatResponse.Map(Body(null, "length"));

        Assert.Empty(reply.Blocks);
        Assert.Equal("length", reply.StopReason);
        Assert.False(reply.Refused);
    }

    [Fact]
    public void Map_HandlesEmptyChoicesWithoutThrowing()
    {
        // ⚠️ 上游异常时可能给空 choices——不崩
        var reply = OpenAiChatResponse.Map("""{"id":"x","choices":[]}""");

        Assert.Empty(reply.Blocks);
    }
}
```

- [ ] **Step 2: 跑测试，确认失败**

```bash
dotnet test --filter "FullyQualifiedName~OpenAiChatResponseTests"
```

Expected: FAIL — `CS0246: 找不到 'OpenAiChatResponse'`

- [ ] **Step 3: 写实现**

```csharp
using System.Text.Json;
using Lilara.Core.Canonical;

namespace Lilara.Core.Adapters.OpenAi;

/// <summary>
/// OpenAI Chat 响应 → canonical。⚠️ 纯函数。
/// ⚠️ 容忍缺失字段：实测见过 `content` 为 `null`（推理模型烧完 token）、`choices` 为空。
/// 上游出什么都不能让核心崩。
/// </summary>
public static class OpenAiChatResponse
{
    public static ModelReply Map(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string? text = null;
        var finish = "unknown";

        if (root.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            if (first.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                finish = fr.GetString() ?? "unknown";

            if (first.TryGetProperty("message", out var msg)
                && msg.TryGetProperty("content", out var c)
                && c.ValueKind == JsonValueKind.String)
                text = c.GetString();
        }

        // ⚠️ Chat 的拒答信号是 finish_reason，不是 HTTP 状态
        var refused = finish == "content_filter";

        // ⚠️ 推理模型可能返回 content=null + finish_reason=length：**没有正文**，
        // 不是错误也不是拒答——如实返回空块，让调用方去提示「被截断」。
        var blocks = text is { Length: > 0 }
            ? new List<Block> { new Block.TextBlock(text) }
            : [];

        return new ModelReply { Blocks = blocks, StopReason = finish, Refused = refused };
    }
}
```

- [ ] **Step 4: 跑测试，确认通过**

```bash
dotnet test --filter "FullyQualifiedName~OpenAiChatResponseTests"
```

Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "feat(core): OpenAI Chat 响应映射（拒答=content_filter，容忍 null content）"
```

---

### Task 7: OpenAI Responses——请求构造（纯函数）

**Files:**
- Create: `src/Lilara.Core/Adapters/OpenAi/OpenAiResponsesRequest.cs`
- Test: `tests/Lilara.Core.Tests/OpenAiResponsesRequestTests.cs`

**Interfaces:**
- Consumes: `ModelRequest`、`Block`、`ReasoningPolicy`
- Produces:
  - `Lilara.Core.Adapters.OpenAi.OpenAiResponsesRequest.Build(ModelRequest) → string`
  - `Lilara.Core.Adapters.OpenAi.OpenAiResponsesRequest.Path`（`const string = "/v1/responses"`）

- [ ] **Step 1: 写失败的测试**

```csharp
using System.Text.Json;
using Lilara.Core.Adapters;
using Lilara.Core.Adapters.OpenAi;
using Lilara.Core.Canonical;
using Lilara.Core.Records;

namespace Lilara.Core.Tests;

public class OpenAiResponsesRequestTests
{
    private static TimelineEvent Msg(Speaker from, params Block[] blocks) =>
        new TimelineEvent.MessageEvent { At = DateTimeOffset.UnixEpoch, From = from, Blocks = blocks };

    private static ModelRequest Req(params TimelineEvent[] tl) => new()
    {
        Protocol = Protocols.OpenAiResponses,
        ModelId = "deepseek-flash", MaxTokens = 16000, Effort = "high",
        SystemBlocks = [], Timeline = tl,
    };

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Build_UsesMaxOutputTokensNotMaxTokens()
    {
        // ⚠️ Responses 的字段名是 max_output_tokens（实测）
        var root = Parse(OpenAiResponsesRequest.Build(Req(Msg(Speaker.User, new Block.TextBlock("hi")))));

        Assert.Equal(16000, root.GetProperty("max_output_tokens").GetInt32());
        Assert.False(root.TryGetProperty("max_tokens", out _));
    }

    [Fact]
    public void Build_EveryInputItemCarriesTypeMessage()
    {
        // ⚠️ 实测：少了 "type":"message" 上游直接报 upstream_error。
        // 这条是本任务最容易被漏掉的一行。
        var root = Parse(OpenAiResponsesRequest.Build(Req(Msg(Speaker.User, new Block.TextBlock("hi")))));

        Assert.Equal("message", root.GetProperty("input")[0].GetProperty("type").GetString());
    }

    [Fact]
    public void Build_UserBlocksAreInputText()
    {
        var root = Parse(OpenAiResponsesRequest.Build(Req(Msg(Speaker.User, new Block.TextBlock("问")))));

        var content = root.GetProperty("input")[0].GetProperty("content")[0];
        Assert.Equal("input_text", content.GetProperty("type").GetString());
        Assert.Equal("问", content.GetProperty("text").GetString());
    }

    [Fact]
    public void Build_AssistantBlocksAreOutputText()
    {
        // ⚠️ 助手回放用 output_text（实测两种都能收，但 output_text 是语义正确的那个）
        var root = Parse(OpenAiResponsesRequest.Build(Req(
            Msg(Speaker.User, new Block.TextBlock("问")),
            Msg(Speaker.Model, new Block.TextBlock("答")))));

        var assistant = root.GetProperty("input")[1];
        Assert.Equal("assistant", assistant.GetProperty("role").GetString());
        Assert.Equal("output_text", assistant.GetProperty("content")[0].GetProperty("type").GetString());
    }

    [Fact]
    public void Build_DropsReasoningBecauseResponsesCannotCarryIt()
    {
        var root = Parse(OpenAiResponsesRequest.Build(Req(Msg(Speaker.Model,
            new Block.ReasoningBlock("sig", "想想"), new Block.TextBlock("答案")))));

        var content = root.GetProperty("input")[0].GetProperty("content");
        Assert.Equal(1, content.GetArrayLength());
        Assert.Equal("答案", content[0].GetProperty("text").GetString());
    }

    [Fact]
    public void Build_UsesInstructionsForSystem()
    {
        // ⚠️ Responses 的系统提示是顶层 `instructions`，不是一条消息
        var request = Req(Msg(Speaker.User, new Block.TextBlock("hi"))) with
        {
            SystemBlocks = [new Block.TextBlock("你是助手")],
        };

        Assert.Equal("你是助手", Parse(OpenAiResponsesRequest.Build(request)).GetProperty("instructions").GetString());
    }

    [Fact]
    public void Build_OmitsInstructionsWhenThereAreNoSystemBlocks()
    {
        // ⚠️ 空时不发 instructions，而不是发个空串
        var root = Parse(OpenAiResponsesRequest.Build(Req(Msg(Speaker.User, new Block.TextBlock("hi")))));

        Assert.False(root.TryGetProperty("instructions", out _));
    }

    [Fact]
    public void Build_ThrowsOnEmptyTimeline()
    {
        Assert.Contains("时间线为空", Assert.Throws<ArgumentException>(() => OpenAiResponsesRequest.Build(Req())).Message);
    }

    [Fact]
    public void Build_ThrowsWhenAMessageHasNoReplayableBlocks()
    {
        Assert.Contains("空消息", Assert.Throws<ArgumentException>(() =>
            OpenAiResponsesRequest.Build(Req(Msg(Speaker.Model, new Block.ReasoningBlock(null, "只想不说"))))).Message);
    }

    [Fact]
    public void Build_IsByteStable()
    {
        var request = Req(Msg(Speaker.User, new Block.TextBlock("hi")));

        Assert.Equal(OpenAiResponsesRequest.Build(request), OpenAiResponsesRequest.Build(request));
    }
}
```

- [ ] **Step 2: 跑测试，确认失败**

```bash
dotnet test --filter "FullyQualifiedName~OpenAiResponsesRequestTests"
```

Expected: FAIL — `CS0246: 找不到 'OpenAiResponsesRequest'`

- [ ] **Step 3: 写实现**

```csharp
using System.Text.Json.Nodes;
using Lilara.Core.Canonical;
using Lilara.Core.Records;

namespace Lilara.Core.Adapters.OpenAi;

/// <summary>
/// canonical → OpenAI Responses 请求体。
/// ⚠️ 纯函数，键序显式（同 `OpenAiChatRequest`）。
/// ⚠️ 两个最容易漏的实测事实：字段名是 `max_output_tokens`；
///    每个 input 项**必须带 `"type":"message"`**（少了上游报 upstream_error）。
/// </summary>
public static class OpenAiResponsesRequest
{
    public const string Path = "/v1/responses";

    public static string Build(ModelRequest request)
    {
        if (request.Timeline.Count == 0)
            throw new ArgumentException("时间线为空，无法构造请求", nameof(request));

        var input = new JsonArray();
        foreach (var evt in request.Timeline)
            input.Add(ToItem(evt, request.Protocol));

        var body = new JsonObject
        {
            ["model"] = request.ModelId,
            ["max_output_tokens"] = request.MaxTokens,
        };

        // ⚠️ 系统提示走顶层 instructions（不占一条消息）。核心自带零提示词，
        // 通常为空——空时**不发这个字段**，而不是发个空串。
        var systemText = string.Concat(
            request.SystemBlocks.OfType<Block.TextBlock>().Select(b => b.Text));
        if (systemText.Length > 0)
            body["instructions"] = systemText;

        body["input"] = input;

        return body.ToJsonString(Json.Options);
    }

    private static JsonObject ToItem(TimelineEvent evt, string protocol)
    {
        var message = (TimelineEvent.MessageEvent)evt;
        var role = message.From == Speaker.User ? "user" : "assistant";

        // ⚠️ Responses 不承载推理 → 过滤（丢且不伪造）
        var blocks = ReasoningPolicy.FilterFor(message.Blocks, protocol);
        var texts = blocks.OfType<Block.TextBlock>().Select(b => b.Text).ToList();

        if (texts.Count == 0)
            throw new ArgumentException(
                $"时间线里有空消息（speaker={message.From}），无法构造请求", nameof(evt));

        // ⚠️ 用户用 input_text、助手用 output_text（实测皆可，这是语义正确的分法）
        var blockType = message.From == Speaker.User ? "input_text" : "output_text";
        var content = new JsonArray(
            texts.Select(t => (JsonNode)new JsonObject { ["type"] = blockType, ["text"] = t }).ToArray());

        return new JsonObject
        {
            ["type"] = "message",   // ⚠️ 必须——少了它上游报 upstream_error（实测）
            ["role"] = role,
            ["content"] = content,
        };
    }
}
```

- [ ] **Step 4: 跑测试，确认通过**

```bash
dotnet test --filter "FullyQualifiedName~OpenAiResponsesRequestTests"
```

Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "feat(core): OpenAI Responses 请求构造（type:message + max_output_tokens）"
```

---

### Task 8: OpenAI Responses——响应映射（纯函数）

**Files:**
- Create: `src/Lilara.Core/Adapters/OpenAi/OpenAiResponsesResponse.cs`
- Test: `tests/Lilara.Core.Tests/OpenAiResponsesResponseTests.cs`

**Interfaces:**
- Consumes: `ModelReply`、`Block`
- Produces: `Lilara.Core.Adapters.OpenAi.OpenAiResponsesResponse.Map(string json) → ModelReply`

- [ ] **Step 1: 写失败的测试**

```csharp
using Lilara.Core.Adapters.OpenAi;
using Lilara.Core.Canonical;

namespace Lilara.Core.Tests;

public class OpenAiResponsesResponseTests
{
    private static string Body(string? text, string status = "completed") =>
        $$"""
        {"id":"resp_1","object":"response","status":"{{status}}","model":"deepseek-flash",
         "output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":{{Json(text)}}}]}],
         "usage":{"input_tokens":1,"output_tokens":1,"total_tokens":2}}
        """;

    private static string Json(string? s) =>
        s is null ? "null" : System.Text.Json.JsonSerializer.Serialize(s);

    [Fact]
    public void Map_ExtractsOutputText()
    {
        var reply = OpenAiResponsesResponse.Map(Body("READY"));

        Assert.Equal("READY", ((Block.TextBlock)reply.Blocks[0]).Text);
        Assert.False(reply.Refused);
    }

    [Fact]
    public void Map_SurfacesStatus()
    {
        Assert.Equal("incomplete", OpenAiResponsesResponse.Map(Body("x", "incomplete")).StopReason);
    }

    [Fact]
    public void Map_SkipsNonMessageOutputItems()
    {
        // ⚠️ output 里可能混着非 message 项（reasoning 等）——只取 message
        var json = """
        {"id":"r","status":"completed","output":[
          {"type":"reasoning","summary":[]},
          {"type":"message","role":"assistant","content":[{"type":"output_text","text":"答案"}]}]}
        """;

        var reply = OpenAiResponsesResponse.Map(json);

        Assert.Single(reply.Blocks);
        Assert.Equal("答案", ((Block.TextBlock)reply.Blocks[0]).Text);
    }

    [Fact]
    public void Map_HandlesEmptyOutputWithoutThrowing()
    {
        var reply = OpenAiResponsesResponse.Map("""{"id":"r","status":"incomplete","output":[]}""");

        Assert.Empty(reply.Blocks);
        Assert.Equal("incomplete", reply.StopReason);
    }

    [Fact]
    public void Map_TreatsIncompleteAsNotRefused()
    {
        // ⚠️ `incomplete` 是「没生成完」，不是拒答——别混为一谈
        Assert.False(OpenAiResponsesResponse.Map(Body("x", "incomplete")).Refused);
    }
}
```

- [ ] **Step 2: 跑测试，确认失败**

```bash
dotnet test --filter "FullyQualifiedName~OpenAiResponsesResponseTests"
```

Expected: FAIL — `CS0246: 找不到 'OpenAiResponsesResponse'`

- [ ] **Step 3: 写实现**

```csharp
using System.Text.Json;
using Lilara.Core.Canonical;

namespace Lilara.Core.Adapters.OpenAi;

/// <summary>
/// OpenAI Responses 响应 → canonical。⚠️ 纯函数。
/// ⚠️ `output` 是异构数组——只取 `type == "message"` 的项；其余（reasoning 等）跳过。
/// </summary>
public static class OpenAiResponsesResponse
{
    public static ModelReply Map(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var status = root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString() ?? "unknown"
            : "unknown";

        var texts = new List<string>();
        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var t) || t.GetString() != "message") continue;
                if (!item.TryGetProperty("content", out var content)
                    || content.ValueKind != JsonValueKind.Array) continue;

                foreach (var block in content.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out var bt) && bt.GetString() == "output_text"
                        && block.TryGetProperty("text", out var txt)
                        && txt.ValueKind == JsonValueKind.String
                        && txt.GetString() is { Length: > 0 } str)
                        texts.Add(str);
                }
            }
        }

        // ⚠️ Responses 的拒答信号不在 status 里（`incomplete` 是「没生成完」不是拒答）。
        // 目前没有可靠的拒答指示，故恒 false——有的话如实透传即可。
        var blocks = texts.Count > 0
            ? texts.Select(t => (Block)new Block.TextBlock(t)).ToList()
            : [];

        return new ModelReply { Blocks = blocks, StopReason = status, Refused = false };
    }
}
```

- [ ] **Step 4: 跑测试，确认通过**

```bash
dotnet test --filter "FullyQualifiedName~OpenAiResponsesResponseTests"
```

Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "feat(core): OpenAI Responses 响应映射（只取 message 项）"
```

---

### Task 9: HTTP 层 + 两个适配器填实

**为什么**：前四个任务是**纯函数**（可离线测）。HTTP 只在这一层出现——因为 `04` §1.2 说适配器是薄壳，而**网络是这个壳里唯一的行为**。

**Files:**
- Create: `src/Lilara.Core/Adapters/OpenAi/OpenAiHttp.cs`
- Modify: `src/Lilara.Core/Adapters/OpenAi/OpenAiChatAdapter.cs`（填实）
- Modify: `src/Lilara.Core/Adapters/OpenAi/OpenAiResponsesAdapter.cs`（填实）
- Test: `tests/Lilara.Core.Tests/OpenAiHttpTests.cs`

**Interfaces:**
- Consumes: `ProviderDefinition`、`ResolvedEndpoint`、`AuthSchemes`
- Produces:
  - `Lilara.Core.Adapters.OpenAi.OpenAiHttp`（`OpenAiHttp(ProviderDefinition)`；`Task<string> PostAsync(string path, string body, CancellationToken) → string`）
  - `OpenAiChatAdapter` / `OpenAiResponsesAdapter` 实装 `IModelAdapter`

- [ ] **Step 1: 写失败的测试**

⚠️ 这一层**不测网络本身**（那要真调用）；测的是**构造期**与**错误翻译**——沿用 `ClaudeAdapter` 的既有做法（它也只测构造守卫）。

```csharp
using Lilara.Core.Adapters;
using Lilara.Core.Adapters.OpenAi;
using Lilara.Core.Providers;

namespace Lilara.Core.Tests;

public class OpenAiHttpTests
{
    private static ProviderDefinition Def(string protocol) => new()
    {
        Id = "gw", Protocol = protocol, ApiKey = "k",
        DefaultModel = "m", Modality = "vision",
    };

    [Fact]
    public void ClaudeAdapter_StillRejectsWrongProtocol()
    {
        Assert.Throws<ArgumentException>(() => new Adapters.Claude.ClaudeAdapter(Def(Protocols.OpenAiChat)));
    }

    [Fact]
    public void ChatAdapter_RejectsWrongProtocol()
    {
        var ex = Assert.Throws<ArgumentException>(() => new OpenAiChatAdapter(Def(Protocols.ClaudeMessages)));
        Assert.Contains(Protocols.OpenAiChat, ex.Message);
    }

    [Fact]
    public void ResponsesAdapter_RejectsWrongProtocol()
    {
        var ex = Assert.Throws<ArgumentException>(() => new OpenAiResponsesAdapter(Def(Protocols.ClaudeMessages)));
        Assert.Contains(Protocols.OpenAiResponses, ex.Message);
    }

    [Fact]
    public void ChatAdapter_RejectsNullProvider()
    {
        Assert.Throws<ArgumentNullException>(() => new OpenAiChatAdapter(null!));
    }

    [Fact]
    public void OpenAiHttp_DefaultAuthIsBearer()
    {
        // ⚠️ OpenAI 兼容端点的通用约定就是 Bearer；
        // 缺省与 Claude 那套（x-api-key）相反，所以必须各测各的。
        Assert.Equal(AuthSchemes.Bearer, new OpenAiHttp(Def(Protocols.OpenAiChat)).AuthScheme);
    }

    [Fact]
    public void OpenAiHttp_HonoursExplicitApiKeyScheme()
    {
        var def = Def(Protocols.OpenAiChat) with { AuthScheme = AuthSchemes.ApiKey };
        Assert.Equal(AuthSchemes.ApiKey, new OpenAiHttp(def).AuthScheme);
    }
}
```

- [ ] **Step 2: 跑测试，确认失败**

```bash
dotnet test --filter "FullyQualifiedName~OpenAiHttpTests"
```

Expected: FAIL — `CS0246: 找不到 'OpenAiHttp'` / 适配器无对应构造器

- [ ] **Step 3: 写实现**

`src/Lilara.Core/Adapters/OpenAi/OpenAiHttp.cs`：

```csharp
using System.Net.Http.Headers;
using System.Text;
using Lilara.Core.Providers;

namespace Lilara.Core.Adapters.OpenAi;

/// <summary>
/// OpenAI 两条链路共用的 HTTP 层。
/// ⚠️ **本文件是整个计划里唯一出现 `HttpClient` 的地方**——纯函数层绝不知道网络存在。
/// ⚠️ 认证缺省是 **Bearer**（OpenAI 兼容端点的通用约定），与 Claude 那套（x-api-key）相反。
/// </summary>
public sealed class OpenAiHttp
{
    private readonly HttpClient _client = new();
    private readonly string _key;

    public string AuthScheme { get; }
    public ResolvedEndpoint Endpoint { get; }

    public OpenAiHttp(ProviderDefinition provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        // ⚠️ 缺省与 Claude 相反：OpenAI 兼容端点默认就是 Bearer
        AuthScheme = provider.AuthScheme ?? AuthSchemes.Bearer;
        Endpoint = EndpointResolver.Resolve(provider.Endpoint);
        _key = provider.ApiKey;
    }

    public async Task<string> PostAsync(string path, string body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint.Url + path);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        if (AuthScheme == AuthSchemes.Bearer)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        else
            request.Headers.TryAddWithoutValidation("x-api-key", _key);

        try
        {
            using var response = await _client.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                throw new ModelCallFailedException(Describe(response.StatusCode, text));

            return text;
        }
        // ⚠️ ModelCallFailedException 要**原样放行**——否则上面那句 401 的人话
        // 会被下面这个 catch 再包一层，变成「模型调用失败：API key 无效或已吊销（401）…」。
        // 这正是 Claude 那条踩过的坑（Build 的异常被重复包装）。
        catch (ModelCallFailedException) { throw; }
        // ⚠️ 调用方取消要放行（与 Claude 那条同一判据：HttpClient 超时也是 TaskCanceledException，
        // 那个要包装；只有请求方自己的 token 被取消才放行）
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw new ModelCallFailedException($"模型调用失败：{ex.Message}", ex);
        }
    }

    /// <summary>把 HTTP 失败翻译成一句人话。</summary>
    private static string Describe(System.Net.HttpStatusCode status, string body) => status switch
    {
        System.Net.HttpStatusCode.Unauthorized =>
            "API key 无效或已吊销（401）。检查 provider 配置里的 key。",
        System.Net.HttpStatusCode.Forbidden =>
            "这个 key 没有访问该模型的权限（403）。",
        System.Net.HttpStatusCode.NotFound =>
            "端点或模型不存在（404）。检查 provider 的 endpoint 与 model。",
        System.Net.HttpStatusCode.TooManyRequests =>
            "被限流了（429）。等一会儿再试。",
        >= System.Net.HttpStatusCode.InternalServerError =>
            $"上游 {(int)status}。这是临时的，重试即可。",
        _ => $"模型调用失败（{(int)status}）：{Truncate(body)}",
    };

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200] + "…";
}
```

`OpenAiChatAdapter.cs` 填实：

```csharp
using Lilara.Core.Providers;

namespace Lilara.Core.Adapters.OpenAi;

/// <summary>OpenAI Chat 适配器。⚠️ 薄壳：构造 → 发 → 映射。翻译全在两个纯函数里。</summary>
public sealed class OpenAiChatAdapter : IModelAdapter
{
    public const string ProtocolId = Protocols.OpenAiChat;

    private readonly OpenAiHttp _http;

    public OpenAiChatAdapter(ProviderDefinition provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (provider.Protocol != ProtocolId)
            throw new ArgumentException(
                $"OpenAiChatAdapter 只认协议 '{ProtocolId}'，收到 '{provider.Protocol}'", nameof(provider));

        _http = new OpenAiHttp(provider);
    }

    public async Task<ModelReply> CallAsync(ModelRequest request, CancellationToken ct)
    {
        var body = OpenAiChatRequest.Build(request);          // 纯函数：失败也是「调用失败」
        var json = await _http.PostAsync(OpenAiChatRequest.Path, body, ct);
        return OpenAiChatResponse.Map(json);                  // 纯函数
    }
}
```

`OpenAiResponsesAdapter.cs` 同形，换成 `OpenAiResponsesRequest` / `OpenAiResponsesResponse` 与 `ProtocolId = Protocols.OpenAiResponses`。

⚠️ **`CallAsync` 里 `Build` 必须在 try 之外、还是之内？** 沿用 Claude 那条的结论：**包装它**。上面写法把 `Build` 放在 `PostAsync` 之前且都不在 try 里——但 `PostAsync` 自己 catch 了 HTTP 异常，而 `Build` 的 `ArgumentException` 会**裸奔**。
**所以要把 `Build` 也包进去**：

```csharp
    public async Task<ModelReply> CallAsync(ModelRequest request, CancellationToken ct)
    {
        try
        {
            var body = OpenAiChatRequest.Build(request);
            var json = await _http.PostAsync(OpenAiChatRequest.Path, body, ct);
            return OpenAiChatResponse.Map(json);
        }
        catch (ModelCallFailedException) { throw; }        // 已经是人话了，别套两层
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw new ModelCallFailedException($"模型调用失败：{ex.Message}", ex);
        }
    }
```

⚠️ 这条正是 Claude 那条踩过的坑（`Build` 在 try 外面 → 未知 effort 裸奔）。**两个 OpenAI 适配器都要按这个形状写**。

- [ ] **Step 4: 跑测试，确认通过**

```bash
dotnet test
```

Expected: PASS（全量）

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "feat(core): OpenAI HTTP 层 + 两条适配器填实（Build 包进 try）"
```

---

### Task 10: 控制台 `--protocol` + 真实联调

**为什么**：到这里三条链路都实现了，但**控制台建不了非 Claude 的提供商**（`Repl.HandleProviderSet` 把协议写死成 `ClaudeAdapter.ProtocolId`）。而且**必须真联调**——三个协议的形状是我实测的，但**没验证过我们自己的构造器发出去上游认不认**。

**Files:**
- Modify: `src/Lilara.Core/Console/Repl.cs`（`HandleProviderSet` 支持 `--protocol`）
- Modify: `src/Lilara.Core/Console/Repl.cs`（`provider set` 帮助文案）
- Test: `tests/Lilara.Core.Tests/ReplTests.cs`（加）

**Interfaces:**
- Consumes: `Protocols.All`、`AdapterFactory`
- Produces: `provider set <id> --key <k> [--model <m>] [--protocol <p>] [--endpoint <u>] [--auth <a>]`

- [ ] **Step 1: 写失败的测试**

```csharp
    [Fact]
    public async Task ProviderSet_HonoursProtocolFlag()
    {
        var repl = NewRepl();

        await repl.ExecuteAsync(
            "provider set gw --key sk-x --model deepseek-flash --protocol openai-chat --endpoint http://localhost:5129 --auth bearer",
            CancellationToken.None);

        var saved = _providers.Load("gw")!;
        Assert.Equal(Protocols.OpenAiChat, saved.Protocol);
        Assert.Equal("http://localhost:5129", saved.Endpoint);
        Assert.Equal(AuthSchemes.Bearer, saved.AuthScheme);
    }

    [Fact]
    public async Task ProviderSet_DefaultsToClaudeWhenProtocolOmitted()
    {
        // ⚠️ 缺省保持既有行为，不破坏已经配好的用法
        var repl = NewRepl();

        await repl.ExecuteAsync("provider set c --key sk-x", CancellationToken.None);

        Assert.Equal(Protocols.ClaudeMessages, _providers.Load("c")!.Protocol);
    }

    [Fact]
    public async Task ProviderSet_RejectsAnUnknownProtocolWithoutSaving()
    {
        var repl = NewRepl();

        await repl.ExecuteAsync("provider set bad --key k --protocol gemini", CancellationToken.None);

        Assert.Null(_providers.Load("bad"));
        Assert.Contains("gemini", _out.ToString());
    }
```

- [ ] **Step 2: 跑测试，确认失败**

```bash
dotnet test --filter "FullyQualifiedName~ReplTests"
```

Expected: FAIL —— `Assert.Equal() Failure: Expected: "openai-chat", Actual: "claude-messages"`

- [ ] **Step 3: 写实现**

`Repl.HandleProviderSet` 扩参数解析（保留既有的保留逻辑，别退回去）：

```csharp
    private void HandleProviderSet(string line)
    {
        // provider set <id> --key <k> [--model <m>] [--protocol <p>] [--endpoint <u>] [--auth <a>]
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var id = parts[2];

        string? key = null, model = null, protocol = null, endpoint = null, auth = null;
        for (var i = 3; i < parts.Length - 1; i++)
        {
            // ⚠️ 缺值的标志位要拒绝——否则 `--key --model m` 会把 "--model" 当成 key 吞掉
            var value = parts[i + 1];
            if (value.StartsWith("--", StringComparison.Ordinal))
            {
                _out.WriteLine($"参数 {parts[i]} 缺少值。");
                return;
            }

            switch (parts[i])
            {
                case "--key": key = value; break;
                case "--model": model = value; break;
                case "--protocol": protocol = value; break;
                case "--endpoint": endpoint = value; break;
                case "--auth": auth = value; break;
            }
        }

        if (key is null)
        {
            _out.WriteLine("用法：provider set <id> --key <key> [--model <model>] "
                + $"[--protocol <{string.Join("|", Protocols.All)}>] [--endpoint <url>] "
                + "[--auth api-key|bearer]");
            return;
        }

        // 协议必须是已知的——否则工厂会抛，而那时已经落盘了
        if (protocol is not null && !Protocols.All.Contains(protocol))
        {
            _out.WriteLine($"未知协议 '{protocol}'。支持：{string.Join(" / ", Protocols.All)}");
            return;
        }

        // ⚠️ 先读旧定义，**保留本次没提到的字段**——整文件覆盖会抹掉手写配置
        var existing = _providers.Load(id);

        _providers.Save(new ProviderDefinition
        {
            Id = id,
            Protocol = protocol ?? Protocols.ClaudeMessages,   // 缺省保持既有行为
            ApiKey = key,
            DefaultModel = model ?? existing?.DefaultModel ?? "claude-opus-5",
            Modality = existing?.Modality ?? "vision",
            Endpoint = endpoint ?? existing?.Endpoint,
            AuthScheme = auth ?? existing?.AuthScheme,
        });

        _out.WriteLine($"✓ 提供商 '{id}' 已保存（{protocol ?? Protocols.ClaudeMessages}）。");
    }
```

- [ ] **Step 4: 跑测试，确认通过**

```bash
dotnet test
```

Expected: PASS（全量）

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "feat(core): provider set 支持 --protocol/--endpoint/--auth"
```

- [ ] **Step 6: 真实联调——三条链路各打一轮** ⚠️ **这是本计划的验收，必须真跑**

用控制器实测过的网关（`localhost:5129`，三种协议都兼容）。**先备份**已有的数据根再动：

```bash
cp -r ~/.lilara ~/.lilara.bak-$(date +%s) 2>/dev/null || echo "（首次运行，无数据根可备份）"
```

**三条链路各跑一次双轮**（双轮才验得出推理块往返）：

```bash
export LILARA_HOME=/tmp/lilara-3proto && rm -rf "$LILARA_HOME"
K=sk-ap-9862f39624ad8c04b3c08c3dcb07fbadd52bb35926066b93694d1a31a6e5046ce12c0acf0a2486018c315551adb5f80b
U=http://localhost:5129
for P in claude-messages openai-chat openai-responses; do
  echo "════ $P ════"
  printf "provider set gw --key $K --model deepseek-flash --protocol $P --endpoint $U --auth bearer\nsession create\nSay only: ONE\nSay only: TWO\nquit\n" \
    | env -u ANTHROPIC_BASE_URL -u ANTHROPIC_AUTH_TOKEN dotnet run --project src/Lilara.Core 2>&1 | tail -6
done
```

Expected（每条链路）：
- 横幅显示 `端点 http://localhost:5129（来自 provider 配置）`
- 两轮都拿到回复（`⏺  ONE` / `⏺  TWO`）——**第二轮成功 = 回放被上游接受了**
- `history` 能回放，且 `history.jsonl` 里只有该协议的块

⚠️ **`max_tokens` 要够**：推理模型会把 token 烧在思考上，实测 400 会得到 `finish_reason: length` + 空 content。若见到空回复，**先调大再判断是不是 bug**。

- [ ] **Step 7: 把联调结果写进报告**

逐条记录：哪三条通过、哪条失败、失败时的**原始响应体**（截断到 200 字），以及**推理块在三条链路各自的实际表现**。

---

## 完成判据

1. `dotnet test` 全绿。
2. **三条链路各能跑通双轮真实对话**（Task 10 Step 6）。
3. 推理块：Claude 条原样往返；Chat / Responses 条**丢弃且不伪造**（`history.jsonl` 里能看出来）。
4. `provider set` 能配三种协议、三种端点、两种认证；**未知协议被拒且不落盘**。
5. 同一个 `ModelRequest` 两次构造出的 JSON **逐字节相同**（三条链路各自的 `Build_IsByteStable`）。
6. HTTP 失败给人话，REPL 不崩。

## 明确不在本计划内

- `tool_call` / `tool_result` 两种块 —— **核心不带工具**（`04` §1.3），请求里永不声明 tools
- 图片 / 附件 / 库 —— `01` §8.4–8.5 的模态路由是另一件事
- 流式 —— `01` §11.9
- 压缩 / release / GC / 声明锁 —— `01` §11–§12
- 已确认延后的 20 条 minor（见 SDD 账本）
- `ProviderDefinition.Modality` 的实际使用——字段在，路由未做
