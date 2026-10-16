using System.Security.Cryptography;
using System.Text;
using OpenAgent.Router.Models;

namespace OpenAgent.Router.Endpoints;

/// <summary>
/// Maps a stateless standard chat request to a Router-issued session. A continuation is found by, in order:
/// the <c>X-Session-Id</c> header, a Responses <c>previous_response_id</c> (the chat ID Router issued),
/// or the fingerprint of the first user message once the history contains an assistant turn.
/// </summary>
internal sealed class ChatSessionTracker(IConversationProviderResolver conversations)
{
    internal const string SessionHeader = "X-Session-Id";
    private const string ChatKeyPrefix = "chat:";
    private const string FingerprintKeyPrefix = "fp:";

    internal async Task<string?> FindSessionAsync(
        AgentProviderRequestContext context, ChatRequestInfo info, string? headerSessionId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(headerSessionId))
        {
            return headerSessionId.Trim();
        }
        if (info.PreviousResponseId != null)
        {
            ConversationProviderAffinity? chat = await conversations.TryResolveAsync(
                context, ChatKeyPrefix + info.PreviousResponseId, cancellationToken).ConfigureAwait(false);
            return chat?.SessionId ?? throw new AgentRoutingException(StatusCodes.Status404NotFound,
                RouterErrorCodes.ConversationNotFound, "The previous response was not found");
        }
        if (info.HasAssistantTurn && info.FirstUserText != null)
        {
            return (await conversations.TryResolveAsync(context, Fingerprint(context, info), cancellationToken)
                .ConfigureAwait(false))?.SessionId;
        }
        return null;
    }

    /// <summary>Records what a later request needs to find this session again.</summary>
    internal async Task RememberAsync(
        AgentProviderRequestContext context, ChatProtocol protocol, ChatRequestInfo info,
        AgentSelection selection, string chatId, CancellationToken cancellationToken)
    {
        if (protocol == ChatProtocol.Responses)
        {
            await conversations.BindAsync(context, ChatKeyPrefix + chatId, selection.ProviderId,
                selection.AgentId!, cancellationToken, selection.ConversationId).ConfigureAwait(false);
        }
        else if (!info.HasAssistantTurn && info.FirstUserText != null)
        {
            await conversations.OverwriteAsync(context, Fingerprint(context, info), selection.ProviderId,
                selection.AgentId!, selection.ConversationId!, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string Fingerprint(AgentProviderRequestContext context, ChatRequestInfo info) =>
        FingerprintKeyPrefix + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{context.UserContext.UserId}\0{info.FirstUserText}")));
}
