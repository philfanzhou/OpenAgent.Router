using Microsoft.AspNetCore.Http;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Endpoints;
using OpenAgent.Router.Models;
using OpenAgent.Router.Options;
using OpenAgent.Router.Routing;
using Xunit;

namespace OpenAgent.Router.Tests.Endpoints;

public class AgentSelectionServiceTests
{
    [Fact]
    public async Task NewConversation_ModelSelectsProviderAndPersistsOwnerAndAgent()
    {
        var store = new StubConversationProviderStore();
        var selector = new StubSelector("finance");
        var service = CreateService([new StubProvider("engine", [Agent("general")]),
            new StubProvider("partner", [Agent("finance")])], selector, store);
        AgentSelection? selection = await service.SelectAsync("invoice", null, null, CancellationToken.None);
        Assert.NotNull(selection);
        Assert.True(Guid.TryParse(selection.ConversationId, out _));
        Assert.Equal("partner", selection.ProviderId);
        var binding = await store.GetAsync("tenant-1", selection.ConversationId!);
        Assert.Equal("finance", binding?.AgentId);
        Assert.Equal("user-1", binding?.UserId);
        Assert.Equal(1, selector.CallCount);
    }

    [Fact]
    public async Task ExplicitAgent_SkipsModelAndCreatesConversation()
    {
        var selector = new StubSelector("general");
        var service = CreateService([new StubProvider("engine", [Agent("general"), Agent("finance")])], selector);
        var selected = await service.SelectAsync("invoice", null, "finance", CancellationToken.None);
        Assert.Equal("finance", selected?.AgentId);
        Assert.NotNull(selected?.ConversationId);
        Assert.Equal(0, selector.CallCount);
    }

    [Fact]
    public async Task FollowUp_KeepsAgentDespiteDifferentPromptAndDoesNotInvokeModel()
    {
        var selector = new StubSelector("finance");
        var service = CreateService([new StubProvider("engine", [Agent("general"), Agent("finance")])], selector);
        var first = await service.SelectAsync("invoice", null, null, CancellationToken.None);
        var next = await service.SelectAsync("write code instead", first!.ConversationId, null, CancellationToken.None);
        Assert.Equal(first, next);
        Assert.Equal(1, selector.CallCount);
    }

