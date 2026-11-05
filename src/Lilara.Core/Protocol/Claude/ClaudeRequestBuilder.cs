using System.Text.Json;
using Anthropic.Models.Messages;
using Lilara.Core.Kernel;
using CanonicalBlock = Lilara.Core.Kernel.Block;

namespace Lilara.Core.Protocol.Claude;

// canonical → Claude Messages 请求。纯函数：同一个 ModelRequest 必须构造出逐字节相同的请求（前缀稳定性）。
public static class ClaudeRequestBuilder
{
    public static MessageCreateParams Build(ModelRequest request, bool explicitCache = false)
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
            Messages = BuildMessages(request.Timeline, request.Slots.Tail, request.Protocol, explicitCache),
            System = BuildSystem(request.Slots.System, request.Slots.Premier, explicitCache),

            // ⚠️ 空工具表 ⇒ **不发这个键**（不是发一个空数组）：与 `System` 同一条判据——
            // 空值会改变前缀的字节，而「没有工具」的会话应当与加工具支持之前**逐字节相同**。
            Tools = BuildTools(request.Tools),

            OutputConfig = ToOutputConfig(request.Effort),
            Thinking = ToThinking(request.Effort),

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
        IReadOnlyList<TimelineEvent> timeline, IReadOnlyList<CanonicalBlock> tail, string protocol,
        bool explicitCache)
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

        // ⚠️ 断点②：**最后一条不受注入影响的对话消息**（`CacheBreakpoint.LastStableIndex`）。
        // 浮动读数那对每步都在变，断点必须落在它们之前——否则等于把「每步都变」写进缓存前缀。
        var breakpoint = explicitCache ? CacheBreakpoint.LastStableIndex(timeline) : -1;

        for (var index = 0; index < timeline.Count; index++)
        {
            var evt = timeline[index];
            // ⚠️ **两种事件在这里合流，而它们的角色不同**：模型消息是 assistant，
            // 而工具结果住在 **user** 角色的消息里（Claude 的 `tool_result` 就是这么定义的）
            // ⇒ 不能把它们当成一类。这个不对称是协议给的，`01` §3.6 那条「工具结果不是块、
            // 是它自己的事件类型」正是为了在类型层面把它分开。
            var (isUser, blocks) = evt switch
            {
                TimelineEvent.MessageEvent m => (m.From == Speaker.User, ToContentBlocks(m, evt, protocol, index == breakpoint)),
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
        // ⚠️ 若哪天真有引用漏到这里，下面那个 `switch` 的默认分支会给一句占位
        // （见 `UnsupportedBlock`）——**不需要额外的守卫**，那正是这一行的职责。
        //
        // ⚠️ **直接拼文本，不走 `content` 那个联合列表再 OfType 回来**。
        // 那个写法看着更「统一」，但它错得**静默**：`new TextBlockParam{…}` 是**隐式**转成
        // `ContentBlockParam` 才进列表的，所以 `content.OfType<TextBlockParam>()` **永远是空**
        // ⇒ 每一条结果（包括正常读到内容的）都会被换成 `(无输出)`。
        // 这不是我推出来的——是 CA2021 抓到的（分析器说「此调用将始终导致空序列」）。

        // ⚠️ **带图的结果走块列表那条**（2026-10-29）：`read` 一张图之后，那条结果就是
        // 「一张图」本身，而协议允许 `tool_result.content` 是一串块。⚠️ 只在**真的有图**时
        // 才换形状：纯文本结果仍旧走字符串那条，**逐字节不变**（前缀缓存按字节算）。
        if (result.Content.Any(b => b is CanonicalBlock.ImageBlock image && ImagePayload.CanSend(image)))
        {
            // ⚠️ **整条 `tool_result` 也走 raw**（同 `ImageParam` 的理由）：它的 `Content`
            // 在 SDK 里同样是联合类型，而「一串块」那一支的元素类型不是 `ContentBlockParam`。
            // ⇒ 直接给协议形状（`03` 里那五个键），少一层翻译。
            var parts = new List<object>();
            foreach (var b in result.Content)
            {
                if (b is CanonicalBlock.TextBlock tb) parts.Add(new { type = "text", text = tb.Text });
                else if (b is CanonicalBlock.ImageBlock image && ImagePayload.CanSend(image))
                    parts.Add(new
                    {
                        type = "image",
                        source = new { type = "base64", media_type = image.MediaType, data = ImagePayload.Base64(image) },
                    });
                else parts.Add(new { type = "text", text = UnsupportedBlock.Note(b) });
            }

            return
            [
                ToolResultBlockParam.FromRawUnchecked(new Dictionary<string, JsonElement>
                {
                    ["type"] = JsonSerializer.SerializeToElement("tool_result"),
                    ["tool_use_id"] = JsonSerializer.SerializeToElement(result.CallId),
                    ["content"] = JsonSerializer.SerializeToElement(parts),
                }),
            ];
        }

        var text = string.Concat(result.Content.Select(b => b switch
        {
            CanonicalBlock.TextBlock t => t.Text,

            // ⚠️ **翻译不了也要给一句话，不能抛**（2026-10-29，见 `UnsupportedBlock`）：
            // 抛会把「一条坏记录」升级成「一个永远发不出消息的会话」，而记录只追加。
            _ => UnsupportedBlock.Note(b),
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
        TimelineEvent.MessageEvent message, TimelineEvent evt, string protocol, bool cache)
    {
        var content = new List<ContentBlockParam>();
        // ⚠️ 断点②只能落在**最后一个文本块**上（`CacheControl` 是 init-only ⇒ 必须在构造时给）。
        // 先算出它在 Blocks 里的下标；文本块可能前面还有推理块与 tool_use。
        var lastText = -1;
        for (var i = 0; i < message.Blocks.Count; i++)
            if (message.Blocks[i] is CanonicalBlock.TextBlock) lastText = i;
        var blockIndex = -1;
        foreach (var b in message.Blocks)
        {
            switch (b)
            {
                case CanonicalBlock.TextBlock t:
                    blockIndex++;
                    content.Add(TextParam(t.Text, cache && blockIndex == lastText));
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

                case CanonicalBlock.ToolUseBlock u:                    // ⚠️ **模型的调用必须原样回传**，与推理块同一条理由：上游按 `tool_use`
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

                // ⚠️ **图文是兄弟块**（`01` §3.6）：图片必须作为**自己的块**发出去，
                // 而它前面那块文本仍然是一块文本。⚠️ 字节由 `TimelineRender` 贴
                // （`ImageBlock.Bytes`）——**适配器不认识库**，见那边注释。
                case CanonicalBlock.ImageBlock image when ImagePayload.CanSend(image):
                    content.Add(ImageParam(image));
                    break;

                default:
                    // 不该让这个会话此后**再也发不出消息**（记录只追加，改不掉）。
                    // ⚠️ 但它**不是**静默丢：这段话会作为正文进请求，模型读得到「这里有一块被省略了」。
                    // ⚠️ 而 `blockIndex` **不动**：它是「第几个**文本块**」的计数（缓存断点按它挑），
                    // 一句占位不是可以挂断点的正文。
                    if (UnsupportedBlock.Skippable(b) is { } note)
                        content.Add(TextParam(note, false));
                    break;
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

    /// <summary>
    /// **一块图片 → 协议原生图片块**（2026-10-29）。
    ///
    /// <para>⚠️ <b>为什么走 `FromRawUnchecked` 而不是强类型构造</b>：SDK 把 `source` 做成了
    /// 一个带隐式转换的联合类型（`ImageBlockParamSource`），而它的 `MediaType` 是
    /// `ApiEnum`——用强类型那条路要为「png/jpeg/gif/webp → 枚举成员」再写一张映射表；
    /// 而这张表**本来就不该存在**：协议那一格就是**媒体类型字符串**（`BlockKinds.ImageMediaTypes`
    /// 已经在入口判过白名单了）。⇒ 直接给协议形状，少一层会漂的翻译。</para>
    ///
    /// <para>⚠️ <b>调用点必须先用 <see cref="ImagePayload.CanSend"/> 判过</b>：没有字节时这里
    /// 拼出来的是一串空的 base64，而上游只会回一句「图片无效」，不会告诉你图根本没发出去。</para>
    /// </summary>
    private static ContentBlockParam ImageParam(CanonicalBlock.ImageBlock image) =>
        ImageBlockParam.FromRawUnchecked(new Dictionary<string, JsonElement>
        {
            ["type"] = JsonSerializer.SerializeToElement("image"),
            ["source"] = JsonSerializer.SerializeToElement(new
            {
                type = "base64",
                media_type = image.MediaType,
                data = ImagePayload.Base64(image),
            }),
        });

    // 两块头槽位 → `system`。**顺序是契约**：系统块在前、首要块在后（`01` §3.1
    // 「头槽位 = [系统提示词块][首要提示词块]」）——倒过来会让「位置 0 是核心的字节」
    // 这条前提作废，而它是失效半径那套论证（§2.3）的地基。
    //
    // 没有块时不设 System，而不是设成空串或空表：空值会变成一个真实的空 system 块，白白改变前缀。
    /// 造一个文本块。⚠️ `CacheControl` 是 **init-only**：断点只能在构造时给，事后设不了
    /// （SDK 的 `ContentBlockParam` 是带隐式转换的包装类型，也不能从中取回 `TextBlockParam`）。
    private static TextBlockParam TextParam(string text, bool cache) => cache        ? new TextBlockParam { Text = text, CacheControl = new CacheControlEphemeral() }
        : new TextBlockParam { Text = text };

    private static MessageCreateParamsSystem? BuildSystem(
        IReadOnlyList<CanonicalBlock> system, IReadOnlyList<CanonicalBlock> premier, bool explicitCache)
    {
        // 先 materialize 再判空：输入非空但全是非文本块时过滤后是空列表，会发出一个空的 system 块。
        var text = system.Concat(premier)
                         .OfType<CanonicalBlock.TextBlock>()
                         .Select(t => new TextBlockParam { Text = t.Text })
                         .ToList();

        // 用早返回而不是三元：三元会让可空流经隐式转换触发 CS8604（本项目警告即错误）。
        if (text.Count == 0) return null;

        // ⚠️ 断点①：头槽位的最后一块。它护住 header——历史再长、再变，只要预设没改就命中。
        if (explicitCache) text[^1] = TextParam(text[^1].Text, cache: true);

        return text;
    }

    /// <summary>
    /// 思考强度轴 → `output_config`。**`off` 档在这里就被分流出去了**：返回
    /// <see langword="null"/> ⇒ 整个 `output_config` 键**不发**。
    ///
    /// <para>⚠️ <b>为什么 `off` 不发 `output_config`</b>：那是「思考多深」这根杆，而 `off` 要说的
    /// 是「不要思考」——两件事。关思考走另一根杆（<see cref="ToThinking"/> 的
    /// <c>thinking.type = "disabled"</c>）。⚠️ 不能换成「发一个空对象」：`output_config: {}`
    /// 既没表达意图又改了请求字节（与 `Tools` / `System` 那两条「空值不发这个键」同一条纪律）。</para>
    /// </summary>
    private static OutputConfig? ToOutputConfig(string effort) =>
        EffortParam.IsOff(effort) ? null : new OutputConfig { Effort = ToEffort(effort) };

    /// <summary>
    /// 思考强度轴 → <c>thinking</c>。**只有 `off` 发这个键**（2026-11-03）：其余五档是「思考多深」，
    /// 由 <c>output_config.effort</c> 表达，`thinking` 保持不出现（= 请求字节与加这个档之前逐字节相同）。
    ///
    /// <para>⚠️ <b>关思考必须走这根杆</b>：<c>thinking: {"type": "disabled"}</c> 是 Anthropic 官方的
    /// 关闭写法（platform.claude.com/docs/en/build-with-claude/thinking），SDK 12.51.0 里就是
    /// <c>ThinkingConfigDisabled</c> 这个专门类型。⚠️ 只看 SDK 的 <c>Effort</c> 枚举会以为「没有关档」
    /// ——那正是第一版实现踩的坑（判据与证据见 <see cref="Kernel.Axes.EffortOff"/>）。</para>
    ///
    /// <para>⚠️ <b>它也是「取决于模型」的那一档</b>：Claude 的 adaptive-only 模型（Fable/Mythos 5.x）
    /// 对 <c>disabled</c> 同样回 400，Sonnet 5.5 的 extended thinking 已弃用、关不掉
    /// ⇒ 那些模型上这一档会**响亮地失败**。这是刻意的：用户明确要关，就不该让请求悄悄照旧思考。</para>
    /// </summary>
    private static ThinkingConfigParam? ToThinking(string effort) =>
        // ⚠️ 显式转一次：`ThinkingConfigDisabled → ThinkingConfigParam` 是 SDK 的隐式转换，
        // 而这行是三元表达式 + 目标类型可空，可空分析会把 `new` 那侧读成「可能为空」而报
        // CS8604（实测）。写死转换比关警告诚实。
        EffortParam.IsOff(effort) ? (ThinkingConfigParam)new ThinkingConfigDisabled() : null;

    // 思考强度轴 → 协议参数。SDK 支持五档：low / medium / high / xhigh / max，如实透传。
    // 未知值抛异常，不静默降级（否则拼写错误会悄悄跑成某一档）。
    // ⚠️ `off` 到不了这里：它在 `ToOutputConfig` 就分流成「不发」了。
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
