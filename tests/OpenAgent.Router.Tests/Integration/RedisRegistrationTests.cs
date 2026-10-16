using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Xunit;

namespace OpenAgent.Router.Tests.Integration;

public class RedisRegistrationTests
{
    [RedisFact]
    public async Task RegisteredInstance_IsDiscoveredAndRemovedByTheSnapshotCache()
    {
        using var redis = await ConnectionMultiplexer.ConnectAsync(
            Environment.GetEnvironmentVariable("ROUTER_TEST_REDIS")!);
        EngineRegistryWriter writer = new(redis, TimeProvider.System);
        EngineRegistrySnapshotCache cache = new(
            redis, NullLogger<EngineRegistrySnapshotCache>.Instance, new ConfigurationBuilder().Build());
        string instanceId = "reg-test-" + Guid.NewGuid().ToString("N")[..8];

        await writer.PutAsync(
            instanceId,
            new Uri("http://engine-test:5208"),
            new RegistrationRequest { BaseUrl = "http://engine-test:5208", Load = 3, Intents = ["chat"] });
        await cache.RefreshAsync();
        EngineRegistryEntry entry = Assert.Single(cache.Snapshot, e => e.EngineId == instanceId);
        Assert.Equal(("engine-test", 5208, 3), (entry.Host, entry.Port, entry.Load));
        Assert.True(await redis.GetDatabase().KeyTimeToLiveAsync(
            EngineRegistrySnapshotCache.RegistryKey(instanceId)) > TimeSpan.Zero);

        await writer.RemoveAsync(instanceId);
        await cache.RefreshAsync();
        Assert.DoesNotContain(cache.Snapshot, e => e.EngineId == instanceId);
    }
}
