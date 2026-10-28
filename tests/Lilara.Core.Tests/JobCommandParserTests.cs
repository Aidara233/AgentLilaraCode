using System.Text.Json;
using Lilara.Core.Console;
using Lilara.Core.Control;
using Lilara.Core.Hosting;

namespace Lilara.Core.Tests;

public class JobCommandParserTests
{
    [Theory]
    [InlineData("job")]
    [InlineData("job bogus j1")]
    [InlineData("job kills j1")]
    [InlineData("job killj1")]
    [InlineData("job kill j1 extra")]
    [InlineData("job output")]
    [InlineData("job output j1 --offset -1")]
    [InlineData("job output j1 --offset 1.5")]
    [InlineData("job output j1 --offset 9223372036854775808")]
    [InlineData("job output j1 --unknown 2")]
    [InlineData("jobs s1 extra")]
    public void InvalidJobCommandsCannotBecomeMessages(string line)
    {
        var parsed = CoreCommandParser.Parse(line, "s1");
        Assert.Null(parsed.Command);
        Assert.NotNull(parsed.Error);
    }

    [Theory]
    [InlineData("job kill j1")]
    [InlineData("job output j1")]
    public void MissingSessionIsAnError(string line)
    {
        var parsed = CoreCommandParser.Parse(line);
        Assert.Null(parsed.Command);
        Assert.NotNull(parsed.Error);
    }

    [Fact]
    public void OutputPreservesSessionJobAndLongOffset()
    {
        var parsed = CoreCommandParser.Parse("JOB\tOUTPUT\u00a0j1 --offset 4294967296", "s1");
        Assert.Null(parsed.Error);
        Assert.Equal(new CoreCommand.JobOutput("s1", "j1", 4294967296), parsed.Command);
        using var wire = JsonDocument.Parse("""{"kind":"job.output","sessionId":"s1","jobId":"j1","offset":4294967296}""");
        Assert.Equal(parsed.Command, ControlServer.ParseCommand(wire.RootElement));
        Assert.Equal(new CoreCommand.JobOutput("s1", "j1"), CoreCommandParser.Parse("job output j1", "s1").Command);
        Assert.Equal(new CoreCommand.JobKill("s1", "j1"), CoreCommandParser.Parse("JOB KILL j1", "s1").Command);
    }
}
