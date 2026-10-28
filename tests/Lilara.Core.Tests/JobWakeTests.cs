using Lilara.Core.Hosting;
using Lilara.Core.Kernel;
using Lilara.Core.Session;
using Lilara.Core.Session.Tools;
using Lilara.Core.Tests.Fakes;

namespace Lilara.Core.Tests;

public sealed class JobWakeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinishedJob_IsPaired_AndDoesNotDismissPendingQuestion(bool asking)
    {
        var root = Path.Combine(Path.GetTempPath(), "lilara-wake-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var host = new CoreHost(root, root);
        try
        {
            var handle = SessionHandle.Create(host.Store, root, new ModelSelection
            {
                ProviderId = "test", ModelId = "test", Protocol = Protocols.ClaudeMessages,
                Effort = "high", MaxTokens = 1000,
            });
            host.Register(handle);
            var adapter = new FakeAdapter().Reply("consumed report");
            var dispatcher = new CommandDispatcher(host, new EventBus(), _ => new Stepper(host.Store, adapter));
            await dispatcher.DispatchAsync(new CoreCommand.JobsList(handle.SessionId), "wire", false, CancellationToken.None);
            if (asking)
                handle.UpdateState(s => s with { Status = SessionStatus.WaitingForInput,
                    Ask = new PausedAsk("ask", AskKind.Question, "c", "ask", "hash", null) });
            Assert.True(ShRunner.TryLocate(null, ShRunner.DefaultExists, out var sh, out _));
            var job = host.BackgroundJobs.Start(handle.SessionId,
                ShRunner.ShellRequest(sh, "echo wake-output", root, "wake test")).Job!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!host.Store.ReadTimeline(handle.ProjectKey, handle.SessionId)
                .OfType<TimelineEvent.ToolResultEvent>().Any(r => r.CallId == job.Id))
                await Task.Delay(20, timeout.Token);
            var history = host.Store.ReadTimeline(handle.ProjectKey, handle.SessionId);
            Assert.Single(history.OfType<TimelineEvent.MessageEvent>()
                .SelectMany(m => m.Blocks.OfType<Block.ToolUseBlock>()), c => c.CallId == job.Id);
            if (asking)
            {
                Assert.Equal(SessionStatus.WaitingForInput, handle.State.Status);
                Assert.NotNull(handle.State.Ask);
                Assert.Empty(adapter.Calls);
            }
            else
            {
                while (handle.State.Turn == 0 || handle.IsRunning) await Task.Delay(20, timeout.Token);
                Assert.Single(adapter.Calls);
            }
        }
        finally
        {
            host.BackgroundJobs.KillAll("test cleanup");
            Directory.Delete(root, true);
        }
    }
}
