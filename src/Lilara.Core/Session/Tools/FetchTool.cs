using System.Text;
using Lilara.Core.Kernel;

namespace Lilara.Core.Session.Tools;

/// <summary>
/// `fetch`——「**我当时看到了什么**」。
///
/// <para>⚠️ <b>它是普通工具，不是核心特权</b>（`01` §8.0.2 六，2026-10-06 用户裁定）：
/// 它只是「读存档 + 返回一个工具结果」，而它读到的一切核心本来就都能读
/// ⇒ 不对称的特权本来就是空头。它只需要句柄里有**数据根**（`04` §5.1），
/// 而实例化它不需要核心做任何额外的事。</para>
///
/// <para>⚠️ <b>它必须常驻工具表</b>（§8.0.2 五）：工具表是前缀指纹的一部分，
/// 而「有回收物时才出现」会让**第一次回收**全废一次前缀——正好抵消回收省下的东西。
/// ⇒ 它与有没有回收物无关，一直在那儿。</para>
///
/// <para>⚠️ <b>它必须是独立工具</b>，不能做成 `read(path, 旧版: true)`：
/// 那样模型得先判断「这个工具有没有旧版模式」，正是 §8.0.2 三要消除的那种纠结。</para>
///
/// <para>⚠️ <b>取回 ≠ 重调</b>（§8.0.2 六那张表）：取回答「我当时看到了什么」，
/// 代价是读一次库、无副作用；重调答「现在是什么」，代价是做一次副作用。
/// 两者都由**模型自己**选方向——核心不替它分。</para>
///
/// <para>⚠️ <b>它不跑任何工具代码</b>（§8.0.2 二）：取回 = 读 `store/&lt;sha256&gt;` 原样交回。
/// <b>重渲染不是恢复**——重渲染要过 `render(贡献, now)`，而 `now` 一步一个 ⇒ 交回的是
/// 另一个时刻的产物，那不叫恢复，叫「重造一份看起来像的」。</para>
/// </summary>
public sealed class FetchTool : ITool
{
    public string Name => "fetch";

    /// <summary>只读：取回旧结果是读存档（`01` §8.0.2 六——它<b>不跑任何工具代码</b>）。</summary>
    public PermissionClass PermissionClass => PermissionClass.ReadOnly;


    public string Description =>
        "取回一条**旧工具结果**当时的内容（被回收之后你只看到一条 [已回收] 注记，"
        + "注记里有它的 id）。给的是**当时那份字节**，不是现在的值——"
        + "想要现在的值，自己重调一次那个工具。";

    public string ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "id": {
              "type": "string",
              "description": "[已回收] 注记里给出的那个 id。"
            }
          },
          "required": ["id"]
        }
        """;

    public Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct)
    {
        var id = ToolArgs.String(call.ArgumentsJson, "id");

        if (string.IsNullOrWhiteSpace(id))
            return Task.FromResult(ToolText.Of("缺少 `id` 参数。要取回哪一条结果？"));

        // ⚠️ **句柄交回的是「当时那串块」，不是一个地址、也不是一段文本**——
        // 一条结果可以是「这是那张图」+ 图那样一串块，也可以是纯图、一个字都没有
        // （`01` §3.6 ①之一）。⇒ 原样交回，**不折成一段文本**：
        // 折了之后图没了而描述还在 ⇒ 多一条**假信息**（图上写着 42，图已经不在了）。
        if (context.SnapshotOf(id) is not { } blocks)
        {
            // ⚠️ 取不到要合成**一句人话**，而不是一个空结果：空结果会让模型
            // 以为「那次调用没有输出」，而它其实只是**找不到了**（`01` §8.0.2 六末）。
            return Task.FromResult(ToolText.Of(
                $"取不回 id 为 {id} 的那条结果：没有这一条（可能写错了 id，"
                + "或者它所在的那段历史已经被截断）。"));
        }

        // ⚠️ **取回的永远是「一串块」，不是一条 `Ref`**（`01` §8.0.2 六「解到最底」）：
        // 句柄走的是**库地址**那条路（`SnapshotOf` → `TimelineRender.Dereference`），
        // 取回来的是**当时的字节**，所以 `fetch(fetch(x) 的 id)` 天然不会串成链。
        //
        // ⚠️ 而这条性质**不是可有可无的**：`TimelineRender` 解 `Ref` 时**不递归**
        // （它只解一层）。⇒ 若哪天 `SnapshotOf` 改成返回引用而不是内容，
        // 一条指向 `fetch` 自己的引用会在渲染时解出一串空内容——**静默地少一段**。
        // 那条测试（`FetchTests` 里钉形状的那条）就是为此存在的。
        //
        // ⚠️ **不包信封、不注入 `tool_use`**（§8.0.2 六那两条硬理由）：
        // 身份信息（id 在参数里、是什么工具在注记里）不需要塞进结果，再包一层是同一件事说两遍；
        // 而伪造一条 `tool_use` 会让**记录谎报「模型看到过什么」**，还顺手砸掉头存在的理由。
        return Task.FromResult(new ToolOutcome { Content = blocks });
    }
}
