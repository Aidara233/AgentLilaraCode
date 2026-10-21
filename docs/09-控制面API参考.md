# 控制面 API 参考

> **最后更新：master `9efbe0a` + 本工作树（2026-10-18，三个线上键名缺陷已修）**
>
> 本文是**远程 API 的接口面**：HTTP 端点、WS 帧、命令、事件、快照字段。
> 读者是**接核心的程序**（头、脚本、第三方客户端），不是读设计的开发者。
>
> ⚠️ **与 `03-控制面协议.md` 的分工**：`03` 答「**为什么**是这样」（三层消息、投递即返回、
> `rejected` 与 `error` 的分界、流的划分）。本文答「**线上到底有什么、字段叫什么、什么情况下会变**」。
> 两者冲突时**以本文为准**——因为本文最后核对的是代码，而 `03` 里那几张清单是有名的会腐（§8 记了它腐过的地方）。

## 0. 怎么用这份文档

- **形状的判据**：每个 JSON 都配了**代码落点**（`文件:行`）。改代码不改本文 ⇒ 本文错；
  改本文不改代码 ⇒ 客户端会按错的形状写。
- **键的顺序不保证**，字段**大小写严格**（camelCase）。
- **值为 `null` 的字段整个键不出现**（`WireJson.Options` 的 `WhenWritingNull`）。
  这是全篇最容易踩的一处，§2.4 单列。
- **枚举一律 camelCase 小写**（`"user"` / `"permission"` / `"toolResult"`）。
- **不确定一个字段在不在**：`03` §4.2 与 §6 的旧表已经不准，别参考。

## 1. 连接

| 项 | 值 | 证据 |
|---|---|---|
| 监听地址 | **仅 `127.0.0.1`** | `ControlServer.BindAddress`（`Control/ControlServer.cs:52`） |
| 默认端口 | `17988`；环境变量 `LILARA_PORT` 覆盖 | `ControlServer.DefaultPort`（`:36`）、`Program.cs` |
| HTTP 基址 | `http://127.0.0.1:{port}` | `ControlServer.Url`（`:56`） |
| WS 端点 | `ws://127.0.0.1:{port}/ws` | `ControlServer.cs:648` |
| 鉴权 | **没有**。「本机唯一核心、本机进程即可信」（`03` §10） | — |
| CORS | `Access-Control-Allow-Origin: *`；预检 `OPTIONS` 回 `204` | `ControlServer.cs:79-89` |

### 1.1 WS 握手的 Origin 闸

- 请求**不带** `Origin` 头 ⇒ **放行**（curl / 脚本 / 测试都不带）。
- 带了 ⇒ 必须**逐字等于**生效的 allowed origin（默认 `http://127.0.0.1:17990`，
  即头的默认端口；由 `LILARA_ALLOWED_ORIGIN` 或 `LILARA_HEAD_PORT` 推导），否则 **403**。
- ⚠️ `localhost` 与 `127.0.0.1` **不是同一个 origin**（按浏览器的判法），`localhost` 会被拒。
- ⚠️ 这是**防网页**，不是鉴权：非浏览器客户端可以伪造 `Origin`。
- 403 的响应体只有非浏览器客户端看得到（浏览器不把 WS 握手的响应体交给页面 JS）。
  想知道核心接受了哪个 origin，读 `GET /info` 的 `allowedOrigin`。

## 2. WS 线格式

### 2.1 帧（信封）

核心 → 客户端：三种，判别式是 `kind`（`Hosting/WireEvent.cs:207-222`，`WireFormatTests` 钉着）。

```jsonc
// 事件
{ "kind": "event", "topic": "session:s1", "seq": 42, "payload": { "kind": "session.step.begin", ... } }

// 回执（对命令 / 对畸形帧）
{ "kind": "ack", "id": "c1", "accepted": true, "sessionId": "s1", "reason": null, "note": null }

// 该流的补发起点已不在缓冲里 ⇒ 拉快照重建
{ "kind": "resync_required", "topic": "session:s1" }
```

`ack` 的字段：

| 字段 | 含义 |
|---|---|
| `id` | 客户端发命令时给的 id，核心**原样带回**。畸形帧的 `id` 是 `"?"` |
| `accepted` | `true` = 命令被受理（**不代表业务成功**）；`false` = 命令本身不合法或帧畸形 |
| `sessionId` | 分配型命令（`session.create`）回传；其余为 `null`（被省略） |
| `reason` | 只在 `accepted:false` 时出现，一句人话 |
| `note` | 只在 `accepted:true` 时出现，**给人看的补充**，不是协议的一部分 |

⚠️ **`accepted:true` 不等于成功**。业务失败（模型 400、上游 503、配置文件被占用）走
**`error` 事件**，回执照样是 `accepted:true`（`03` §3 的硬约束；`ControlServer.cs:838-843`）。

### 2.2 客户端 → 核心：入站帧

⚠️ `03` 从未列出这一半。代码在 `ControlServer.cs:778-818`。

```jsonc
// 订阅一条流
{ "kind": "subscribe", "topic": "session:s1", "since": 42 }

// 退订
{ "kind": "unsubscribe", "topic": "session:s1" }

// 发一条命令
{ "kind": "command", "id": "c1", "payload": { "kind": "session.send", ... } }
```

- **`since`**（数字，可省）：补齐起点。省略 = 只要**从现在起**的事件。
  - `since` 早于该流缓冲里最早的一条 ⇒ 收到 `resync_required`，然后该订阅**静默失聪**，
    必须重新 `subscribe`（`EventBus.cs:132-160`）。
  - `since` 大于核心已发的序号 ⇒ 同样 `resync_required`（客户端记错了游标，或连了另一个核心的数据）。
  - 该流从没发过事件 ⇒ `since: 0` 安全。
- **重复 `subscribe` 同一个 topic** ⇒ **换掉**旧订阅（不是 no-op）。旧订阅的泵会停。
- **退订一个没订过的 topic** 是合法 no-op，不回错。
- **认不出的帧 `kind`** ⇒ 回 `ack{id:"?", accepted:false, reason:"认不出的帧 kind='…'"}`。
- **畸形帧**（JSON 坏了、缺 `topic`、`payload` 不对）⇒ 回 `ack{id:"?", accepted:false, reason:"帧形状不对：…"}`，
  **连接不断**。
- ⚠️ **`03` §7 说的 `hello(lastSeq)` 帧不存在**。重连的正确姿势是逐个 topic 重新 `subscribe`，
  带上各自的高水位（`GET /sessions/{id}/state` 与 `/history` 返回的 `highWater`）。

### 2.3 流（topic）

| topic | 内容 | 生产者 | 消费者 |
|---|---|---|---|
| `session:{sessionId}` | 这个会话的全部事件 | ✅ | 头 |
| `project:{projectKey}` | 跨会话：会话生死、声明、`session.created` | ✅ | 头 |
| `approvals` | **全部**待审批（`approval.requested` / `.resolved`），跨会话 | ✅ | 头 |
| `core` | 设备级告警 | ❌ **零个 `Publish` 点**（`EventBus.CoreTopic` 只有常量） | — |

命名见 `EventBus.cs:199-215`。⚠️ `project:{pk}` 里的 `pk` 是 `ProjectKey.FromPath(工作目录)` 的
**16 位小写 hex**（`Kernel/ProjectKey.cs:6-17`），不是路径本身。

### 2.4 `seq` 与高水位

- `seq` 是**每个 topic 内**的单调增整数（不是全局）。
- 快照端点（`/state`、`/history`）返回 `highWater` = 该会话流**此刻的最后一个 seq**。
  客户端拿到它之后 `subscribe(topic, since: highWater)`，两者之间落的事件会被补发，不漏。
- ⚠️ **`seq` 是 WS 游标；`/history` 的 `nextCursor` 是历史分页游标。两者不能混用。**

### 2.5 事件载荷

