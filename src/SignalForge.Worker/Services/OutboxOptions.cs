namespace SignalForge.Worker.Services
{
    /// <summary>
    /// Configuration options for the outbox processing pipeline.
    /// Bound from the "Outbox" configuration section.
    /// </summary>
    public class OutboxOptions
    {
        /// <summary>Maximum number of outbox messages to claim per poll cycle.</summary>
        public int BatchSize { get; set; } = 100;

        /// <summary>Base delay between poll cycles when things are healthy or idle.</summary>
        public double PollIntervalSeconds { get; set; } = 5;

        /// <summary>Maximum times a single message may be retried before being dead-lettered.</summary>
        public int MaxAttempts { get; set; } = 5;

        /// <summary>Consecutive failing poll cycles that trigger the circuit breaker (deep backoff).</summary>
        public int FailureThreshold { get; set; } = 3;

        /// <summary>Upper bound for the exponential backoff between cycles.</summary>
        public double MaxBackoffSeconds { get; set; } = 300;
    }
}