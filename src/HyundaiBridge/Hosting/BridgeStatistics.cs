namespace HyundaiBridge.Hosting;

// Only diagnostic metadata belongs here: never URLs, headers, bodies or exceptions.
internal sealed class BridgeStatistics(TimeProvider time)
{
    private readonly object gate = new();
    private readonly DateTimeOffset startedAt = time.GetUtcNow();
    private readonly Queue<ApiRequestObservation> recentRequests = new();
    private long requestCount, requestFailures, rateLimitedResponses, logins, renewals;
    private double totalDurationMs;
    private DateTimeOffset? sessionExpiresAt, lastSuccessAt, lastFailureAt, nextPollAt;
    private string? sessionSource, lastFailure;
    private bool? apiReachable;
    private int? vehicleCount;

    internal void RequestCompleted(string operation, int? statusCode, TimeSpan duration, string? failure)
    {
        lock (gate)
        {
            requestCount++;
            totalDurationMs += duration.TotalMilliseconds;
            if (statusCode >= 400 || failure is "network" or "timeout") requestFailures++;
            if (statusCode == 429) rateLimitedResponses++;
            recentRequests.Enqueue(new(time.GetUtcNow(), operation, statusCode,
                Math.Round(duration.TotalMilliseconds, 1), failure));
            while (recentRequests.Count > 20) recentRequests.Dequeue();
        }
    }

    internal void SessionUpdated(string source, DateTimeOffset expiresAt)
    {
        lock (gate)
        {
            sessionSource = source;
            sessionExpiresAt = expiresAt;
            if (source == "login") logins++;
            if (source == "refresh") renewals++;
        }
    }

    internal void PollSucceeded(int count)
    {
        lock (gate)
        {
            apiReachable = true;
            lastSuccessAt = time.GetUtcNow();
            vehicleCount = count;
        }
    }

    internal void PollFailed(string failure)
    {
        lock (gate)
        {
            apiReachable = false;
            lastFailureAt = time.GetUtcNow();
            lastFailure = failure;
        }
    }

    internal void PollScheduled(DateTimeOffset? dueAt) { lock (gate) nextPollAt = dueAt; }

    internal BridgeStatus Snapshot(bool mqttConnected)
    {
        lock (gate)
            return new(startedAt, time.GetUtcNow(), apiReachable, mqttConnected, requestCount,
                requestFailures, rateLimitedResponses,
                requestCount == 0 ? null : Math.Round(totalDurationMs / requestCount, 1),
                logins, renewals, sessionSource, sessionExpiresAt, vehicleCount, lastSuccessAt,
                lastFailureAt, lastFailure, nextPollAt, recentRequests.Reverse().ToArray());
    }
}

internal sealed record ApiRequestObservation(DateTimeOffset At, string Operation, int? StatusCode,
    double DurationMs, string? Failure);

internal sealed record BridgeStatus(DateTimeOffset StartedAt, DateTimeOffset ObservedAt,
    bool? ApiReachable, bool MqttConnected, long RequestCount, long RequestFailures,
    long RateLimitedResponses, double? AverageDurationMs, long Logins, long Renewals,
    string? SessionSource, DateTimeOffset? SessionExpiresAt, int? VehicleCount,
    DateTimeOffset? LastSuccessAt, DateTimeOffset? LastFailureAt, string? LastFailure,
    DateTimeOffset? NextPollAt, IReadOnlyList<ApiRequestObservation> RecentRequests);