以下是**核心 → 客户端**的全部事件（`WireEvent.cs:8-28`，20 个类型）。
所有事件都带判别式 `kind`；所有时间都是 ISO-8601 带时区（`DateTimeOffset`）。

| `kind` | 载荷 | 发在哪条流 | 何时发 |
|---|---|---|---|
| `session.created` | `sessionId` · `projectKey` | 项目流 **+** 会话流（发两次） | `session.create` 成功之后 |
| `session.step.begin` | `sessionId` · `turn` · `at` | 会话流 | 每一步开始 |
| `session.step.end` | `sessionId` · `turn` · `refused` · `stopReason` | 会话流 | 每一步结束（含拒答） |
| `message.appended` | `sessionId` · `from` · `blocks` · `at` · **`historyId`（可空）** | 会话流 | 用户消息入队时（`from:"user"`）、模型回复落盘后（`from:"model"`） |
| `session.state` | `sessionId` · `status` · `turn` | 会话流 | 状态迁移 / 步结束 / 中断 |
| `axis.changed` | `sessionId` · `axis` · `value` | 会话流 | **真的变了才发**（同值不发） |
| `spec.changed` | `sessionId` · `model` · `systemPromptPreset` | 会话流 | `session.configure` **真的改了才发** |
| `claim.acquired` | `sessionId` · `path` | 项目流 | `claim.acquire` 成功 |
| `claim.released` | `sessionId` · `path` | 项目流 | `claim.release`、`session.delete`、模型侧 `release` 摘锁 |
| `session.deactivated` | `sessionId` · `projectKey` | 会话流 **+** 项目流 | `session.deactivate` |
| `session.archived` | `sessionId` · `projectKey` · `archivedAt`（`null` = 取消归档） | 会话流 **+** 项目流 | `session.archive` 状态真的变了 |
| `session.deleted` | `sessionId` · `projectKey` | **只发项目流** | `session.delete` 成功 |
| `session.reclaimed` | `sessionId` · `reason`（一句回执原话） | 会话流 | `session.gc`、询问超时收尾 |
| `release.recorded` | `sessionId` · `id` · `scope` · `summary` · `at` | 会话流 | 模型侧 `release` 画了一条线 |
| `tool.result.appended` | `sessionId` · `id` · `callId` · `blocks` · `at` | 会话流 | 工具结果落盘（一条结果一条事件） |
| `usage.updated` | `sessionId` · `call`（`UsageCall`）· `summary`（`UsageSummary`） | 会话流 | 一次会话模型调用记账之后 |
| `session.compaction.completed` | `sessionId` · `checkpointId` · `coveredThroughEventId` · `summaryTokens`（可空） | 会话流 | `session.compact` 完成 |
| `approval.requested` | `sessionId` · `askId` · `askKind` · `items` · `remember` · `deadlineAt` | `approvals` 流 **+** 会话流 | 会话挂起等回答 |
| `approval.resolved` | `sessionId` · `askId` · `outcome` | `approvals` 流 **+** 会话流 | 回答 / 作废 / 超时 / **顶替** |
| `error` | `sessionId` · `message` | 会话流 | 业务失败（模型调用失败等） |

#### 各事件的字段细节

**`message.appended`**
- `from`：`"user"` / `"model"`。
- `blocks`：块数组，形状见 §2.6。
- `historyId`：这条消息在 `history.jsonl` 里的**记录 id**，可空。
  - 用户消息**总是**有它（等于 `session.send` 创建的记录 id）。
  - 模型消息在落盘之后的那条事件里有它；**拒答路径**没有模型消息，也就没有这条事件。
- ⚠️ **会重复**：用户消息在 `session.send` 时发一次；步末若模型有输出，模型那条单独发。
  客户端必须按 `historyId` 幂等（无 `historyId` 的按「不重放」处理）。
  这是当前实现的可观测行为，不是协议承诺。

**`session.state`**
- `status` 取值：`"Idle"` / `"Running"` / `"WaitingForInput"`（**PascalCase**，与 `state.status` 一致）。
- ⚠️ `"Running"` 是**内存事实**，只在这里和 `/sessions`、`/state` 出现，盘上永远不写。

**`session.step.end`**
- `refused`：这一步模型是否拒答（`stopReason` 由上游给）。
- ⚠️ 字段名是 `refused`，**不是**「有没有工具调用」。

**`spec.changed`**
- `model`：`{ providerId, modelId, protocol, effort, maxTokens }`（只带**会变的那一块**）。
- `systemPromptPreset`：可空。**今天恒等于原值**（没有改预设的命令）。

**`approval.requested`**
- `askKind`：`"permission"` / `"question"`（**小写**，camelCase）。
- `items`：`AskItem[]`，形状见 §2.7。
- `remember`：`["args","tool"]` 的子集，**空数组 = 这张卡不给记忆选项**。
- `deadlineAt`：可空 = 不超时。
- ⚠️ 这条事件**不会在重启后补发**：问题原文住在内存里，进程死过一次就没有生产者了。
  重启后要靠 `GET /sessions` 的 `ask` 字段看到「哪个会话在等」。

**`approval.resolved`**
- `outcome` 取值：`"submit"` / `"cancel"` / `"modify"` / **`"superseded"`**。
  - `"superseded"` = 用户没答，直接发了新消息把这次询问顶替掉了。
  - ⚠️ **`"expired"` 今天发不出来**：超时由收尾那条路处理，它发的是 `session.reclaimed`。
    客户端**不要**等 `expired`——按 `deadlineAt` 自己倒计时。

**`release.recorded`**
- `id`：那条线在记录里的稳定 id。
- `scope`：字符串数组（路径）。
- `summary`：可空。

**`tool.result.appended`**
- `id`：这条结果在 `history.jsonl` 里的**记录 id**；`callId`：配对的协议 id。
- `blocks`：结果的内容块（与 `/history` 里同一条的现有形态一致——可能是回收注记）。
- ⚠️ **一条结果一条事件，不打包**。头按 `callId` 挂到前面那条 `toolUse` 下面，按 `id` 去重。
- ⚠️ 它和 `message.appended` 是两件事：模型消息走后者，工具结果走这一条。之前只有前者，
  所以实时流里工具结果看起来「没有下文」，只有重拉 `/history` 才出现。

**`usage.updated`**
- `call`：本次调用的 `UsageCall`（见 §3.14 的字段）。
- `summary`：该会话的 `UsageSummary`（累计值）。
- 每条会话流事件都可能来；只在这条会话的账本**真的多了**一条时发（`PublishLatestUsage`）。

**`session.compaction.completed`**
- `checkpointId`：压缩检查点的稳定 id。
- `coveredThroughEventId`：这条检查点覆盖到的记录 id（此前的记录在后续渲染中被摘要替代）。
- `summaryTokens`：摘要那次模型调用报的 `outputTokens`，可空（没报就是 `null` ⇒ 键不存在）。

**`reset` / 重连**
- 收到 `resync_required` ⇒ 该 topic 的旧订阅已经失聪。拉一次快照，再重新 `subscribe`。
- 头今天的做法（`src/Lilara.Head/wwwroot/app.js:1462-1470`）可参考：重拉会话列表 + 重新订阅项目流。

### 2.6 块（`blocks`）

`Block` 是多态类型，判别式是 `kind`（`Kernel/Block.cs:14-25`）。

```jsonc
{ "kind": "text",    "text": "你好" }

{ "kind": "reasoning", "signature": "abc…", "text": "思考内容", "reasoningKind": "text" }
//   reasoningKind: "text" | "redacted"
//   signature 可空（本机网关的模型就不给）
//   reasoningKind = "redacted" 时 text 是加密载荷的 base64，**不要**当正文显示

{ "kind": "toolUse", "callId": "toolu_01…", "name": "read", "arguments": "{\"path\":\"a.txt\"}" }
//   ⚠️ arguments 是**原文 JSON 字符串**，不是对象。不要解析再拼回去。

{ "kind": "image",   "sha256": "…", "mediaType": "image/png" }
{ "kind": "store",   "sha256": "…", "mediaType": "application/vnd.lilara.blocks+json" }
```

