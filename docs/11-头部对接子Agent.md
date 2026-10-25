# 头部对接子 agent

> 本文是给 `src/Lilara.Head` 实现者的控制面接线说明。它记录的是**当前核心已经提供的协议**与头部建议行为，不定义新的核心机制。
>
> 核心设计与状态记账见 [`01-会话设计.md` §10](01-会话设计.md) 和 [`实现现状.md`](实现现状.md) 的 2026-10-24 子 agent 批；控制面通用形状见 [`09-控制面API参考.md`](09-控制面API参考.md)。

## 1. 头部需要做什么

子 agent 是普通会话，带一条 `parentSessionId` 派生关系。头部要让用户：

1. 在会话列表中看出父子关系，并能打开任一子会话；
2. 看到子会话正在等待输入、正在推进或已经空闲；
3. 从父会话里的回流记录跳到对应子会话；
4. 像操作普通会话一样，直接向子发送消息、回答它的询问、启动或停止它；
5. 执行父会话的停止、归档、删除时，理解这些操作会级联停止子树，但子会话记录会保留。

头部仍然只是**快照读取者与命令源**：不要在浏览器端创建子会话、推导权限、复制记录或实现级联停止。这些行为归核心。

## 2. HTTP 快照

### 2.1 `GET /sessions`

它是会话列表和树形关系的主要数据源，一次返回全部项目中的会话，不激活会话。

```jsonc
[
  {
    "sessionId": "parent-id",
    "projectKey": "project-key",
    "active": true,
    "status": "Running",        // Idle | Running | WaitingForInput
    "turn": 4,
    "title": "主会话",            // 未命名时键不存在
    "parentSessionId": "",       // 顶层会话为空串
    "ask": {},                    // 无挂起询问时是空对象
    "goal": { "active": false }
  },
  {
    "sessionId": "child-id",
    "projectKey": "project-key",
    "active": true,
    "status": "Idle",
    "turn": 2,
    "title": "检索资料",
    "parentSessionId": "parent-id",
    "ask": {},
    "goal": { "active": false }
  }
]
```

头部应按 `parentSessionId` 构建树，并在同一项目列表中把子节点排在父节点之后。顶层的 `parentSessionId` 是空串；判断时用 `if (row.parentSessionId)` 即可。父会话已删除、子记录仍保留时，父 id 会指向一个不存在的列表项：把这个孤儿会话作为顶层显示，仍允许用户打开和操作。

`status` 是服务端状态；`active` 只表示会话已载入内存。不要根据 `active` 推断它是否正在运行。列表端点会跳过不可读取的 `state.json`，不会因单个坏文件让整张列表失败。

### 2.2 `GET /sessions/{id}/state`

快照的 `spec.parentSessionId` 同样是父 id（顶层序列化时该可空属性会省略）。`spec.depth` / `spec.maxDepth` 供诊断或显示深度使用；头部不应自行执行深度许可判定。

`slots.floating` 是核心渲染后的浮动块。它可能包含一行子会话摘要，例如：

```text
父会话 parent-id | 子会话 child-a 空闲 · child-b 等你回答
```

此行由核心生成，状态变更后才形成新的浮动读数。头部只显示它，不要再根据 `/sessions` 拼一份同义的提示词内容。

### 2.3 `GET /sessions/{id}/history`

工具调用按调用与结果分组，结果仍是扁平事件。父收到子回流时会看到一个名为 `subagent_report` 的合成工具调用与 `toolResult`：

- 调用参数的 `child` 是子会话 id；
- 结果的 `callId` 与调用块的 `callId` 配对；
- `toolResult.id` 是稳定记录 id；
- `toolResult.blocks` 是回报正文；内容太长时，核心只给开头并在注记中提供 `fetch(id, offset)` 取回地址。

头部应像呈现其他工具调用一样呈现它，并利用 `child` id 提供「打开子会话」入口。不要把合成调用当成用户消息，也不要据此假设父真的执行过一个子 agent 工具。

## 3. WS 事件

子会话创建会向其**项目流**发布 `session.created`；核心也向新会话流发布一份。头部通常靠项目流刷新会话列表，然后订阅新会话的 `session:<childId>` 流。

子会话向父回流时，父的会话流依次收到：

1. `message.appended`，`from: "model"`，其中带 `toolUse` 块；
2. `tool.result.appended`，带相同 `callId` 与结果块。

收到回流后，头部按普通模型工具调用更新当前历史；如果需要完整历史或分页状态，重拉父会话 history。子会话自己的普通事件（用户消息、模型消息、询问、工具结果、状态变化）仍发布在它自己的会话流上。

