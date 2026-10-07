using HyundaiBridge.Domain;
using HyundaiBridge.Hyundai;
using HyundaiBridge.Mqtt;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HyundaiBridge.Hosting;

internal sealed class BridgeWorker(MqttBridge mqtt, Func<CancellationToken, Task<BridgeSnapshot>> poll,
    SemaphoreSlim backendGate, TimeProvider time, ILogger<BridgeWorker> logger,
    BridgeStatistics statistics,
    Func<VehicleMetadata, CancellationToken, Task<VehicleState>>? readState = null) : BackgroundService
{
    internal PollingSchedule Schedule { get; } = new(time);
    private IReadOnlyList<VehicleMetadata>? discoveredVehicles;
    private readonly Dictionary<string, (DateTimeOffset DueAt, int Failures)> stateSchedule = new();

    internal async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await backendGate.WaitAsync(cancellationToken);
            try
            {
                BridgeSnapshot snapshot;
                try
                {
                    snapshot = await poll(cancellationToken);
                    statistics.PollSucceeded(snapshot.Vehicles.Count);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    statistics.PollFailed(error is HyundaiException { StatusCode: { } status }
                        ? "HTTP " + status : error.GetType().Name);
                    throw;
                }
                await mqtt.UpdateSnapshotAsync(snapshot, cancellationToken);
                discoveredVehicles = snapshot.Vehicles;
                foreach (var vehicle in snapshot.Vehicles) stateSchedule[vehicle.VehicleId] = (time.GetUtcNow(), 0);
                Schedule.Succeeded();
                statistics.PollScheduled(Schedule.DueAt);
                logger.LogInformation("Cached discovery retrieved: {VehicleCount} vehicles", snapshot.Vehicles.Count);
            }
            finally { backendGate.Release(); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            Schedule.Failed((error as HyundaiException)?.RetryAfter);
            statistics.PollScheduled(Schedule.DueAt);
            await mqtt.ApiFailedAsync(cancellationToken);
            if (Schedule.DueAt is { } nextPoll)
                logger.LogWarning("Hyundai poll failed: {FailureType}; next attempt at {NextPoll}",
                    error.GetType().Name, nextPoll);
            else
                logger.LogWarning("Hyundai discovery stopped after five failed attempts: {FailureType}; restart the bridge to retry",
                    error.GetType().Name);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var transport = mqtt.RunAsync(lifetime.Token);
        var polling = PollAsync(lifetime.Token);
        try
        {
            await Task.WhenAny(transport, polling);
            await lifetime.CancelAsync();
            await Task.WhenAll(transport, polling);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        while (discoveredVehicles is null)
        {
            await Task.Delay(Schedule.Remaining, time, cancellationToken);
            await PollOnceAsync(cancellationToken);
        }
        while (!cancellationToken.IsCancellationRequested)
        {
            if (readState is null || discoveredVehicles.Count == 0)
                await Task.Delay(Timeout.InfiniteTimeSpan, time, cancellationToken);
            await PollStatesOnceAsync(cancellationToken);
            var next = stateSchedule.Values.Min(x => x.DueAt);
            await Task.Delay(next > time.GetUtcNow() ? next - time.GetUtcNow() : TimeSpan.Zero, time, cancellationToken);
        }
    }

    internal async Task PollStatesOnceAsync(CancellationToken cancellationToken)
    {
        if (readState is null || discoveredVehicles is null) return;
        var failed = false;
        var read = false;
        foreach (var vehicle in discoveredVehicles)
        {
            var schedule = stateSchedule[vehicle.VehicleId];
            if (schedule.DueAt > time.GetUtcNow()) continue;
            await backendGate.WaitAsync(cancellationToken);
            read = true;
            try
            {
                var state = await readState(vehicle, cancellationToken);
                await mqtt.PublishObservedStateAsync(state, cancellationToken);
                stateSchedule[vehicle.VehicleId] = (time.GetUtcNow().AddMinutes(10), 0);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                failed = true;
                var failures = Math.Min(schedule.Failures + 1, 4);
                var delay = TimeSpan.FromMinutes(Math.Min(10 * Math.Pow(2, failures - 1), 60));
                if (error is HyundaiException { RetryAfter: { } retry } && retry > delay) delay = retry;
                var due = time.GetUtcNow() + delay;
                stateSchedule[vehicle.VehicleId] = (due, failures);
                statistics.PollFailed(error is HyundaiException { StatusCode: { } status } ? "HTTP " + status : error.GetType().Name);
                await mqtt.ApiFailedAsync(cancellationToken, vehicle.VehicleId);
                logger.LogWarning("Cached vehicle state failed: {FailureType}; next attempt at {NextPoll}", error.GetType().Name, due);
                if (error is HyundaiException { StatusCode: 429 })
                {
                    // The upstream quota is shared by the account; do not continue with other cars.
                    foreach (var id in stateSchedule.Keys.ToArray())
                    {
                        var existing = stateSchedule[id];
                        if (existing.DueAt < due) stateSchedule[id] = (due, existing.Failures);
                    }
                    break;
                }
            }
            finally { backendGate.Release(); }
        }
        if (read && !failed && stateSchedule.Values.All(x => x.Failures == 0)) statistics.PollSucceeded(discoveredVehicles.Count);
        if (stateSchedule.Count > 0) statistics.StatePollScheduled(stateSchedule.Values.Min(x => x.DueAt));
    }
}
