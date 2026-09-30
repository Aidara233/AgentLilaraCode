namespace Lilara.Core.Adapters;

/// <summary>
/// 取消 vs 失败的判据——**抽出来之前它在三个地方各写了一遍**
/// （`ClaudeAdapter` / `OpenAiHttp`，而控制面正要写第三份）。
///
/// <para>⚠️ **取消不是失败**：用户按 Ctrl-C / 发 `session.interrupt` 要原样冒出去
/// （`03` §3 `session.interrupt`、`01` §9 的工具取消钩子），不能被报成「模型调用失败」。
/// 但反过来，<c>HttpClient</c> 超时**也**是以 <c>TaskCanceledException</c>
/// （→ <c>OperationCanceledException</c>）到达的，那是一次**真失败**，必须包。</para>
///
/// <para>⇒ 判据是「**调用方自己的 token 被取消了没有**」，**不是异常类型**。
/// 这一条必须只有一份：三份各自演化的话，某一处判反了就是
/// 「按 Ctrl-C 报成了模型失败」或「超时被静默吞掉」——**两种都只在特定时机显形**。</para>
/// </summary>
public static class Cancellation
{
    /// <summary>
    /// 这个异常该不该包成 <see cref="ModelCallFailedException"/>？
    /// ⚠️ 它就是 <c>catch (Exception ex) when (Cancellation.ShouldWrap(ex, ct))</c> 的那个谓词。
    /// </summary>
    public static bool ShouldWrap(Exception ex, CancellationToken ct) =>
        ex is not OperationCanceledException || !ct.IsCancellationRequested;
}