- `image` / `store` 的字节**不在线上**：`GET /store/{sha256}` 今天**不存在**（`03` §9.4 未实现）。
  ⚠️ 头目前只能按「库里有个对象」显示，显示不了图。
- `store` 的 `mediaType` 若是 `application/vnd.lilara.blocks+json`，内容是一串块；否则是原始文本。

### 2.7 询问项（`AskItem`）

`approval.requested.items` 的元素，也是 `GET /state` 的 `ask.items` 的元素
（`Kernel/Ask.cs:96-180`）。

```jsonc
{
  "id": "q1",                 // 稳定 id，回答时用它
  "shape": "choice",          // "choice" | "text" | "form" | "verbatim"
  "question": "用哪个布局？",   // 给人看的那句话
  "options": [                // shape=choice 才有
    { "value": "a", "label": "方案 A" }
  ],
  "value": null,              // shape=text 才有（初始值，可空）
  "fields": [                 // shape=form / verbatim 才有
    { "name": "path", "label": "路径", "value": "a.txt" }
  ],
  "multi": false              // shape=choice 才有：能否多选
}
```

- ⚠️ **空集合不省略**：`options: []` / `fields: []` 会出现在所有形状上。
  客户端按 `shape` 选渲染，不要按「有没有 `options`」判断。
- `shape=verbatim` 时 `fields` 恰有一项且 `name` 是 `"arguments"`，`value` 是模型给的参数原文。
  用户改完的东西会**逐字**成为执行用的参数。
- 同一信封里 `id` 唯一。

## 3. HTTP 端点

⚠️ **只有 `GET`**。没有任何写端点（写全部走 WS 命令；附件上传 `POST /files` 未实现）。

所有响应 `Content-Type: application/json`，字段 camelCase，`null` 值的键**整个省略**。

### 3.1 `GET /health`

自检用：核心跑在哪个数据根、哪个工作目录、几个会话在内存里。

```jsonc
{ "ok": true, "dataRoot": "C:\\Users\\x\\.lilara", "workingDirectory": "E:\\proj", "sessions": 2 }
```

- `sessions` 是**在内存里的**会话数（`CoreHost.Activated`），不是盘上的总数。
- ⚠️ `dataRoot` / `workingDirectory` 是本机绝对路径。CORS 是 `*`，任何本机网页都读得到。
  这是接受过的取舍（核心只绑本机）。
- 证据：`ControlServer.cs:98-103`。

### 3.2 `GET /info`

```jsonc
{
  "dataRoot": "…",
  "workingDirectory": "…",
  "coreVersion": "1.0.0.0",
  "allowedOrigin": "http://127.0.0.1:17990",
  "endpoints": { "http": "http://127.0.0.1:17988", "ws": "ws://127.0.0.1:17988/ws" }
}
```

- `allowedOrigin` 是**生效值**，不是默认常量。
- WS 握手被拒的浏览器页面看不到 403 响应体 ⇒ 头部应该拉这个端点、跟 `location.origin` 比对后显示原因。
- 证据：`ControlServer.cs:111-120`。

### 3.3 `GET /projects`

```jsonc
["3f2a9c1d0e4b5a67", "8c1d…"]
```

16 位小写 hex，`<数据根>/projects/` 下的**每一个目录**（不要求有会话）。
顺序：按 pk 的 **ordinal 升序**（`SessionStore.ListProjects`，`Storage/SessionStore.cs:50-58`）。
证据：`ControlServer.cs:122`。

### 3.4 `GET /directories?path=<绝对路径>`

在**核心所在的设备**上浏览目录（头只能回传核心给的路径，不能读浏览器本机目录）。

```jsonc
{
  "root": "E:\\",              // 当前路径所在的根；盘符列表那一路是 null
  "current": "E:\\Workspace",  // 当前目录；盘符列表那一路是 null
  "parent": "E:\\",            // 父目录；到根为 null
  "directories": [
    { "name": "AgentLilaraCode", "path": "E:\\Workspace\\AgentLilaraCode" }
  ],
  "error": null                // 目录不可读时是原因串，否则 null（注意：是 null，不是省略）
}
```

- **不给 `path`**：Windows 回**可用盘符列表**（`root` / `current` / `parent` 都是 `null`）；
  其他平台从当前目录的根开始。
- 只列**直接子目录**，不递归；跳过 reparse point；按名称 ordinal 排序。
- 不创建任何东西、不激活会话。
- 路径不合法 / 目录不存在 ⇒ **400** `{ "error": "…" }`（⚠️ 这两个错误体的键是小写 `error`）。
  - ⚠️ 目录**存在但不可读** ⇒ **200**，带 `error` 一句原因、`directories: []`。
- ⚠️ `error` 在成功响应里**显式是 `null`**（匿名类型里写了 `(string?)null`），不会因
  `WhenWritingNull` 被省略——客户端判断「有没有错误」要按 `error` 的值，不是按键在不在。
- 证据：`ControlServer.cs:126-189`。

### 3.5 `GET /projects/{pk}/sessions`

```jsonc
["9eac068ae472", "c0ffee123456"]
```

某个项目下的会话 id 列表（`SessionStore.ListSessions`）。**包含归档会话**。
排序：`createdAt` **降序**（最新的在前），同一时刻按 id 升序（`SessionStore.cs:161-173`）。
不存在的 pk ⇒ `[]`（不报 404）。
⚠️ `spec.json` 读不出来的会话**不列**（版本太新或损坏都算）。
证据：`ControlServer.cs:191-192`。

### 3.6 `GET /sessions`

**一次拿全**所有会话的运行状态（`ControlServer.cs:204-281`）。

```jsonc
[
  {
    "sessionId": "9eac068ae472",
    "projectKey": "3f2a…",
    "active": true,             // 在内存激活表里
    "status": "Running",        // "Idle" | "Running" | "WaitingForInput"
    "turn": 3,
    "title": "重构",             // 可空 ⇒ 键可能整个不存在
    "ask": {                    // 没在等的时候是 {}
      "askId": "…",
      "toolName": "write",
      "kind": "permission",
      "deadlineAt": "2026-10-18T12:00:00+08:00"
    }
  }
]
```

- ⚠️ **「没在等」的 `ask` 是 `{}`，不是 `null`、也不是省略**。判据是 `row.ask?.askId`——
  `{}` 在 JS 里是 truthy，`if (row.ask)` 会误判。
- ⚠️ `title` 未起名时**键不存在**（`WhenWritingNull`）。判据是 `row.title`。
- `status` 与 `active` 是**两件事**：`active` = 载入内存了；`running` 是 `status`。
  今天两者恰好同延（队列随内存一起丢），但别当同一个概念。
- ⚠️ **这个端点绝不激活会话**（与 `/state` 不同）。读它不会把会话载入内存、不会触发回收。
- 顺序：项目序 × 项目内会话序，由既有的 `ListProjects` / `ListSessions` 定。
- 坏 `state.json` 的会话**跳过**（不 500）。坏 `spec.json` 的会话**列不出来**。
- 证据：`ControlServer.cs:204-281`；键集守卫 `ControlServerTests.Snapshot_Sessions_CarriesExactlyTheseKeys`。

### 3.7 `GET /sessions/{id}/state`

会话的整体快照。**会激活会话**（不在内存里的会被载入并登记）。

