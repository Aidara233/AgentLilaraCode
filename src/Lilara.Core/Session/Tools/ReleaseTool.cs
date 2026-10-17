using System.Text.Json;
using Lilara.Core.Kernel;

namespace Lilara.Core.Session.Tools;

/// <summary>
/// `release`——**画一条线**：这批资源我用完了（`01` §12.4）。
///
/// <para>⚠️ <b>它是「线」的第一个生产者。</b>在它之前，§12.4 那一整节是纸上的：
/// 线上的 `claim.release` 只改 `ClaimStore`、**时间线一个字不写** ⇒ 事后没有锚可倒查；
/// 而模型这一侧根本没有那个工具。⇒ 「回收按任务边界收」这句话**从来没有执行过**。</para>
///
/// <para>⚠️ <b>它不是「一个更聪明的 `session.gc`」</b>，两件事必须分清
/// （`00` §8.3 把「标记 / 回收分离」整条作废了，而这里不许把它偷偷装回来）：</para>
/// <list type="bullet">
///   <item><b>模型做的是「释放资源」</b>——一个正常的动作，它本来就该在做完一个任务之后做。
///   ⚠️ 它**不需要理解 GC**，参数里也没有一个字提到「回收」。</item>
///   <item><b>回收是这件事引发的</b>：<see cref="TimelineEvent.ReleaseEvent"/> 落进记录，
///   而 `Reclaim.Decide` 读它。⚠️ 判定的落点**不在这个工具里**——工具只负责**把线画下来**。</item>
/// </list>
///
/// <para>⚠️ <b>为什么这个工具不返回内容</b>：§11.2 给压缩写过同一句
/// （「工具结果为空……它的意义在于**标记**」）。它唯一的产出就是那条记录。
/// ⚠️ 它的结果是**一句不带内容的确认**——而**不能是空内容**：
/// `Reclaim.Recoverable` 对空内容给假（那条判据是对的：没有内容可收），
/// 于是一个**真的**落过的 `tool_use` 会被渲染成「这里本该有内容，但它丢了」——
/// 那是**一条关于丢失的假信息**，而它只是「本来就没有内容」。</para>
///
/// <para>⚠️ <b>它不释放锁</b>（今天）：§12.4 说 `release` 同时做三件事（解锁 / 触发回收 /
/// 清理残留），而**解锁那一半今天没有落点**——`ClaimStore` 的写手在
/// `CommandDispatcher.HandleClaimRelease`（命令层），而工具**够不着**它
/// （`IToolContext` 只读，见那里的注释）。⇒ 这一半**如实记在 §14**，不假装它做了。</para>
///
/// <para>⚠️ <b>`PermissionClass` = <see cref="PermissionClass.ReadOnly"/>，而这是有理由的</b>：
/// 它**什么都不改**——不写盘、不动别人的锁、不发外部请求。它做的唯一一件事是
/// **说出一句话**（「我用完了」）。⇒ 它不需要授权。
/// ⚠️ 而它**不是没有后果**的：那句话会让上下文变小。但那**不是**「需要许可的副作用」那一类
/// （`01` §3.5 那一列问的是「会不会改到世界」），而且**它有审计痕迹**（那条记录它自己）。</para>
/// </summary>
public sealed class ReleaseTool : ITool
{
    /// <summary>
    /// ⚠️ **工具名在这里是<b>常量</b>，而不是只由实例那个属性给**：写手
    /// （`Stepper.RecordRelease`）要按名字挑出这批调用里的 `release`，
    /// 而它拿不到实例（工具表是 `IReadOnlyList&lt;ITool&gt;`，推进器不认具体类型）。
    /// ⇒ 名字写成两遍就会漂，而漂的表现是**线一条都不落**——静默。
    /// </summary>
    public const string NameOf = "release";

    public string Name => NameOf;

    /// <summary>见类型注释：它什么都不改，只是说一句话。</summary>
    public PermissionClass PermissionClass => PermissionClass.ReadOnly;

    public string Description =>
        "说你用完了这一批文件——一个任务做完了、或者一摊子事收了尾的时候调它。"
        + "给 `scope`：这次用完了哪些路径（目录就行，不用逐个文件列）。"
        + "画完这条线，**这条线之前的**那些文件内容就不再留在对话里了（要用的时候还取得回）；"
        + "线之后读的东西不受影响。所以：**任务边界上再调**，别在中途调。";

    public string ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "scope": {
              "type": "array",
              "items": { "type": "string" },
              "description": "用完了的路径，相对于工作目录。给目录就够（`src` 会盖住 `src/` 下的一切）。"
            },
            "summary": {
              "type": "string",
              "description": "可省：这一段干了什么。省了就是不给摘要。"
            }
          },
          "required": ["scope"]
        }
        """;

    public Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct)
    {
        // ⚠️ **它不读文件、不解析路径**（不调 `context.ResolvePath`）：
        // scope 是**要被记下来**的东西，不是要被打开的东西——越界在这里没有意义
        // （越界的路径只会什么也匹配不上，见 `Reclaim.InScope` 的前缀判据）。
        // ⚠️ 而**规范化**（反斜杠 / 尾部斜杠）在判定那一侧做，且只有那一处
        // ——在这里也做一遍就是两份判据，而漂的表现是「释放了却收不到」，静默。

        // ⚠️ **scope 必须是数组、而且必须是字符串**：模型偶尔会发 `"src"`（一个裸串）
        // 或 `"src,lib"`（一段逗号分隔的话）。两种都不解析 ⇒ 这里回一句说得清的话，
        // **而不是悄悄当成一个路径**——那会让一次「释放 src 和 lib」变成「释放一个叫
        // `src,lib` 的路径」，**一条也收不掉**，而两边都以为说清了。
        // ⚠️ **非空也是约定的一半**：JSON Schema 里 `scope` 只写 `type: array`
        // （这个字符串原样发给 provider，保持最保守的形状）——空数组由上面那句显式拒绝。
        // ⚠️ **空数组也要回一句**：`ToolArgs.StringArray` 对 `[]` 返回**空列表**
        // （不是 `null`——`[]` 是模型明确说的「没有」，见那里的注释），而推进器
        // （`Stepper.RecordRelease`）对空 scope **不画线**。⇒ 这里只判 `null` 的话，
        // 一次「释放了个寂寞」会回一句「已释放。」，而记录里一条线都没有——
        // 模型以为收掉了，上下文一个字没变。两处判据必须是同一个。
        if (ToolArgs.StringArray(call.ArgumentsJson, "scope") is not { Count: > 0 })
        {
            return Task.FromResult(ToolText.Of(
                "`scope` 要给一个**非空**的路径数组，比如 [\"src\", \"docs\"]。"
                + "给单个字符串、一串逗号分隔的话、或者空数组，我都读不出来。"));
        }

        // 工具本身**什么都不做**：线的落点在推进器（它要写进记录）。
        // ⚠️ **两条执行路径各画一次**（2026-10-17）：`Stepper.StepAsync` 与本批挂起后
        // 用户批准的恢复路径 `Stepper.ResumeAsync`——两处都只给执行器交回的
        // 「真正执行过」的调用画线（`ToolExecutor.Batch.ExecutedCallIds`）。
        // ⇒ 这一句「已释放。」是**两个判据都成立之后**才说得出的话。
        // ⇒ 这条结果是一句确认，篇幅压到最短——它进上下文，而它没有信息量。
        return Task.FromResult(ToolText.Of("已释放。"));
    }
}
