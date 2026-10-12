using System.Text.Json;
using Anthropic.Models.Messages;
using Lilara.Core.Kernel;
using CanonicalBlock = Lilara.Core.Kernel.Block;

namespace Lilara.Core.Protocol.Claude;

// canonical → Claude Messages 请求。纯函数：同一个 ModelRequest 必须构造出逐字节相同的请求（前缀稳定性）。
public static class ClaudeRequestBuilder
{
    public static MessageCreateParams Build(ModelRequest request)
    {
        // ⚠️ **空时间线不是错误——前提是有槽位撑着**（2026-10-11 改，本批之前这里是无条件抛）。
        //
        // ⚠️ **原判据（「空 messages 会被 API 400 拒绝」）是对的一半**：裸的空 messages
        // 确实发不出去，所以**两个槽位都空 + 没有历史**时这里仍然早失败。
        // 但**有系统块时 `messages: []` 是合法的**——本机网关实测（2026-10-11）：
        // Claude 形状下 `system` + `messages: []` 回 HTTP 200 并正常作答。
        //
        // ⚠️ **而这不是「放开一个守卫」**：合法性的**来源**是本批新增的槽位。
        // 本批之前 `Timeline.Count == 0` 时确实无路可走（那时系统块可能为空、尾块不存在），
        // 那句 throw 是**当时正确的**。⇒ 改判据要连着「谁提供了合法性」一起改。
        //
        // ⚠️ **它挡的是一个真实的新会话路径**：用户 `session create` 之后发的第一句话，
        // 时间线里**只有**那条用户消息——而 `session.start` 是 no-op、没有别的入口
        // 能造出「有历史」的会话。所以这条守卫一旦错判，**每个新会话都跑不起来**。
        if (request.Timeline.Count == 0
            && request.Slots.System.Count == 0
            && request.Slots.Premier.Count == 0)
            throw new ArgumentException("时间线为空，且没有任何槽位内容，无法构造请求", nameof(request));

        return new MessageCreateParams
        {
            Model = request.ModelId,
            MaxTokens = request.MaxTokens,
            Messages = BuildMessages(request.Timeline, request.Slots.Tail, request.Protocol),
            System = BuildSystem(request.Slots.System, request.Slots.Premier),

            // ⚠️ 空工具表 ⇒ **不发这个键**（不是发一个空数组）：与 `System` 同一条判据——
            // 空值会改变前缀的字节，而「没有工具」的会话应当与加工具支持之前**逐字节相同**。
            Tools = BuildTools(request.Tools),

            OutputConfig = new OutputConfig { Effort = ToEffort(request.Effort) },

            // ⚠️ **两个事实同时成立，别只记一个：**
            //
            // ① **SDK 把这个属性标成过时**（编译器 CS0618，原文）：
            //    「Models released after Claude Opus 4.6 do not support setting temperature.
            //      A value of 1.0 will be accepted for backwards compatibility,
            //      all other values will be rejected with a 400 error.」
            // ② **但是**——本项目实际用的端点（本机网关）**实测接受**它：
            //    0.7 / 1.5 / 2.0 全部 200，2.5 与 -0.1 才 400（`Axes.Temperature` 的范围就是这么量出来的），
            //    且**与 `output_config.effort` 同时发也不报错**。
            //
            // ⇒ 判断：既然用的是网关而不是官方 API，就**发**（否则这条轴在这个项目上是个静默的摆设）。
            // ⚠️ **代价要写在明处**：若把这个 provider 指向**官方** anthropic.com 且模型是 4.6 之后的，
            // 那么**非 1.0 的温度会被 400**。到那时这条 pragma 就是该回头的地方。
            // （`#pragma` 是**必须**的：`TreatWarningsAsErrors` 下这个过时警告会挡住整个构建。
            //  收窄到这一行，别在文件头关——那样会连带盖掉将来真正的过时警告。）
#pragma warning disable CS0618
            Temperature = request.Temperature,
#pragma warning restore CS0618
        };
    }

