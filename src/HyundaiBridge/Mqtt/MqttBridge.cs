using System.Buffers;
using System.Text;
using System.Threading.Channels;
using HyundaiBridge.Domain;
using HyundaiBridge.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;

namespace HyundaiBridge.Mqtt;

internal sealed class MqttBridge : IDisposable
{
    private readonly BridgeOptions options;
    private readonly ILogger<MqttBridge> logger;
    private readonly TimeProvider time;
    private readonly CommandProcessor commands;
    private readonly IMqttClient client = new MqttClientFactory().CreateMqttClient();
    private readonly SemaphoreSlim publishing = new(1, 1);
    private readonly Dictionary<string, string> retained = [];
    private readonly Dictionary<string, VehicleMetadata> vehicles = [];
    private readonly Dictionary<string, VehicleState> states = [];
    private readonly Channel<(VehicleCommand Command, long Generation, long ReceivedAt)> incoming = Channel.CreateBounded<(VehicleCommand, long, long)>(64);
    private long generation;
    private bool ready;
    private string BridgeTopic => $"{Contract.Prefix}/bridges/{options.BridgeId}";
    internal IReadOnlyDictionary<string, VehicleState> States => states;
    internal bool IsConnected => Volatile.Read(ref ready) && client.IsConnected;

    internal MqttBridge(BridgeOptions options, CommandProcessor commands, ILogger<MqttBridge> logger, TimeProvider time)
    {
        this.options = options;
        this.commands = commands;
        this.logger = logger;
        this.time = time;
        retained[BridgeTopic + "/manifest"] = Contract.Manifest(options.BridgeId, new([], []));
        client.ApplicationMessageReceivedAsync += ReceivedAsync;
        client.DisconnectedAsync += _ =>
        {
            ready = false;
            Interlocked.Increment(ref generation);
            logger.LogInformation("MQTT disconnected");
            return Task.CompletedTask;
        };
    }

    internal async Task UpdateSnapshotAsync(BridgeSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (snapshot.Vehicles.Any(x => !Contract.ValidId(x.VehicleId) || string.IsNullOrWhiteSpace(x.Vin)) ||
            snapshot.Vehicles.Select(x => x.VehicleId).Distinct().Count() != snapshot.Vehicles.Count ||
            snapshot.Vehicles.Select(x => x.Vin).Distinct().Count() != snapshot.Vehicles.Count)
            throw new FormatException("Invalid normalized vehicle metadata");
        foreach (var state in snapshot.States)
        {
            var info = snapshot.Vehicles.SingleOrDefault(x => x.VehicleId == state.VehicleId);
            if (info is null || info.Vin != state.Vin) throw new FormatException("State does not match vehicle identity");
            Contract.ValidateState(state);
        }
        await publishing.WaitAsync(cancellationToken);
        try
        {
            foreach (var info in snapshot.Vehicles)
                if (vehicles.TryGetValue(info.VehicleId, out var previous) && previous.Vin != info.Vin)
                    throw new FormatException("Vehicle topic identity was reassigned");
            foreach (var oldId in vehicles.Keys.Except(snapshot.Vehicles.Select(x => x.VehicleId)).ToArray())
            {
                // Delete retained state for removed vehicles, not just metadata.
                foreach (var suffix in new[] { "state", "availability" })
                {
                    var topic = $"{Contract.Prefix}/{oldId}/{suffix}";
                    retained.Remove(topic);
                    await PublishIfConnectedAsync(topic, "", true, cancellationToken);
                }
                vehicles.Remove(oldId);
                states.Remove(oldId);
            }
            foreach (var info in snapshot.Vehicles)
            {
                vehicles[info.VehicleId] = info;
                retained[$"{Contract.Prefix}/{info.VehicleId}/availability"] = Contract.Serialize(new VehicleAvailability(true));
            }
            retained[BridgeTopic + "/manifest"] = Contract.Manifest(options.BridgeId, snapshot);
            foreach (var state in snapshot.States) CacheState(state);
            await PublishCacheAsync(cancellationToken);
        }
        finally { publishing.Release(); }
    }

    internal async Task ApiFailedAsync(CancellationToken cancellationToken, string? vehicleId = null)
    {
        await publishing.WaitAsync(cancellationToken);
        try
        {
            foreach (var id in vehicles.Keys.Where(id => vehicleId is null || id == vehicleId))
            {
                var topic = $"{Contract.Prefix}/{id}/availability";
                retained[topic] = Contract.Serialize(new VehicleAvailability(false));
                await PublishIfConnectedAsync(topic, retained[topic], true, cancellationToken);
            }
        }
        finally { publishing.Release(); }
    }

    internal async Task PublishObservedStateAsync(VehicleState state, CancellationToken cancellationToken)
    {
        Contract.ValidateState(state);
        await publishing.WaitAsync(cancellationToken);
        try
        {
            var info = vehicles[state.VehicleId];
            using var document = System.Text.Json.JsonDocument.Parse(Contract.Serialize(state));
            var fields = document.RootElement.EnumerateObject()
                .Where(p => p.Value.ValueKind != System.Text.Json.JsonValueKind.Null &&
                    p.Name is not ("vehicleId" or "vin" or "vehicleUpdatedAt" or "bridgeUpdatedAt"))
                .Select(p => p.Name).Union(info.Capabilities.StateFields).ToArray();
            vehicles[state.VehicleId] = info with { Capabilities = info.Capabilities with { StateFields = fields } };
            CacheState(state);
            retained[$"{Contract.Prefix}/{state.VehicleId}/availability"] = Contract.Serialize(new VehicleAvailability(true));
            retained[BridgeTopic + "/manifest"] = Contract.Manifest(options.BridgeId, new(vehicles.Values.ToArray(), states.Values.ToArray()));
            await PublishCacheAsync(cancellationToken);
        }
        finally { publishing.Release(); }
    }

