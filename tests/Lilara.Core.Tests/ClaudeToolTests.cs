using System.Text.Json;
using Anthropic.Models.Messages;
using Lilara.Core.Kernel;
using Lilara.Core.Protocol;
using Lilara.Core.Protocol.Claude;
using Block = Lilara.Core.Kernel.Block;
using AnthropicRole = Anthropic.Models.Messages.Role;

namespace Lilara.Core.Tests;

/// <summary>
/// 工具在 Claude 协议上的四条线（`01` §9 / `00` §5）。
/// ⚠️ 这组测试存在的理由与 `CommandSurfaceTests` 同源：**一条命令有四条线，漏一条就是死命令**——
/// 工具在这里是「发工具表 / 收 tool_use / 渲染历史里的 tool_use / 回注结果」四条，
/// 而漏掉哪一条都是**静默**的（模型看不见工具、或上游 400）。
/// </summary>
public class ClaudeToolTests
{
    private static ModelRequest Req(
        IReadOnlyList<TimelineEvent> timeline,
        IReadOnlyList<ToolSpec>? tools = null) => new()
    {
        ModelId = "claude-opus-5",
        MaxTokens = 16000,
        Protocol = Protocols.ClaudeMessages,
        Effort = "high",
        Temperature = null,
        SystemBlocks = [],
        Tools = tools ?? [],
        Timeline = timeline,
    };

    private static TimelineEvent.ToolResultEvent Result(string callId, string text) => new()
    {
        Id = "r1", At = DateTimeOffset.UnixEpoch, CallId = callId,
        Content = [new Block.TextBlock(text)],
        DependsOn = "a.txt", Fingerprint = "fp",
    };

    private static TimelineEvent.MessageEvent ModelMsg(params Block[] blocks) => new()
    {
        Id = "m1", At = DateTimeOffset.UnixEpoch, From = Speaker.Model, Blocks = blocks,
    };

    private static TimelineEvent.MessageEvent UserMsg(string text) => new()
    {
        Id = "u1", At = DateTimeOffset.UnixEpoch, From = Speaker.User,
        Blocks = [new Block.TextBlock(text)],
    };

    private static ToolSpec Spec(string name = "read") =>
        new(name, "读一个文件", """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""");

    // ---- 线 ①：发工具表 ----

    [Fact]
    public void Tools_AreSent()
    {
        var built = ClaudeRequestBuilder.Build(Req([UserMsg("hi")], [Spec()]));

        Assert.NotNull(built.Tools);
        Assert.True(Assert.Single(built.Tools!).TryPickTool(out var tool));
        Assert.Equal("read", tool!.Name);
        Assert.Equal("读一个文件", tool.Description);
        // ⚠️ `Properties` 在 SDK 里可空（JSON 里可以没有）⇒ `!`；我们刚构造过它，非空有依据。
        Assert.Equal("string", tool.InputSchema.Properties!["path"].GetProperty("type").GetString());
    }

    // ⚠️ **空工具表 ⇒ 整个键不发**，不是发一个空数组：空值会改变前缀的字节。
    // 与 `System` 同一条判据，也是「没配工具的会话请求逐字节不变」那句话的依据。
    [Fact]
    public void NoTools_TheKeyIsAbsent()
    {
        Assert.Null(ClaudeRequestBuilder.Build(Req([UserMsg("hi")])).Tools);
    }

    // ---- 线 ②：渲染历史里的 tool_use（模型的调用必须原样回传） ----

    // ⚠️ 少了它那条 `tool_result` 就是悬空的 ⇒ 上游 400（`01` §3.6）。
    [Fact]
    public void ToolUseBlock_IsReplayedBack()
    {
        var call = new Block.ToolUseBlock("call_1", "read", """{"path":"a.txt"}""");

        var messages = ClaudeRequestBuilder.Build(
            Req([UserMsg("读一下"), ModelMsg(call), Result("call_1", "文件内容")])).Messages;

        var assistant = messages.Single(m => m.Role.Value() == AnthropicRole.Assistant);
        Assert.True(assistant.Content.TryPickContentBlockParams(out var assistantBlocks));
        var use = Assert.Single(assistantBlocks!);
        Assert.True(use.TryPickToolUse(out var toolUse));
        Assert.Equal("call_1", toolUse!.ID);
        Assert.Equal("read", toolUse.Name);
        Assert.Equal("a.txt", toolUse.Input["path"].GetString());
    }

    // ---- 线 ③：回注结果（住在 **user** 角色里，这是协议给的不对称） ----

