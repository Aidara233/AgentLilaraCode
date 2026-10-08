using Lilara.Core.Kernel;

namespace Lilara.Core.Session.Tools;

/// <summary>
/// **一步之内**执行模型的全部工具调用。
///
/// <para>⚠️ <b>三件事在这里定死，而它们的次序不是随意的</b>（`01` §3.6 ③④）：</para>
/// <list type="number">
///   <item><b>顺序 = 调用序</b>（模型发出的次序），**不是完成序**——
///   否则重放时记录会漂，而记录是事实（`01` §4）。</item>
///   <item><b>快照进库由这里做</b>，不是工具自己做。判据是 `BlobStore.InlineThresholdBytes`
///   一处判，而工具因此可以真的是只读的（`04` §5.1）。</item>
///   <item><b>失败是内容</b>，不是异常——见 <see cref="ToolOutcome"/>。</item>
/// </list>
/// </summary>
/// <param name="tools">工具表。⚠️ 它是**派生**的（`04` §1.3），不是注册表；
/// 名字查不到时走「这个工具不存在」，那条**也是内容**（与「工具在、但被拒」要分开显示，
/// 但今天那条线还没做，见下）。</param>
/// <param name="blobs">库。⚠️ **今天只读**：写入方是下面的 <see cref="Materialize"/>。</param>
/// <param name="permissionLevel">
/// 当前权限档位（`01` §3.5 的 `permission` 轴值）。
/// ⚠️ <b>它没有默认值，而这是刻意的</b>：给一个默认（哪怕是 `null`）就等于让
/// 「用哪个档位」变成**沉默的选择**——而那正是 `04` §5 警告过的那种默认
/// （未声明的东西会被静静地用掉）。⇒ 每个调用点**必须写出来**。
/// ⚠️ <b>`null` 是合法的，且表示"未知档位"</b>，而 <see cref="Gate.Decide"/> 把未知
/// 当作**最严的那一档**（宁可漏放不可错放）。
/// </param>
public sealed class ToolExecutor(IReadOnlyList<ITool> tools, Storage.BlobStore blobs, string? permissionLevel)
{
    /// <summary>
    /// 全部执行完，返回与入参**一一对应、同序**的结果。
    ///
    /// <para>⚠️ <b>今天循环执行（串行），而这是刻意的偏离</b>：`01` §4 说「步内默认并发」。
    /// 偏离的理由是<b>本批只有两个纯读工具</b>——并发零收益，而取消钩子打进一个执行
    /// 比打进 N 个并发简单得多。⚠️ <b>改成并发不需要动别的</b>：本方法下面的「按调用序」
    /// 是<b>结构</b>保证的（结果按 <c>callId</c> 归位），与执行策略无关
    /// ⇒ 换成 <c>Task.WhenAll</c> 时，缓冲、排序、落盘全都不用动。</para>
    /// </summary>
    public async Task<IReadOnlyList<TimelineEvent.ToolResultEvent>> ExecuteAsync(
        IReadOnlyList<Block.ToolUseBlock> calls, IToolContext context, CancellationToken ct)
    {
        var results = new List<TimelineEvent.ToolResultEvent>(calls.Count);
        var byName = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);

        foreach (var call in calls)
        {
            // ⚠️ **三路，次序是刻意的**：不存在的工具 → 闸门 → 执行。
            // ⚠️ **被拒的调用绝不 `RunAsync`**——否则就是「拒了，但还是删了库」。
            // ⚠️ 闸门在这里（**执行期**）而不是工具表里（请求期）：那是 `01` §9.2 的分工，
            // 改闸门**零前缀代价**，而改工具表破最前端。
            var outcome = byName.TryGetValue(call.Name, out var tool)
                ? Gate.Decide(tool.PermissionClass, permissionLevel) is Verdict.Allow
                    ? await tool.RunAsync(new ToolCall(call.CallId, call.Name, call.Arguments), context, ct)
                    : ToolText.Of(Gate.DenyReason(tool.PermissionClass, permissionLevel))
                : new ToolOutcome { Content = [new Block.TextBlock($"没有名为 '{call.Name}' 的工具。")] };

            results.Add(new TimelineEvent.ToolResultEvent
            {
                // ⚠️ 本记录的稳定 id（与协议 id `CallId` **不是一回事**）。见 `01` §8.0.2 第七节。
                Id = Ids.New(),
                At = CoreClock.Now(),
                CallId = call.CallId,
                Content = Reclaim.Materialize(outcome.Content, blobs),
                // ⚠️ 框架**不进 `Materialize`**：它是**事实**（薄薄几个字段），
                // 不是"内容"——把它塞进库会为几十个字节开一个对象，而
                // `Materialize` 的判据（`InlineThresholdBytes`）管的是**字节量**。
                Framing = outcome.Framing,
                DependsOn = outcome.DependsOn,
                Fingerprint = outcome.Fingerprint,
            });
        }

        return results;
    }
}
