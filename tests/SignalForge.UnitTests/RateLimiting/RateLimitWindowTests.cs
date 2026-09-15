using SignalForge.Application.RateLimiting;

namespace SignalForge.UnitTests.RateLimiting;

/// <summary>
/// Pins the fixed-window math shared by every <see cref="IRateLimiter"/> store: the same wall-clock
/// instant must map to the same bucket start across processes, and crossing a window boundary must
/// select a fresh bucket.
/// </summary>
public sealed class RateLimitWindowTests
{
    [Theory]
    [InlineData(60)]
    [InlineData(5)]
    [InlineData(1)]
    public void Same_Instant_Maps_To_Same_Window_Key_On_Every_Call(int windowSeconds)
    {
        var window = TimeSpan.FromSeconds(windowSeconds);
        var now = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(
            RateLimitWindow.GetWindowKey(now, window),
            RateLimitWindow.GetWindowKey(now, window));
    }

    [Fact]
    public void Window_Key_Is_Aligned_To_Window_Boundary_Not_To_Request_Time()
    {
        var window = TimeSpan.FromSeconds(60);
        var boundary = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
        var midWindow = boundary.AddSeconds(59).AddTicks(1);

        Assert.Equal(boundary.Ticks, RateLimitWindow.GetWindowKey(midWindow, window));
    }

    [Fact]
    public void Next_Window_Gets_A_New_Key_Exactly_One_Window_Later()
    {
        var window = TimeSpan.FromSeconds(60);
        var first = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
        var second = first.Add(window + TimeSpan.FromTicks(1));

        var firstKey = RateLimitWindow.GetWindowKey(first, window);
        var secondKey = RateLimitWindow.GetWindowKey(second, window);

        Assert.NotEqual(firstKey, secondKey);
        Assert.Equal(firstKey + window.Ticks, secondKey);
    }

    [Fact]
    public void Degenerate_Window_Falls_Back_To_One_Minute()
    {
        var now = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(
            RateLimitWindow.GetWindowKey(now, TimeSpan.Zero),
            RateLimitWindow.GetWindowKey(now, TimeSpan.FromMinutes(1)));
    }
}