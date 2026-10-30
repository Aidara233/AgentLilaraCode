using Lilara.Core.Kernel;
using Lilara.Core.Protocol;
using Lilara.Core.Protocol.OpenAi;
using System.Text;
using Lilara.Core.Session;
using Lilara.Core.Session.Tools;
using Lilara.Core.Storage;
using Block = Lilara.Core.Kernel.Block;

namespace Lilara.Core.Tests;

/// <summary>
/// **看图这条路的三段**（2026-10-29）：工具认得出图片 · 渲染期把地址换成字节 ·
/// 三条协议各自把它拼成原生图片块。
///
/// <para>⚠️ <b>这组测试为什么必须存在</b>：在这次改动之前，一条 `ImageBlock` 在这三段上
/// <b>一段都不通</b>——`read` 只会把 PNG 解成 U+FFFD，渲染期不认识它，而三条适配器
/// 遇到它就抛（Claude / OpenAI 结果那条）或静默丢（OpenAI 消息那条）。
/// ⇒ 判据不能只是「不抛」，必须是<b>协议里真的出现了那张图</b>。</para>
/// </summary>
public class ImageTests : IDisposable
{
    /// <summary>一张**真实的** 1×1 透明 PNG（67 字节）。⚠️ 用真字节，不用"随便几个字节"：
    /// 大小与头都是这条链上会被读的东西（magic 判型、base64、data URL）。</summary>
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg==");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "lilara-img-" + Guid.NewGuid().ToString("N"));
    private readonly string _work = Path.Combine(Path.GetTempPath(), "lilara-imgwork-" + Guid.NewGuid().ToString("N"));

