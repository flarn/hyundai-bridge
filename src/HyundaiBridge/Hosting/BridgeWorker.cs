using HyundaiBridge.Domain;
using HyundaiBridge.Hyundai;
using HyundaiBridge.Mqtt;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HyundaiBridge.Hosting;

internal sealed class BridgeWorker(MqttBridge mqtt, Func<CancellationToken, Task<BridgeSnapshot>> poll,
    SemaphoreSlim backendGate, TimeProvider time, ILogger<BridgeWorker> logger,
    BridgeStatistics statistics) : BackgroundService
{
    internal PollingSchedule Schedule { get; } = new(time);

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
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(Schedule.Remaining, time, cancellationToken);
            await PollOnceAsync(cancellationToken);
        }
    }
}
