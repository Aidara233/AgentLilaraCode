namespace Lilara.Core.Adapters;

// 取消不是失败：调用方的 token 被取消了就原样放行（Ctrl-C / session.interrupt），
// 但 HttpClient 超时也以 TaskCanceledException 到达，那是一步真失败、必须包。
// 判据是「调用方自己的 token 被取消了没有」，不是异常类型。
public static class Cancellation
{
    public static bool ShouldWrap(Exception ex, CancellationToken ct) =>
        ex is not OperationCanceledException || !ct.IsCancellationRequested;
}
