using Lilara.Core.Adapters.OpenAi;
using Lilara.Core.Canonical;

namespace Lilara.Core.Tests;

public class OpenAiResponsesResponseTests
{
    private static string Body(string? text, string status = "completed") =>
        "{\"id\":\"resp_1\",\"object\":\"response\",\"status\":\"" + status
        + "\",\"model\":\"deepseek-flash\",\"output\":[{\"type\":\"message\",\"role\":\"assistant\","
        + "\"content\":[{\"type\":\"output_text\",\"text\":" + Json(text) + "}]}],"
        + "\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"total_tokens\":2}}";

    private static string Json(string? s) =>
        s is null ? "null" : System.Text.Json.JsonSerializer.Serialize(s);

    [Fact]
    public void Map_ExtractsOutputText()
    {
        var reply = OpenAiResponsesResponse.Map(Body("READY"));

        Assert.Equal("READY", ((Block.TextBlock)reply.Blocks[0]).Text);
        Assert.False(reply.Refused);
    }

    [Fact]
    public void Map_SurfacesStatus()
    {
        Assert.Equal("incomplete", OpenAiResponsesResponse.Map(Body("x", "incomplete")).StopReason);
    }

    [Fact]
    public void Map_SkipsNonMessageOutputItems()
    {
        // ⚠️ 这个非 message 项的形状是**构造出来的，不是网关实测形状**——目的是让
        // `type == "message"` 这条检查**成为唯一挡得住它的东西**。
        // 控制器实测过三代形状，只有这一代能钉住该检查：
        //   · `{"type":"reasoning","summary":[]}`（brief 原版，无 content 键）
        //     → 删掉 `type` 过滤后**照样绿**（被 content 守卫吸收，`type` 是死代码）
        //   · `summary:[{"type":"summary_text",…}]` → 仍绿（被 `output_text` 守卫吸收）
        //   · 本版：非 message 项、content 里装 `output_text` → 删掉 `type` 过滤后**翻红** ✓
        // ⇒ 断言顺序是 content 守卫 → type 守卫，所以只有「最内层也像 message」才试得出 type 守卫。
        var json = """
        {"id":"r","status":"completed","output":[
          {"type":"reasoning","id":"rs_1","content":[{"type":"output_text","text":"内部推理"}]},
          {"type":"message","role":"assistant","content":[{"type":"output_text","text":"答案"}]}]}
        """;

        var reply = OpenAiResponsesResponse.Map(json);

        Assert.Single(reply.Blocks);
        Assert.Equal("答案", ((Block.TextBlock)reply.Blocks[0]).Text);
    }

    [Fact]
    public void Map_HandlesEmptyOutputWithoutThrowing()
    {
        var reply = OpenAiResponsesResponse.Map("""{"id":"r","status":"incomplete","output":[]}""");

        Assert.Empty(reply.Blocks);
        Assert.Equal("incomplete", reply.StopReason);
    }

    [Fact]
    public void Map_TreatsIncompleteAsNotRefused()
    {
        // ⚠️ `incomplete` 是「没生成完」，不是拒答——别混为一谈
        Assert.False(OpenAiResponsesResponse.Map(Body("x", "incomplete")).Refused);
    }

    /// <summary>
    /// ⚠️ **实测**：`JsonElement.TryGetProperty` 在**非对象根**上抛
    /// <c>InvalidOperationException</c>（`[]` / `null` / `5` / `"hi"` 全抛，
    /// 控制器已用 brief 原版实现复现，4/4 红）。
    /// 「容忍缺失字段」必须从「根是对象」这一步就开始——否则第一个非对象响应就是一次崩溃。
    /// 这与 <see cref="OpenAiChatResponseTests.Map_ToleratesNonObjectRoot"/> 是同一条实测事实。
    /// </summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("5")]
    [InlineData("\"hi\"")]
    public void Map_ToleratesNonObjectRoot(string body)
    {
        var reply = OpenAiResponsesResponse.Map(body);

        Assert.Empty(reply.Blocks);
        Assert.Equal("unknown", reply.StopReason);
        Assert.False(reply.Refused);
    }

    /// <summary>
    /// ⚠️ 空串 `text` 与 null 是**两条不同的分支**。Task 6 实测过截断响应给的是**空串**
    /// （Chat：`max_tokens=4` → `content:""`）。`is not null` 会在这里造出一个空的 TextBlock
    /// 写进时间线——正是 `04` 要防的「空模型消息」。判据必须是 `Length > 0`。
    /// </summary>
    [Theory]
    [InlineData("""{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":""}]}]}""")]
    [InlineData("""{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":null}]}]}""")]
    [InlineData("""{"status":"completed","output":[{"type":"message","content":[{"type":"output_text"}]}]}""")]
    [InlineData("""{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":42}]}]}""")]
    public void Map_HandlesEmptyOrMissingTextAsNoBlocks(string body)
    {
        var reply = OpenAiResponsesResponse.Map(body);

        Assert.Empty(reply.Blocks);
        Assert.Equal("completed", reply.StopReason);
    }

