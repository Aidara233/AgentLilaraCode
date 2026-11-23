using Lilara.Core.Kernel;

namespace Lilara.Core.Session.Tools;

/// <summary>
/// Read-only, on-demand runtime metadata for cases where the core system prompt is empty
/// or the model needs details beyond the compact system metadata block.
/// </summary>
public sealed class RuntimeInfoTool : ITool
{
    public const string NameOf = "runtime_info";

    public string Name => NameOf;

    public PermissionClass PermissionClass => PermissionClass.ReadOnly;

    public string Description =>
        "Read the current Lilara Core framework and session metadata when needed (for example, the absolute working directory). "
        + "Returns the selected provider/model and the adapter protocol, plus the last matching upstream-reported model if one exists. "
        + "The selected model ID may be an alias; the reported model is only an observation from a previous completed response. "
        + "This tool does not return API keys, endpoints, or request fingerprints.";

    public string ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {}
        }
        """;

    public Task<ToolOutcome> RunAsync(ToolCall call, IToolContext context, CancellationToken ct)
    {
        var snapshot = context.RuntimeInfo ?? new RuntimeInfoSnapshot
        {
            WorkingDirectory = context.WorkingDirectory,
        };
        return Task.FromResult(ToolText.Of(snapshot.ToToolJson()));
    }
}
