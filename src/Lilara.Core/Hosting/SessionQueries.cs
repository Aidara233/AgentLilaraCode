using Lilara.Core.Kernel;
using Lilara.Core.Session;

namespace Lilara.Core.Hosting;

/// <summary>
/// 快照读取。
///
/// 抽出来是因为控制台的 `history` / `status` 与头的 `GET /sessions/{id}/history` / `state`
/// 读的是**同一份东西**——各写一份就是复制语义。两个前端走同一个读取点，形状就不会漂。
/// </summary>
public static class SessionQueries
{
    /// <summary>
    /// 会话状态。
    /// **先查内存激活表，miss 才回落磁盘**——这不是优化，是**正确性**：
    /// 步进行中磁盘上的状态是 **Idle**（`Running` 只写内存、到步末才落盘）⇒
    /// 头渲染「空闲」⇒ 用户以为没在跑 ⇒ **再点一次发送** ⇒ 队列两条 ⇒ 烧钱。
    /// </summary>
    public static SessionState StateOf(CoreHost host, SessionHandle handle) =>
        host.TryGetActivated(handle.SessionId, out var live) ? live.State : handle.State;

    /// <summary>
    /// 时间线——**这一刻的形态**（库引用解开、被回收的换成注记、`fetch` 的引用解开）。
    /// 未激活的会话从磁盘读；已激活的也读磁盘——因为 `history.jsonl` **只追加**，
    /// 磁盘就是事实（不像 `State` 有「内存领先」的窗口）。
    ///
    /// <para>⚠️ <b>它必须与 `Stepper` 喂给模型的那份是同一个函数</b>（`TimelineRender.Render`）：
    /// 各写一份的话，用户看到的与模型看到的会**悄悄分叉**——而两边各自都「看起来是对的」，
    /// 这正是本仓栽过的形状（两处副本只改一处）。</para>
    /// </summary>
    public static IReadOnlyList<TimelineEvent> HistoryOf(CoreHost host, SessionHandle handle) =>
        Session.TimelineRender.Render(host.Store, handle);

    /// <summary>助手回合的可显示正文。</summary>
    /// <para>回复可能**只有推理块、没有正文**（推理块确实会落盘），于是正文是空串——
    /// 不能印成一个光秃秃的 `⏺  `，那看起来像渲染坏了。
    /// 推理块的 Text 只是摘要，**显示与否是显示层的事，块本身不能因此丢**。</para>
    public static string ModelBody(IReadOnlyList<Block> blocks)
    {
        var text = string.Concat(blocks.OfType<Block.TextBlock>().Select(b => b.Text));
        return text.Length == 0 ? "（这一步没有正文输出）" : text;
    }

    /// <summary>用户回合的可显示正文（控制台只拼文本块；附件是头的事）。</summary>
    public static string UserBody(IReadOnlyList<Block> blocks) =>
        string.Concat(blocks.OfType<Block.TextBlock>().Select(b => b.Text));
}
