using Lilara.Core.Canonical;
using Lilara.Core.Persistence;
using Lilara.Core.Records;

namespace Lilara.Core.Tests;

public class HistoryLogTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "lilara-hist-" + Guid.NewGuid().ToString("N"));

    private string LogPath => Path.Combine(_dir, "history.jsonl");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static TimelineEvent Evt(string text) => new TimelineEvent.MessageEvent
    {
        At = DateTimeOffset.UnixEpoch,
        From = Speaker.User,
        Blocks = [new Block.TextBlock(text)],
    };

    /// <summary>
    /// ⚠️ **实测过的真缺陷**：`File.AppendAllText` 的默认共享模式是 <c>FileShare.Read</c>
    /// ——**只允许别人读**，所以并发两个 append 会在开句柄那一刻抛 <c>IOException</c>
    /// （Windows：文件正被另一进程使用）。**这不是脏数据，是硬失败**。
    /// 真实时延：控制台敲 `history` 读历史的同时，头发的消息 append——
    /// 而那条用户消息会**彻底消失**（append 在调模型之前，模型压根没被调用）。
    /// </summary>
    [Fact]
    public void Append_ToleratesConcurrentWriters()
    {
        var errors = new List<Exception>();

        static Task Hammer(string tag, string path, List<Exception> errors) => Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                try { HistoryLog.Append(path, Evt(tag)); }
                catch (Exception e) { lock (errors) errors.Add(e); }
            }
        });

        Task.WaitAll(Hammer("a", LogPath, errors), Hammer("b", LogPath, errors));

        Assert.Empty(errors);
    }

    /// <summary>
    /// ⚠️ **读挡写**：旧实现的 <c>ReadAll</c> 用 <c>File.ReadLines</c>，其共享模式是
    /// <c>FileShare.Read</c>——**只允许别人读，不允许别人写**。于是「读历史的同时来了条消息」
    /// 会让那个 append 抛 <c>IOException</c>。真实时延：控制台敲 `history` 读历史，
    /// 同时头发的消息 append（而那条消息会**彻底消失**——append 在调模型之前）。
    /// <para>⚠️ **测试形状的要害**：必须让**我们自己的** reader 与 writer 并发。
    /// 第一版写成「测试自己开一个 <c>FileShare.ReadWrite</c> 的读句柄」——那从一开始就不挡写，
    /// **新旧代码都绿**，是一条空转测试（控制器实测发现）。文件共享是**双向协商**：
    /// 拿别的方式开的读句柄去测「我们的读会不会挡写」，测的不是我们的代码。</para>
    /// </summary>
    [Fact]
    public void ReadAll_DoesNotBlockAConcurrentAppend()
    {
        for (var i = 0; i < 50; i++) HistoryLog.Append(LogPath, Evt($"预热{i}"));

        var errors = new List<Exception>();

        var reader = Task.Run(() =>
        {
            for (var i = 0; i < 300; i++)
            {
                try { HistoryLog.ReadAll(LogPath); }
                catch (Exception e) { lock (errors) errors.Add(e); }
            }
        });

        var writer = Task.Run(() =>
        {
            for (var i = 0; i < 300; i++)
            {
                try { HistoryLog.Append(LogPath, Evt($"写{i}")); }
                catch (Exception e) { lock (errors) errors.Add(e); }
            }
        });

        Task.WaitAll(reader, writer);

        Assert.Empty(errors);
    }

    /// <summary>顺序写的常规路径仍然正确（守卫别把正常情况改坏）。</summary>
    [Fact]
    public void Append_AndReadAll_RoundTripInOrder()
    {
        HistoryLog.Append(LogPath, Evt("一"));
        HistoryLog.Append(LogPath, Evt("二"));

        var events = HistoryLog.ReadAll(LogPath);

        Assert.Equal(2, events.Count);
        Assert.Equal("一", ((Block.TextBlock)((TimelineEvent.MessageEvent)events[0]).Blocks[0]).Text);
        Assert.Equal("二", ((Block.TextBlock)((TimelineEvent.MessageEvent)events[1]).Blocks[0]).Text);
    }
}
