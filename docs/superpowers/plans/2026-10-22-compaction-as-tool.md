# 压缩改成一个工具（2026-10-22）

## 为什么

`session.compact` 今天的实现是核心自己跑一次模型调用（`Stepper.CompactAsync`），并且要求回复是「纯文本」。
2026-10-22 的真实账本证明了这条判据在思考模型上必然失败：会话 `6185a86fd329` 的压缩调用
`status:"failed"`、`error:"压缩模型返回的不是纯文本摘要。"`，而它 `outputTokens=3651`、
`reasoningTokens=1141`，且压缩请求不带工具 —— 摘要已经写出来了，只是回复里多了一个推理块。

那次修复（`Stepper` 里放行推理块）是对的，但它没有回答一个更根本的问题：**为什么摘要要走「回复正文」这条路。**
摘要落到历史里之后，它的落脚点本来就在记录里；而模型自己写的摘要，最自然的落点就是它自己那次调用的参数。

`01` §11.2 第 6 条已经写过这个形态：「不做『压缩特例』：`compact` 就是画线 + 放摘要」。
本计划把它落地。

## 契约（用户 2026-10-22 逐条拍板）

| | 决定 |
|---|---|
| 形态 | 常驻工具 `compact{summary}`；摘要**只**存在于调用参数里 |
| 生效期 | 只有用户下发过压缩指令时有效；指令消费即清 |
| 权限 | **不受权限档位管**（`off` 档也放行）——最底层的机制不能依赖一个可能把人锁死的开关 |
| 边界 | 一条检查点，覆盖到**本步之前**最后一条记录；本步的调用、结果、其后的一切都在保留区 |
| 多条 | 同一批里全部原样落盘；检查点取**最后一条执行成功**的那条；其余不特殊处理 |
| 摘要副本 | `CompactionCheckpoint` 不再写 `summary`；渲染/UI 按 `sourceCallId` 回查记录 |
| 前置摘要消息 | 只给旧检查点回落（后缀里找不到对应的 `compact` 调用时） |
| 长度 | `MaxSummaryChars = 20_000`，schema `maxLength` 与核心校验共用 |
| 写序 | 记录 → 检查点 → 清指令 → 回收窗口 |
| 记账 | **不**给 `purpose:"compaction"` 开特例；压缩混在普通步的 `session` 行里 |
| 闸门 | 被拒的调用**不是**压缩（工具根本没跑）；指令原样留着，否则是自造的无限重试 |

## 流程

```
用户: session.compact
  → state.json: pendingCompact = true（持久化：核心崩在途中，重启后指令还在）
  → 推一步（同一回合里模型可以先读几个文件、再写摘要）
  → 模型: compact{summary}
     · 无指令 ⇒ 收下一句人话，什么都不做
     · 有指令 ⇒ 核心写检查点（覆盖到本步之前）、清指令、开回收窗口
  → 本回合结束、没调工具、指令还在 ⇒ 以 explicitStart 再推一步（不塞假消息）
```

## 批次

### A · 状态与格式

1. `Kernel/SessionState.cs`：加

   ```csharp
   /// 用户下发、尚未被 compact 消费的压缩指令。⚠️ 持久化的理由……
   public bool PendingCompact { get; init; }
   ```

   ⚠️ **必须是 `bool`，带默认值、不做 `required`**：`SessionHandle.Create` 与三处测试都直接
   `new SessionState { … }`，加 `required`/`required?` 会让它们全部编译失败，而那条规则
   （`Json.FormatVersion` 的注释）管的是「下一次有人**写**这些东西的时候顺手加」，不是「加一个字段就顺手改七处初始化」。

2. `Kernel/Json.cs`：`FormatVersion` 8 → 9，注释写清两条理由：
   - `state.json` 加了 `pendingCompact`（老程序会静默忽略 ⇒ 一条指令被悄悄丢掉，会话永远压不成）；
   - `compaction.jsonl` 的 `summary` 不再写（同一个检查点文件，新行没有那个键）。

   不跟着变的：`UsageCall.NormalizedInputSinceFormat` 写死 8（见 `UsageStore.cs:118`，那条注释说的就是「别跟着走」）。

3. `Storage/CompactionStore.cs`：
   - `CompactionCheckpoint.Summary` 从 `required` 改成 `string?`（**不删** —— 老文件里那些行还要读）；
   - 新增 `SourceSummary(SessionStore, checkpoint)`：按 `SourceCallId` 在记录里回查那条
     `compact` 调用、取 `summary` 参数；**找不到（旧检查点）⇒ 返回 `checkpoint.Summary` 那份回落**。

4. `CoreCommand.SessionCompact` 的处理：**不再**跑模型，只 `UpdateStateAndPersist(s => s with { PendingCompact = true })`，
   然后按会话状态决定推 / 排队 / 直接回执（见批次 C）。

5. 金样：`DiskFormatGoldenTests` 里 `"version":8` → 9（四处）；`SessionState` 的金样在
   「不设 `pendingCompact` 时键不出现」那一格**逐字节不变**，另加一条「设上之后长什么样」。
   `ControlServerTests` 的快照键集若因序列化整个 `State` 而带上新键，一并加。

### B · 工具与闸门

6. `Kernel/Tool.cs`：
   - `ITool.GatedByPermission => true`（默认值 + 注释：**默认受管**是安全的一侧，与 `AsksBeforeRunning` 同一条理由）；
   - `IToolContext.CompactRequested { get; }`（只读；消费者写明是 `CompactTool` 与尾块指令）。

