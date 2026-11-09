namespace OpenAgent.Router.Models;

internal sealed record RoutingFeedbackRequest(
    string SessionId,
    string ExpectedAgentId,
    string Message,
    string? Reason = null);

internal sealed record RoutingQualitySample(
    string SampleId,
    DateTimeOffset OccurredAt,
    string SessionHash,
    string SelectedAgentId,
    string ExpectedAgentId,
    string MessageHash,
    string? Message,
    string? Reason);