```jsonc
{
  "sessionId": "9eac068ae472",
  "state": {                    // SessionState，见下面
    "version": 7,
    "sessionId": "…",
    "status": "Idle",
    "turn": 2,
    "pending": [],
    "axes": { "initiative": "step", "permission": "standard", … },
    "pluginAxes": { "style.detail": "brief" },   // 可空 ⇒ 键可能不存在
    "ask": { "askId": "…", "kind": "permission", "callId": "toolu_…", "toolName": "write",
             "argsHash": "…", "deadlineAt": null },   // 可空
    "pendingIntent": { "turn": 2, "callId": "toolu_…" } // 可空，崩溃恢复用的痕迹
  },
  "highWater": 17,
  "axes": { "initiative": "step", …, "style.detail": "brief" },  // 核心轴 + 插件轴合并后的**当前值**
  "axisDefinitions": [ /* 见下 */ ],
  "pluginProblems": ["插件 'style' 版本不一致：绑定 1，现有 2"],  // 无问题时 []
  "spec": {                     // SessionSpec，见下面
    "version": 7,
    "sessionId": "…",
    "projectKey": "…",
    "workingDirectory": "E:\\proj",
    "createdAt": "2026-10-01T09:00:00+08:00",
    "model": { "providerId": "local", "modelId": "deepseek-flash",
               "protocol": "claude-messages", "effort": "high", "maxTokens": 16000 },
    "title": "重构",              // 可空
    "archivedAt": null,          // 未归档 ⇒ 键不存在
    "systemPromptPreset": "task", // 可空
    "premierPrompt": null,        // 今天恒为 null ⇒ 键不存在
    "askTimeout": "00:30:00",     // 可空；.NET TimeSpan 格式
    "plugins": [ { "id": "style", "version": "1", "defaults": { "style.detail": "brief" } } ]
  },
  "slots": {
    "problem": null,            // 插件贡献收集失败时的原因；否则 null
    "system":   [ /* Block[] */ ],
    "premier":  [ /* Block[] */ ],
    "floating": [ /* Block[] */ ]
  },
  "ask": {                      // 有询问在等时非 null，否则键不存在
    "askId": "…",
    "kind": "permission",       // 小写
    "toolName": "write",
    "deadlineAt": "…",          // 可空
    "items": [ /* AskItem[] */ ],
    "remember": ["args","tool"]
  }
}
```

**`axisDefinitions` 的元素**（`Session/PluginSessionView.cs:15-33`）：

```jsonc
{ "name": "permission", "label": "权限", "values": ["off","readonly",…],
  "inContext": true, "range": null }          // 核心轴

{ "name": "style.detail", "label": "详细程度", "values": ["brief","detailed"],
  "inContext": true, "available": true, "problem": null, "pluginId": "style" }  // 插件轴
```

- **核心轴的元素没有 `available` / `problem` / `pluginId`**（那个匿名类型没有这几格）。
  客户端不能假定所有元素同形状。
- `range` 是数值范围轴才有：`{ "min": 0, "max": 2, "step": 0.1 }`。
  ⚠️ **有 `range` 时 `values` 仍然是那张网格**（21 个字符串），不认识 `range` 的客户端画下拉照样可用。
- `values` 的**顺序是契约**（权限轴按阶梯排，头据此算「授权及以上」）。

**`slots`**

- `system` / `premier` / `floating` 都是块数组，**可能是 `[]`**。
  空数组是**一条事实**（「这个会话没配过那一块」），不是「核心没实现」。
- 三个槽与**发往模型的请求走同一个来源**（`SystemBlock.Render`）⇒ 头显示的就是模型收到的。
- ⚠️ 线上键名是 `floating`，代码里叫 `Tail`（同一个东西）。
- `floating` 是「此刻的浮动上下文」，**不在历史里**——`/history` 里找不到它。
- 这些格子的值随**文件 / 轴当前值**变；模型请求那边是**步开始冻结**的。
  用户在步中途改预设文件时，`/state` 会先变、请求里要等下一步。

⚠️ **`/state` 的读有副作用**：不在内存的会话会被**激活**（载入 + 登记）。
大批量轮询所有会话时用 `GET /sessions`，别用这个。

证据：`ControlServer.cs:284-370`；键集守卫 `Snapshot_State_CarriesExactlyTheseKeys`
（⚠️ 它钉顶层 9 个键、`slots` 的 3 个子键、`spec.model` 的 5 个子键，**不钉** `state` 与 `spec` 自身——
它们由 `SessionSpec_HasExactlyTheseProperties` 一族单独钉）。

### 3.8 `GET /sessions/{id}/history?limit=50&before=<cursor>`

会话的历史**渲染形态**（核心渲染、头只显示）。

```jsonc
{
  "highWater": 42,
  "rendered": [ /* 历史项，见下 */ ],
  "hasMore": true,
  "nextCursor": "MS45ZWFj…"      // 没有更早的了 ⇒ ""（空串，不是 null）
}
```

**历史项**（`ControlServer.cs:600-630` 的 `Render`），判别式 `kind`：

```jsonc
// 一条消息
{ "kind": "message", "id": "h1a2…", "from": "user", "at": "…", "blocks": [ /* Block[] */ ] }

// 一条工具结果
{ "kind": "toolResult", "id": "h3c4…", "callId": "toolu_…", "at": "…", "blocks": [ /* Block[] */ ] }

// 一条线（模型侧 release）
{ "kind": "release", "id": "h5e6…", "at": "…", "scope": ["src/a.cs"], "summary": "这批看完了" }
```

- **分页**：缺省取最新 `50` 条；`limit` 允许 `1..200`，越界 ⇒ **400** `{ "error": "…" }`。
  - `before` 取**更旧的**一页。响应按时间**正序**；要往时间线顶部插入。
  - `hasMore` = 还有更旧的；`nextCursor` 是拿给它下一次 `before=` 的游标。
  - 游标由核心生成、**不得解析或拼接**。它带会话 id：拿别会话的游标 ⇒ 400。
  - 已经在更旧方向到头了再说 `before` ⇒ 400「历史游标已失效」。
- **`highWater` 是 WS 游标**，`nextCursor` 是分页游标。别混。
- **分页发生在**库解引用、回收注记替换、framing、内部事件过滤**之后** ⇒ 页边界是「渲染后的项」，
  不是 `history.jsonl` 的行。
- ⚠️ **分页单位是「组」，不是单条**（2026-10-17）：核心把「模型消息 + 它发出的那些结果」
  当成一组交给头（`GroupToolResults`）。一条调用与它的结果**不会被切到两页**
  （切了头就配不上）。组内次序仍是「消息在前、结果按出现序在后」。
  `rendered` 仍是**扁平数组**；分页只是按组切，客户端仍按 `callId` / `id` 自行组装。
  `nextCursor` 指向组的第一条。
- ⚠️ **`release` 项**只出现在这里，不进模型上下文。
- ⚠️ 工具结果里 `blocks` 的 `store` 块只有地址，**没有内容**。取内容没有端点（见 §2.6）。
- ⚠️ 用户能看到的历史是**不带点杀**的回收形态（`SessionQueries.HistoryOf` 的注释记着这条差额）。
- 不存在的会话 ⇒ **404**（无响应体约定）。
- ⚠️ `spec.json` **版本比本核心新**的会话会**抛异常**（不是 404）：
  `SessionStore.FindSession` 刻意让 `FileFormatException` 冒上去——
  回一句「不存在」会把用户引向「删掉重来」，而那正是唯一会丢数据的做法（`SessionStore.cs:190-214`）。
  客户端应把 5xx 当成「这个核心版本太旧，读不了那个会话」，而不是「会话没了」。
- 证据：`ControlServer.cs:379-424`。

### 3.9 `GET /sessions/{id}/queue`

```jsonc
{ "isRunning": true, "queueDepth": 0 }
```

- `isRunning` = 有一步在跑；`queueDepth` = 待推进的用户消息条数。
- ⚠️ 这两个是**内存事实**：不在内存的会话拿到的 handle 是磁盘载入的，队列恒 0。
- **会激活会话**（走 `TryFind`）。
- 证据：`ControlServer.cs:426-430`。

