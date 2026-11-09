using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Router.Models;
using OpenAgent.Router.Routing;
using Xunit;

namespace OpenAgent.Router.Tests.Routing;

public class ConversationProviderStoreTests
{
    [Fact]
    public async Task BindAsync_ConcurrentProviderClaims_FirstProviderWinsWithinTenant()
    {
        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        using ServiceProvider serviceProvider = services.BuildServiceProvider();
        var store = new ConversationProviderStore(
            serviceProvider.GetRequiredService<IDistributedCache>());
        var engine = new ConversationProviderAffinity(
            "self-engine",
            ConversationAffinityState.Pending);
        var partner = new ConversationProviderAffinity(
            "partner",
            ConversationAffinityState.Pending);

        ConversationProviderAffinity first = await store.BindAsync(
            "tenant-1",
            "conversation-1",
            engine,
            CancellationToken.None);
        ConversationProviderAffinity second = await store.BindAsync(
            "tenant-1",
            "conversation-1",
            partner,
            CancellationToken.None);
        ConversationProviderAffinity otherTenant = await store.BindAsync(
            "tenant-2",
            "conversation-1",
            partner,
            CancellationToken.None);

        Assert.Equal("self-engine", first.ProviderId);
        Assert.Equal("self-engine", second.ProviderId);
        Assert.Equal("partner", otherTenant.ProviderId);
    }
    [Fact]
    public async Task CompareExchangeAsync_OnlyReplacesExpectedBinding()
    {
        var services = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var store = new ConversationProviderStore(services.GetRequiredService<IDistributedCache>());
        var original = new ConversationProviderAffinity("engine", ConversationAffinityState.Confirmed, "general");
        var replacement = original with { ProviderId = "partner", AgentId = "finance" };
        await store.SetAsync("tenant", "session", original, CancellationToken.None);

        Assert.Equal(replacement, await store.CompareExchangeAsync(
            "tenant", "session", original, replacement, CancellationToken.None));
        Assert.Equal(replacement, await store.CompareExchangeAsync(
            "tenant", "session", original, original, CancellationToken.None));
    }
}