    [Fact]
    public async Task UnknownConversation_IsRejectedWithoutModelOrProviderProbe()
    {
        var selector = new StubSelector("general");
        var provider = new StubProvider("engine", [Agent("general")]);
        var service = CreateService([provider], selector);
        var exception = await Assert.ThrowsAsync<AgentRoutingException>(() =>
            service.SelectAsync("hello", "legacy", "general", CancellationToken.None));
        Assert.Equal(404, exception.StatusCode);
        Assert.Equal(0, selector.CallCount);
        Assert.Null(provider.LastTenantId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FollowUp_ExplicitDifferentAgentIsConflictEvenOnSameProvider(bool sameProvider)
    {
        var service = CreateService(sameProvider
            ? [new StubProvider("engine", [Agent("general"), Agent("finance")])]
            : [new StubProvider("engine", [Agent("general")]), new StubProvider("partner", [Agent("finance")])]);
        var first = await service.SelectAsync("hello", null, "general", CancellationToken.None);
        var error = await Assert.ThrowsAsync<AgentRoutingException>(() => service.SelectAsync(
            "change", first!.ConversationId, "finance", CancellationToken.None));
        Assert.Equal(409, error.StatusCode);
    }

    [Theory]
    [InlineData("another-user", false)]
    [InlineData("user-1", true)]
    public async Task InaccessibleOrDeletedConversation_ReturnsNotFound(string owner, bool deleted)
    {
        var store = new StubConversationProviderStore();
        await store.SetAsync("tenant-1", "known", new ConversationProviderAffinity("engine",
            ConversationAffinityState.Confirmed, "general", owner, Deleted: deleted));
        var service = CreateService([new StubProvider("engine", [Agent("general")])], store: store);
        var error = await Assert.ThrowsAsync<AgentRoutingException>(() =>
            service.SelectAsync("hello", "known", null, CancellationToken.None));
        Assert.Equal(404, error.StatusCode);
    }

    [Fact]
    public async Task NoDecision_UsesOnlyAuthorizedFallback()
    {
        var service = CreateService([new StubProvider("engine", [Agent("general"), Agent("finance")])],
            new StubSelector(null));
        Assert.Equal("general", (await service.SelectAsync("hi", null, null, CancellationToken.None))?.AgentId);
        var restricted = CreateService([new StubProvider("engine", [Agent("general"), Agent("finance")])],
            new StubSelector(null), accessControls: [new AllowOnlyAccessControl("finance")]);
        Assert.Null(await restricted.SelectAsync("hi", null, null, CancellationToken.None));
    }

    [Fact]
    public async Task ExplicitUnauthorizedAgent_ReturnsNotFound()
    {
        var service = CreateService([new StubProvider("engine", [Agent("general"), Agent("finance")])],
            accessControls: [new AllowOnlyAccessControl("general")]);
        var error = await Assert.ThrowsAsync<AgentRoutingException>(() =>
            service.SelectAsync("hello", null, "finance", CancellationToken.None));
        Assert.Equal(404, error.StatusCode);
    }

    [Fact]
    public async Task DuplicateAgentIds_ReturnConflict()
    {
        var service = CreateService([new StubProvider("one", [Agent("general")]),
            new StubProvider("two", [Agent("general")])]);
        var error = await Assert.ThrowsAsync<AgentRoutingException>(() =>
            service.SelectAsync("hello", null, null, CancellationToken.None));
        Assert.Equal(409, error.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AutomaticSelection_RequiresPrompt(string message)
    {
        var service = CreateService([new StubProvider("engine", [Agent("general")])]);
        var error = await Assert.ThrowsAsync<AgentRoutingException>(() =>
            service.SelectAsync(message, null, null, CancellationToken.None));
        Assert.Equal(400, error.StatusCode);
    }

    private static AgentSelectionService CreateService(
        IReadOnlyList<IAgentProvider> providers,
        StubSelector? selector = null,
        StubConversationProviderStore? store = null,
        IReadOnlyList<IAgentAccessControl>? accessControls = null)
    {
        var registry = new StubProviderRegistry(providers[0], providers);
        var affinityStore = store ?? new StubConversationProviderStore();
        return new AgentSelectionService(
            new AgentCatalogService(registry, accessControls ?? []),
            new ConversationProviderResolver(registry, affinityStore,
                Microsoft.Extensions.Options.Options.Create(new ConversationRoutingOptions())),
            selector ?? new StubSelector("general"),
            new AgentUserContext
            {
                UserId = "user-1",
                TenantId = "tenant-1",
                IsAuthenticated = true
            },
            Microsoft.Extensions.Options.Options.Create(new IntentRecognitionOptions
            {
                Enabled = true,
                ProviderId = "self-engine",
                AgentId = "intent-router",
                FallbackAgentId = "general",
                MinimumConfidence = 0.5,
                TimeoutMs = 5000
            }));
    }

    private static AgentSummary Agent(string agentId) => new() { AgentId = agentId };

    private sealed class StubProvider(
        string id,
        IReadOnlyList<AgentSummary> agents) : IAgentProvider
    {
        public string Id => id;
        public bool Supports(ChatProtocol protocol) => true;

        public string? LastTenantId { get; private set; }

        public Task<AgentProviderCatalog> GetAgentsAsync(
            AgentProviderRequestContext requestContext,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AgentProviderCatalog(agents));

        public Task<AgentForwardingTarget?> ResolveForwardingAsync(
            ChatProtocol protocol,
            string? tenantId,
            string? sessionId,
            CancellationToken cancellationToken) =>
            Task.FromResult<AgentForwardingTarget?>(null);

        public ValueTask ConfigureRequestAsync(
            HttpRequestMessage request,
            AgentForwardingTarget target,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class StubProviderRegistry(
        IAgentProvider defaultProvider,
        IReadOnlyList<IAgentProvider> providers) : IAgentProviderRegistry
    {
        public IReadOnlyList<IAgentProvider> Providers => providers;

        public IAgentProvider DefaultProvider => defaultProvider;

        public bool TryGet(string providerId, out IAgentProvider? provider)
        {
            provider = providers.FirstOrDefault(item => string.Equals(
                item.Id,
                providerId,
                StringComparison.OrdinalIgnoreCase));
            return provider != null;
        }
    }

    private sealed class StubSelector(string? result) : IIntentAgentSelector
    {
        public int CallCount { get; private set; }

        public Task<string?> SelectAsync(
            AgentProviderRequestContext requestContext,
            string message,
            IReadOnlyList<AgentSummary> candidates,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class AllowOnlyAccessControl(string agentId) : IAgentAccessControl
    {
        public Task<IReadOnlyList<AgentSummary>> GetAuthorizedAgentsAsync(
            IAgentUserContext userContext,
            IReadOnlyList<AgentSummary> agents,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AgentSummary>>(
                agents.Where(agent => agent.AgentId == agentId).ToArray());
    }

    private sealed class StubConversationProviderStore : IConversationProviderStore
    {
        private readonly Dictionary<string, ConversationProviderAffinity> _affinities =
            new(StringComparer.OrdinalIgnoreCase);

        public Task<ConversationProviderAffinity?> GetAsync(
            string tenantId,
            string conversationId,
            CancellationToken cancellationToken = default)
        {
            _affinities.TryGetValue($"{tenantId}|{conversationId}", out ConversationProviderAffinity? affinity);
            return Task.FromResult(affinity);
        }

        public Task SetAsync(
            string tenantId,
            string conversationId,
            ConversationProviderAffinity affinity,
            CancellationToken cancellationToken = default)
        {
            _affinities[$"{tenantId}|{conversationId}"] = affinity;
            return Task.CompletedTask;
        }

        public Task<ConversationProviderAffinity> BindAsync(
            string tenantId,
            string conversationId,
            ConversationProviderAffinity affinity,
            CancellationToken cancellationToken = default)
        {
            string key = $"{tenantId}|{conversationId}";
            if (!_affinities.TryGetValue(key, out ConversationProviderAffinity? bound))
            {
                _affinities[key] = affinity;
                bound = affinity;
            }

            return Task.FromResult(bound);
        }
    }
}