询问同时发布到 `approvals` 全局流和对应子会话流。头部要按 `approval.requested.sessionId` 把卡片路由到具体子会话；用户可以直接在子会话上回答。`Question` 类型询问也可能在父会话中形成一条 `subagent_report` 通知，但这不会移除子会话自己的审批卡。`Permission` 类型审批由用户处理，父 agent 不代批。

父子注入也会产生普通会话流事件：父向子说话时，子流收到 `message.appended`（`from: "user"`）与后续状态事件；子向父说话时，父流收到合成回流的 `message.appended` 与 `tool.result.appended`。

## 4. 命令：子会话仍是普通会话

对任意子会话，头部可以继续使用已有命令，不需要新增专用 API：

| 用户动作 | 命令 | 备注 |
|---|---|---|
| 打开子会话 | `session.activate`（若使用该命令）+ `GET /sessions/{id}/state` + history | 读取状态端点会激活会话；列表端点不会 |
| 直接发话 | `session.send` | 用户可以绕过父，直接干预子 |
| 手动推进 | `session.start` | 按普通会话语义启动 |
| 停止 | `session.interrupt` | 核心级联停止该会话的子树 |
| 回答询问 | `approval.respond` | `sessionId` 填正在等待的子会话 id |
| 归档 | `session.archive` | 归档前核心会级联停止子树 |
| 删除 | `session.delete` | 删除父之前级联停止子树；子目录与记录保留 |

要在父 agent 之间继续对话，模型通过内建工具 `tell_child(child, text)` 与 `tell_parent(text)` 反复注入。头部用户干预继续走 `session.send`。所有注入都是异步追加：发送方不会等待对方回复。

## 5. 左栏树形列表实现要点

- 用 `/sessions` 返回的全部行建 `id → row` 映射；按 `parentSessionId` 建直接孩子列表。
- 对每个顶层会话按现有列表顺序做稳定的深度优先展开，把子会话排在父会话下面；同父的孩子保留 `/sessions` 返回的顺序。
- 子行增加缩进或连接线；标题优先用 `title`，无标题时显示 id。悬停信息可加入父 id、深度和状态。
- 父节点可折叠，但不要因此过滤掉核心快照或阻止深层链接打开子会话。
- 父不在列表中时，将子当作根节点显示，保留「孤儿」信息供用户辨认；不要静默隐藏它。
- 归档过滤仍按现有策略执行。若父被折叠或过滤，需确保用户仍能通过项目列表或回流中的 child id 打开孩子。
- 状态徽标使用服务端 `status` / `ask`。顶层会话的 `parentSessionId` 为空串，子会话才有父 id。

当前 `app.js` 的 `loadSessions()` 已把 `parentSessionId` 保留在 `sessions` map 中；`renderSessions()` 已按树顺序重排并缩进。本文列出的是后续头部改动、测试与维护应遵循的契约。

## 6. 修改后验证

头部改动至少核对这些点：

1. 新会话仍出现在 `/sessions`；顶层字段为空串，派生会话字段等于父的 session id。
2. 左栏先父后子，父子都可点击打开；多层数据也按递归树形显示；孤儿作为顶层可见。
3. 父 `session.created` 后刷新列表，并能订阅子会话流；重新加载页面后关系仍从快照恢复。
4. 子的 `Question` 与 `Permission` 卡片都能从全局 approvals 流路由到正确的子会话，且直接打开子页面可提交回答。
5. 父工具调用实时出现后，子回流结果能按 `callId` 配对到 `subagent_report` 调用；长结果分页/`fetch(offset)` 注记照常显示。
6. 从父和子各自打开头部控制点，分别验证 `session.send` / `session.interrupt` / `session.archive` / `session.delete` 的现有 UI 能作用到目标会话；父停止/归档/删除后子仍可从列表或链接中打开，但不再推进。
7. 运行 `dotnet test`，尤其是 `HeadAssetsTests`、`HeadScriptTests`、`ControlServerTests` 与 `SubagentTests`。

## 7. 头部不应实现的逻辑

- 不在浏览器中创建子会话：创建由模型调用 `spawn`，核心写 spec、初始化状态、追加首条 task 并启动。
- 不根据父权限或 depth 自行裁定是否允许 spawn；核心只接受不越过父权限与树深度的子会话。
- 不把父的审批点击当成子 `Permission` 的批准；危险操作审批始终是用户的权利。
- 不把 `subagent_report` 转成虚构的用户消息；它在历史中是一对核心合成的工具调用与工具结果。
- 不依赖 `Note` 展示创建结果或回报正文；稳定来源是会话快照、WS 事件与 history。
