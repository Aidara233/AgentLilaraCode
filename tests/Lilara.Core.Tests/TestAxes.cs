using Lilara.Core.Kernel;
using Lilara.Core.Session;

namespace Lilara.Core.Tests;

/// <summary>
/// 在用例里**显式钉住一条轴的值**。
///
/// <para>⚠️ <b>为什么需要它</b>：2026-10-27 起 <c>cache</c> 轴的默认值是 <c>stable</c>
/// （用户裁定：前缀稳定优先）。而本仓有一大批用例是围绕 <c>dynamic</c> 形态的形状写的
/// ——「每步在位置 0 插一对读数」「记录里每步留一对占位」「一次调用 = 两条事件」。
/// 那些用例钉的是**一条真实存在的形态**，不该因为默认值变了就跟着改断言：
/// 把它们显式调到 <c>dynamic</c>，它们继续钉那条形态；默认形态由
/// <c>CacheStableTests</c> 与 <c>FloatingContextTests</c> 的那一组钉。
/// ⇒ 这样两条形态各有一组用例，而「默认是哪一个」只由 <c>Axes</c> 的表决定。</para>
///
/// <para>⚠️ **只改内存、不落盘**（`UpdateState`）：用例不需要多一次写，而 `StepAsync` 成功的那一步
/// 自然会把整份 state 写下去。</para>
/// </summary>
internal static class TestAxes
{
    public static SessionHandle WithAxis(this SessionHandle handle, string axis, string value)
    {
        handle.UpdateState(s => s with
        {
            Axes = new Dictionary<string, string>(s.Axes ?? Axes.Defaults(), StringComparer.Ordinal)
            {
                [axis] = value,
            },
        });

        return handle;
    }

    /// <summary>把会话钉到 <c>cache=dynamic</c>（旧形态）——见类型注释。</summary>
    public static SessionHandle DynamicCache(this SessionHandle handle) =>
        handle.WithAxis(Axes.Cache, Axes.CacheDynamic);
}