    [Fact]
    public void ToolResult_GoesIntoAUserMessage()
    {
        var call = new Block.ToolUseBlock("call_1", "read", """{"path":"a.txt"}""");

        var messages = ClaudeRequestBuilder.Build(
            Req([UserMsg("读一下"), ModelMsg(call), Result("call_1", "文件内容")])).Messages;

        // ⚠️ 结果在 user 角色里 ⇒ 它两条 user 消息（或与用户那条合并），绝不会在 assistant 里。
        ToolResultBlockParam? got = null;
        AnthropicRole role = default;
        foreach (var m in messages)
        {
            if (!m.Content.TryPickContentBlockParams(out var cs)) continue;
            foreach (var c in cs!)
                if (c.TryPickToolResult(out var tr)) { got = tr; role = m.Role.Value(); }
        }

        Assert.NotNull(got);
        Assert.Equal(AnthropicRole.User, role);
        // ⚠️ 配对的 id 必须是**协议 id**，不是我们的稳定 id。
        Assert.Equal("call_1", got!.ToolUseID);
    }

    // ⚠️ 「什么都没返回」是**合法**形态，而协议不允许空的 content ⇒ 补占位串，**不抛**。
    // 抛的话一次正常的无输出会把整步打掉。
    [Fact]
    public void EmptyResult_GetsAPlaceholder_NotAnException()
    {
        var empty = new TimelineEvent.ToolResultEvent
        {
            Id = "r1", At = DateTimeOffset.UnixEpoch, CallId = "c1", Content = [],
        };

        // ⚠️ 模型消息里必须有块——空消息会被 builder 拒掉（上游对空 content 回 400），
        // 那是既有的正确行为，不是这条测试要验的东西。
        var call = new Block.ToolUseBlock("c1", "read", """{"path":"a.txt"}""");
        var messages = ClaudeRequestBuilder.Build(Req([UserMsg("x"), ModelMsg(call), empty])).Messages;

        ToolResultBlockParam? got = null;
        foreach (var m in messages)
        {
            if (!m.Content.TryPickContentBlockParams(out var cs)) continue;
            foreach (var c in cs!)
                if (c.TryPickToolResult(out var tr)) got = tr;
        }

        Assert.NotNull(got);
        Assert.NotNull(got!.Content);   // 占位串在（协议不允许空 content）
    }

    // ⚠️ 认不出的形状**响亮地失败**，不静默给个空结果——空结果会让模型以为
    // 「那个工具什么都没返回」，而它其实是被丢弃了。
    [Fact]
    public void UnrecognizedResultShape_Throws()
    {
        var withRef = new TimelineEvent.ToolResultEvent
        {
            Id = "r1", At = DateTimeOffset.UnixEpoch, CallId = "c1", Content = [], Ref = "old-row",
        };

        var call = new Block.ToolUseBlock("c1", "read", """{"path":"a.txt"}""");
        Assert.Throws<ArgumentException>(
            () => ClaudeRequestBuilder.Build(Req([UserMsg("x"), ModelMsg(call), withRef])));
    }

    // ---- 线 ④：收 tool_use（响应 → canonical） ----

    [Fact]
    public void Response_ToolUse_BecomesACanonicalBlock()
    {
        var response = Message.FromRawUnchecked(new Dictionary<string, JsonElement>
        {
            ["id"] = Json("\"msg_1\""),
            ["type"] = Json("\"message\""),
            ["role"] = Json("\"assistant\""),
            ["model"] = Json("\"claude-opus-5\""),
            ["stop_reason"] = Json("\"tool_use\""),
            ["content"] = Json("""
                [{"type":"text","text":"我读一下"},
                 {"type":"tool_use","id":"call_1","name":"read","input":{"path":"a.txt"}}]
                """),
            ["usage"] = Json("""{"input_tokens":1,"output_tokens":1}"""),
        });

        var reply = ClaudeResponseMapper.Map(response);

        Assert.False(reply.Refused);
        Assert.Equal("tool_use", reply.StopReason);
        var use = Assert.IsType<Block.ToolUseBlock>(reply.Blocks[1]);
        Assert.Equal("call_1", use.CallId);
        Assert.Equal("read", use.Name);
        // ⚠️ 参数是可解析的 JSON（逐字保真的**边界**在请求侧，见 ParseInput 的注释）。
        using var doc = JsonDocument.Parse(use.Arguments);
        Assert.Equal("a.txt", doc.RootElement.GetProperty("path").GetString());
    }

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement;
}
