using HyundaiBridge.Domain;
using HyundaiBridge.Hyundai;
using HyundaiBridge.Mqtt;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HyundaiBridge.Hosting;

internal sealed class BridgeWorker(MqttBridge mqtt, Func<CancellationToken, Task<BridgeSnapshot>> poll,
    SemaphoreSlim backendGate, TimeProvider time, ILogger<BridgeWorker> logger) : BackgroundService
{
    internal PollingSchedule Schedule { get; } = new(time);

    internal async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await backendGate.WaitAsync(cancellationToken);
            try
            {
                var snapshot = await poll(cancellationToken);
                await mqtt.UpdateSnapshotAsync(snapshot, cancellationToken);
                Schedule.Succeeded();
                logger.LogInformation("Cached discovery retrieved: {VehicleCount} vehicles", snapshot.Vehicles.Count);
            }
            finally { backendGate.Release(); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            Schedule.Failed((error as HyundaiException)?.RetryAfter);
            await mqtt.ApiFailedAsync(cancellationToken);
            logger.LogWarning("Hyundai poll failed: {FailureType}; next attempt at {NextPoll}",
                error.GetType().Name, Schedule.DueAt);
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
