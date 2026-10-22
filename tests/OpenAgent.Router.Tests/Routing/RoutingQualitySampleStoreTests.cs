using Microsoft.Extensions.Options;
using OpenAgent.Router.Models;
using OpenAgent.Router.Options;
using OpenAgent.Router.Routing;
using Xunit;

namespace OpenAgent.Router.Tests.Routing;

public class RoutingQualitySampleStoreTests
{
    [Fact]
    public async Task MemoryStore_RetainsNewestSamplesWithinTenantLimit()
    {
        var store = new RoutingQualitySampleStore(Microsoft.Extensions.Options.Options.Create(new RoutingQualityOptions
        {
            MaxSamplesPerTenant = 2
        }));

        await store.SaveAsync("tenant", Sample("one"));
        await store.SaveAsync("tenant", Sample("two"));
        await store.SaveAsync("tenant", Sample("three"));

        IReadOnlyList<RoutingQualitySample> samples = await store.GetRecentAsync("tenant", 10);

        Assert.Equal(["three", "two"], samples.Select(sample => sample.SampleId));
        Assert.Empty(await store.GetRecentAsync("other", 10));
    }

    private static RoutingQualitySample Sample(string id) => new(
        id,
        DateTimeOffset.UtcNow,
        "session-hash",
        "selected",
        "expected",
        "message-hash",
        null,
        "test");
}
