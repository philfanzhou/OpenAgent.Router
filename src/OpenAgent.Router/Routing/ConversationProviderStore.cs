using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using OpenAgent.Router.Models;
using StackExchange.Redis;

namespace OpenAgent.Router.Routing;

internal sealed class ConversationProviderStore(
    IDistributedCache cache,
    IConnectionMultiplexer? redis = null) : IConversationProviderStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _memoryGate = new(1, 1);

    public async Task<ConversationProviderAffinity?> GetAsync(
        string tenantId, string conversationId, CancellationToken cancellationToken)
    {
        string key = BuildKey(tenantId, conversationId);
        string? value = redis != null
            ? (string?)await redis.GetDatabase().StringGetAsync(key).WaitAsync(cancellationToken).ConfigureAwait(false)
            : await cache.GetStringAsync(key, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(value) ? null
            : JsonSerializer.Deserialize<ConversationProviderAffinity>(value, JsonOptions);
    }

    public async Task SetAsync(string tenantId, string conversationId,
        ConversationProviderAffinity affinity, CancellationToken cancellationToken)
    {
        string key = BuildKey(tenantId, conversationId);
        string value = JsonSerializer.Serialize(affinity, JsonOptions);
        TimeSpan ttl = GetTimeToLive(affinity);
        if (redis != null)
        {
            await redis.GetDatabase().StringSetAsync(key, value, ttl)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        await cache.SetStringAsync(key, value, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = ttl
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConversationProviderAffinity> BindAsync(string tenantId, string conversationId,
        ConversationProviderAffinity affinity, CancellationToken cancellationToken)
    {
        if (redis != null)
        {
            string key = BuildKey(tenantId, conversationId);
            bool created = await redis.GetDatabase().StringSetAsync(key,
                JsonSerializer.Serialize(affinity, JsonOptions), GetTimeToLive(affinity), When.NotExists)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            return created ? affinity : await GetAsync(tenantId, conversationId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Conversation binding expired during creation");
        }
        // Development only. Production requires Redis; this lock is not a distributed lock.
        await _memoryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ConversationProviderAffinity? existing = await GetAsync(tenantId, conversationId,
                cancellationToken).ConfigureAwait(false);
            if (existing != null) return existing;
            await SetAsync(tenantId, conversationId, affinity, cancellationToken).ConfigureAwait(false);
            return affinity;
        }
        finally { _memoryGate.Release(); }
    }

    public async Task<ConversationProviderAffinity> CompareExchangeAsync(
        string tenantId,
        string conversationId,
        ConversationProviderAffinity expected,
        ConversationProviderAffinity replacement,
        CancellationToken cancellationToken)
    {
        string key = BuildKey(tenantId, conversationId);
        if (redis != null)
        {
            IDatabase database = redis.GetDatabase();
            string expectedValue = JsonSerializer.Serialize(expected, JsonOptions);
            string replacementValue = JsonSerializer.Serialize(replacement, JsonOptions);
            ITransaction transaction = database.CreateTransaction();
            transaction.AddCondition(Condition.StringEqual(key, expectedValue));
            _ = transaction.StringSetAsync(key, replacementValue, GetTimeToLive(replacement));
            bool committed = await transaction.ExecuteAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            return committed
                ? replacement
                : await GetAsync(tenantId, conversationId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Conversation binding expired during switch");
        }

        await _memoryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ConversationProviderAffinity? current = await GetAsync(tenantId, conversationId, cancellationToken)
                .ConfigureAwait(false);
            if (current != expected)
            {
                return current ?? throw new InvalidOperationException("Conversation binding expired during switch");
            }
            await SetAsync(tenantId, conversationId, replacement, cancellationToken).ConfigureAwait(false);
            return replacement;
        }
        finally { _memoryGate.Release(); }
    }

    private static TimeSpan GetTimeToLive(ConversationProviderAffinity affinity)
    {
        TimeSpan remaining = (affinity.ExpiresAt ?? DateTimeOffset.UtcNow.AddDays(30)) - DateTimeOffset.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1);
    }

    private static string BuildKey(string tenantId, string conversationId) =>
        $"openagent:router:conversation-provider:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId}\0{conversationId}")))}";
}
