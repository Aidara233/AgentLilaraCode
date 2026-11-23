using System.Reflection;
using System.Text.Json;
using System.Text.Encodings.Web;
namespace Lilara.Core.Kernel;

/// <summary>
/// A point-in-time, non-secret view of the runtime context available to the model.
/// The system prompt uses only the stable request/session fields; the read-only
/// <c>runtime.info</c> tool may additionally return the last upstream-reported model.
/// </summary>
public sealed record RuntimeInfoSnapshot
{
    public const string FrameworkName = "Lilara Core";

    private static readonly JsonSerializerOptions PromptJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Default,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public string FrameworkVersion { get; init; } = ReadFrameworkVersion();
    public string SessionId { get; init; } = "";
    public string ProjectKey { get; init; } = "";
    public string? SessionTitle { get; init; }
    public string WorkingDirectory { get; init; } = "";
    public string? ProviderId { get; init; }
    public string? SelectedModelId { get; init; }
    public string Protocol { get; init; } = "unknown";
    public string? LastReportedModel { get; init; }
    public DateTimeOffset? LastReportedAt { get; init; }

    /// <summary>
    /// Serialize the stable, core-owned context for the system prompt. Values are JSON
    /// escaped so paths or provider/model labels cannot break the metadata boundary.
    /// Secrets and the previous response observation are deliberately excluded.
    /// </summary>
    public string ToSystemPromptBlock()
    {
        var data = new SystemPromptRuntimeData(
            FrameworkName,
            FrameworkVersion,
            WorkingDirectory,
            ProviderId,
            SelectedModelId,
            Protocol);
        var json = JsonSerializer.Serialize(data, PromptJsonOptions);
        return $"[lilara runtime metadata]\n{json}\n[/lilara runtime metadata]";
    }

    /// <summary>
    /// Full on-demand diagnostics. The configured selector and the last upstream-reported
    /// model are separate fields; no API key, endpoint, or request fingerprint is included.
    /// </summary>
    public string ToToolJson()
    {
        var data = new
        {
            source = "Lilara Core runtime snapshot",
            framework = new { name = FrameworkName, version = FrameworkVersion },
            session = new
            {
                id = SessionId,
                projectKey = ProjectKey,
                title = SessionTitle,
                workingDirectory = WorkingDirectory,
            },
            requestTarget = new
            {
                providerId = ProviderId,
                selectedModelId = SelectedModelId,
                adapterProtocol = Protocol,
                note = "selectedModelId is the configured request target; it may be an alias and does not prove which upstream model handled a future request.",
            },
            lastUpstreamObservation = LastReportedModel is null
                ? null
                : new
                {
                    reportedModelId = LastReportedModel,
                    observedAt = LastReportedAt,
                    note = "This is the model identifier reported by the most recent completed matching response, not a guarantee about the current or next request.",
                },
        };
        return JsonSerializer.Serialize(data, Json.Options);
    }

    private static string ReadFrameworkVersion()
    {
        var assembly = typeof(RuntimeInfoSnapshot).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var buildMetadata = informational.IndexOf('+', StringComparison.Ordinal);
            return buildMetadata < 0 ? informational : informational[..buildMetadata];
        }

        return assembly.GetName().Version?.ToString(3) ?? "unknown";
    }

    private sealed record SystemPromptRuntimeData(
        string Framework,
        string FrameworkVersion,
        string WorkingDirectory,
        string? ProviderId,
        string? SelectedModelId,
        string Protocol);
}
