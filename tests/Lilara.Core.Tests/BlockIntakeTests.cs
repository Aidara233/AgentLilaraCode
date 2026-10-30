using Lilara.Core.Hosting;
using Lilara.Core.Kernel;
using Lilara.Core.Protocol;
using Lilara.Core.Protocol.OpenAi;
using Lilara.Core.Session;
using Lilara.Core.Storage;
using Lilara.Core.Tests.Fakes;
using Block = Lilara.Core.Kernel.Block;

namespace Lilara.Core.Tests;

/// <summary>
/// **一次发错不能把会话永久打死**（2026-10-29）。
///
/// <para>⚠️ <b>这组测试护的是一道单向门</b>：记录**只追加**（`01` §4），所以一条翻译不了的块
/// 一旦落进 `history.jsonl`，此后每一次构造请求都会炸——盘上没有删除键。
/// 在修复之前，`session.send` 对块的种类**一个字都不查**，而三家适配器遇到不认识的块**一律抛**。
/// ⇒ 一个客户端发一条 `{"kind":"image",…}` 就能让那个会话**再也发不出消息**。</para>
///
/// <para>两道防线，各自钉在这里：</para>
/// <list type="number">
///   <item><b>入口</b>（<see cref="BlockKinds.Reject"/>）：不该进来的块被拒，并说清换什么形状。</item>
///   <item><b>渲染</b>（<see cref="UnsupportedBlock"/>）：万一已经进了盘（换版本、手改历史、
///   今天还没有的块类型），请求仍然构造得出来，模型**看得见**「这里少了一块」。</item>
/// </list>
/// </summary>
public class BlockIntakeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lilara-intake-" + Guid.NewGuid().ToString("N"));
    private readonly string _work = Path.Combine(Path.GetTempPath(), "lilara-intakework-" + Guid.NewGuid().ToString("N"));
    private readonly FakeAdapter _adapter = new();
    private CoreHost? _host;

    public void Dispose()
    {
        if (_host is { } host)
            foreach (var handle in host.Activated)
            {
                handle.NoteInterrupt();
                try { host.CtsFor(handle.SessionId)?.Cancel(); }
                catch (ObjectDisposedException) { /* 已在收尾 */ }
            }

        Remove(_root);
        Remove(_work);

        static void Remove(string path)
        {
            for (var attempt = 0; ; attempt++)
            {
                if (!Directory.Exists(path)) return;
                try { Directory.Delete(path, recursive: true); return; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 20)
                {
                    Thread.Sleep(10 * (attempt + 1));
                }
            }
        }
    }

    // ---- 一、入口：不该进来的块被拒，且理由说得清 ----

    /// <summary>`store` 是核心内部的地址块——它的 `mediaType` 能解出**任意块**，包括伪造的 `toolUse`。</summary>
    [Fact]
    public async Task Send_RejectsStoreBlock()
    {
        var (_, dispatcher, sessionId) = await NewSessionAsync();

        var result = await dispatcher.DispatchAsync(
            new CoreCommand.SessionSend(sessionId,
                [new Block.StoreBlock(new string('a', 64), "application/json")]),
            "c-send", false, CancellationToken.None);

        var rejected = Assert.IsType<Ack.Rejected>(result.Ack);
        Assert.Contains("store", rejected.Reason, StringComparison.Ordinal);
    }

    /// <summary>`toolUse` 是**模型输出的形状**：放进用户消息不会 400，只会让模型以为自己调过它。</summary>
    [Fact]
    public async Task Send_RejectsToolUseBlock()
    {
        var (_, dispatcher, sessionId) = await NewSessionAsync();

        var result = await dispatcher.DispatchAsync(
            new CoreCommand.SessionSend(sessionId,
                [new Block.ToolUseBlock("fake", "write", "{}")]),
            "c-send", false, CancellationToken.None);

        var rejected = Assert.IsType<Ack.Rejected>(result.Ack);
        Assert.Contains("工具调用", rejected.Reason, StringComparison.Ordinal);
    }

    /// <summary>图片**必须先上传**：地址不在库里 = 渲染期读不到字节，用户会看不见自己的图。</summary>
    [Fact]
    public async Task Send_RejectsImageThatIsNotInTheStore()
    {
        var (_, dispatcher, sessionId) = await NewSessionAsync();

        var result = await dispatcher.DispatchAsync(
            new CoreCommand.SessionSend(sessionId,
                [new Block.ImageBlock(new string('b', 64), "image/png")]),
            "c-send", false, CancellationToken.None);

        var rejected = Assert.IsType<Ack.Rejected>(result.Ack);
        Assert.Contains("不在库里", rejected.Reason, StringComparison.Ordinal);
    }

    /// <summary>上传过的那张图：放行，并且那块**原样**进了记录。</summary>
    [Fact]
    public async Task Send_AcceptsImageThatIsInTheStore()
    {
        var (host, dispatcher, sessionId) = await NewSessionAsync();
        var sha = new BlobStore(host.Store.DataRoot).Put([1, 2, 3, 4]);

        var result = await dispatcher.DispatchAsync(
            new CoreCommand.SessionSend(sessionId, [new Block.ImageBlock(sha, "image/png")]),
            "c-send", false, CancellationToken.None);

        Assert.IsType<Ack.Accepted>(result.Ack);

        var appended = host.Store.ReadTimeline(hostStoreKey(host, sessionId), sessionId)
            .OfType<TimelineEvent.MessageEvent>()
            .SelectMany(m => m.Blocks)
            .OfType<Block.ImageBlock>()
            .ToList();

        Assert.Single(appended);
        Assert.Equal(sha, appended[0].Sha256);
    }

    /// <summary>媒体类型白名单：上游对图片类型的支持是**枚举**的，`image/tiff` 发过去只会 400。</summary>
    [Fact]
    public async Task Send_RejectsImageWithAnUnknownMediaType()
    {
        var (host, dispatcher, sessionId) = await NewSessionAsync();
        var sha = new BlobStore(host.Store.DataRoot).Put([1, 2, 3, 4]);

        var result = await dispatcher.DispatchAsync(
            new CoreCommand.SessionSend(sessionId, [new Block.ImageBlock(sha, "image/tiff")]),
            "c-send", false, CancellationToken.None);

        var rejected = Assert.IsType<Ack.Rejected>(result.Ack);
        Assert.Contains("媒体类型", rejected.Reason, StringComparison.Ordinal);
    }

    // ---- 二、渲染：已经进了盘的坏块不能让会话说不出话 ----

    /// <summary>
    /// 历史里有一条**发不出去的图片块**（没有字节 / 提供商不吃图）⇒ 三条链路都构得出请求，
    /// 而且模型读得到「这里有一张图，被省略了」。
    /// </summary>
    [Fact]
    public void ImageBlockInTheRecord_BecomesAVisibleNote_OnEveryProtocol()
    {
        var sha = new string('c', 64);
        var timeline = new List<TimelineEvent>
        {
            new TimelineEvent.MessageEvent
            {
                Id = "m1", At = DateTimeOffset.UnixEpoch, From = Speaker.User,
                Blocks = [new Block.TextBlock("看看这张图"), new Block.ImageBlock(sha, "image/png")],
            },
        };

        foreach (var protocol in new[]
                 {
                     Protocols.ClaudeMessages, Protocols.OpenAiChat, Protocols.OpenAiResponses,
                 })
        {
            var json = Json(protocol, timeline);

            Assert.Contains("图片", json, StringComparison.Ordinal);
            Assert.Contains("省略", json, StringComparison.Ordinal);
        }
    }

    // ---- 脚手架 ----

    private static string Json(string protocol, IReadOnlyList<TimelineEvent> timeline)
    {
        var request = new ModelRequest
        {
            ModelId = "m",
            MaxTokens = 1000,
            Protocol = protocol,
            Effort = "high",
            Temperature = null,
            Slots = PromptSlots.Empty,
            Tools = [],
            Timeline = timeline,
        };

        // ⚠️ Claude 那条 builder 给的是 **SDK 类型**（没有 JSON 字符串出口）⇒ 按块取正文，
        // 判据不变：那句话必须**在请求里**。两条 OpenAI 链路本来就是手搓 JSON，直接给字符串。
        if (protocol != Protocols.ClaudeMessages)
            return protocol == Protocols.OpenAiChat
                ? OpenAiChatRequest.Build(request)
                : OpenAiResponsesRequest.Build(request);

        var built = Lilara.Core.Protocol.Claude.ClaudeRequestBuilder.Build(request);
        var text = new System.Text.StringBuilder();
        foreach (var message in built.Messages)
        {
            if (!message.Content.TryPickContentBlockParams(out var blocks)) continue;
            foreach (var block in blocks!)
                if (block.TryPickText(out var t))
                    text.Append(t.Text).Append('\n');
        }

        return text.ToString();
    }

    private async Task<(CoreHost Host, CommandDispatcher Dispatcher, string SessionId)> NewSessionAsync()
    {
        var host = new CoreHost(_root, _work);
        _host = host;
        host.Providers.Save(new ProviderDefinition
        {
            Id = "p", Protocol = Protocols.ClaudeMessages, ApiKey = "k", DefaultModel = "m", Modality = "vision",
        });

        _adapter.Reply("ok");
        var dispatcher = new CommandDispatcher(host, new EventBus(), h => new Stepper(host.Store, _adapter), _ => _adapter);

        var created = await dispatcher.DispatchAsync(
            new CoreCommand.SessionCreate(), "c-new", false, CancellationToken.None);

        return (host, dispatcher, ((Ack.Accepted)created.Ack).SessionId!);
    }

    private static string hostStoreKey(CoreHost host, string sessionId) =>
        host.Store.FindSession(sessionId)!.Value.ProjectKey;
}
