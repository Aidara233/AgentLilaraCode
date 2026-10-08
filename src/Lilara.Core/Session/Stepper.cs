using Lilara.Core.Protocol;
using Lilara.Core.Storage;
using Lilara.Core.Kernel;
using Lilara.Core.Session.Tools;

namespace Lilara.Core.Session;

/// <summary>
/// 推进器：跑一步 = 渲染 → 调用 → **执行** → 落盘（02 §2）。
///
/// <para>⚠️ <b>一步 = <b>一次</b>模型调用 + 其工具执行</b>（`01` §4 的单数形式）。
/// ⇒ 「模型又调了工具」不是在这里绕圈，而是**再一次入队 + 启动**（`01` §6.1 的队列机制）；
/// 循环因此住在驱动层（`CommandDispatcher.RunStepAsync`），不在这里。
/// 这条单射性质还是 <b>步原子性</b>的安全来源：至多一次模型调用 ⇒ 至多一组 `tool_use`。</para>
///
/// <para>⚠️ 步是持久化的原子单位（`01` §4）。这里的次序是刻意的：
///   1. 用户消息**先落盘**——它是事实，模型调用失败也抹不掉
///   2. 模型消息与它那批工具结果**一次落盘**（`01` §3.6 ④）
///   3. state.json 最后写——崩在 2 和 3 之间只会让 turn 计数偏小，历史不损坏</para>
/// </summary>
public sealed class Stepper(
    SessionStore store, IModelAdapter adapter, IReadOnlyList<ITool>? tools = null)
{
    private readonly SessionStore _store = store;
    private readonly IModelAdapter _adapter = adapter;

    /// <summary>
    /// 工具表。⚠️ **默认空** = 纯 chat（`04` §1.3 推论 1：「没有任何插件」是完全合法的形态）。
    /// ⚠️ 可空是给既有测试的：它们构造的是「没有工具」的推进器，而那条路上的行为
    /// **逐字节不变**（工具相位整段跳过）。
    /// </summary>
    private readonly IReadOnlyList<ITool> _tools = tools ?? [];

    /// <summary>
    /// 构造时定下的适配器。⚠️ 这个只读访问器存在的唯一理由是**可测性**：
    /// `StepperResolver` 的契约就是「解析出来的是不是占位适配器」，
    /// 而适配器是私有字段时那条契约没法断言。它不是给运行时用的。
    /// ⚠️ 表达式别名，不是第二个字段——同一个事实只能有一份存储。
    /// </summary>
    public IModelAdapter Adapter => _adapter;

    /// <summary>
    /// 一步 = 渲染 → 调用 → 落盘（02 §2）。
    ///
    /// <para>⚠️ **它不再追加用户消息**（Task 4）。按 `01` §6.1 的次序，
    /// 「入队(x) → 若 x 是事件，**立即追加进记录**」——那次追加归 `CommandDispatcher`，
    /// 因为**入队与追加是同一个动作的两半**：一旦分开，
    /// 「已经落盘但还没入队」或「已入队但没落盘」这两种中间态就会各自丢消息。
    /// 这里的 <paramref name="blocks"/> 是**取空的整个队列**（可能来自多条消息）。</para>
    ///
    /// <para>⚠️ 次序仍是刻意的（`01` §4）：模型输出在内存合并完只落一次盘；
    /// `state.json` 最后写——崩在中间只让 turn 偏小，历史不损坏。</para>
    /// </summary>
    public async Task<ModelReply> StepAsync(
        SessionHandle handle, IReadOnlyList<Kernel.Block> blocks, CancellationToken ct)
    {
        // 「now 冻结在步开始」（00 §2.5）——同一步内重复渲染必须逐字节相同
        var at = CoreClock.Now();

        // ⚠️ 先记下落盘的那份，失败了原样放回去。
        // 调用方 catch 之后继续用**同一个把手**：不回滚的话，
        // 用户刚看着一步失败，`status` 却报 Running——而磁盘上从来没写过 Running。
        // 「内存是落盘的镜像」这条不变量在这里也必须成立。
        var persisted = handle.State;
        // ⚠️ 走 `UpdateState`（原子读-改-写）：它与 `setAxis` 的读-改-写共用**同一把锁**，
        // 两条交错时不会互相整份盖掉（终审 I2）。下同。
        handle.UpdateState(s => s with { Status = SessionStatus.Running });

        ModelReply reply;
        try
        {
            // 渲染：读回完整时间线 → canonical（核心永远发全量，00 §6）
            var request = new ModelRequest
            {
                ModelId = handle.Spec.Model.ModelId,
                MaxTokens = handle.Spec.Model.MaxTokens,
                // ⚠️ 用**适配器实际会说的**协议，不是 spec 里那份快照。
                // 适配器是按**当前** provider 定义选的（`StepperResolver`），而 spec 的协议是
                // 建会话时的快照——`provider set --protocol` 一改，两者就分叉。
                // 分叉的后果不是报错，是 `ReasoningPolicy` 按快照判断「能不能回放」，
                // 于是**在唯一能承载推理的那条链路上把推理静默丢掉**（复审实测的路径：
                // 会话建于 openai-chat，之后把 provider 改成 claude-messages）。
                // ⇒ 单一真相来源放在适配器上（`IModelAdapter.Protocol`）。
                Protocol = _adapter.Protocol,
                // ⚠️ **从轴读，不从 Spec 读。** Spec 是**建会话时冻结的快照**，
                // 而轴是**活**的（`session.setAxis` 改的就是它）。
                // 按 Spec 读的话，用户在头上把 effort 拨到 `max`、头显示 `max`，
                // 而**请求里发的还是建会话时那个值**——「设了没用」，且无从发现。
                // ⚠️ 五条轴里只有 effort 与 temperature 进请求（`01` §3.5：纯机械轴）。
                // ⚠️ **加一条有机械层的轴 = 加这里的一行 + 三个构造器各一行**——
                // 这条规则可 grep（搜 `NumberOrNull`）。别指望有别的机制替你接上。
                // ⚠️ `!` 是 **`Load` / `Create` 的契约**：它们保证 `State.Axes` 非空
                // （见 `SessionState.Axes` 的注释）；能拿到 null 的只有手工 `new SessionHandle`，
                // 那不是运行时路径。
                Effort = handle.State.Axes![Axes.Effort],
                Temperature = Axes.NumberOrNull(Axes.Temperature, handle.State.Axes![Axes.Temperature]),

                // ⚠️ **系统块在步开始读一次、冻进这一趟**（与上面那行 `now` 同一个道理）。
                // 它是**每步都可能变的外部输入**（预设是个用户随时能编辑的文件），
                // 而「渲染是纯函数」要求同一步内重复渲染**逐字节相同** ⇒ 随手现读会变成
                // 「半新半旧 ⇒ 每步破前缀」。⚠️ 所以这里**不是**每步缓存的偷懒，
                // 而是**刻意的每步一冻**：跨步实时（下一步就生效）、步内冻结。
                // 完整理由见 `SystemBlock.Render` 的注释。
                SystemBlocks = SystemBlock.Render(new PresetStore(_store.DataRoot), handle.Spec.SystemPromptPreset),

                // ⚠️ 工具表**从工具表投影**，不是从 spec 读的——spec 里没有它，
                // 而「这一步有哪些工具」是**派生**的（04 §1.3：工具表不是独立实体）。
                // ⚠️ 它的顺序必须**确定**（前缀指纹按它算）；`_tools` 是构造时定下的一个列表，
                // 不经过任何容器枚举。
                Tools = [.. _tools.Select(t => new ToolSpec(t.Name, t.Description, t.ParametersSchemaJson))],

                // ⚠️ **去引用在这里做，不在适配器里**：记录里的快照可能是个
                // `StoreBlock`（大对象进了库，`01` §8.4）⇒ 必须换成真内容再交给适配器。
                // 三个适配器因此**都不需要认识库**——库是控制面概念（`01` §8.4：
                // 「模型压根不该意识到库存在」），让适配器去查库等于把它泄漏到协议层。
                //
                // ⚠️ **这条曾经漏掉，后果是一次单向门**：写入方（`ToolExecutor` 过阈值就进库）
                // 先上线了，而这里没解引用 ⇒ 适配器遇到 `StoreBlock` 直接抛
                // ⇒ **那个会话从此永久发不出消息**（记录只追加，改不掉）。
                // ⇒ 「只写不读」是这类改动最该先问的一句：**写下去的东西，谁读得回来？**
                //
                // ⚠️ 同一条线上现在还有两件事（都在 `TimelineRender` 里）：按记录 id **回收**（换渲染）、
                // 以及把 `fetch` 的 `Ref` 解开。三件事都得在这里做完——**适配器只看见普通块**。
                // ⚠️ 那份渲染**与头 / 控制台读的是同一个函数**（`SessionQueries.HistoryOf` 也走它）：
                // 各写一份就会出现「模型看到的」与「用户看到的」悄悄分叉。
                Timeline = TimelineRender.Render(_store, handle),
            };

            // 调用（抛异常时直接传播——这一步没完成，Turn 不动）
            reply = await _adapter.CallAsync(request, ct);
        }
        catch
        {
            // 回到落盘的那份。⚠️ 用捕获的引用回放，不去读磁盘：
            // 读盘本身可能失败，那会把原始的失败原因盖掉。
            //
            // ⚠️ **回滚保留轴**：轴是**控制面状态**，不是这一步的产物——
            // 回滚要撤销的是「这一步推进了什么」（`Turn` / `Status`），轴不在其中。
            // `session.setAxis` **不拿会话闸**（`HandleSessionSetAxis` 里没有 `GateFor`），
            // 所以它能在**整个模型调用**的窗口里发生——那正是这里会走到的失败 / 取消窗口。
            // （不拿闸是刻意的：`01` §3.5 要求权限这类轴**即时生效**——「调整权限 =
            // 改一个浮动字段，零前缀代价、即时生效」；排在闸后面等这一步跑完就不是即时了。）
            // ⇒ 不保留的话，用户改的轴会被一次失败 / 中断**静默吞掉**：头收到过
            // `axis.changed`、显示新值，而内存回到旧值、下一次请求带旧值发出去；
            // 再下一次成功的步把旧值写回磁盘 ⇒ 用户看到的那个新值**就此消失，且没有报错**。
            // ⚠️ `s.Axes` 是**锁内**读到的那份——用它，不重新读 `handle.State`：
            // 两条交错时「重新读」会拿到别人的写入，把回滚的基准弄歪（终审 I2）。
            handle.UpdateState(s => persisted with { Axes = s.Axes });
            throw;
        }

        // ---- 执行相位（`01` §4：一步 = 一次模型调用 **+ 其工具执行**）----
        //
        // ⚠️ 它排在**落盘之前**，是为了把「模型消息 + 它那批结果」凑成**一次** append
        // （`01` §3.6 ④ 的「步末按调用序一次落盘」）。分两次落就等于把步这个原子拆成两个崩溃窗口。
        var toolCalls = reply.Blocks.OfType<Kernel.Block.ToolUseBlock>().ToList();
        var events = new List<TimelineEvent>();

        // ⚠️ **记录那一份**（未经回收、未经解引用）——`fetch` 取地址必须从它算，
        // 理由见 `SessionToolContext` 的构造参数注释。
        var recorded = _store.ReadTimeline(handle.ProjectKey, handle.SessionId);

        // 拒答不写空模型消息（Review Focus #2）
        if (!reply.Refused && reply.Blocks.Count > 0)
        {
            events.Add(new TimelineEvent.MessageEvent
            {
                Id = Ids.New(),
                At = at,
                From = Speaker.Model,
                Blocks = reply.Blocks,
            });
        }

        // ⚠️ **工具一次都还没跑，所以这一趟的落盘完全不同**（`01` §3.8）。
        // 有问 ⇒ 事件列表**空**（模型消息也不落——见下），状态转 `WaitingForInput`，
        // 那次暂停冻进 handle，等用户答完由 `CommandDispatcher` 续跑。
        AskRequest? pending = null;

        // ⚠️ 工具调用是**模型消息的一部分** ⇒ 有调用就一定有那条模型消息（上面那个 if 通过）。
        // `events.Count > 0` 不是多余的守卫，它挡的是「凭空落一堆悬空结果」——而悬空的
        // `tool_result` 与悬空的 `tool_use` 一样会让下一步的请求非法（`01` §3.6）。
        if (toolCalls.Count > 0 && events.Count > 0)
        {
            var blobs = new BlobStore(_store.DataRoot);
            // ⚠️ **权限档位在这里读一次，冻进这一趟**（与上面那个 `now` 同一个道理）：
            // 它是**浮动字段**（`01` §9.2：改它零前缀代价），而一步之内的判定必须一致——
            // 否则同一步里两条调用会按两个档位判，而记录里看不出为什么。
            var executor = new ToolExecutor(
                _tools, blobs, handle.State.Axes![Axes.Permission],
                new ApprovalStore(_store.DataRoot, handle.ProjectKey, handle.SessionId));
            var context = new SessionToolContext(handle.Spec.WorkingDirectory, blobs, recorded);

            var batch = await executor.ExecuteAsync(toolCalls, context, ct);
            pending = batch.Pending;
            events.AddRange(batch.Results);
        }

        // ⚠️ **有问 ⇒ 中止这一步，一条结果都不落**（`01` §3.8）。
        // ⚠️ **连那条模型消息也不落**——它与工具结果同属「这一步的产物」，
        // 落它而不落结果，就会在下一步的请求里留下**悬空的 `tool_use`** ⇒ 上游 400。
        // ⇒ 这一步**整个**没发生：模型那段话会被重放（前缀没变，模型吐同样的东西）。
        // 代价如实认下：一次需要授权的调用 = **多花一次模型调用**。
        // ⚠️ 判据在这条与「先判全、再执行」那条之间是**同一条**：宁可让一步整个不算，
        // 也不许留下半个状态（`01` §3.6 ②「一对不能拆」的结构性后果）。
        if (pending is not null)
        {
            handle.AwaitingInput = new SessionHandle.PendingAsk(pending, toolCalls);
            handle.UpdateStateAndPersist(_store, s => s with
            {
                Status = SessionStatus.WaitingForInput,
                Ask = new PausedAsk(
                    pending.Id, pending.Kind,
                    // ⚠️ 一条询问今天只可能属于**一个** Kind，而 `CallId` 那一格只装得下一个。
                    // 取第一条；真正的配对靠 `PendingAsk.Calls`（它是列表）。
                    toolCalls[0].CallId,
                    toolCalls[0].Name,
                    ApprovalStore.HashArgs(toolCalls[0].Arguments),
                    DeadlineOf(handle.Spec.AskTimeout)),
                Turn = s.Turn + 1,
            }, diskStatus: SessionStatus.WaitingForInput);

            return reply;
        }

        // 一次落盘 = 一个原子。⚠️ 空批次**不写**（拒答且没有工具的那一步就是空的），
        // 而 `AppendEvents` 对空批次是 no-op、不抛——见它的注释。
        _store.AppendEvents(handle.ProjectKey, handle.SessionId, events);

        // ⚠️ Turn 递增**在拒答分支之外**：拒答是成功返回的、完成的一步，
        // 只是没有内容可写。让它和「抛异常」一样不计数，
        // 会让 state.json 里「拒答过一次」与「从没跑过」无法区分。
        // ⚠️ **Turn 自增 + 落盘走 `UpdateStateAndPersist`（同一个临界区）**，不能拆成
        // 「先换内存、再单独写盘」两句：`setAxis` 不拿会话闸，
        // 它的读-改-写能与这里交错，分开写会丢 `Turn` 或把状态卡在 `Running`（终审 I2）。
        // ⚠️ `diskStatus` 必须是**字面量 `Idle`**，不能写 `handle.State.Status`——
        // 实参是**调用前**求值的，而那一刻内存里还是上面刚置的 `Running` ⇒
        // 那会把 `Running` 写进磁盘，正是整套机制要防的那件事（`SessionStatus` 的类型注释里有完整后果）。
        // 一句话：**「原样落盘」不能用「读当前值」来表达**，因为调用点往往正好处在「内存已经领先」的时刻。
        handle.UpdateStateAndPersist(_store, s => s with
        {
            Status = SessionStatus.Idle,
            Turn = s.Turn + 1,
        }, diskStatus: SessionStatus.Idle);

        return reply;
    }

    /// <summary>
    /// 超时的**绝对时刻**（`01` §3.8）。⚠️ <b><c>null</c> = 不超时</b>。
    ///
    /// <para>⚠️ <b>它在暂停那一刻算一次、冻进盘</b>，而不是每读一次算一次：
    /// 「等多久」是用户设的**时长**，而「什么时候到点」必须在**盘上**（重启之后
    /// 那段计时还得接着走）。现算的话，重启会把时限**重新起算** ⇒ 一个设了 5 分钟的
    /// 询问，重启几次就能无限等下去。</para>
    /// </summary>
    private static DateTimeOffset? DeadlineOf(TimeSpan? timeout) =>
        timeout is { } t ? CoreClock.Now() + t : null;

    /// <summary>
    /// 一步：**追加 + 入队 + 推进**——`01` §6.1 那条复合动作的完整形态。
    ///
    /// <para>⚠️ 它保留下来是为了**让既有测试的语义不被稀释**：
    /// 那些测试断言的是「用户消息会落盘」，而 Task 4 把「追加」挪去了
    /// <see cref="Hosting.CommandDispatcher"/>。删掉这个入口就等于删掉那些断言；
    /// 而它们守的是一条**真的不变量**（用户消息是事实，模型调用失败也抹不掉）。
    /// ⇒ 生产者路径（dispatcher）走「追加 + 入队 + 取空队列」，
    /// 这个壳子给「我就想跑一步」的调用方用。</para>
    /// </summary>
    public Task<ModelReply> StepAsync(SessionHandle handle, string userText, CancellationToken ct)
    {
        var blocks = new List<Kernel.Block> { new Kernel.Block.TextBlock(userText) };

        // ⚠️ 与 dispatcher 同一条次序：**先追加再入队**，然后取空。
        _store.AppendEvent(handle.ProjectKey, handle.SessionId, new TimelineEvent.MessageEvent
        {
            Id = Ids.New(),
            At = CoreClock.Now(),
            From = Speaker.User,
            Blocks = blocks,
        });
        handle.Enqueue(blocks);

        return StepAsync(handle, handle.DrainQueue(), ct);
    }
}
