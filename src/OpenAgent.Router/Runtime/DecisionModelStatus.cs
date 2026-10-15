namespace OpenAgent.Router;

// Passive health: probes never perform a paid inference or block existing conversations.
internal sealed class DecisionModelStatus
{
    private int _lastResult;
    internal bool IsDegraded => Volatile.Read(ref _lastResult) < 0;
    internal void Report(bool succeeded) => Volatile.Write(ref _lastResult, succeeded ? 1 : -1);
}
