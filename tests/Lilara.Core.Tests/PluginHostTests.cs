using Lilara.Core.Plugins;

namespace Lilara.Core.Tests;

public sealed class PluginHostTests
{
    [Fact]
    public void RegistrationAndPromptCollectionAreDeterministic()
    {
        var host = new PluginHost();
        host.Register(new TestPlugin("zeta", "zeta.axis", "z", "Z 字段"));
        host.Register(new TestPlugin("alpha", "alpha.axis", "a", "A 字段"));

        var values = new Dictionary<string, string>
        {
            ["zeta.axis"] = "z",
            ["alpha.axis"] = "a",
        };
        var prompt = host.Collect(host.Freeze(values, DateTimeOffset.UnixEpoch));

        Assert.Equal(["alpha", "zeta"], host.Plugins.Select(p => p.Plugin.Id));
        Assert.Equal("[alpha/alpha.axis]\nA 字段\n[zeta/zeta.axis]\nZ 字段", prompt.Text(PromptSlot.Floating));
    }

    [Fact]
    public void PluginCannotClaimAnotherNamespace()
    {
        var host = new PluginHost();
        Assert.Throws<ArgumentException>(() =>
            host.Register(new TestPlugin("alpha", "zeta.axis", "x", "bad")));
    }

    [Fact]
    public void DuplicateAxisAndInvalidDefaultAreRejected()
    {
        var host = new PluginHost();
        host.Register(new TestPlugin("alpha", "alpha.axis", "a", "A"));

        Assert.Throws<InvalidOperationException>(() =>
            host.Register(new TestPlugin("alpha", "alpha.other", "b", "B")));
        Assert.Throws<ArgumentException>(() =>
            host.Register(new TestPlugin("gamma", "gamma.axis", "valid", "bad", defaultValue: "not-valid")));
    }

    [Fact]
    public void FloatingContributionCannotPretendToBeStable()
    {
        var host = new PluginHost();
        host.Register(new TestPlugin("alpha", "alpha.axis", "a", "A", stable: true));

        Assert.Throws<InvalidOperationException>(() =>
            host.Collect(host.Freeze(new Dictionary<string, string>(), DateTimeOffset.UnixEpoch)));
    }