### 3.10 `GET /axes`

核心的**轴注册表**（头唯一的轴来源，不许手抄）。

```jsonc
{
  "axes": [
    { "name": "initiative", "label": "主动性", "values": ["step","standard","goal"],
      "inContext": true, "range": null }
  ],
  "alertSafety": "prod",
  "alertFrom": "authorized"
}
```

- 这里是**全局**注册表（核心轴 + 当前注册的全部插件轴），没有会话绑定信息。
  **会话级的轴清单**用 `/state` 的 `axisDefinitions`。
- `alertSafety` / `alertFrom`：跨轴告警的两端（安全轴取到 `prod` 且权限轴 `>= authorized` 时告警）。
- `axes` 的元素与 `/state` 的 `axisDefinitions` 里的**核心轴**元素**同形**：
  `{ name, label, values, inContext, range }`。
  插件轴在这里**也是这个形状**（没有 `available` / `problem` / `pluginId`——那三格只在
  `/state` 的会话级清单里，见 §3.7）。
- 证据：`ControlServer.cs:439-445`。

### 3.11 `GET /claims`

「谁在动哪些文件」（声明）。

```jsonc
[
  { "sessionId": "9eac…", "projectKey": "3f2a…", "path": "src/a.cs",
    "at": "2026-10-01T09:00:00+08:00", "stale": false }
]
```

- `path` 是**规范化后的一段文本**（按会话工作目录解析），不是树节点。
- `stale: true` = 主人**已经不存在**（会话目录没了）。核心**不自动清理**，只如实显示。
- 没有声明 ⇒ `[]`。
- ⚠️ **按工作目录去读** `claims.json`，而工作目录只有 `spec.json` 里有 ⇒
  **`spec.json` 损坏的会话，它自己那份声明列不出来**。
- 证据：`ControlServer.cs:449-482`。

### 3.12 `GET /providers`

```jsonc
[
  { "id": "local", "protocol": "claude-messages", "defaultModel": "deepseek-flash",
    "endpoint": "http://127.0.0.1:15721", "authScheme": "bearer", "modality": "vision",
    "models": ["deepseek-flash", "deepseek-pro"], "hasApiKey": true }
]
```

- **永远不含 `apiKey`**。`hasApiKey` 只说「有没有」。
- 可空字段（`endpoint` / `authScheme` / `models`）**没设时键不存在**。
- `models` 存在 = 供应商声明了可选模型（头画下拉）；缺 = 没声明（头退回文本框）。
- 证据：`ControlServer.cs:483-496`；键集守卫 `Snapshot_Providers_CarriesExactlyTheseKeys`
  与 `Snapshot_Providers_OmitsNullFields`。

### 3.13 `GET /provider-schema`

头渲染配置表单的**真相来源**。

```jsonc
{
  "protocols": ["claude-messages", "openai-chat", "openai-responses"],
  "authSchemes": ["api-key", "bearer"],
  "defaultAuthScheme": "api-key",
  "providerIdRule": "提供商 id …（一句人话）"
}
```

- `authSchemes` 的顺序即下拉的显示序。
- `providerIdRule` 与 `provider.upsert` / `.delete` 的真实闸共用一份文案
  （`CommandDispatcher.ProviderIdRule`）。
- 证据：`ControlServer.cs:497-510`。

### 3.14 `GET /usage/calls?skip=0&limit=100`

核心的模型调用账本（token 用量；**费用不在核心**）。

```jsonc
{
  "asOf": "2026-10-18T09:00:00+08:00",
  "skip": 0,
  "limit": 100,
  "total": 37,
  "calls": [ /* UsageCall[]，见下 */ ]
}
```

- `skip` 缺省 `0`（负数按 `0`）；`limit` 缺省 `100`，**夹在 `1..1000`**（越界不报错，夹到边界）。
- `calls` 顺序 = `usage.jsonl` 的**文件序**（追加序，`UsageStore.ReadAll`）；`total` 是全部条数。

**`UsageCall`**（`Storage/UsageStore.cs`）：

```jsonc
{
  "callId": "…",
  "projectKey": "…",              // 可空
  "sessionId": "…",               // 可空（provider 探针没有）
  "step": 3,                      // 可空
  "purpose": "session",           // ⚠️ 今天只有三个字面量："session" | "probe" | "compaction"
  "providerId": "local",
  "modelId": "deepseek-flash",
  "protocol": "openai-chat",
  "startedAt": "…",
  "endedAt": "…",                 // 可空（进行中 / 崩了）
  "status": "completed",          // ⚠️ 今天只有 "completed" | "failed"
  "usage": {                      // 可空（上游没报用量）
    // ⚠️ 口径（`08` §3.1）：inputTokens 是**未命中输入**，与 cacheReadTokens **不相交**；
    //    两族协议在映射层已归一（OpenAI 的 prompt_tokens 含缓存 ⇒ 减去命中）。
    //    全部输入 = inputTokens + cacheReadTokens（cacheWriteTokens 是「这次新写进缓存的前缀」，不算复用）。
    "inputTokens": 411, "outputTokens": 300, "totalTokens": 3945,
    "cacheReadTokens": 3456, "cacheWriteTokens": 0, "reasoningTokens": 0,
    "rawJson": "…"                // 可空：受限的原始 usage 摘要
  },
  "formatVersion": 8,             // 写盘时的格式版本；缺这个键 = 格式 8 之前的行（旧口径）
  "promptTokens": 3867,           // 派生：inputTokens + cacheReadTokens；任一未知 ⇒ 键省略
  "cacheHitRate": 0.8937,         // 派生：cacheReadTokens / promptTokens；分母未知或为 0 ⇒ 键省略
  "reportedModel": "…",           // 可空：上游自报的模型名
  "responseId": "…",              // 可空
  "requestFingerprint": "…"       // 可空
}
```

- ⚠️ `usage` 为 `null` = **上游没报**，不是「0」。客户端不要把它当 0 累加。
- ⚠️ **`promptTokens` / `cacheHitRate` 是派生投影**，落盘也会带，但读回时被忽略、每次从 `usage` 现算
  （盘上那两行不是第二份真相）。
- ⚠️ **格式 8 之前的行**（无 `formatVersion`）：OpenAI 系的 `inputTokens` 是上游原值（**含**缓存），
  服务端读盘时已按 `NormalizeLegacyInput` 减掉命中再交出去——下面三条端点交的都是归一后的值。
- 证据：`ControlServer.cs` 的 `/usage/*` 三处与 `Normalized`（文件尾部）。

### 3.15 `GET /usage/summary`

全部调用的 token 总览（**没有过滤参数**——按会话汇总用 §3.16）。

```jsonc
{
  "calls": 37,
  "callsWithKnownInput": 35,
  "callsWithKnownOutput": 35,
  "callsWithUnknownUsage": 2,
  "inputTokens": 41000,            // 未命中输入
  "outputTokens": 9000,
  "cacheReadTokens": 12000,
  "cacheWriteTokens": 3000,
  "reasoningTokens": 4000,
  "callsWithKnownCache": 35,       // 参与命中率的调用数（分母已知的那些）
  "cacheHitTokens": 12000,         // 只累加分母已知的调用
  "promptTokens": 53000,           // 这些调用的全部输入 = 41000 + 12000
  "cacheHitRate": 0.2264,          // 12000 / 53000
  "byProvider": [ { "key": "local", "calls": 37, "callsWithUnknownUsage": 2,
                    "inputTokens": 41000, "outputTokens": 9000,
                    "cacheReadTokens": 12000, "cacheWriteTokens": 3000, "reasoningTokens": 4000,
                    "callsWithKnownCache": 35, "cacheHitTokens": 12000,
                    "promptTokens": 53000, "cacheHitRate": 0.2264 } ],
  "byModel":    [ /* 同形状，key = modelId */ ]
}
```