    private void CacheState(VehicleState state)
    {
        if (!vehicles.TryGetValue(state.VehicleId, out var vehicle) || vehicle.Vin != state.Vin)
            throw new FormatException("State does not match vehicle identity");
        Contract.ValidateState(state);
        states[state.VehicleId] = state;
        retained[$"{Contract.Prefix}/{state.VehicleId}/state"] = Contract.Serialize(state);
    }

    private async Task UpdateStateAsync(VehicleState state, CancellationToken cancellationToken)
    {
        await publishing.WaitAsync(cancellationToken);
        try { CacheState(state); await PublishCacheAsync(cancellationToken); }
        finally { publishing.Release(); }
    }

    private Task ReceivedAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        if (args.ApplicationMessage.Retain)
        {
            logger.LogWarning("Retained control rejected");
            return Task.CompletedTask;
        }
        try
        {
            if (args.ApplicationMessage.Payload.Length > 4096) throw new FormatException("Command payload too large");
            var command = Contract.ParseCommand(args.ApplicationMessage.Topic, args.ApplicationMessage.Payload.ToArray());
            if (!ready || !incoming.Writer.TryWrite((command, Interlocked.Read(ref generation), time.GetTimestamp())))
                logger.LogWarning("Command not queued: bridge disconnected or command queue full");
        }
        catch (FormatException) { logger.LogWarning("Malformed MQTT command rejected"); }
        return Task.CompletedTask;
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        var processing = ProcessCommandsAsync(cancellationToken);
        var mqttOptions = new MqttClientOptionsBuilder().WithClientId("hyundai-bridge-" + options.BridgeId)
            .WithTcpServer(options.Host, options.Port).WithCleanSession()
            .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V500)
            .WithTimeout(TimeSpan.FromSeconds(10)).WithKeepAlivePeriod(TimeSpan.FromSeconds(15))
            .WithWillTopic(BridgeTopic + "/availability").WithWillPayload("offline")
            .WithWillRetain().WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);
        if (options.Username is not null) mqttOptions.WithCredentials(options.Username, options.Password);
        if (options.Tls) mqttOptions.WithTlsOptions(tls => tls.UseTls());
        var failures = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await client.ConnectAsync(mqttOptions.Build(), cancellationToken);
                    await client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                        .WithTopicFilter(filter => filter.WithTopic($"{Contract.Prefix}/+/command/#")
                            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).WithRetainAsPublished())
                        .Build(), cancellationToken);
                    await publishing.WaitAsync(cancellationToken);
                    try
                    {
                        await PublishCacheAsync(cancellationToken);
                        await PublishIfConnectedAsync(BridgeTopic + "/availability", "online", true, cancellationToken);
                        ready = client.IsConnected;
                    }
                    finally { publishing.Release(); }
                    failures = 0;
                    logger.LogInformation("MQTT connected");
                    while (client.IsConnected)
                    {
                        if (processing.IsCompleted) await processing;
                        await Task.Delay(TimeSpan.FromSeconds(1), time, cancellationToken);
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    // Never include MQTT exception text (broker credentials/URL).
                    logger.LogWarning("MQTT operation failed: {FailureType}", error.GetType().Name);
                    if (processing.IsFaulted) throw;
                    if (client.IsConnected) await DisconnectAsync();
                }
                ready = false;
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(2 * Math.Pow(2, Math.Min(failures++, 5)), 60)), time, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            ready = false;
            incoming.Writer.TryComplete();
            await processing;
            if (client.IsConnected)
            {
                using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await PublishIfConnectedAsync(BridgeTopic + "/availability", "offline", true, closing.Token);
                await DisconnectAsync();
            }
        }
    }

    private async Task ProcessCommandsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var (command, receivedGeneration, receivedAt) in incoming.Reader.ReadAllAsync(cancellationToken))
            {
                if (receivedGeneration != Interlocked.Read(ref generation)) continue;
                VehicleMetadata? vehicle;
                await publishing.WaitAsync(cancellationToken);
                try { vehicle = vehicles.GetValueOrDefault(command.VehicleId); }
                finally { publishing.Release(); }
                // Vehicle ids are globally unique across bridges on a broker.
                // A command owned by another bridge must receive no reply here.
                if (vehicle is null) continue;
                await commands.ProcessAsync(command, vehicle,
                    result => PublishIfConnectedAsync($"{Contract.Prefix}/{command.VehicleId}/command-result", Contract.Serialize(result), false, cancellationToken),
                    state => UpdateStateAsync(state, cancellationToken), cancellationToken,
                    commands.Timeout - time.GetElapsedTime(receivedAt));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task PublishCacheAsync(CancellationToken cancellationToken)
    {
        foreach (var (topic, payload) in retained)
            await PublishIfConnectedAsync(topic, payload, true, cancellationToken);
    }

    private async Task PublishIfConnectedAsync(string topic, string payload, bool retain, CancellationToken cancellationToken)
    {
        if (!client.IsConnected) return;
        try
        {
            var result = await client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic)
                .WithPayload(Encoding.UTF8.GetBytes(payload)).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag(retain).Build(), cancellationToken);
            if (!result.IsSuccess) throw new IOException("Broker rejected publication");
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("MQTT publish failed: {FailureType}; retained data will replay after reconnect", error.GetType().Name);
            ready = false;
            await DisconnectAsync();
        }
    }

    private async Task DisconnectAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await client.DisconnectAsync(new MqttClientDisconnectOptions(), timeout.Token); }
        catch (Exception error) { logger.LogWarning("MQTT disconnect failed: {FailureType}", error.GetType().Name); }
    }

    public void Dispose() { client.Dispose(); publishing.Dispose(); }
}
