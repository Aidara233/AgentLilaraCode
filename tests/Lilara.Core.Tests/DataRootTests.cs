using Lilara.Core.Persistence;

namespace Lilara.Core.Tests;

public class DataRootTests
{
    [Fact]
    public void Resolve_UsesEnvVarWhenSet()
    {
        var actual = DataRoot.Resolve(name =>
            name == DataRoot.EnvVar ? @"C:\tmp\lilara-home" : null);

        Assert.Equal(Path.GetFullPath(@"C:\tmp\lilara-home"), actual);
    }

    [Fact]
    public void Resolve_FallsBackToUserProfile()
    {
        var actual = DataRoot.Resolve(_ => null);

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(profile, ".lilara"), actual);
    }

    [Fact]
    public void Resolve_IgnoresBlankEnvVar()
    {
        var actual = DataRoot.Resolve(name =>
            name == DataRoot.EnvVar ? "   " : null);

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(profile, ".lilara"), actual);
    }
}