    // canonical 时间线 → 协议消息列表。合并相邻同角色的事件：拒答不写模型事件，
    // 于是时间线上会出现连着两条 user，而上游把 `roles must alternate` 列为 400。
    private static List<MessageParam> BuildMessages(
        IReadOnlyList<TimelineEvent> timeline, IReadOnlyList<CanonicalBlock> tail, string protocol)
    {
        var messages = new List<MessageParam>();

        bool? pendingIsUser = null;
        List<ContentBlockParam>? pending = null;

        void Flush()
        {
            if (pending is null) return;
            messages.Add(new MessageParam
            {
                Role = pendingIsUser!.Value ? Role.User : Role.Assistant,
                Content = pending,
            });
            pending = null;
        }

        foreach (var evt in timeline)
        {
            // ⚠️ **两种事件在这里合流，而它们的角色不同**：模型消息是 assistant，
            // 而工具结果住在 **user** 角色的消息里（Claude 的 `tool_result` 就是这么定义的）
            // ⇒ 不能把它们当成一类。这个不对称是协议给的，`01` §3.6 那条「工具结果不是块、
            // 是它自己的事件类型」正是为了在类型层面把它分开。
            var (isUser, blocks) = evt switch
            {
                TimelineEvent.MessageEvent m => (m.From == Speaker.User, ToContentBlocks(m, evt, protocol)),
                TimelineEvent.ToolResultEvent r => (true, ToResultBlocks(r, evt)),
                _ => throw new ArgumentException(
                    $"时间线里有无法翻译的事件（{evt.GetType().Name}）", nameof(timeline)),
            };

            if (pending is not null && pendingIsUser == isUser)
            {
                pending.AddRange(blocks);
                continue;
            }

            Flush();
            pending = blocks;
            pendingIsUser = isUser;
        }

        // ---- 尾槽位：**拼进末尾那条 user 消息**（`01` §3.2 / §3.3）----
        //
        // ⚠️ **判据只有一条：末尾是不是 user。** 是 ⇒ 拼进去；不是 ⇒ 补一条 user 消息。
        //
        // ⚠️ **为什么不一律补一条独立的消息**：尾槽位的语义是**替换而非追加**
        // （「水面」——永远在最后、永远只有一份）。而**拼进末尾**这个做法与那条语义
        // 一个字都不冲突：它照样每步只出现一次、不累积。
        // ⚠️ 反过来，一律补一条的代价是实在的：**多一个块、多一条消息**，
        // 而 Claude 协议对相邻同角色是有约束的（上面那个 `Flush` 合并正是为它写的）。
        //
        // ⚠️ **为什么今天几乎总是走「拼进去」那一支**：每轮 = 模型的 `tool_use`（assistant）
        // + 我们的 `tool_result`（**user** 角色）⇒ 时间线**恰好总是以 user 结尾**。
        // ⚠️ **不精确的那一格如实记**：用户刚 `send` 完、那一步还没跑完时，
        // 末尾是那条**用户消息**，于是尾块追加在它后面而不是独立成块。
        // 那不是缺陷（内容一字不差），是「拼进末尾」这个方向的固有代价。
        if (tail.Count > 0)
        {
            // ⚠️ 只取文本块：尾块是核心拼出来的**一段话**（`SystemBlock`），
            // 里面不会有别的种类。真有的话这里**静默少一块**——所以下面的
            // `pending` 为空那条分支仍要判（别假设它一定非空）。
            var tailBlocks = tail.OfType<CanonicalBlock.TextBlock>()
                .Select(t => (ContentBlockParam)new TextBlockParam { Text = t.Text })
                .ToList();

            if (pending is not null && pendingIsUser == true)
            {
                pending.AddRange(tailBlocks);
            }
            else
            {
                Flush();
                pending = tailBlocks;
                pendingIsUser = true;
            }
        }

        Flush();
        return messages;
    }