    public ImageTests()
    {
        Directory.CreateDirectory(_work);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        foreach (var path in new[] { _root, _work })
        {
            for (var attempt = 0; ; attempt++)
            {
                try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); return; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 20)
                {
                    Thread.Sleep(10 * (attempt + 1));
                }
            }
        }
    }

    // ---- 一、工具：`read` 一张图给的是**图**，不是乱码 ----

    [Fact]
    public async Task Read_AnImageFile_ProducesAnImageBlockAndStoresTheBytes()
    {
        File.WriteAllBytes(Path.Combine(_work, "shot.png"), OnePixelPng);

        var blobs = new BlobStore(_root);
        var outcome = await new ReadTool().RunAsync(
            new ToolCall("c1", "read", """{"path":"shot.png"}"""),
            new SessionToolContext(_work, blobs), CancellationToken.None);

        var image = Assert.IsType<Block.ImageBlock>(Assert.Single(outcome.Content));
        Assert.Equal(MediaTypes.Png, image.MediaType);
        Assert.Equal(OnePixelPng, blobs.TryGet(image.Sha256));   // 字节真的进了库（地址可兑现）
        Assert.NotNull(outcome.Framing);                          // 「这是一张图」写进框架
    }

    [Fact]
    public async Task Read_AnImageWithOffset_SaysItCannotBePaged_InsteadOfSilentlyIgnoring()
    {
        File.WriteAllBytes(Path.Combine(_work, "shot.png"), OnePixelPng);

        var outcome = await new ReadTool().RunAsync(
            new ToolCall("c1", "read", """{"path":"shot.png","offset":10}"""),
            new SessionToolContext(_work, new BlobStore(_root)), CancellationToken.None);

        var text = string.Concat(outcome.Content.OfType<Block.TextBlock>().Select(b => b.Text));
        Assert.Contains("不能按字节分段读", text, StringComparison.Ordinal);
    }

    // ---- 二、三条协议：真的出现了那张图 ----

    [Fact]
    public void ImageWithBytes_BecomesANativeImage_OnClaude()
    {
        var request = Req(Protocols.ClaudeMessages);
        var built = Lilara.Core.Protocol.Claude.ClaudeRequestBuilder.Build(request);

        var images = built.Messages
            .SelectMany(m => m.Content.TryPickContentBlockParams(out var cs) ? cs! : [])
            .Count(c => c.TryPickImage(out _));

        Assert.Equal(1, images);
    }

    [Fact]
    public void ImageWithBytes_BecomesAnImageUrl_OnOpenAiChat()
    {
        var json = OpenAiChatRequest.Build(Req(Protocols.OpenAiChat));

        Assert.Contains("\"image_url\"", json, StringComparison.Ordinal);
        Assert.Contains("data:image/png;base64,", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ImageWithBytes_BecomesAnInputImage_OnOpenAiResponses()
    {
        var json = OpenAiResponsesRequest.Build(Req(Protocols.OpenAiResponses));

        Assert.Contains("\"input_image\"", json, StringComparison.Ordinal);
        Assert.Contains("data:image/png;base64,", json, StringComparison.Ordinal);
    }

    // ---- 三、提供商不吃图：渲染期降级成占位，记录里的图不动 ----

    [Fact]
    public void ImageInRecord_IsDroppedWithANote_WhenTheProviderDeclaresNoVision()
    {
        var store = ProvenSession(modality: "text");
        var handle = Handle(store);

        // ⚠️ 图必须**真的在库里**：否则测到的是「字节丢了」那条分支，而不是「模态不吃图」。
        var sha = new BlobStore(store.DataRoot).Put(OnePixelPng);
        store.AppendEvent(handle.ProjectKey, handle.SessionId, new TimelineEvent.MessageEvent
        {
            Id = "m1", At = CoreClock.Now(), From = Speaker.User,
            Blocks = [new Block.TextBlock("看看这个"), new Block.ImageBlock(sha, MediaTypes.Png)],
        });

        var rendered = TimelineRender.RenderForModel(store, handle);

        var text = string.Concat(rendered.OfType<TimelineEvent.MessageEvent>()
            .SelectMany(m => m.Blocks).OfType<Block.TextBlock>().Select(b => b.Text));
        Assert.Contains("模态声明不是 vision", text, StringComparison.Ordinal);
        Assert.DoesNotContain(rendered.OfType<TimelineEvent.MessageEvent>()
            .SelectMany(m => m.Blocks), b => b is Block.ImageBlock);

        // ⚠️ **记录里那块图必须还在**（降级只发生在渲染层）：换回吃图的提供商就能看见。
        Assert.Contains(store.ReadTimeline(handle.ProjectKey, handle.SessionId)
            .OfType<TimelineEvent.MessageEvent>().SelectMany(m => m.Blocks), b => b is Block.ImageBlock);
    }

    [Fact]
    public void ImageInRecord_CarriesItsBytes_WhenTheProviderSeesImages()
    {
        var store = ProvenSession(modality: "vision");
        var handle = Handle(store);
        var sha = new BlobStore(store.DataRoot).Put(OnePixelPng);
        store.AppendEvent(handle.ProjectKey, handle.SessionId, new TimelineEvent.MessageEvent
        {
            Id = "m1", At = CoreClock.Now(), From = Speaker.User,
            Blocks = [new Block.TextBlock("看看这个"), new Block.ImageBlock(sha, MediaTypes.Png)],
        });

        var image = Assert.IsType<Block.ImageBlock>(TimelineRender.RenderForModel(store, handle)
            .OfType<TimelineEvent.MessageEvent>()
            .SelectMany(m => m.Blocks).OfType<Block.ImageBlock>().Single());

        Assert.Equal(OnePixelPng, image.Bytes);       // 渲染期贴上了字节（适配器要的那份）
    }

    [Fact]
    public void ImageInsideAStoredBlockArray_AlsoGetsItsBytes()
    {
        // ⚠️ 这条钉的是**嵌套那一格**：工具结果过阈值之后，记录里存的是 `StoreBlock`
        // （内容是一段**块数组 JSON**），而那份 JSON 里可以有一块图——图的字节在**另一个**
        // 库对象里。少了解引用那一步，适配器拿到的图就是"没有字节的图"（静默降级成占位）。
        var store = ProvenSession(modality: "vision");
        var handle = Handle(store);
        var blobs = new BlobStore(store.DataRoot);

        var imageSha = blobs.Put(OnePixelPng);
        var nested = new List<Block>
        {
            new Block.TextBlock("这是那张图："),
            new Block.ImageBlock(imageSha, MediaTypes.Png),
        };
        var container = blobs.Put(Encoding.UTF8.GetBytes(BlockList.ToJson(nested)));

        store.AppendEvent(handle.ProjectKey, handle.SessionId, new TimelineEvent.ToolResultEvent
        {
            Id = "r1", At = CoreClock.Now(), CallId = "c1",
            Content = [new Block.StoreBlock(container, BlockList.MediaType)],
        });

        var image = Assert.IsType<Block.ImageBlock>(TimelineRender.RenderForModel(store, handle)
            .OfType<TimelineEvent.ToolResultEvent>()
            .SelectMany(r => r.Content).OfType<Block.ImageBlock>().Single());

        Assert.Equal(OnePixelPng, image.Bytes);
    }

    // ---- 脚手架 ----

    private static ModelRequest Req(string protocol) => new()
    {
        ModelId = "m",
        MaxTokens = 1000,
        Protocol = protocol,
        Effort = "high",
        Temperature = null,
        Slots = PromptSlots.Empty,
        Tools = [],
        Timeline =
        [
            new TimelineEvent.MessageEvent
            {
                Id = "m1", At = DateTimeOffset.UnixEpoch, From = Speaker.User,
                Blocks =
                [
                    new Block.TextBlock("这张图里写了什么？"),
                    new Block.ImageBlock(new string('a', 64), MediaTypes.Png) { Bytes = OnePixelPng },
                ],
            },
        ],
    };

    /// <summary>造一个会话，并把它的提供商按 <paramref name="modality"/> 声明好。</summary>
    private SessionStore ProvenSession(string modality)
    {
        var store = new SessionStore(_root);
        new Providers.ProviderStore(store.DataRoot).Save(new ProviderDefinition
        {
            Id = "p", Protocol = Protocols.ClaudeMessages, ApiKey = "k", DefaultModel = "m", Modality = modality,
        });

        return store;
    }

    private SessionHandle Handle(SessionStore store) =>
        SessionHandle.Create(store, _work, new ModelSelection
        {
            ProviderId = "p",
            ModelId = "m",
            Protocol = Protocols.ClaudeMessages,
            Effort = "high",
            MaxTokens = 1000,
        }, sessionId: "s-img-1").DynamicCache();
}
