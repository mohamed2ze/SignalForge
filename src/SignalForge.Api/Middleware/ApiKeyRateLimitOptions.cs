namespace SignalForge.Api.Middleware;

public class ApiKeyRateLimitOptions
{
    public const string SectionName = "RateLimiting";

    public int PermitLimit { get; set; } = 1000;

    public int WindowSeconds { get; set; } = 60;

    /// <summary>
    /// Counter store behind the limiter: <c>InMemory</c> (default) enforces the budget per API
    /// process; <c>Sql</c> enforces it across the whole deployment via the shared SQL table.
    /// </summary>
    public string Store { get; set; } = "InMemory";
}