- ⚠️ **汇总的数字是可空的**（`long?`）：一个 token 都没报过 ⇒ `null`，不是 `0`。
- ⚠️ **命中率的分母只收「分母已知」的调用**：没报缓存的调用若被算进分母，命中率会被它拉低——
  那看起来像「缓存变差了」，其实是「这笔没报」。未报告的量由 `callsWithKnownCache` 对 `calls` 的差体现。
  没有任何一笔分母已知 ⇒ `cacheHitRate` 键省略（未报告，不是 0%）。
- ⚠️ **命中率按调用+按桶各算**，不是从总量倒推（总量口径下，一笔没报缓存的调用会稀释结果）。
- `byProvider` / `byModel` 按 key 的 **ordinal 升序**。
- 证据：`ControlServer.cs` 的 `/usage/summary`；`Storage/UsageStore.cs` 的 `UsageSummary.From` / `UsageBucket.From`。

### 3.16 `GET /sessions/{id}/usage`

```jsonc
{ "sessionId": "…", "projectKey": "…", "summary": { /* UsageSummary，同 §3.15 */ },
  "calls": [ /* 该会话的 UsageCall[]，按 startedAt 升序 */ ] }
```

- 不存在 ⇒ **404**。
- ⚠️ 它**不激活会话**（走 `Store.FindSession`，不是 `TryFind`）⇒ 没有副作用。
- 证据：`ControlServer.cs` 的 `/sessions/{id}/usage`。

### 3.17 `GET /sessions/{id}/context`

**最近一次实际请求的输入 token**（上下文长度的事实来源）。

```jsonc
{
  "sessionId": "…",
  "projectKey": "…",
  "latest": {                       // 没有任何一次**报了输入**的调用 ⇒ null（键被省略）
    "callId": "…",
    "startedAt": "…", "endedAt": "…",
    "modelId": "deepseek-flash",
    "protocol": "claude-messages",
    "inputTokens": 1200,
    "source": "reported",           // 能走到这里就一定是上报的（没报输入的那次不会入选）
    "requestFingerprint": "…"       // 可空
  },
  "preview": null                   // 本地 tokenizer 预估；**今天恒为 null**
}
```

- ⚠️ `preview` 是显式 `null`（键**存在**，值为 `null`），不是省略——两者在线上可分辨。
  今天它恒为 `null`：预估未实现。
- ⚠️ `latest` 只取**报了输入**（`usage.inputTokens`）的最近一条——不是「带了 usage」：
  一次失败的尝试可以只报输出、没报输入，挑到它会把「上下文长度」显示成未报告，
  那比一条旧值更糟（看起来像上下文没了）。`source` 因此今天恒是 `"reported"`
  （`"unknown"` 那条分支实际够不着，留着是给将来）。
- 不存在 ⇒ **404**；不激活会话。
- 证据：`ControlServer.cs` 的 `/sessions/{id}/context`。

### 3.18 不存在的端点

| 路径 | 状态 |
|---|---|
| `GET /sessions/{id}/tree` | ❌ 没有子会话 |
| `GET /store/{sha256}` | ❌ 没有库的读端点（图 / 附件显示不了） |
| `POST /files` | ❌ 附件上传未实现 |
| `GET /approvals` | ❌ 审批走 WS 的 `approvals` 流，没有 HTTP 端点 |
| `GET /providers/{id}` | ❌ 只有列表 |

## 4. WS 命令

⚠️ 一共 **19** 条（`ControlServer.ParseCommand` 的 `kind`，`ControlServer.cs:856-946`）。
**对照 `03` §3 那张设计全集**：`project.setConfig` · `tool.setEnabled` · `session.continue` 不存在；
`claim.acquire` 有 dispatcher 与控制台解析，**线上没有**（刻意的，用户裁定）。

命令的统一形状：

```jsonc
{ "kind": "command", "id": "c1", "payload": { "kind": "session.send", "sessionId": "s1", "blocks": [] } }
```

### 4.1 通用约定

- **必给键缺失** ⇒ `ack{accepted:false, reason:"命令形状不对：…"}`（`ParseCommand` 抛异常）。
- **只认 JSON 的正确类型**：该是字符串的字段给数字、该是布尔的给字符串 `"false"`，
  一律按「没给」处理（`ControlServer.cs:966-980` 的 `Opt` / `OptBool` / `OptNumber`）。
  ⚠️ 这不是宽容，是刻意：`session.archive` 的 `"false"` 会被当成「没给」⇒ **归档**。
- **`rejected`（`accepted:false`）只用于命令不合法**：会话不存在、id 不合法、参数自相矛盾。
  业务失败走 `error` 事件。
- **回执立刻到，效果在后台**：`session.send` / `session.start` 返回后，模型调用在后台跑，
  结果看事件。唯一例外是 `provider.test`（它在返回回执之前同步跑完，最多 10 秒）。
- 每个命令的 **id 由客户端生成**，核心原样带回。同一个 id 发两次 = 两条命令。

### 4.2 命令清单

#### `session.create`

```jsonc
{ "kind": "session.create", "cwd": "E:\\proj", "title": "重构", "preset": "task", "askTimeoutMinutes": 30 }
```

| 键 | 必给 | 语义 |
|---|---|---|
| `cwd` | 否 | 工作目录（绝对或相对核心的工作目录）。决定会话属于哪个项目。**不存在 ⇒ rejected** |
| `title` | 否 | 会话名。只在这里写一次，**此后没有改名命令** |
| `preset` | 否 | 系统提示词预设 id。保存的是**引用**；文件不存在不报错（渲染回落内置模板） |
| `askTimeoutMinutes` | 否 | 询问最多等多少**分钟**，必须 > 0；不给 = 不超时 |

- 成功回执带 `sessionId`；`note` 是新会话的 `projectKey`。
- 没有提供商 ⇒ rejected「先配提供商」。
- ⚠️ 本命令**没有** `projectKey` 参数（键推不回目录）。
- 证据：`ControlServer.cs:973-984`；`CommandDispatcher.cs:577-625`。

#### `session.activate`

```jsonc
{ "kind": "session.activate", "sessionId": "s1" }
```

把会话载入内存。成功 `note` 是 `null`；回执带 `sessionId`。
证据：`CommandDispatcher.cs:636-644`。

#### `session.deactivate`

```jsonc
{ "kind": "session.deactivate", "sessionId": "s1" }
```

把会话**摘出内存**，磁盘一字不动。**在跑 ⇒ rejected**。
拒绝理由分两种：「不存在」与「本来就没激活」（别并成一句——用户要能分辨是敲错了 id 还是砍在空气上）。
证据：`ControlServer.cs:895-896`；`CommandDispatcher.cs:1268-1295`。

#### `session.archive`

```jsonc
{ "kind": "session.archive", "sessionId": "s1" }               // 归档
{ "kind": "session.archive", "sessionId": "s1", "archived": false }  // 取消归档
```

- `archived` 是**三态**：缺省 / `true` = 归档；`false` = 取消归档。
  ⚠️ **字符串 `"false"` 被当成「没给」⇒ 归档**。
- 已经是那个状态 ⇒ rejected（不是幂等成功）。
- **在跑 ⇒ rejected**。
- 证据：`ControlServer.cs:900-901`；`CommandDispatcher.cs:1298-1350`。

#### `session.delete`

```jsonc
{ "kind": "session.delete", "sessionId": "s1" }
```

**不可逆**：目录、记录、状态一起没了。**在跑 ⇒ rejected**。该会话的声明一并释放。
证据：`CommandDispatcher.cs:1370-1420`。

#### `session.start`

```jsonc
{ "kind": "session.start", "sessionId": "s1" }
```

**真的推进一步**（队列空也推）。这是「崩在步中」之后收回会话的**唯一**命令。
- 已在推进 ⇒ `note: "已经在推进了。"`
- 队列非空 ⇒ `note: "开始推进。"`
- 队列空 ⇒ `note: "没有待办，直接推进一步（继续）。"`
- 证据：`CommandDispatcher.cs:1055-1078`。