7. `Session/Tools/Gate.cs`：加

   ```csharp
   public static Verdict Decide(ITool tool, string? level) =>
       tool.GatedByPermission ? Decide(tool.PermissionClass, level) : Verdict.Allow;
   ```

   现有 `Decide(PermissionClass, string?)` 保留给表测试。`ToolExecutor` 改调新重载。

8. `Session/Tools/CompactTool.cs`（新）：

   - `NameOf = "compact"`、`PermissionClass = ReadOnly`、`GatedByPermission => false`；
   - schema：`{ summary: string, maxLength: MaxSummaryChars }`，`required: ["summary"]`；
   - `RunAsync`：无指令 ⇒ 一句人话；`summary` 缺失 / 空白 / 超长 ⇒ 一句人话（**指令保留**）；
     其余 ⇒ 「已请求压缩。此后的记录将以此摘要代替；本调用与它之后的一切会保留。」。
   - `MaxSummaryChars = 20_000`，注释写清：它是**进上下文的**，每一步都要重发。

9. `Session/Tools/ListTool.cs`：`ToolArgs` 加 `Bool(json, key)`（同 `String`/`Number` 的宽严：只认它说的话）。

10. `Session/Tools/BuiltInTools.cs`：加 `new CompactTool()`（19 → 20）。

### C · 推进器与控制面

11. `Session/Stepper.cs`：
    - 构造参数加 `string? pendingCompactText = null`（尾块的指令文本，**由解析器渲染、推进器不碰预设**）；
    - 构造 `SessionToolContext` 时传 `CompactRequested: handle.State.PendingCompact`；
    - 执行后（`batch.ExecutedCallIds` 的顺序之后）：

      ```
      var executed = 本批里被真正执行、且名为 compact 的调用；
      取最后一条；ToolArgs.String(arguments, "summary") 为 null/空白/超长 ⇒ 不开窗（工具已经说过了）
      否则：
        AppendEvents（本步的记录）                       ← 先记录
        CompactionStore.Append(checkpoint)                ← 再检查点（CoveredThroughEventId = recorded[^1].Id）
        UpdateStateAndPersist(PendingCompact = false)     ← 再清指令
        Reclaim.Window("compaction", …)                   ← 最后开窗
      ```

    - `StepOutcome` 加 `CompactionCheckpoint? Compacted`；
    - **删** `CompactAsync`。

    ⚠️ 次序是这套东西里唯一不可随意改的地方：检查点先于记录写，崩在中间会得到一个「覆盖到 X、而 X 之后什么都没有」的窗口，
    而模型既看不到摘要也没有历史（前置摘要事件对新检查点是不插的）。

12. `Hosting/CommandDispatcher.cs`：
    - `HandleSessionCompactAsync` / `StartBackgroundCompaction` / `CompactAndPublishAsync` 改成「写指令 + 推进」，
      不再自己调模型；
    - `RunStepAsync` 循环尾：在 goal 那一支之前加一支

      ```
      if (interruptEpoch 未变 && handle.State.PendingCompact && 回复里没有工具调用
          && !reply.Refused && reply.Blocks.Count > 0)
          ⇒ ContinueGoalAsync 同形的 explicitStart 推进
      ```

    - 发 `session.compaction.completed`（照旧事件；载荷取 `step.Compacted`）。

13. `Session/TimelineRender.cs` / `Session/ContextCompaction.cs`：
    - `SummaryEvent` 的插入条件改成「检查点没有 `SourceCallId`、或后缀里找不到那条调用」；
    - 文本从 `CompactionStore.SourceSummary` 取。

14. `Kernel/CorePrompts.cs` + `Session/SystemBlock.cs`：加 `CompactDirective` 尾块（与 `GoalDirective` 同形）：
    「用户要求压缩本会话的上下文。先继续收集需要的信息（可以读文件、跑命令）；
     准备好之后调用 compact，把摘要写进 summary 参数。调用之后，之前的记录会被这段摘要代替，
     而这次调用本身与它之后的一切保留。」

### D · 拆除

15. 删 `Stepper.CompactAsync`、`CompactionRetryTests`（重试的调用点从三处回到两处，`RetryPolicy` 的注释跟着改）；
    `CommandDispatcherTests.Compact_RunsInBackground_...` 改写成「下发指令 ⇒ 推进 ⇒ 模型调 compact ⇒ 检查点落地」；
    `docs/03` 的 `session.compact` 语义、`docs/01` §11.2 / §11.5 的窗口图。

### E · 测试

新增（`tests/Lilara.Core.Tests/CompactionFlowTests.cs` + `CompactToolTests.cs`）：

| 用例 | 断言 |
|---|---|
| 无指令调用 | 结果是一句说明；没有检查点；指令状态不变 |
| 有指令调用 | 检查点落地；`CoveredThroughEventId` = 本步前最后一条；`SourceCallId` = 那条调用 |
| 同一批两条 | 两个检查点（第二条覆盖含第一条调用）；渲染取最后一条的摘要 |
| 摘要缺失 / 超长 | 无检查点；指令**仍在**（下一步重试） |
| `off` 档 | compact 仍被执行（`GateTests` 加一条） |
| 旧检查点 | 后缀里没有对应调用 ⇒ 前置摘要事件照常插入、文本来自 `Summary` 字段 |
| 指令持久化 | 写 state、重新 `Load`、`PendingCompact` 仍在 |
| 续跑 | 模型没调工具就停 ⇒ 又推一步，且记录里**没有**假用户消息 |
| 多轮压缩 | 第二次的窗口 = 第一次之后的历史，摘要只有第二份 |

⚠️ **验证边界**：不做端到端实测（不在 `~/.lilara` 上跑真模型）。摘要质量、模型是否会在有指令时正确调用 compact，
只有单测与假适配器覆盖。
