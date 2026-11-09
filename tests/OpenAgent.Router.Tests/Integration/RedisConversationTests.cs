using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Router.Models;
using OpenAgent.Router.Routing;
using StackExchange.Redis;
using Xunit;

namespace OpenAgent.Router.Tests.Integration;

public class RedisConversationTests
{
    [RedisFact]
    public async Task ConcurrentClaimsAcrossConnections_PersistOneOwnerAndAgent()
    {
        string connection = Environment.GetEnvironmentVariable("ROUTER_TEST_REDIS")!;
        using var redisA = await ConnectionMultiplexer.ConnectAsync(connection);
        using var redisB = await ConnectionMultiplexer.ConnectAsync(connection);
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var storeA = new ConversationProviderStore(cache, redisA);
        var storeB = new ConversationProviderStore(cache, redisB);
        string tenant = Guid.NewGuid().ToString();
        var first = new ConversationProviderAffinity("provider-a", ConversationAffinityState.Confirmed,
            "agent-a", "owner", ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(1));
        var second = first with { ProviderId = "provider-b", AgentId = "agent-b" };
        var results = await Task.WhenAll(storeA.BindAsync(tenant, "conversation", first, CancellationToken.None),
            storeB.BindAsync(tenant, "conversation", second, CancellationToken.None));
        Assert.Equal(results[0], results[1]);
        var recreatedStore = new ConversationProviderStore(cache, redisB);
        Assert.Equal(results[0], await recreatedStore.GetAsync(tenant, "conversation", CancellationToken.None));
        Assert.Null(await recreatedStore.GetAsync(tenant + "-other", "conversation", CancellationToken.None));
    }
    [RedisFact]
    public async Task ConcurrentSwitches_CommitExactlyOneReplacement()
    {
        string connection = Environment.GetEnvironmentVariable("ROUTER_TEST_REDIS")!;
        using var redisA = await ConnectionMultiplexer.ConnectAsync(connection);
        using var redisB = await ConnectionMultiplexer.ConnectAsync(connection);
        using var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        var storeA = new ConversationProviderStore(cache, redisA);
        var storeB = new ConversationProviderStore(cache, redisB);
        string tenant = Guid.NewGuid().ToString();
        var original = new ConversationProviderAffinity("provider", ConversationAffinityState.Confirmed,
            "original", "owner", ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(1));
        var first = original with { AgentId = "first" };
        var second = original with { AgentId = "second" };
        await storeA.SetAsync(tenant, "switch", original, CancellationToken.None);

        ConversationProviderAffinity[] results = await Task.WhenAll(
            storeA.CompareExchangeAsync(tenant, "switch", original, first, CancellationToken.None),
            storeB.CompareExchangeAsync(tenant, "switch", original, second, CancellationToken.None));
        ConversationProviderAffinity persisted = (await storeA.GetAsync(
            tenant, "switch", CancellationToken.None))!;

        Assert.Contains(persisted.AgentId, new[] { "first", "second" });
        Assert.All(results, result => Assert.Equal(persisted, result));
    }
}