#### `session.send`

```jsonc
{ "kind": "session.send", "sessionId": "s1",
  "blocks": [ { "kind": "text", "text": "看看这个" } ] }
```

- `blocks` 必给，是块数组（§2.6）。
- 用户消息**立刻落盘**、立刻发 `message.appended`，然后（若没有推进在飞）在后台起一步。
- 撞上 `WaitingForInput` ⇒ **不拒绝**：先顶替那次询问（落一条「用户没回答，说了别的」的结果、
  发 `approval.resolved{outcome:"superseded"}`），再当普通消息处理。
- 证据：`ControlServer.cs:873-875`；`CommandDispatcher.cs:709-800`。

#### `session.interrupt`

```jsonc
{ "kind": "session.interrupt", "sessionId": "s1" }
```

停当前推进。不是失败（不发 `error`）；用户消息已落盘、`turn` 不递增。
空闲时是 **no-op，但照样回 `accepted:true`**（命令本身合法，语义上「停下」已经成立）。
证据：`CommandDispatcher.cs:1074-1100`。

#### `session.setAxis`

```jsonc
{ "kind": "session.setAxis", "sessionId": "s1", "axis": "permission", "value": "authorized" }
```

- 未知轴 / 非法取值 ⇒ rejected，`reason` 里列出可用值。
- 数值轴（`temperature`）走数值解析，落在网格外（如 `0.75`）⇒ rejected。
- 同值 ⇒ no-op，不发 `axis.changed`。
- 插件轴的值按**不区分大小写**匹配，保存声明里的规范拼写。
- 插件不可用 ⇒ rejected，`reason` 是插件问题。
- 证据：`ControlServer.cs:878-881`；`CommandDispatcher.cs:1105-1160`。

#### `session.configure`

```jsonc
{ "kind": "session.configure", "sessionId": "s1", "providerId": "local", "modelId": "deepseek-flash",
  "askTimeoutMinutes": 30 }
```

| 键 | 必给 | 语义 |
|---|---|---|
| `providerId` | 否 | 换绑供应商；省略 = 保留 |
| `modelId` | 否 | 换模型；省略 = 保留。⚠️ **键名是 `modelId`**，与 `03` §3 的签名、`/state` 的 `spec.model.modelId` 一致。（`provider.upsert` 的键仍叫 `model`——那是 provider 的 `defaultModel`，不是同一个字段。） |
| `askTimeoutMinutes` | 否 | 改询问超时，必须 > 0 |

- 三个都没给 ⇒ rejected。
- 供应商不存在 ⇒ rejected。
- 换模型时 `effort` / `maxTokens` 保留当前值；换供应商时 `protocol` **跟着供应商定义重取**。
- 真的变了才发 `spec.changed`；没变也回 `accepted:true`（`note: null`）。
- 成功 `note` 是 `"{providerId} / {modelId}"`。
- ⚠️ **没有「取消询问超时」的入口**（`askTimeoutMinutes` 只能设成正数，不能设回「不超时」）。
- 证据：`ControlServer.cs:1005-1011`；`CommandDispatcher.cs:1176-1265`。

#### `session.gc`

```jsonc
{ "kind": "session.gc", "sessionId": "s1" }
```

**开一次回收窗口**（2026-10-17 起）：解析一次判据（距离 + 点杀），落一条 `ReclaimEvent` 记录，
发 `session.reclaimed`，`note` 说清结果（「回收了 N 条结果」/「没有可回收的结果…」）。
⚠️ 渲染**不再每次现算**——没有窗口就不回收（那条路曾让每步都改写前缀、缓存连续失效）。
证据：`CommandDispatcher.cs:705-746`。

#### `session.compact`

```jsonc
{ "kind": "session.compact", "sessionId": "s1" }
```

把此前的上下文压成一条摘要检查点。完成时发 `session.compaction.completed`。

- 以下任一 ⇒ **rejected**：会话在跑 / 在等回答 / 队列非空（先推进再压）。
- 回执立刻到，压缩在后台跑；完成或失败都经事件（失败发 `error`）。
- 成功 `note` 是 `"已压缩至 {coveredThroughEventId}。"`。
- ⚠️ 压缩会**破前缀**（摘要替代了原来的中段）——这是它唯一的目的，也是它的代价。
- 证据：`ControlServer.cs:1027-1028`；`CommandDispatcher.cs:740-850`。

#### `preset.set` / `preset.list` / `preset.delete`

```jsonc
{ "kind": "preset.set", "id": "task", "content": "你是……" }     // 覆盖写
{ "kind": "preset.list" }                                        // note 里是 "a / b / c"
{ "kind": "preset.delete", "id": "task" }
```

- `preset.set` 的 `content` **必给**；空串是合法值（= 纯聊天）。
- 不存在预设 ⇒ `preset.delete` rejected。
- 结果都在 `note` 里（`preset.list` 就是那个下拉的数据源，`"a / b / c"` 一句；
  空表 ⇒ `"还没有预设。…"`，仍然是 `accepted:true`）。
- 证据：`ControlServer.cs:929-934`；`CommandDispatcher.cs:399-476`。

#### `claim.release`

```jsonc
{ "kind": "claim.release", "sessionId": "s1", "scope": ["src/a.cs"] }
```

强制释放声明（逃生口）。**不检查占用者是不是自己**，**不写时间线**。
⚠️ `scope` 是数组，逐个**规范化后逐字比较**，不展开子树。释放成功会发 `claim.released`。
证据：`ControlServer.cs:891-893`；`CommandDispatcher.cs:1512-1534`。

#### `provider.upsert`

```jsonc
{ "kind": "provider.upsert", "id": "local", "apiKey": "sk-…", "model": "deepseek-flash",
  "protocol": "claude-messages", "endpoint": "http://127.0.0.1:15721", "auth": "bearer",
  "models": ["deepseek-flash", "deepseek-pro"] }
```

| 键 | 必给 | 语义 |
|---|---|---|
| `id` | **是** | 提供商 id（也是文件名，有闸） |
| `apiKey` | 新 provider **必须**；已有 provider 省略 = 保留 | — |
| `model` | 否 | `DefaultModel`；已有 provider 省略 = 保留 |
| `protocol` | 否 | 见 `/provider-schema` 的 `protocols` |
| `endpoint` | 否 | 空 = 官方地址；省略 = 保留 |
| `auth` | 否 | `api-key` / `bearer`（**小写**）；省略 = 保留 |
| `models` | 否 | 字符串数组；省略 = 保留 |

- id 不合法（含路径语法、Windows 设备名）⇒ rejected。
- 未知协议 / 认证方式 ⇒ rejected。
- 新 provider 不给 key ⇒ rejected；已有 provider 省略 key ⇒ 保留。
- 成功 `note` 是**生效的协议名**。
- 证据：`ControlServer.cs:907-918`；`CommandDispatcher.cs:287-341`。

#### `provider.delete`

```jsonc
{ "kind": "provider.delete", "id": "local" }
```

**不会阻止删除被会话引用的提供商**。不存在 ⇒ rejected。证据：`CommandDispatcher.cs:361-385`。

#### `provider.test`

```jsonc
{ "kind": "provider.test", "id": "local" }
```

用该配置发一次最小请求（**同步**，最多 10 秒）。**失败也是 `accepted:true`**，
结果在 `note` 里：`✓ 通（model @ url（来源））` / `✗ 不通（url（来源））：原因`。
证据：`CommandDispatcher.cs:478-566`。

#### `approval.respond`

```jsonc
{ "kind": "approval.respond", "sessionId": "s1", "askId": "a1", "outcome": "submit",
  "answers": { "decision": "allow", "q2": "B", "arguments": "{…}" },
  "remember": "tool" }
```