    /// <summary>
    /// ⚠️ 空数组（<c>output:[]</c> / <c>content:[]</c>）是**合法输入**，不是畸形——必须解析得通。
    /// </summary>
    /// <para>⚠️ 第 1 例是唯一产出块的，故按例断言块数（<c>Assert.Equal(expectedBlocks, …)</c>）；
    /// 三例若都只断言 <c>Refused</c>/<c>StopReason</c>，则在断言上无从区分（审阅者指出）。</para>
    [Theory]
    [InlineData("""{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"ok"}]}]}""", 1)]
    [InlineData("""{"status":"completed","output":[{"type":"message","content":[]}]}""", 0)]
    [InlineData("""{"status":"completed","output":[]}""", 0)]
    public void Map_ToleratesEmptyArrays(string body, int expectedBlocks)
    {
        var reply = OpenAiResponsesResponse.Map(body);

        Assert.Equal(expectedBlocks, reply.Blocks.Count);
        Assert.False(reply.Refused);
        Assert.Equal("completed", reply.StopReason);
    }

    /// <summary>
    /// ⚠️ <c>output</c> / <c>content</c> **不是数组**（数字 / 字符串 / null）时也必须不崩。
    /// <para>⚠️ 本测试**钉住两条数组守卫**——实测（控制器复跑，审阅者独立复算，两次一致）：
    /// 只删 <c>output</c> 的 <c>ValueKind == Array</c> → 本测试**前 3 行红**；
    /// 只删 <c>content</c> 的 <c>ValueKind == Array</c> → 本测试**后 3 行红**。
    /// 少了它们，非数组输入会在 <c>EnumerateArray</c> 上抛 <c>InvalidOperationException</c>。</para>
    /// <para>⚠️ <b>控制器起初把这段写成「两条数组守卫无法被钉住」，那是错的。过程记在此处，
    /// 因为错误的形状比结论更值得留：</b>我拿这条测试去跑<b>未变异</b>的代码，看到绿就断定「区分不了」——
    /// 而<b>正确的代码上本来就该是绿的</b>。钉住一个守卫要跑<b>两次</b>：变异版必须红，正确版必须绿。
    /// 我只跑了后一半。<b>接着我又补了一句自认为「真实」的局限，说删掉数组守卫后下游对象守卫会吸收——
    /// 那句也是错的</b>（<c>EnumerateArray</c> 在循环体之前求值，对象守卫根本到不了），
    /// 由复审者实测指出。<b>⇒ 两处错误同源：都是写下未经运行的断言。</b></para>
    /// <para>⚠️ <b>被钉住的崩溃守卫——按分支类别列出，<u>不列总数</u></b>（总数易腐，且本注释已因写错它返工过）：
    /// <b>类型守卫</b>见 <c>Map_ToleratesNonObjectRoot</c>（根）、<c>Map_ToleratesNonStringStatus</c>（<c>status</c>）、
    /// <c>Map_ToleratesNonArrayOutputAndContent</c>（<c>output</c>/<c>content</c> 的数组）、
    /// <c>Map_ToleratesNonObjectItemsAndBlocks</c>（<c>item</c>/<c>block</c> 的对象）、
    /// <c>Map_HandlesEmptyOrMissingTextAsNoBlocks</c>（<c>text</c>）；
    /// <b>缺键守卫</b>（<c>TryGetProperty</c> 的成员检查——换成 <c>GetProperty</c> 即抛 <c>KeyNotFoundException</c>）
    /// 见 <c>Map_ToleratesMissingKeys</c>；<b>值判据</b>（<c>Length &gt; 0</c>）见 <c>Map_HandlesEmptyOrMissingTextAsNoBlocks</c>。</para>
    /// <para>⚠️ <c>status</c> 的类型守卫起初被漏掉、<b>缺键那一整类起初也未被计入</b>，两次都是复审者逐条列举后补测的。
    /// <b>本段注释在四个轮次里改了四次，每次都是因为写下了未经运行的断言</b>——故此处只列测试名，不列数字。</para>
    /// </summary>
    [Theory]
    [InlineData("""{"status":"completed","output":5}""")]
    [InlineData("""{"status":"completed","output":"str"}""")]
    [InlineData("""{"status":"completed","output":null}""")]
    [InlineData("""{"status":"completed","output":[{"type":"message","content":5}]}""")]
    [InlineData("""{"status":"completed","output":[{"type":"message","content":"str"}]}""")]
    [InlineData("""{"status":"completed","output":[{"type":"message","content":null}]}""")]
    public void Map_ToleratesNonArrayOutputAndContent(string body)
    {
        var reply = OpenAiResponsesResponse.Map(body);

        Assert.Empty(reply.Blocks);
        Assert.Equal("completed", reply.StopReason);
    }