    /// <summary>
    /// 工具结果 → `user` 角色的一条 `tool_result` 块。
    ///
    /// <para>⚠️ <b>`ToolUseId` 用的是<b>协议 id</b>（<c>CallId</c>），不是我们的稳定 id</b>——
    /// 上游按它配对，而它不认我们那个 id（`01` §8.0.2 第七节的两个 id）。</para>
    ///
    /// <para>⚠️ <b>`IsError` 刻意不设。</b>本项目的工具**失败也是内容**
    /// （见 `ToolOutcome`：文件不存在 ⇒ 一段说人话的文本），所以这里没有「错误」可标。
    /// 真要标的话得先有「这条结果是失败」这个信息——而它在执行层就被有意抹平成内容了。</para>
    /// </summary>
    private static List<ContentBlockParam> ToResultBlocks(TimelineEvent.ToolResultEvent result, TimelineEvent evt)
    {
        // ⚠️ **`Ref` 到不了这里**（2026-10-06）：`fetch` 的结果带引用，而引用由
        // `TimelineRender` **在进适配器之前**就解掉了（那里解、不在适配器里解，
        // 理由见那个类）。⇒ 这里原先有一条「`Ref` 非空 ⇒ 抛」的守卫，它已经**永远到不了**，
        // 删掉它。⚠️ 而**留着它更坏**：`fetch` 一上线，那句话会变成一条指着错方向的假诊断。
        //
        // ⚠️ 若哪天真有引用漏到这里，下面那个 `switch` 的默认分支会响亮地抛
        // （「工具结果里有无法翻译的块」）——**不需要额外的守卫**，那正是这一行的职责。
        //
        // ⚠️ **直接拼文本，不走 `content` 那个联合列表再 OfType 回来**。
        // 那个写法看着更「统一」，但它错得**静默**：`new TextBlockParam{…}` 是**隐式**转成
        // `ContentBlockParam` 才进列表的，所以 `content.OfType<TextBlockParam>()` **永远是空**
        // ⇒ 每一条结果（包括正常读到内容的）都会被换成 `(无输出)`。
        // 这不是我推出来的——是 CA2021 抓到的（分析器说「此调用将始终导致空序列」）。
        var text = string.Concat(result.Content.Select(b => b switch
        {
            CanonicalBlock.TextBlock t => t.Text,

            // 图 / 库对象要等库那条读取路径接上（今天没有任何工具产得出它们）。
            _ => throw new ArgumentException(
                $"工具结果里有无法翻译的块（{b.GetType().Name}）", nameof(evt)),
        }));

        // 内容为空 = 「这个工具什么都没返回」，而协议**不允许**一个空的 content。
        // ⚠️ 补一个占位串而不是抛：这是**合法**形态（`00` §5：两个都空 = 什么都没返回），
        // 抛的话一次正常的「无输出」会把整步打掉。
        // ⚠️ 它是**给上游看的形状**，而我们的记录里仍然是空列表——两处不一样是刻意的。
        if (text.Length == 0) text = "(无输出)";

        // ⚠️ `Content` 在 SDK 里是联合类型（一串块 / 一个纯字符串）。我们只产文本 ⇒ 走字符串那条。
        return [new ToolResultBlockParam { ToolUseID = result.CallId, Content = text }];
    }

    // 一条 canonical 消息 → 协议内容块列表。按原始顺序映射，绝不能只挑 text：
    // 推理块必须逐字回传（含签名），顺序或内容一动上游就 400 `invalid_request_error`。
    private static List<ContentBlockParam> ToContentBlocks(
        TimelineEvent.MessageEvent message, TimelineEvent evt, string protocol)
    {
        var content = new List<ContentBlockParam>();
        foreach (var b in message.Blocks)
        {
            switch (b)
            {
                case CanonicalBlock.TextBlock t:
                    content.Add(new TextBlockParam { Text = t.Text });
                    break;

                case CanonicalBlock.ReasoningBlock r:
                    // 必须过能力表：签名缺失时 SDK 构不出 ThinkingBlockParam（Signature 是 required）。
                    // 拦住我们的是客户端库的类型，不是上游协议——实测无签名 thinking 块上游回 HTTP 200 并正常作答。
                    if (!ReasoningPolicy.CanReplayBlock(r, protocol))
                        break;

                    content.Add(new ThinkingBlockParam
                    {
                        Thinking = r.Text,
                        // CanReplayBlock 刚确认过 Signature is not null，这里的 `!` 有依据。
                        Signature = r.Signature!,
                    });
                    break;

                case CanonicalBlock.ToolUseBlock u:
                    // ⚠️ **模型的调用必须原样回传**，与推理块同一条理由：上游按 `tool_use`
                    // 与 `tool_result` 配对，少了它那条结果就是悬空的 ⇒ 400（`01` §3.6）。
                    // ⚠️ 参数走 `Input`——SDK 的值类型（`Dictionary<string, JsonElement?>`），
                    // **不是**原始文本。这是本批**唯一**一处参数要过一遍类型的地方；
                    // 我们的记录里存的仍是逐字原文（`ToolUseBlock.Arguments` 那条铁律没动）。
                    content.Add(new ToolUseBlockParam
                    {
                        ID = u.CallId,
                        Name = u.Name,
                        Input = ParseInput(u),
                    });
                    break;

                default:
                    // 认不出的块抛，不静默丢。
                    throw new ArgumentException(
                        $"时间线里有无法翻译的块（{b.GetType().Name}），无法构造请求", nameof(evt));
            }
        }

        // `Content = []` 会被 API 400 拒绝；Blocks 没有非空不变量，真能走到这里。
        if (content.Count == 0)
            throw new ArgumentException(
                $"时间线里有空消息（speaker={message.From}），无法构造请求", nameof(evt));

        return content;
    }

