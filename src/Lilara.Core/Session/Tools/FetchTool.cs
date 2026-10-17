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
        "取回一条**旧工具结果**当时的原文。被回收的注记写着 `fetch(id=…)`；"
        + "被**截断**的注记写着 `fetch(id=…, offset=…)`——照它给的位置接着读即可，不必重跑工具。"
        + "给的是**当时那份字节**，不是现在的值。"
        + "`offset` 是字节位置（0 基），`length` 最多取多少字节；都不给 = 从头取（单次仍有上限）。";

    public string ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "id": {
              "type": "string",
              "description": "注记里给出的那个稳定 id。"
            },
            "offset": {
              "type": "integer",
              "description": "从这个字节开始取（0 基）。截断注记里给的就是这个位置。省略 = 0。"
            },
            "length": {
              "type": "integer",
              "description": "最多取多少字节（正整数）。省略 = 取到结果尾（但单次仍有上限）。"
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
        if (!ToolPaths.HasKey(call.ArgumentsJson, "offset") && !ToolPaths.HasKey(call.ArgumentsJson, "length"))
        {
            if (context.SnapshotOf(id) is not { } blocks)
                return Task.FromResult(NotFound(id));

            // ⚠️ **取回的永远是「一串块」，不是一条 `Ref`**（`01` §8.0.2 六「解到最底」）：
            // 句柄走的是**库地址**那条路（`SnapshotOf` → `TimelineRender.Dereference`），
            // 取回来的是**当时的字节**，所以 `fetch(fetch(x) 的 id)` 天然不会串成链。
            //
            // ⚠️ **不包信封、不注入 `tool_use`**（§8.0.2 六那两条硬理由）：
            // 身份信息（id 在参数里、是什么工具在注记里）不需要塞进结果，再包一层是同一件事说两遍；
            // 而伪造一条 `tool_use` 会让**记录谎报「模型看到过什么」**。
            return Task.FromResult(new ToolOutcome { Content = blocks });
        }

        // ---- 窗口取回：只对「单文本块」的结果成立 ----
        var offset = ToolArgs.Number(call.ArgumentsJson, "offset") ?? 0;
        if (offset < 0) return Task.FromResult(ToolText.Of($"`offset` 不能是负数（给的是 {offset}）。"));

        long? length = null;
        if (ToolPaths.HasKey(call.ArgumentsJson, "length"))
        {
            length = ToolArgs.Number(call.ArgumentsJson, "length");
            if (length is not { } l || l <= 0)
                return Task.FromResult(ToolText.Of("`length` 要是正整数（最多取多少字节）。"));
        }

        if (context.RawBytesOf(id) is not { } bytes)
        {
            // ⚠️ 两种「按字节取不了」要分开说：**没有这条**（id 错 / 被截断的历史）
            // 与**形状不对**（混排 / 图片）。用户的下一步动作不同。
            return Task.FromResult(context.SnapshotOf(id) is null
                ? NotFound(id)
                : ToolText.Of(
                    $"id {id} 的那条结果不是单文本内容（可能含图片或多个块），不能按字节偏移取回；"
                    + "去掉 `offset` / `length` 可以取回它的全部内容。"));
        }

        if (offset >= bytes.Length)
        {
            // ⚠️ **不是空结果**：空结果会让模型以为「那次调用没有输出」，
            // 而它其实只是**取过头了**（同 `read` 那条判据）。
            return Task.FromResult(ToolText.Of(
                $"这条结果一共 {bytes.Length} 字节，从第 {offset} 字节之后就没有内容了。"));
        }

        // ⚠️ 窗口上限 = **保存上限**，不是 `MaxResultBytes`：取回是模型点名要的一段，
        // 而投递预算对 `fetch` 也是保存上限（`ToolLimits.DeliveryBudgetOf`）——
        // 两边共用一个数，取回来的东西才不会在渲染时又被砍掉。
        var count = Math.Min(Math.Min(length ?? ToolLimits.MaxToolOutputBytes, ToolLimits.MaxToolOutputBytes),
            bytes.Length - offset);
        var window = bytes.AsSpan((int)offset, (int)count);
        var text = DecodeFixed(window, offset > 0, offset + count < bytes.Length);

        return Task.FromResult(new ToolOutcome
        {
            Content = [new Block.TextBlock(text)],
            // ⚠️ 范围写进框架（`01` §3.7）：被截断这件事必须**明说**，
            // 否则模型会把「前一段」当成全文——那是「多了一条假信息」。
            Framing = new ToolFraming("fetch", [
                new ToolFramingField("id", id),
                new ToolFramingField("第", $"{offset}–{offset + count}"),
                new ToolFramingField("共", bytes.Length.ToString()),
            ]),
        });
    }

    private static ToolOutcome NotFound(string id) => ToolText.Of(
        $"取不回 id 为 {id} 的那条结果：没有这一条（可能写错了 id，"
        + "或者它所在的那段历史已经被截断）。");

    /// <summary>
    /// 把一段**可能是半截字符**的字节解成文本，必要时往两侧对齐到字符边界。
    /// ⚠️ 与 `read` 的 `Decode` 同一判据（剥掉首尾不完整的序列，而不是换成 U+FFFD）：
    /// 边界处的半个字符**根本不是内容**——它是我们切出来的。
    /// </summary>
    private static string DecodeFixed(ReadOnlySpan<byte> bytes, bool trimHead, bool trimTail)
    {
        var start = 0;
        var end = bytes.Length;

        if (trimHead)
            while (start < end && (bytes[start] & 0xC0) == 0x80) start++;

        if (trimTail)
        {
            var i = end - 1;
            while (i >= start && (bytes[i] & 0xC0) == 0x80) i--;
            if (i >= start)
            {
                var need = bytes[i] switch
                {
                    >= 0xF0 => 4,
                    >= 0xE0 => 3,
                    >= 0xC0 => 2,
                    _ => 1,
                };
                if (i + need > end) end = i;
            }
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false)
            .GetString(bytes[start..end]);
    }
}
