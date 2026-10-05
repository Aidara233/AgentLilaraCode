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

    // File.AppendAllText 默认共享 FileShare.Read，并发两个 append 会在开句柄时抛
    // IOException（Windows：文件正被另一进程使用）——是硬失败，不是脏数据。
    [Fact]
    public async Task Append_ToleratesConcurrentWriters()
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

        await Task.WhenAll(Hammer("a", LogPath, errors), Hammer("b", LogPath, errors));

        Assert.Empty(errors);
    }

    // ReadAll 用 File.ReadLines（共享模式 FileShare.Read）会挡住并发的 append。
    // 必须让「我们自己的」reader 与 writer 并发——文件共享是双向协商，用别的方式开的读句柄测不到我们的代码。
    [Fact]
    public async Task ReadAll_DoesNotBlockAConcurrentAppend()
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

        await Task.WhenAll(reader, writer);

        Assert.Empty(errors);
    }

    // 顺序写的常规路径仍然正确。
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