    /// <summary>
    /// 把逐字原文的工具参数解析成 SDK 要的那个字典。
    ///
    /// <para>⚠️ <b>这是「逐字」这条铁律在<b>这一层</b>的边界，如实记下来</b>：
    /// 我们的记录里存的是**原文**（`ToolUseBlock.Arguments`），而 SDK 的值类型是
    /// <c>Dictionary&lt;string, JsonElement?&gt;</c> ⇒ 必然过一次解析。
    /// 已知的代价是**对象内的键序与空白会被规范化**（数字字面量由 <c>JsonElement</c> 保住）。
    /// 上游对工具参数**不做逐字比对**（它只解析语义），所以这不构成 400 风险；
    /// 而<b>记录层那一份仍是逐字</b>的——两件事别混。</para>
    ///
    /// <para>⚠️ 参数不是 JSON 对象时**抛**：`ToolUseBlock` 在落盘时已经校验过合法性
    /// （`JsonRawConverter` 的写出会抛），走到这里还不合法说明那份记录被人手改坏了。</para>
    /// </summary>
    private static Dictionary<string, JsonElement> ParseInput(CanonicalBlock.ToolUseBlock u)
    {
        var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(u.Arguments);

        return parsed ?? throw new ArgumentException(
            $"工具 '{u.Name}' 的参数不是 JSON 对象：{u.Arguments}", nameof(u));
    }

    // 工具表 → 协议工具定义。空表 ⇒ null（**整个键不发**），与 System 同一条判据。
    private static List<ToolUnion>? BuildTools(IReadOnlyList<ToolSpec> tools)
    {
        if (tools.Count == 0) return null;

        return tools.Select(t =>
        {
            var schema = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(t.ParametersSchemaJson)
                ?? throw new ArgumentException($"工具 '{t.Name}' 的参数 schema 是 null", nameof(tools));
            return (ToolUnion)new Tool
            {
                Name = t.Name,
                Description = t.Description,
                InputSchema = new InputSchema(schema),
            };
        }).ToList();
    }

    // 两块头槽位 → `system`。**顺序是契约**：系统块在前、首要块在后（`01` §3.1
    // 「头槽位 = [系统提示词块][首要提示词块]」）——倒过来会让「位置 0 是核心的字节」
    // 这条前提作废，而它是失效半径那套论证（§2.3）的地基。
    //
    // 没有块时不设 System，而不是设成空串或空表：空值会变成一个真实的空 system 块，白白改变前缀。
    private static MessageCreateParamsSystem? BuildSystem(
        IReadOnlyList<CanonicalBlock> system, IReadOnlyList<CanonicalBlock> premier)
    {
        // 先 materialize 再判空：输入非空但全是非文本块时过滤后是空列表，会发出一个空的 system 块。
        var text = system.Concat(premier)
                         .OfType<CanonicalBlock.TextBlock>()
                         .Select(t => new TextBlockParam { Text = t.Text })
                         .ToList();

        // 用早返回而不是三元：三元会让可空流经隐式转换触发 CS8604（本项目警告即错误）。
        if (text.Count == 0) return null;

        return text;
    }

    // 思考强度轴 → 协议参数。SDK 支持五档：low / medium / high / xhigh / max，如实透传。
    // 未知值抛异常，不静默降级（否则拼写错误会悄悄跑成某一档）。
    private static Effort ToEffort(string effort) => effort switch
    {
        "low" => Effort.Low,
        "medium" => Effort.Medium,
        "high" => Effort.High,
        "xhigh" => Effort.Xhigh,
        "max" => Effort.Max,
        _ => throw new ArgumentException(
            $"未知的思考强度 '{effort}'。有效值：low / medium / high / xhigh / max", nameof(effort)),
    };
}
