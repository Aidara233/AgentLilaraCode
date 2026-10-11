using System.Text;
using Lilara.Core.Kernel;
using Lilara.Core.Storage;

namespace Lilara.Core.Session;

/// <summary>
/// **`/fetch &lt;id&gt;`——把一条旧工具结果取回来**（`01` §8.0.2 六）。
///
/// <para>⚠️ <b>为什么控制台需要它</b>：控制台是**控制面的第二个客户端**（`04` §1.1），
/// 而它看的 `history` 与模型看的是**同一份渲染**（`SessionQueries.HistoryOf`）
/// ⇒ 它也会**看见注记**（`[已回收] … fetch(id="…")`）。看见一个地址却够不着它，
/// 是这条链上最后一处只写了一半的地方。</para>
///
/// <para>⚠️ <b>语义与模型那条<b>逐字相同</b></b>：读 <c>store/&lt;sha256&gt;</c>
/// **原样交回**——不跑任何工具代码、**不重渲染**（`01` §8.0.2 六）。
/// 「那时是什么样」与「现在是什么样」是两个问题，后者让用户自己重跑那条命令。</para>
///
/// <para>⚠️ <b>只认<b>记录</b>的稳定 id，不认协议 `CallId`</b>（`01` §8.0.2 七）：
/// 注记里给的是前者，而两者看起来都是短十六进制串——认错一个的表现是
/// 「明明有这条却说没有」。⇒ 找不到时那句话要把**这件事**说出来，而不是只说「没找到」。</para>
///
/// <para>⚠️ <b>它是只读的</b>：不改盘、不改任何内存状态、不碰事件总线。
/// 「取回」这件事在记录上**没有痕迹**——取回不是一次工具调用（它没有 `tool_use` 可配对），
/// 所以它**不能**做成一条塞回时间线的工具调用，那会留下悬空的一对。</para>
/// </summary>
public static class FetchQuery
{
    /// <summary>
    /// 按 id 取回并在控制台上渲染。返回的是**给人看的那段文本**。
    ///
    /// <para>⚠️ <b>三种「拿不到」分开说</b>，因为用户的下一步动作不同：</para>
    /// <list type="number">
    ///   <item><b>没有这条记录</b> ⇒ 核 id（或它被截断了）。⚠️ 顺手把「id 有两个」
    ///   这件事说出来——`CallId` 与记录 id 都是短十六进制串，认错是**最可能的**原因。</item>
    ///   <item><b>有这条记录，但它不是工具结果</b> ⇒ 换个对象（用户消息与模型消息也有 id）。</item>
    ///   <item><b>有记录、可内容指向的库对象没了</b> ⇒ 这一档由
    ///   <see cref="TimelineRender.Dereference"/> 渲染成一句「找不到了」，
    ///   与模型看到的是**同一个字符串**——别在这里另写一句。</item>
    /// </list>
    /// </summary>
    public static string ById(SessionStore store, SessionHandle handle, string id)
    {
        foreach (var evt in store.ReadTimeline(handle.ProjectKey, handle.SessionId))
        {
            if (!string.Equals(evt.Id, id, StringComparison.Ordinal)) continue;

            if (evt is not TimelineEvent.ToolResultEvent result)
            {
                return $"`{id}` 是这条时间线上的一条记录，但它不是**工具结果**"
                     + $"（它是 {evt.GetType().Name}）。只有工具结果才取回得了。";
            }

            // ⚠️ 解引用走**同一份实现**：库对象有两种存法（原文 / 块数组 JSON），
            // 读的人必须分得开——这里另写一遍就会漂，而漂的后果是把一段 JSON 交给人看。
            return Render(TimelineRender.Dereference(result, new BlobStore(store.DataRoot)).Content);
        }

        return $"这个会话里没有 id 为 `{id}` 的记录——可能写错了，也可能它所在的那段历史已经被截断。"
             + "⚠️ 注意**有两个 id**：注记里给的是**记录**的 id，而 `tool_use` 上那个是**协议** id，"
             + "两者看起来都是短十六进制串，认错就会走到这里。";
    }

    /// <summary>
    /// 把取回的内容渲染成终端上的一段字。
    ///
    /// <para>⚠️ <b>文本块原样、不加任何包装</b>——「取回 = 交回当时的字节」这条语义
    /// 对人和对模型是同一个（`01` §8.0.2 六：不包信封、不加身份信息）。
    /// ⚠️ 图片与别的非文本块**给一句说得出的话**而不是把地址糊上去：
    /// 把 64 位哈希打到终端上既没用又淹屏，而「控制台显示不了」是一件**说得清**的事。</para>
    /// </summary>
    private static string Render(IReadOnlyList<Block> content)
    {
        var sb = new StringBuilder();
        foreach (var b in content)
        {
            sb.AppendLine(b switch
            {
                Block.TextBlock t => t.Text,
                Block.ImageBlock i => $"（这是一张图片：{i.MediaType}）；控制台显示不了它。",
                Block.StoreBlock s => $"（这里本该有一段内容，但库对象 {s.Sha256[..8]}… 找不到了）",
                _ => $"（这一块是 {b.GetType().Name}，控制台显示不了。）",
            });
        }

        var text = sb.ToString().TrimEnd('\n', '\r');

        // ⚠️ **空内容要吭一声**：不吭的话用户看到的是「敲了回车什么都没发生」，
        // 与「命令没被认出来」长得一模一样（那正是本仓在 `session.gc` 的回执里
        // 专门写一句「没有可回收的结果」所要躲开的那个形状）。
        return text.Length == 0 ? "（这条结果当时就是空的——它没有输出任何内容）" : text;
    }
}
