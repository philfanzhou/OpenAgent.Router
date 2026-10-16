namespace OpenAgent.Router;

// Passive health: probes never perform a paid inference or block existing conversations.
internal sealed class DecisionModelStatus(TimeProvider? timeProvider = null)
{
    internal const int BreakerThreshold = 5;
    internal static readonly TimeSpan BreakerOpenDuration = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private int _lastResult;
    private int _consecutiveFailures;
    private long _openUntilTicks;

    internal bool IsDegraded => Volatile.Read(ref _lastResult) < 0;
    internal void Report(bool succeeded) => Volatile.Write(ref _lastResult, succeeded ? 1 : -1);

    // Breaker: after BreakerThreshold consecutive call failures (timeout/transport/oversize) new sessions skip
    // the model for BreakerOpenDuration. The first call afterwards is a probe: one more failure re-opens at once.
    internal bool IsBreakerOpen => _time.GetUtcNow().UtcTicks < Volatile.Read(ref _openUntilTicks);

    internal void ReportCall(bool succeeded)
    {
        if (succeeded)
        {
            Volatile.Write(ref _consecutiveFailures, 0);
            return;
        }
        if (Interlocked.Increment(ref _consecutiveFailures) >= BreakerThreshold)
        {
            Volatile.Write(ref _openUntilTicks, (_time.GetUtcNow() + BreakerOpenDuration).UtcTicks);
        }
    }
}
