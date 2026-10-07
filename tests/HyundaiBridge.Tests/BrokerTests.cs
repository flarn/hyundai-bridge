using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using HyundaiBridge.Domain;
using HyundaiBridge.Mqtt;
using MQTTnet;
using MQTTnet.Protocol;
using Xunit;

namespace HyundaiBridge.Tests;

public sealed class BrokerFactAttribute : FactAttribute
{
    public BrokerFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("HYUNDAI_TEST_MQTT_PORT") is null)
            Skip = "Requires a disposable loopback MQTT broker; see docs/bridge-service.md";
    }
}

public sealed class HaPipelineFactAttribute : FactAttribute
{
    public HaPipelineFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("HYUNDAI_TEST_HA_SIGNALS") is null)
            Skip = "Started by the real HA pipeline test, not the standalone .NET suite";
    }
}

// Tests use genuine MQTTnet connections and the shared normalized fixtures.
// No Hyundai credentials or API requests are made.
public sealed class BrokerTests
{
    [HaPipelineFact]
    public async Task HomeAssistantRoundTrip()
    {
        using var fixture = new CommandFixture();
        var signals = Environment.GetEnvironmentVariable("HYUNDAI_TEST_HA_SIGNALS")!;
        var port = int.Parse(Environment.GetEnvironmentVariable("HYUNDAI_TEST_MQTT_PORT")!);
        using var observer = new MqttClientFactory().CreateMqttClient();
        var online = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        observer.ApplicationMessageReceivedAsync += args =>
        {
            if (args.ApplicationMessage.Topic == "hyundai/v1/bridges/home/availability" &&
                !args.ApplicationMessage.Retain && Encoding.UTF8.GetString(args.ApplicationMessage.Payload.ToArray()) == "online")
                online.TrySetResult();
            return Task.CompletedTask;
        };
        await observer.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", port).Build());
        await observer.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter("hyundai/v1/bridges/home/availability").Build());
        using var bridge = fixture.Bridge(port, async (command, accepted, ct) =>
        {
            Assert.Equal("unlock", command.Command);
            await accepted();
            await Until(() => File.Exists(Path.Combine(signals, "release")));
            ct.ThrowIfCancellationRequested();
            return TestVehicle.State() with { IsLocked = false };
        });
        await bridge.UpdateSnapshotAsync(TestVehicle.Snapshot(), CancellationToken.None);
        using var stopping = new CancellationTokenSource();
        var run = bridge.RunAsync(stopping.Token);
        try
        {
            await online.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await File.WriteAllTextAsync(Path.Combine(signals, "ready"), "ready");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!File.Exists(Path.Combine(signals, "done"))) await Task.Delay(20, timeout.Token);
        }
        finally
        {
            await stopping.CancelAsync();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            await observer.DisconnectAsync();
        }
    }

    [BrokerFact]
    public async Task StateCommandsRetainFlagsDeduplicationReconnectAndOffline()
    {
        using var fixture = new CommandFixture();
        var port = int.Parse(Environment.GetEnvironmentVariable("HYUNDAI_TEST_MQTT_PORT")!);
        using var observer = new MqttClientFactory().CreateMqttClient();
        var messages = new ConcurrentQueue<(string Topic, string Payload, bool Retained)>();
        observer.ApplicationMessageReceivedAsync += args =>
        {
            messages.Enqueue((args.ApplicationMessage.Topic, Encoding.UTF8.GetString(args.ApplicationMessage.Payload.ToArray()), args.ApplicationMessage.Retain));
            return Task.CompletedTask;
        };
        var observerOptions = new MqttClientOptionsBuilder().WithClientId("hyundai-test-observer-" + Guid.NewGuid())
            .WithTcpServer("127.0.0.1", port).WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V500).Build();
        await observer.ConnectAsync(observerOptions);
        await observer.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter("hyundai/v1/#").Build());
        var calls = 0;
        using var bridge = fixture.Bridge(port, async (command, accepted, ct) =>
        {
            Interlocked.Increment(ref calls);
            await accepted();
            await Task.Delay(50, ct);
            return TestVehicle.State() with { IsLocked = false };
        });
        await bridge.UpdateSnapshotAsync(TestVehicle.Snapshot(), CancellationToken.None);
        using var stopping = new CancellationTokenSource();
        var run = bridge.RunAsync(stopping.Token);
        try
        {
            await Until(() => messages.Any(x => x.Topic.EndsWith("bridges/home/availability") && x.Payload == "online" && !x.Retained));
            Assert.Contains(messages, x => x.Topic == "hyundai/v1/example-ev/state" && JsonDocument.Parse(x.Payload).RootElement.GetProperty("batteryPercent").GetInt32() == 73);
            messages.Clear();
            var id = Guid.NewGuid();
            var payload = Contract.Serialize(new { commandId = id });
            await observer.PublishAsync(Message("hyundai/v1/example-ev/command/unlock", payload, true));
            await Task.Delay(150);
            Assert.Equal(0, calls); // Live retained controls, not just startup replay.
            await observer.PublishAsync(Message("hyundai/v1/example-ev/command/unlock", "", true)); // delete bad retained control
            await observer.PublishAsync(Message("hyundai/v1/example-ev/command/unlock", payload, false));
            await Until(() => Results(messages, id).Any(x => x.Status == "completed"));
            Assert.Equal(["accepted", "completed"], Results(messages, id).Select(x => x.Status).ToArray());
            Assert.Equal(1, calls);
            await observer.PublishAsync(Message("hyundai/v1/example-ev/command/unlock", payload, false));
            await Until(() => Results(messages, id).Count(x => x.Status == "completed") == 2);
            Assert.Equal(1, calls);
            Assert.All(messages.Where(x => x.Topic.EndsWith("command-result")), x => Assert.False(x.Retained));

            // Force this client's disconnection using the same MQTT client id;
            // MQTTnet must reconnect and replay retained state independently.
            messages.Clear();
            using var replacement = new MqttClientFactory().CreateMqttClient();
            await replacement.ConnectAsync(new MqttClientOptionsBuilder().WithClientId("hyundai-bridge-home")
                .WithTcpServer("127.0.0.1", port).Build());
            await Until(() => messages.Any(x => x.Topic.EndsWith("bridges/home/availability") && x.Payload == "offline"));
            await replacement.DisconnectAsync();
            await Until(() => messages.Any(x => x.Topic.EndsWith("bridges/home/availability") && x.Payload == "online"));
            Assert.Contains(messages, x => x.Topic == "hyundai/v1/example-ev/state" && JsonDocument.Parse(x.Payload).RootElement.GetProperty("isLocked").GetBoolean() == false);

            // A brand-new subscriber obtains normalized retained documents.
            using var fresh = new MqttClientFactory().CreateMqttClient();
            var retainedState = new TaskCompletionSource<MqttApplicationMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            fresh.ApplicationMessageReceivedAsync += args => { retainedState.TrySetResult(args.ApplicationMessage); return Task.CompletedTask; };
            await fresh.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", port).Build());
            await fresh.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter("hyundai/v1/example-ev/state").Build());
            var received = await retainedState.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(received.Retain);
            Assert.False(JsonDocument.Parse(received.Payload).RootElement.GetProperty("isLocked").GetBoolean());
            await fresh.DisconnectAsync();
        }
        finally
        {
            messages.Clear();
            await stopping.CancelAsync();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            await Until(() => messages.Any(x => x.Topic.EndsWith("bridges/home/availability") && x.Payload == "offline"));
            await observer.DisconnectAsync();
        }
    }

    [BrokerFact]
    public async Task ObservedStatePublishesRetainedReadOnlyCapabilitiesAndSurvivesVehicleFailure()
    {
        using var fixture = new CommandFixture();
        var port = int.Parse(Environment.GetEnvironmentVariable("HYUNDAI_TEST_MQTT_PORT")!);
        using var bridge = fixture.Bridge(port);
        await bridge.UpdateSnapshotAsync(new([TestVehicle.Metadata() with { Capabilities = VehicleCapabilities.None }], []), CancellationToken.None);
        using var stopping = new CancellationTokenSource();
        var run = bridge.RunAsync(stopping.Token);
        try
        {
            await Until(() => bridge.IsConnected);
            await bridge.PublishObservedStateAsync(TestVehicle.State(), CancellationToken.None);
            await bridge.PublishObservedStateAsync(TestVehicle.State() with { BatteryPercent = null }, CancellationToken.None);
            await bridge.ApiFailedAsync(CancellationToken.None, "example-ev");
            using var observer = new MqttClientFactory().CreateMqttClient();
            var received = new ConcurrentDictionary<string, (string Payload, bool Retained)>();
            observer.ApplicationMessageReceivedAsync += args =>
            {
                received[args.ApplicationMessage.Topic] = (Encoding.UTF8.GetString(args.ApplicationMessage.Payload.ToArray()), args.ApplicationMessage.Retain);
                return Task.CompletedTask;
            };
            await observer.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", port).Build());
            await observer.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter("hyundai/v1/#").Build());
            await Until(() => received.ContainsKey("hyundai/v1/example-ev/state") && received.ContainsKey("hyundai/v1/example-ev/availability") && received.ContainsKey("hyundai/v1/bridges/home/manifest"));
            using var manifest = JsonDocument.Parse(received["hyundai/v1/bridges/home/manifest"].Payload);
            var caps = manifest.RootElement.GetProperty("vehicles")[0].GetProperty("capabilities");
            Assert.Contains("batteryPercent", caps.GetProperty("stateFields").EnumerateArray().Select(x => x.GetString()));
            Assert.Empty(caps.GetProperty("commands").EnumerateArray());
            using var state = JsonDocument.Parse(received["hyundai/v1/example-ev/state"].Payload);
            Assert.Equal(JsonValueKind.Null, state.RootElement.GetProperty("batteryPercent").ValueKind);
            Assert.Equal(382, state.RootElement.GetProperty("estimatedRangeKm").GetDouble());
            Assert.True(received["hyundai/v1/example-ev/state"].Retained);
            using var availability = JsonDocument.Parse(received["hyundai/v1/example-ev/availability"].Payload);
            Assert.False(availability.RootElement.GetProperty("apiReachable").GetBoolean());
            await observer.DisconnectAsync();
        }
        finally { await stopping.CancelAsync(); await run.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    internal static MqttApplicationMessage Message(string topic, string payload, bool retain) =>
        new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload).WithRetainFlag(retain)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build();
    private static IEnumerable<CommandResult> Results(ConcurrentQueue<(string Topic, string Payload, bool Retained)> messages, Guid id) =>
        messages.Where(x => x.Topic.EndsWith("command-result")).Select(x => JsonSerializer.Deserialize<CommandResult>(x.Payload, Contract.Json)!)
            .Where(x => x.CommandId == id);
    private static async Task Until(Func<bool> check)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!check()) await Task.Delay(20, timeout.Token);
    }
}
