namespace SignalForge.Worker.Services
{
    public class OutboxOptions
    {
        /// <summary>Maximum number of outbox messages to claim per poll cycle.</summary>
        public int BatchSize { get; set; } = 100;

        /// <summary>
        /// How many times <see cref="BatchSize"/> the processor scans before applying per-tenant
        /// fairness. The batch is then filled round-robin (one candidate per tenant per round,
        /// oldest first) so a flood from one tenant cannot starve quieter tenants' messages.
        /// </summary>
        public int ScanMultiplier { get; set; } = 5;

        public double PollIntervalSeconds { get; set; } = 5;

        public int MaxAttempts { get; set; } = 5;

        /// <summary>Consecutive failing poll cycles that trigger the circuit breaker (deep backoff).</summary>
        public int FailureThreshold { get; set; } = 3;

        /// <summary>Upper bound for the exponential backoff between cycles.</summary>
        public double MaxBackoffSeconds { get; set; } = 300;

        /// <summary>
        /// How long a claimed outbox row is pinned to one worker before another may reclaim it.
        /// Prevents duplicate sends after a crash: a worker that dies between claiming and
        /// processing leaves ClaimedAt set, and no other worker touches the row until this lease
        /// lapses. Must comfortably exceed a single message's send time (<c>BatchSize</c> ×
        /// slowest consumer), otherwise a slow-but-healthy worker could be double-sent.
        /// </summary>
        public double ClaimLeaseSeconds { get; set; } = 300;
    }
}