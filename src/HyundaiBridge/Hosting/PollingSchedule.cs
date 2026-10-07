namespace HyundaiBridge.Hosting;

internal sealed class PollingSchedule(TimeProvider time)
{
    internal static readonly TimeSpan CachedInterval = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan RefreshCooldown = TimeSpan.FromMinutes(10);
    internal DateTimeOffset DueAt { get; private set; } = time.GetUtcNow();
    private int failures;
    internal TimeSpan Remaining => DueAt > time.GetUtcNow() ? DueAt - time.GetUtcNow() : TimeSpan.Zero;
    internal void Succeeded() { failures = 0; DueAt = time.GetUtcNow() + CachedInterval; }
    internal void Failed(TimeSpan? retryAfter = null)
    {
        failures = Math.Min(failures + 1, 4);
        var delay = TimeSpan.FromMinutes(Math.Min(10 * Math.Pow(2, failures - 1), 60));
        if (retryAfter > delay) delay = retryAfter.Value;
        DueAt = time.GetUtcNow() + delay;
    }
}
