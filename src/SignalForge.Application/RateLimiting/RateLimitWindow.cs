namespace SignalForge.Application.RateLimiting;

/// <summary>
/// Fixed-window math shared by every <see cref="IRateLimiter"/> implementation. The window key is
/// deterministic across processes/nodes for the same wall-clock time ({ticks}/{window length}), so
/// all instances agree on which bucket a request belongs to — that is what lets the SQL store
/// enforce a true cross-node budget.
/// </summary>
public static class RateLimitWindow
{
    /// <summary>Returns the fixed-window bucket key owning <paramref name="now"/>.</summary>
    public static long GetWindowKey(DateTimeOffset now, TimeSpan window)
    {
        if (window <= TimeSpan.Zero)
            window = TimeSpan.FromMinutes(1);

        var windowTicks = window.Ticks;
        var nowTicks = now.UtcTicks;
        return windowTicks > 0 ? nowTicks - (nowTicks % windowTicks) : nowTicks;
    }
}