using OpenAgent.Router.Models;

namespace OpenAgent.Router;

internal interface IRoutingQualitySampleStore
{
    Task SaveAsync(
        string tenantId,
        RoutingQualitySample sample,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RoutingQualitySample>> GetRecentAsync(
        string tenantId,
        int count,
        CancellationToken cancellationToken = default);
}