    [Fact]
    public void RegistrationCopiesDefinitions_AndFrozenSnapshotsCannotBeMutated()
    {
        var values = new List<string> { "low", "high" };
        var definitions = new List<AxisDefinition> { new()
            { Id = "demo.mode", Label = "Mode", Values = values, DefaultValue = "LOW" } };
        var host = new PluginHost();
        host.Register(new FlexiblePlugin { Axes = definitions });
        values.Clear();
        definitions.Clear();
        var schema = Assert.Single(host.Plugins);
        Assert.Equal(["low", "high"], Assert.Single(schema.Axes).Values);
        Assert.Equal("low", schema.Axes[0].DefaultValue);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)schema.Axes[0].Values)[0] = "changed");
        var input = new Dictionary<string, string> { ["demo.mode"] = "HIGH" };
        var snapshot = host.Freeze(input, DateTimeOffset.UnixEpoch);
        input["demo.mode"] = "low";
        Assert.Equal("high", snapshot.Values["demo.mode"]);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)snapshot.Values)["demo.mode"] = "low");
        Assert.Throws<InvalidOperationException>(() => host.Register(new TestPlugin("other", "other.axis", "x", "x")));
    }

    [Fact]
    public void PromptOnlySeesVisibleCoreAndOwnAxes_AndReturnedFieldsAreReadOnly()
    {
        var plugin = new FlexiblePlugin
        {
            Axes = [new AxisDefinition { Id = "demo.visible", Label = "Visible", Values = ["yes"], DefaultValue = "yes" },
                new AxisDefinition { Id = "demo.hidden", Label = "Hidden", Values = ["secret"], DefaultValue = "secret", InContext = false }],
            Render = s =>
            {
                Assert.Equal("yes", s.Values["demo.visible"]);
                Assert.True(s.Values.ContainsKey("safety"));
                Assert.False(s.Values.ContainsKey("demo.hidden"));
                Assert.False(s.Values.ContainsKey("other.axis"));
                Assert.False(s.Values.ContainsKey("effort"));
                Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)s.Values)["demo.visible"] = "no");
                return [Field()];
            },
        };
        var host = new PluginHost();
        host.Register(plugin);
        host.Register(new TestPlugin("other", "other.axis", "x", "x"));
        var prompt = host.Collect(host.Freeze(Lilara.Core.Kernel.Axes.Defaults(), DateTimeOffset.UnixEpoch));
        Assert.Throws<NotSupportedException>(() => ((IList<PromptContribution>)prompt.Floating).Clear());
    }

    [Fact]
    public void UnsupportedCapabilitiesFailBeforeAnyRegistrationIsPublished()
    {
        var host = new PluginHost();
        Assert.Throws<NotSupportedException>(() => host.Register(new FlexiblePlugin { Capabilities = [AxisCapability.ToolGate] }));
        Assert.Empty(host.Plugins);
        host.Register(new FlexiblePlugin());
        Assert.Single(host.Plugins);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void InvalidPromptFieldsAreRejected(int scenario)
    {
        var host = new PluginHost();
        host.Register(new FlexiblePlugin { Render = _ => scenario switch
        {
            0 => [Field(), Field()],
            1 => [Field() with { Text = new string('中', PluginHost.MaxFieldBytes / 3 + 1) }],
            2 => [Field() with { PluginId = "other" }],
            3 => [Field() with { Slot = (PromptSlot)99, Stable = true }],
            4 => [Field() with { Key = "field\nforged" }],
            _ => Enumerable.Range(0, PluginHost.MaxFieldsPerPlugin + 1).Select(i => Field() with { Key = "field-" + i }).ToArray(),
        } });
        Assert.Throws<InvalidOperationException>(() => host.Collect(host.Freeze(new Dictionary<string, string>(), DateTimeOffset.UnixEpoch)));
    }

    [Fact]
    public void AggregatePromptLimitAppliesAcrossFields()
    {
        var host = new PluginHost();
        host.Register(new FlexiblePlugin { Render = _ => Enumerable.Range(0, 5)
            .Select(i => Field() with { Key = "field-" + i, Text = new string('a', PluginHost.MaxFieldBytes) }).ToArray() });
        Assert.Throws<InvalidOperationException>(() => host.Collect(host.Freeze(new Dictionary<string, string>(), DateTimeOffset.UnixEpoch)));
    }

    private static PromptContribution Field() => new()
        { PluginId = "demo", Key = "field", Slot = PromptSlot.Floating, Text = "content" };

    private sealed class FlexiblePlugin : IAxisPlugin
    {
        public PluginIdentity Identity => new("demo", "1");
        public IReadOnlyList<AxisDefinition> Axes { get; init; } = [];
        public IReadOnlyList<AxisCapability> Capabilities { get; init; } = [AxisCapability.None];
        public Func<AxisSnapshot, IReadOnlyList<PromptContribution>> Render { get; init; } = _ => [];
        public IReadOnlyList<PromptContribution> Prompt(AxisSnapshot snapshot) => Render(snapshot);
    }

    private sealed class TestPlugin(string id, string axisId, string value, string text, bool stable = false, string? defaultValue = null)
        : IAxisPlugin
    {
        public PluginIdentity Identity { get; } = new(id, "1.0");
        public IReadOnlyList<AxisDefinition> Axes { get; } =
            [new AxisDefinition { Id = axisId, Label = axisId, Values = [value], DefaultValue = defaultValue ?? value }];
        public IReadOnlyList<AxisCapability> Capabilities => [AxisCapability.None];
        public IReadOnlyList<PromptContribution> Prompt(AxisSnapshot snapshot) =>
            [new PromptContribution
            {
                PluginId = id,
                Key = axisId,
                Slot = PromptSlot.Floating,
                Text = text,
                Stable = stable,
            }];
    }
}