| 键 | 必给 | 语义 |
|---|---|---|
| `sessionId` / `askId` / `outcome` | **是** | — |
| `outcome` | **是** | `"submit"` / `"cancel"` / `"modify"` |
| `answers` | 否 | 字符串 → 字符串的映射。`submit` 时允许缺项 |
| `remember` | 否 | `"args"` / `"tool"`；缺省 = 只这一次 |

- **授权（`askKind: "permission"`）批没批，只看 `answers.decision`**：
  `"allow"` / `"approve"` / `"yes"` 算批准（**小写逐字**），其余算拒绝。
- **批准 = 直接执行**（不再让模型重发）。用户改写的那份在 `answers.arguments` 里。
- **提问（`askKind: "question"`）** 的答案就是内容，`decision` 无意义。
- `outcome:"modify"` ⇒ 只落一句「改了但没批」的结果、**解除挂起**、**不续跑**。
- `outcome:"cancel"` ⇒ 作废这次询问，落拒绝结果，不续跑。
- 没有询问在等 / `askId` 对不上 ⇒ rejected。
- 已过 `deadlineAt` ⇒ 答案**不采用**，按超时处理。
- ⚠️ **`remember` 认 `"args"` / `"tool"`（小写，线上契约），也兼容 `"Args"` / `"Tool"`
  （大小写不敏感）**。认不出的值按「只这一次」处理——**不**拒绝整条命令（旧行为，方向安全）。
- `answers` 里的键除 `decision` / `arguments` 外，按**问题 id**（`items[].id`）取值；
  `submit` 时允许缺项，缺的会被写成「未回答」，不会编一个空值。
- 证据：`ControlServer.cs:1061-1076`；`CommandDispatcher.cs:1536-1752`。

## 5. 客户端校验清单

按这个顺序核，能挡住绝大多数「看着对、实际错」的接入：

1. `subscribe` 用的是 `{kind:"subscribe", topic, since}`，不是 `hello`。
2. 事件按 `payload.kind` 分派；`message.appended` 按 `historyId` 幂等，
   `tool.result.appended` 按 `id` 幂等。
3. `ack.accepted:true` 之后**仍然**要处理 `error` 事件。
4. `session.archive` 的 `archived` 发**真布尔**，别发字符串。
5. `session.configure` 的模型键是 **`modelId`**。
6. `approval.respond` 的授权判决放在 `answers.decision`（`"allow"`），
   `remember` 发 `"tool"` 就是工具级放开。
7. `GET /sessions` 的 `ask` 空值是 `{}`，判据是 `ask?.askId`；`title` 可能整个不存在。
8. `GET /directories` 的 `error` 成功时是显式 `null`，不是省略。
9. `GET /sessions/{id}/history` 的 `nextCursor` 到头时是 `""`；
   `GET /sessions/{id}/context` 的 `preview` 今天恒为 `null`（键在）。
10. 收到 `resync_required` ⇒ 拉快照、重新 `subscribe`。
11. 用量里的 `null` **不是 0**：`usage` / 汇总里的 token 可为 `null`（上游没报），别当零累加。

## 6. 与其它文档的差异（本文覆盖它们）

| 位置 | 写的 | 实际 |
|---|---|---|
| `03` §6 `/history`「分页未实现」、§11「零个 `Request.Query`」 | 无分页 | 有 `limit` / `before` / `hasMore` / `nextCursor`（§3.8） |
| `03` §6 端点清单 | 缺 4 条 usage / context | 实际共 **17** 个 `MapGet`（§3.14–§3.17 是新的四条） |
| `03` §4.2 事件载荷表 | 缺新事件 | 缺 `session.reclaimed`、`release.recorded`、`tool.result.appended`、`usage.updated`、`session.compaction.completed`，缺 `message.appended.historyId` |
| `03` §4「已上线」表 | 20 个事件名 | 20 个类型（`WireEvent.cs:8-28`）✅ 数对，但 §4.2 载荷表没跟上 |
| `03` §7「hello(lastSeq)」 | 有这个帧 | ❌ 不存在（§2.2） |
| `03` §8.1「控制台够不到 `session.start`」 | 旧 | ✅ 2026-10-12 已补 |
| `03` §314「审批这条流是空的」 | 空 | 同一页三行前刚说它有生产者；实际有（§2.3） |
| `03` §6 `/sessions/{id}/history` 注释 | 「把当前可渲染的全给我」 | 已分页（§3.8） |
| `实现现状.md` §五 ④「`/history` 无分页」 | 无分页 | 同第一条 |

## 7. 已知缺陷

> ⚠️ **2026-10-18：下面三条都已修复**（同一次提交里配了会红的守卫）。
> 留在这里是因为**线上契约变过**——升级之前写的客户端可能还按旧拼法发。

1. ~~**`session.create` 的 `preset` 在线上被静默丢弃**~~ ✅ **已修**：
   `ParseCommand` 的 `session.create` 臂补读 `preset`（`ControlServer.cs:973-984`）。
   守卫：`CommandSurfaceTests.WireLine_ReadsTheFieldsWhoseKeyNamesDifferFromThePropertyNames`。
   ⚠️ 缺陷曾经存在的原因值得记：`CommandSurfaceTests` 的 ② 只断**类型**，
   不断字段 ⇒「类型对、字段全默认」在它眼里完全合格。

2. ~~**`approval.respond` 的 `remember` 拼法与客户端不一致**~~ ✅ **已修**：
   `Remember` 从 `string?` 改成 `AskMemory?`，线上解析走枚举（大小写不敏感），
   控制台也改成枚举（`CoreCommandParser.cs`）。线的契约是 `"args"` / `"tool"`。
   守卫：`CommandSurfaceTests.WireLine_RememberAcceptsTheWireSpellingAndFallsBackToNull`
   + `AskTests.RememberTool_FromTheCommand_CoversAnyArgumentsOfThatTool`。
   ⚠️ 修复之前**头的「记住这个工具」从没生效过**（它把 `/state` 里的小写值原样发回，
   核心却比 `nameof(...)` 的 `"Tool"`），且全程无报错。

3. ~~**`session.configure` 的模型键名是 `model`，不是 `modelId`**~~ ✅ **已修**：
   线上读 `modelId`（`ControlServer.cs:1005-1011`），头也跟着改（`app.js` 的 `bind`）。
   守卫同上第一条。⚠️ `provider.upsert` 的键**仍然叫 `model`**（那是 `defaultModel`）——
   两条命令的键名不同是**有意的**，别为了「一致」把它改掉。

## 8. 这份文档怎么保持不过期

- **形状的权威仍是代码**：`WireEvent.cs`（事件）、`ControlServer.ParseCommand`（命令入参）、
  `ControlServer.MapSnapshots`（HTTP 响应）、`Kernel/Block.cs`（块）、`Kernel/Ask.cs`（询问项）。
- **已有守卫**：`WireFormatTests`（事件表 + 每个类型有生产者 + 若干载荷键）、
  `ControlServerTests.Snapshot_*_CarriesExactlyTheseKeys` 一族（端点键集）、
  `CommandSurfaceTests`（每条命令的**四条线接没接**——类型 / 线上 / 控制台 / 分隔符）。
- **缺的守卫**（这正是本文可能腐的地方，补上之后本文才能全身而退）：
  1. 每条命令的**必给键集**没有守卫（§7 #1、#3 就是从这里漏过去的）。
  2. `§2.5` 的**事件载荷表**没有键集守卫（只有 `AskKind` 拼法与少数几个字段）。
  3. `§3.7` 的 `axisDefinitions` 元素形状、`spec` / `state` 整份的键集没有守卫。
- 补法见 `03` §11 与 `实现现状.md` §五：键集守卫扩到「每个 kind / 每个事件类型 / 每个快照」，
  再让生成器从这些类型出 `openapi.json`（HTTP）与 `wire.schema.json`（WS）。
