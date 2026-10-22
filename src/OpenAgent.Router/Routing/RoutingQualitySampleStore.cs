using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAgent.Router.Models;
using OpenAgent.Router.Options;
using StackExchange.Redis;

namespace OpenAgent.Router.Routing;

internal sealed class RoutingQualitySampleStore(
    IOptions<RoutingQualityOptions> options,
    IConnectionMultiplexer? redis = null) : IRoutingQualitySampleStore
{
    private const string KeyPrefix = "openagent:router:quality-samples:";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<RoutingQualitySample>> _memory = new();

    public async Task SaveAsync(
        string tenantId,
        RoutingQualitySample sample,
        CancellationToken cancellationToken = default)
    {
        RoutingQualityOptions settings = options.Value;
        if (!settings.Enabled) return;

        string key = BuildKey(tenantId);
        if (redis != null)
        {
            IDatabase database = redis.GetDatabase();
            await database.ListLeftPushAsync(key, JsonSerializer.Serialize(sample, JsonOptions))
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            await database.ListTrimAsync(key, 0, settings.MaxSamplesPerTenant - 1)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            await database.KeyExpireAsync(key, TimeSpan.FromDays(settings.SampleRetentionDays))
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        ConcurrentQueue<RoutingQualitySample> samples = _memory.GetOrAdd(key, _ => new());
        samples.Enqueue(sample);
        while (samples.Count > settings.MaxSamplesPerTenant) samples.TryDequeue(out _);
    }

    public async Task<IReadOnlyList<RoutingQualitySample>> GetRecentAsync(
        string tenantId,
        int count,
        CancellationToken cancellationToken = default)
    {
        int limit = Math.Clamp(count, 0, options.Value.MaxSamplesPerTenant);
        if (limit == 0) return [];

        string key = BuildKey(tenantId);
        if (redis != null)
        {
            RedisValue[] values = await redis.GetDatabase().ListRangeAsync(key, 0, limit - 1)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            return values
                .Select(value => JsonSerializer.Deserialize<RoutingQualitySample>((string)value!, JsonOptions))
                .Where(sample => sample != null)
                .Cast<RoutingQualitySample>()
                .ToArray();
        }

        return _memory.TryGetValue(key, out ConcurrentQueue<RoutingQualitySample>? samples)
            ? samples.Reverse().Take(limit).ToArray()
            : [];
    }

    private static string BuildKey(string tenantId) =>
        KeyPrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tenantId)));
}