    /// <summary>
    /// ⚠️ <c>status</c> **不是字符串**（数字 / 数组 / 对象 / 布尔）时也必须不崩，且落到 <c>"unknown"</c>。
    /// <para>⚠️ 这条钉住 <c>s.ValueKind == JsonValueKind.String</c> 那半句——实测（控制器复跑）：
    /// 删掉它之后，<c>{"status":5}</c> 与 <c>{"status":["a"]}</c> 都会在 <c>s.GetString()</c> 上抛
    /// <c>InvalidOperationException</c>。</para>
    /// <para>⚠️ 它是**第五条**守卫，起初被漏掉：控制器在本文件里写过「四条守卫各自被钉住，无一例外」，
    /// 复审者指出 <c>status</c> 这条也是守卫且无测试——**控制器实测确认属实**。
    /// 于是**补测试**而不是**改措辞**：这句话之所以曾经过宽，是因为少了一条测试，不是因为话说大了。</para>
    /// <para>注：<c>{"status":null}</c> 不列入——<c>GetString()</c> 对 <c>Null</c> 返回 null，走 <c>?? "unknown"</c>，
    /// 两条路径都给 <c>"unknown"</c>，**钉不住**这半句。列入会造成一条空转行。</para>
    /// </summary>
    [Theory]
    [InlineData("""{"status":5,"output":[]}""")]
    [InlineData("""{"status":["a"],"output":[]}""")]
    [InlineData("""{"status":{"a":1},"output":[]}""")]
    [InlineData("""{"status":true,"output":[]}""")]
    public void Map_ToleratesNonStringStatus(string body)
    {
        var reply = OpenAiResponsesResponse.Map(body);

        Assert.Equal("unknown", reply.StopReason);
    }

    /// <summary>
    /// ⚠️ **缺键**（不是缺值、不是类型错）也必须不崩：`TryGetProperty` 的每一个成员检查
    /// 都在这里被钉住——换成 `GetProperty` 就会抛 `KeyNotFoundException`。
    /// <para>⚠️ 这些是**真实**形状，不是构造的：上游省略 <c>output</c>、省略 <c>content</c>、
    /// 或给一个只有 <c>type</c> 的块，都出现过。断言只取共同不变量（不产出块）——
    /// 因为缺 <c>status</c> 时 <c>StopReason</c> 是 <c>"unknown"</c>、不缺时是 <c>"completed"</c>，
    /// 逐例断言状态反而会把这条测试拧成四不像。</para>
    /// <para>⚠️ 控制器起初只在注释里声明「无覆盖缺口」，**没数这些成员检查**，
    /// 被复审者逐条列举后补测——这是同一缺陷类在本文件里的**第四次**，故此处逐行列出被钉住的分支：
    /// <c>status</c> 缺键 · <c>output</c> 缺键 · <c>item.type</c> 缺键 · <c>item.content</c> 缺键 ·
    /// <c>block.type</c> 缺键 · <c>block.type != "output_text"</c>。<c>block.text</c> 缺键由
    /// <see cref="Map_HandlesEmptyOrMissingTextAsNoBlocks"/> 那行 <c>{"type":"output_text"}</c> 钉住。</para>
    /// </summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"id":"r"}""")]
    [InlineData("""{"status":"completed"}""")]
    [InlineData("""{"output":[]}""")]
    [InlineData("""{"status":"completed","output":[{}]}""")]
    [InlineData("""{"status":"completed","output":[{"type":"message"}]}""")]
    [InlineData("""{"status":"completed","output":[{"type":"message","content":[{}]}]}""")]
    [InlineData("""{"status":"completed","output":[{"type":"message","content":[{"type":"input_text","text":"x"}]}]}""")]
    public void Map_ToleratesMissingKeys(string body)
    {
        var reply = OpenAiResponsesResponse.Map(body);

        Assert.Empty(reply.Blocks);
        Assert.False(reply.Refused);
    }

    /// <summary>
    /// ⚠️ **实测**：`output` 数组里的**非对象项**（数字等）会让 `item.TryGetProperty` 抛
    /// `InvalidOperationException`。兄弟实现 `OpenAiChatResponse` 对 `choices[0]` 有同样的守卫，
    /// 且实测 `{"choices":[5]}` 不崩——**这是对齐，不是新增严格性**。
    /// 同一条也适用于 `content` 数组里的非对象块。
    /// </summary>
    [Theory]
    [InlineData("""{"status":"completed","output":[5]}""")]
    [InlineData("""{"status":"completed","output":[{"type":"message","content":[5]}]}""")]
    [InlineData("""{"status":"completed","output":[null]}""")]
    [InlineData("""{"status":"completed","output":[{"type":"message","content":[null]}]}""")]
    public void Map_ToleratesNonObjectItemsAndBlocks(string body)
    {
        var reply = OpenAiResponsesResponse.Map(body);

        Assert.Empty(reply.Blocks);
        Assert.Equal("completed", reply.StopReason);
    }
}
