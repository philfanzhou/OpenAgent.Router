using OpenAgent.Router.Models;

namespace OpenAgent.Router;

internal interface IAgentForwarder
{
    Task ForwardAsync(
        HttpContext context,
        IAgentProvider provider,
        ChatProtocol protocol,
        bool streaming,
        string? sessionId,
        CancellationToken cancellationToken);
}
