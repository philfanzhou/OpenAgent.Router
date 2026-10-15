using Xunit;

namespace OpenAgent.Router.Tests.Integration;

internal sealed class RedisFactAttribute : FactAttribute
{
    public RedisFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ROUTER_TEST_REDIS")))
            Skip = "Set ROUTER_TEST_REDIS to run real Redis multi-instance tests (enabled in CI).";
    }
}
