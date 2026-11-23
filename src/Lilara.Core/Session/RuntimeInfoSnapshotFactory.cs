using Lilara.Core.Kernel;
using Lilara.Core.Storage;

namespace Lilara.Core.Session;

/// <summary>
/// Builds the non-secret runtime view from the session handle, the adapter actually
/// selected for this request, and the usage ledger's last matching response observation.
/// </summary>
public static class RuntimeInfoSnapshotFactory
{
    public static RuntimeInfoSnapshot Capture(
        SessionHandle handle, string actualProtocol, UsageStore? usage)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(actualProtocol);

        var model = handle.Spec.Model;
        var reported = usage?.ForSession(handle.SessionId)
            .Where(call => call.Purpose == "session"
                && call.Status == "completed"
                && call.ProviderId == model.ProviderId
                && call.ModelId == model.ModelId
                && call.Protocol == actualProtocol
                && !string.IsNullOrWhiteSpace(call.ReportedModel))
            .OrderBy(call => call.StartedAt)
            .LastOrDefault();

        return new RuntimeInfoSnapshot
        {
            SessionId = handle.SessionId,
            ProjectKey = handle.ProjectKey,
            SessionTitle = handle.Spec.Title,
            WorkingDirectory = handle.Spec.WorkingDirectory,
            ProviderId = model.ProviderId,
            SelectedModelId = model.ModelId,
            Protocol = actualProtocol,
            LastReportedModel = reported?.ReportedModel,
            LastReportedAt = reported?.EndedAt ?? reported?.StartedAt,
        };
    }
}
