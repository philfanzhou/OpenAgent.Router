using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Models;
using OpenAgent.Router.Security;
using StackExchange.Redis;
using Xunit;

namespace OpenAgent.Router.Tests.Security;

public class AgentVisibilityServiceTests
{
    private static AgentUserContext User => new() { UserId = "user", TenantId = "tenant", IsAuthenticated = true };

    [Fact]
    public async Task AclRevoked_NextRequestDoesNotUseStalePermissions()
    {
        var database = new Mock<IDatabase>();
        database.SetupSequence(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(JsonSerializer.Serialize(new AgentAclEntry { AllowedUserIds = ["user"] }))
            .ReturnsAsync(JsonSerializer.Serialize(new AgentAclEntry { AllowedUserIds = ["other"] }));
        var service = Create(database);
        Assert.True(await service.IsAgentVisibleToUserAsync("finance", User));
        Assert.False(await service.IsAgentVisibleToUserAsync("finance", User));
    }

    [Fact]
    public async Task RedisFailure_DoesNotGrantAccess()
    {
        var database = new Mock<IDatabase>();
        database.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "unavailable"));
        var exception = await Assert.ThrowsAsync<AgentRoutingException>(() =>
            Create(database).IsAgentVisibleToUserAsync("finance", User));
        Assert.Equal(503, exception.StatusCode);
    }

    [Fact]
    public async Task InvalidAcl_DoesNotGrantAccess()
    {
        var database = new Mock<IDatabase>();
        database.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync("invalid JSON");
        var exception = await Assert.ThrowsAsync<AgentRoutingException>(() =>
            Create(database).IsAgentVisibleToUserAsync("finance", User));
        Assert.Equal(503, exception.StatusCode);
    }

    private static AgentVisibilityService Create(Mock<IDatabase> database)
    {
        var redis = new Mock<IConnectionMultiplexer>();
        redis.Setup(connection => connection.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database.Object);
        return new AgentVisibilityService(NullLogger<AgentVisibilityService>.Instance, redis.Object);
    }
}
