using OpenAgent.Contracts.Routing;
using OpenAgent.Router.Endpoints;
using OpenAgent.Router.Options;
using OpenAgent.Router.Providers;
using OpenAgent.Router.Routing;
using OpenAgent.Router.Hosting;
using StackExchange.Redis;

namespace OpenAgent.Router;

public static class RouterServiceCollectionExtensions
{
    public static IServiceCollection AddRouterRuntime(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<IConsistentHashRing, JumpHashConsistentHashRing>();
        services.AddSingleton<IEndpointHealthTracker, EndpointHealthTracker>();
        services.AddSingleton<IEngineReadinessProbe, EngineReadinessProbe>();
        services.AddOptions<IntentRecognitionOptions>()
            .Bind(configuration.GetSection(IntentRecognitionOptions.SectionName))
            .Validate(IntentRecognitionOptions.IsValid, "Intent recognition configuration is invalid")
            .ValidateOnStart();
        services.AddOptions<DecisionModelOptions>()
            .Bind(configuration.GetSection(DecisionModelOptions.SectionName))
            .Validate(options => !configuration.GetValue("RouterSettings:IntentRecognition:Enabled", true)
                || DecisionModelOptions.IsValid(options), "Router decision model configuration is invalid")
            .ValidateOnStart();
        services.AddOptions<ConversationRoutingOptions>()
            .Bind(configuration.GetSection(ConversationRoutingOptions.SectionName))
            .Validate(options => options.RetentionDays is > 0 and <= 3650
                && options.StorageTimeoutMs is > 0 and <= 60000,
                "Conversation routing configuration is invalid")
            .ValidateOnStart();
        services.AddHttpClient(IntentAgentSelector.ClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => HttpClientSecurity.CreateSocketsHttpHandler(
                configuration, TimeSpan.FromSeconds(5)));
        services.AddOptions<AgentProviderOptions>()
            .Bind(configuration.GetSection(AgentProviderOptions.SectionName))
            .Validate(AgentProviderOptions.IsValid, "Agent provider configuration is invalid")
            .ValidateOnStart();
        services.AddSingleton<IAgentProviderFactory, OpenAgentEngineProviderFactory>();
        services.AddSingleton<IAgentProviderRegistry, AgentProviderRegistry>();
        services.AddScoped<IAgentCatalogService, AgentCatalogService>();
        services.AddSingleton<IConversationProviderStore, ConversationProviderStore>();
        services.AddSingleton<IConversationProviderResolver, ConversationProviderResolver>();
        services.AddSingleton<IAgentForwarder, AgentForwarder>();
        services.AddSingleton<IIntentAgentSelector, IntentAgentSelector>();
        services.AddSingleton<DecisionModelStatus>();
        services.AddScoped<IAgentSelectionService, AgentSelectionService>();
        services.AddSingleton(new RouterCacheSettings(configuration));
        services.AddSingleton<IIdempotencyStore, IdempotencyStore>();
        services.AddSingleton<IQueryCache, RouterQueryCache>();

        var redisConnectionString = configuration.GetConnectionString("Redis");
        if (!string.IsNullOrEmpty(redisConnectionString))
        {
            services.AddSingleton<IConnectionMultiplexer>(_ =>
            {
                var redisOptions = ConfigurationOptions.Parse(redisConnectionString);
                redisOptions.AbortOnConnectFail = false;
                return ConnectionMultiplexer.Connect(redisOptions);
            });
            services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = redisConnectionString;
            });
            services.AddSingleton<EngineRegistrySnapshotCache>();
            services.AddHostedService(
                provider => provider.GetRequiredService<EngineRegistrySnapshotCache>());
            services.AddSingleton<IRouteTable>(provider =>
            {
                var dynamicRouteTable = new RedisServiceDiscoveryRouteTable(
                    provider.GetRequiredService<EngineRegistrySnapshotCache>(),
                    provider.GetRequiredService<ILogger<RedisServiceDiscoveryRouteTable>>(),
                    provider.GetRequiredService<IConsistentHashRing>(),
                    provider.GetRequiredService<IEndpointHealthTracker>());
                var staticRouteTable = new InMemoryRouteTable(configuration);
                return new CompositeRouteTable(
                    dynamicRouteTable,
                    staticRouteTable,
                    provider.GetRequiredService<ILogger<CompositeRouteTable>>());
            });
        }
        else
        {
            services.AddDistributedMemoryCache();
            services.AddSingleton<IRouteTable, InMemoryRouteTable>();
        }

        services.AddSingleton<IRateLimiter, RedisRateLimiter>();
        return services;
    }
}
