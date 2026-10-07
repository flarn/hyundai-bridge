using HyundaiBridge.Hyundai;
using System.Text;
using System.Text.Json;
using HyundaiBridge.Domain;
using HyundaiBridge.Hosting;
using HyundaiBridge.Mqtt;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HyundaiBridge.Tests;

public sealed class BridgeTests
{
    [Fact]
    public void SerializationMatchesTheHaContractAndPreservesNullsAndTimes()
    {
        var state = TestVehicle.State();
        using var document = JsonDocument.Parse(Contract.Serialize(state));
        Assert.Equal(73, document.RootElement.GetProperty("batteryPercent").GetInt32());
        Assert.False(document.RootElement.GetProperty("isCharging").GetBoolean());
        Assert.Equal(state.VehicleUpdatedAt, document.RootElement.GetProperty("vehicleUpdatedAt").GetDateTimeOffset());
        using var nullable = JsonDocument.Parse(Contract.Serialize(state with { BatteryPercent = null }));
        Assert.Equal(JsonValueKind.Null, nullable.RootElement.GetProperty("batteryPercent").ValueKind);
        using var manifest = JsonDocument.Parse(Contract.Manifest("home", TestVehicle.Snapshot()));
        Assert.Equal(1, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("Example EV", manifest.RootElement.GetProperty("vehicles")[0].GetProperty("model").GetString());
        Assert.DoesNotContain("ccsp", manifest.RootElement.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DiscoveryDoesNotAdvertiseUnimplementedVehicleFeatures()
    {
        using var fixture = new Fixture();
        await fixture.Store.SaveAsync(fixture.Session(), CancellationToken.None);
        fixture.Http.Enqueue(_ => AuthenticationTests.Response(AuthenticationTests.Vehicles));
        using var client = fixture.Client();
        var snapshot = await client.GetBridgeSnapshotAsync(CancellationToken.None);
        var vehicle = Assert.Single(snapshot.Vehicles);
        Assert.Equal("KONA Electric", vehicle.Model);
        Assert.Equal("SANITIZED-VIN", vehicle.Vin);
        Assert.Empty(vehicle.Capabilities.StateFields);
        Assert.Empty(vehicle.Capabilities.Commands);
        Assert.Empty(snapshot.States);
        Assert.Equal(1, fixture.Http.RequestCount);
    }

    [Theory]
    [InlineData("lock", "{}")]
    [InlineData("lock", "{\"commandId\":true}")]
    [InlineData("lock", "{\"commandId\":\"00000000-0000-0000-0000-000000000000\"}")]
    [InlineData("lock", "[]")]
    [InlineData("lock", "malformed")]
    [InlineData("charge-limit", "{\"acPercent\":80,\"dcPercent\":90.5}")]
    public void MalformedCommandsAreRejected(string command, string payload)
    {
        Assert.Throws<FormatException>(() => Contract.ParseCommand($"hyundai/v1/example-ev/command/{command}", Encoding.UTF8.GetBytes(payload)));
    }

    [Theory]
    [InlineData("refresh")]
    [InlineData("lock")]
    [InlineData("unlock")]
    [InlineData("climate/stop")]
    [InlineData("charging/start")]
    [InlineData("charging/stop")]
    public void NoArgumentCommandParserPreservesCorrelation(string action)
    {
        var id = Guid.NewGuid();
        var command = Contract.ParseCommand($"hyundai/v1/example-ev/command/{action}",
            Encoding.UTF8.GetBytes(Contract.Serialize(new { commandId = id })));
        Assert.Equal(id, command.CommandId);
        Assert.Equal(action, command.Command);
        Assert.Equal("example-ev", command.VehicleId);
    }

    [Fact]
    public void ArgumentsUseVehicleCapabilitiesRatherThanHyundaiDefaults()
    {
        var vehicle = TestVehicle.Metadata();
        var valid = new VehicleCommand(vehicle.VehicleId, Guid.NewGuid(), "climate/start", 21.5, true);
        Contract.Validate(valid, vehicle);
        Assert.Throws<FormatException>(() => Contract.Validate(valid with { TemperatureCelsius = 21.25 }, vehicle));
        Assert.Throws<FormatException>(() => Contract.Validate(valid, vehicle with { Capabilities = VehicleCapabilities.None }));
        Contract.Validate(new(vehicle.VehicleId, Guid.NewGuid(), "charge-limit", AcPercent: 80, DcPercent: 90), vehicle);
        Assert.Throws<FormatException>(() => Contract.Validate(new(vehicle.VehicleId, Guid.NewGuid(), "charge-limit", AcPercent: 75, DcPercent: 90), vehicle));
    }

    [Fact]
    public void PollingBackoffHonorsRateLimitAndRecoversWithoutForcedRefresh()
    {
        var clock = new FixedClock();
        var schedule = new PollingSchedule(clock);
        Assert.Equal(TimeSpan.Zero, schedule.Remaining);
        schedule.Succeeded();
        Assert.Null(schedule.DueAt);
        Assert.Equal(Timeout.InfiniteTimeSpan, schedule.Remaining);
        schedule.Failed(); Assert.Equal(TimeSpan.FromMinutes(10), schedule.Remaining);
        schedule.Failed(); Assert.Equal(TimeSpan.FromMinutes(20), schedule.Remaining);
        schedule.Failed(); Assert.Equal(TimeSpan.FromMinutes(40), schedule.Remaining);
        schedule.Failed(TimeSpan.FromHours(3)); Assert.Equal(TimeSpan.FromHours(3), schedule.Remaining);
        schedule.Failed();
        Assert.Null(schedule.DueAt);
        Assert.Equal(Timeout.InfiniteTimeSpan, schedule.Remaining);
        schedule.Succeeded();
        Assert.Null(schedule.DueAt);
        Assert.Equal(Timeout.InfiniteTimeSpan, schedule.Remaining);
    }

    [Fact]
    public void DiscoveryStopsAfterFiveFailedAttemptsUntilRestart()
    {
        var clock = new FixedClock();
        var schedule = new PollingSchedule(clock);
        foreach (var minutes in new[] { 10, 20, 40, 60 })
        {
            schedule.Failed();
            Assert.Equal(TimeSpan.FromMinutes(minutes), schedule.Remaining);
            clock.Now += schedule.Remaining;
        }
        schedule.Failed(TimeSpan.FromHours(3));
        Assert.Null(schedule.DueAt);
        clock.Now += TimeSpan.FromDays(1);
        Assert.Equal(Timeout.InfiniteTimeSpan, schedule.Remaining);
        Assert.Equal(TimeSpan.Zero, new PollingSchedule(clock).Remaining);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulStartupDiscoveryStopsSchedulingForEmptyAndPopulatedGarages(bool hasVehicle)
    {
        using var fixture = new CommandFixture();
        using var mqtt = fixture.Bridge();
        var statistics = new BridgeStatistics(fixture.Clock);
        var snapshot = hasVehicle ? TestVehicle.Snapshot() : new BridgeSnapshot([], []);
        using var worker = new BridgeWorker(mqtt, _ => Task.FromResult(snapshot),
            fixture.Gate, fixture.Clock, NullLogger<BridgeWorker>.Instance, statistics);
        await worker.PollOnceAsync(CancellationToken.None);
        Assert.Equal(hasVehicle ? 1 : 0, statistics.Snapshot(false).VehicleCount);
        Assert.Null(statistics.Snapshot(false).NextPollAt);
        fixture.Clock.Now += TimeSpan.FromDays(1);
        Assert.Equal(Timeout.InfiniteTimeSpan, worker.Schedule.Remaining);
    }

    [Fact]
    public async Task AcceptedIsReportedSeparatelyAndDuplicatesSurviveRestart()
    {
        using var fixture = new CommandFixture();
        var calls = 0;
        var completion = new TaskCompletionSource<VehicleState?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = fixture.Processor(async (_, accepted, ct) =>
        {
            calls++;
            await accepted();
            return await completion.Task.WaitAsync(ct);
        });
        var cmd = new VehicleCommand("example-ev", Guid.NewGuid(), "unlock");
        var results = new List<CommandResult>();
        var acceptedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = processor.ProcessAsync(cmd, TestVehicle.Metadata(), result =>
        {
            results.Add(result);
            if (result.Status == "accepted") acceptedSignal.TrySetResult();
            return Task.CompletedTask;
        }, _ => throw new InvalidOperationException("No state was observed"), CancellationToken.None);
        await acceptedSignal.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(process.IsCompleted);
        Assert.Equal("accepted", Assert.Single(results).Status);
        completion.SetResult(null);
        await process;
        Assert.Equal("completed", results.Last().Status);
        var restarted = new CommandJournal(fixture.Directory);
        await restarted.LoadAsync(CancellationToken.None);
        var duplicate = new CommandProcessor(restarted, fixture.Clock, fixture.Gate,
            NullLogger<CommandProcessor>.Instance, (_, _, _) => throw new InvalidOperationException("Must not execute twice"));
        await duplicate.ProcessAsync(cmd, TestVehicle.Metadata(), r => { results.Add(r); return Task.CompletedTask; }, _ => Task.CompletedTask, CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.Equal("completed", results.Last().Status);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(fixture.Directory, "commands.json")));
    }

    [Fact]
    public async Task RestartDuringExecutionHasUnknownOutcomeAndCannotReplay()
    {
        using var fixture = new CommandFixture();
        var id = Guid.NewGuid();
        fixture.Journal.Data.Commands[id] = new("example-ev", new string('a', 64), new(id, "lock", "accepted"), true);
        await fixture.Journal.SaveAsync(CancellationToken.None);
        var restarted = new CommandJournal(fixture.Directory);
        await restarted.LoadAsync(CancellationToken.None);
        var result = restarted.Data.Commands[id];
        Assert.False(result.InProgress);
        Assert.Equal("failed", result.Result.Status);
        Assert.Contains("outcome is unknown", result.Result.Message);
    }

    [Fact]
    public async Task RefreshCooldownSurvivesRestartAndIncludesFailedAttempts()
    {
        using var fixture = new CommandFixture();
        var calls = 0;
        var results = new List<CommandResult>();
        var processor = fixture.Processor((_, _, _) => { calls++; throw new IOException("sensitive vendor payload"); });
        Task Publish(CommandResult result) { results.Add(result); return Task.CompletedTask; }
        await processor.ProcessAsync(new("example-ev", Guid.NewGuid(), "refresh"), TestVehicle.Metadata(), Publish, _ => Task.CompletedTask, CancellationToken.None);
        var restarted = new CommandJournal(fixture.Directory);
        await restarted.LoadAsync(CancellationToken.None);
        var next = new CommandProcessor(restarted, fixture.Clock, fixture.Gate, NullLogger<CommandProcessor>.Instance,
            (_, _, _) => { calls++; return Task.FromResult<VehicleState?>(null); });
        await next.ProcessAsync(new("example-ev", Guid.NewGuid(), "refresh"), TestVehicle.Metadata(), Publish, _ => Task.CompletedTask, CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.Contains("cooldown", results.Last().Message);
        Assert.DoesNotContain("sensitive", results.First().Message);
        fixture.Clock.Now += TimeSpan.FromMinutes(10);
        await next.ProcessAsync(new("example-ev", Guid.NewGuid(), "refresh"), TestVehicle.Metadata(), Publish, _ => Task.CompletedTask, CancellationToken.None);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task NonCooperativeTimeoutDoesNotReleaseBackendOrAcceptLateResults()
    {
        using var fixture = new CommandFixture();
        var release = new TaskCompletionSource<VehicleState?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<Task>? lateAccepted = null;
        var processor = fixture.Processor((_, accepted, _) => { lateAccepted = accepted; return release.Task; }, TimeSpan.FromMilliseconds(20));
        var command = new VehicleCommand("example-ev", Guid.NewGuid(), "lock");
        var results = new List<CommandResult>();
        await processor.ProcessAsync(command, TestVehicle.Metadata(), r => { results.Add(r); return Task.CompletedTask; }, _ => Task.CompletedTask, CancellationToken.None);
        Assert.Equal("failed", Assert.Single(results).Status);
        Assert.Contains("unknown", results.Single().Message);
        Assert.Equal(0, fixture.Gate.CurrentCount);
        await lateAccepted!();
        Assert.Single(results);
        release.SetResult(null);
        Assert.True(await fixture.Gate.WaitAsync(TimeSpan.FromSeconds(2)));
        fixture.Gate.Release();
    }

    [Fact]
    public async Task PollOutagePreservesExactLastObservation()
    {
        using var fixture = new CommandFixture();
        using var mqtt = fixture.Bridge();
        var count = 0;
        var statistics = new BridgeStatistics(fixture.Clock);
        var worker = new BridgeWorker(mqtt, _ => ++count == 1 ? Task.FromResult(TestVehicle.Snapshot()) : throw new IOException(),
            fixture.Gate, fixture.Clock, NullLogger<BridgeWorker>.Instance, statistics);
        await worker.PollOnceAsync(CancellationToken.None);
        var original = mqtt.States["example-ev"];
        var success = statistics.Snapshot(false);
        Assert.True(success.ApiReachable);
        fixture.Clock.Now += TimeSpan.FromMinutes(10);
        await worker.PollOnceAsync(CancellationToken.None);
        Assert.Same(original, mqtt.States["example-ev"]);
        Assert.Equal(original.BridgeUpdatedAt, mqtt.States["example-ev"].BridgeUpdatedAt);
        var failure = statistics.Snapshot(false);
        Assert.False(failure.ApiReachable);
        Assert.Equal(success.LastSuccessAt, failure.LastSuccessAt);
        Assert.Equal(success.VehicleCount, failure.VehicleCount);
        Assert.Equal(worker.Schedule.DueAt, failure.NextPollAt);
        Assert.Equal("IOException", failure.LastFailure);
    }

    [Fact]
    public async Task StatusPollingIsTenMinutesAndDoesNotRediscoverVehicles()
    {
        using var fixture = new CommandFixture();
        using var mqtt = fixture.Bridge();
        var discoveryCalls = 0;
        var stateCalls = 0;
        var stats = new BridgeStatistics(fixture.Clock);
        using var worker = new BridgeWorker(mqtt,
            _ => { discoveryCalls++; return Task.FromResult(new BridgeSnapshot([TestVehicle.Metadata() with { Capabilities = VehicleCapabilities.None }], [])); },
            fixture.Gate, fixture.Clock, NullLogger<BridgeWorker>.Instance, stats,
            (_, _) => { stateCalls++; return Task.FromResult(TestVehicle.State() with { BridgeUpdatedAt = fixture.Clock.Now }); });
        await worker.PollOnceAsync(CancellationToken.None);
        await worker.PollStatesOnceAsync(CancellationToken.None);
        Assert.Equal(1, stateCalls);
        Assert.Equal(fixture.Clock.Now.AddMinutes(10), stats.Snapshot(false).NextStatePollAt);
        Assert.Equal(73, mqtt.States["example-ev"].BatteryPercent);
        fixture.Clock.Now += TimeSpan.FromMinutes(9);
        await worker.PollStatesOnceAsync(CancellationToken.None);
        Assert.Equal(1, stateCalls);
        fixture.Clock.Now += TimeSpan.FromMinutes(1);
        await worker.PollStatesOnceAsync(CancellationToken.None);
        Assert.Equal(2, stateCalls);
        Assert.Equal(1, discoveryCalls);
        Assert.Null(stats.Snapshot(false).NextPollAt);
    }

    [Fact]
    public async Task OneVehicleFailureKeepsCacheAndDoesNotBlockOtherVehicles()
    {
        using var fixture = new CommandFixture();
        using var mqtt = fixture.Bridge();
        var first = TestVehicle.Metadata() with { Capabilities = VehicleCapabilities.None };
        var second = first with { VehicleId = "second", Vin = "SECOND-VIN" };
        var fail = false;
        var calls = new Dictionary<string, int>();
        var stats = new BridgeStatistics(fixture.Clock);
        using var worker = new BridgeWorker(mqtt,
            _ => Task.FromResult(new BridgeSnapshot([first, second], [])),
            fixture.Gate, fixture.Clock, NullLogger<BridgeWorker>.Instance, stats,
            (info, _) =>
            {
                calls[info.VehicleId] = calls.GetValueOrDefault(info.VehicleId) + 1;
                if (fail && info.VehicleId == first.VehicleId) throw new HyundaiException("unavailable", 503);
                return Task.FromResult(TestVehicle.State() with { VehicleId = info.VehicleId, Vin = info.Vin, BridgeUpdatedAt = fixture.Clock.Now });
            });
        await worker.PollOnceAsync(CancellationToken.None);
        await worker.PollStatesOnceAsync(CancellationToken.None);
        var original = mqtt.States[first.VehicleId];
        fail = true;
        fixture.Clock.Now += TimeSpan.FromMinutes(10);
        await worker.PollStatesOnceAsync(CancellationToken.None);
        Assert.Same(original, mqtt.States[first.VehicleId]);
        Assert.Equal(fixture.Clock.Now, mqtt.States[second.VehicleId].BridgeUpdatedAt);
        fixture.Clock.Now += TimeSpan.FromMinutes(10);
        await worker.PollStatesOnceAsync(CancellationToken.None);
        fixture.Clock.Now += TimeSpan.FromMinutes(10);
        await worker.PollStatesOnceAsync(CancellationToken.None);
        Assert.Equal(3, calls[first.VehicleId]);
        Assert.Equal(4, calls[second.VehicleId]);
    }

    [Fact]
    public async Task StatusRateLimitDefersTheWholeAccount()
    {
        using var fixture = new CommandFixture();
        using var mqtt = fixture.Bridge();
        var first = TestVehicle.Metadata() with { Capabilities = VehicleCapabilities.None };
        var stats = new BridgeStatistics(fixture.Clock);
        var calls = 0;
        using var worker = new BridgeWorker(mqtt,
            _ => Task.FromResult(new BridgeSnapshot([first, first with { VehicleId = "second", Vin = "SECOND-VIN" }], [])),
            fixture.Gate, fixture.Clock, NullLogger<BridgeWorker>.Instance, stats,
            (_, _) => { calls++; throw new HyundaiException("limited", 429, TimeSpan.FromHours(2)); });
        await worker.PollOnceAsync(CancellationToken.None);
        await worker.PollStatesOnceAsync(CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.Equal(fixture.Clock.Now.AddHours(2), stats.Snapshot(false).NextStatePollAt);
        fixture.Clock.Now += TimeSpan.FromMinutes(60);
        await worker.PollStatesOnceAsync(CancellationToken.None);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CorruptJournalFailsClosedAndCancelledSaveKeepsCommittedFile()
    {
        using var fixture = new CommandFixture();
        await fixture.Journal.SaveAsync(CancellationToken.None);
        using var stopped = new CancellationTokenSource(); stopped.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Journal.SaveAsync(stopped.Token));
        await new CommandJournal(fixture.Directory).LoadAsync(CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(fixture.Directory, "commands.json"), "{}");
        await Assert.ThrowsAsync<IOException>(() => new CommandJournal(fixture.Directory).LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReusedCommandIdAndExpiredQueuedCommandsCannotExecute()
    {
        using var fixture = new CommandFixture();
        var calls = 0;
        var processor = fixture.Processor((_, _, _) => { calls++; return Task.FromResult<VehicleState?>(null); });
        var results = new List<CommandResult>();
        Task Publish(CommandResult r) { results.Add(r); return Task.CompletedTask; }
        var command = new VehicleCommand("example-ev", Guid.NewGuid(), "lock");
        await processor.ProcessAsync(command, TestVehicle.Metadata(), Publish, _ => Task.CompletedTask, CancellationToken.None);
        await processor.ProcessAsync(command with { Command = "unlock" }, TestVehicle.Metadata(), Publish, _ => Task.CompletedTask, CancellationToken.None);
        Assert.Equal("failed", results.Last().Status);
        Assert.Contains("already used", results.Last().Message);
        await processor.ProcessAsync(command with { CommandId = Guid.NewGuid() }, TestVehicle.Metadata(), Publish,
            _ => Task.CompletedTask, CancellationToken.None, TimeSpan.Zero);
        Assert.Contains("expired", results.Last().Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task InvalidStateAndReassignedIdentityCannotReplaceCachedObservation()
    {
        using var fixture = new CommandFixture();
        using var mqtt = fixture.Bridge();
        await mqtt.UpdateSnapshotAsync(TestVehicle.Snapshot(), CancellationToken.None);
        var original = mqtt.States["example-ev"];
        await Assert.ThrowsAsync<FormatException>(() => mqtt.UpdateSnapshotAsync(
            new([TestVehicle.Metadata()], [original with { BatteryPercent = 101 }]), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => mqtt.UpdateSnapshotAsync(
            new([TestVehicle.Metadata()], [original with { OdometerKm = double.NaN }]), CancellationToken.None));
        await Assert.ThrowsAsync<FormatException>(() => mqtt.UpdateSnapshotAsync(
            new([TestVehicle.Metadata() with { Vin = "OTHER" }], []), CancellationToken.None));
        Assert.Same(original, mqtt.States["example-ev"]);
    }

    [Fact]
    public async Task CommandObservationIsPublishedBeforeReleasingBackendForNextPoll()
    {
        using var fixture = new CommandFixture();
        var processor = fixture.Processor((_, _, _) => Task.FromResult<VehicleState?>(TestVehicle.State()));
        var observed = false;
        await processor.ProcessAsync(new("example-ev", Guid.NewGuid(), "lock"), TestVehicle.Metadata(),
            result =>
            {
                if (result.Status == "completed") Assert.True(observed);
                return Task.CompletedTask;
            }, state =>
            {
                Assert.Equal(0, fixture.Gate.CurrentCount);
                Assert.Equal(73, state.BatteryPercent);
                observed = true;
                return Task.CompletedTask;
            }, CancellationToken.None);
        Assert.Equal(1, fixture.Gate.CurrentCount);
    }
}

internal static class TestVehicle
{
    internal static VehicleMetadata Metadata()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Contract/manifest.json")));
        return document.RootElement.GetProperty("vehicles")[0].Deserialize<VehicleMetadata>(Contract.Json)!;
    }
    internal static VehicleState State() => JsonSerializer.Deserialize<VehicleState>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Contract/state.json")), Contract.Json)!;
    internal static BridgeSnapshot Snapshot() => new([Metadata()], [State()]);
}

internal sealed class CommandFixture : IDisposable
{
    internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "hyundai-commands-" + Guid.NewGuid().ToString("N"));
    internal FixedClock Clock { get; } = new();
    internal SemaphoreSlim Gate { get; } = new(1, 1);
    internal CommandJournal Journal { get; }
    internal CommandFixture() { Journal = new(Directory); Journal.LoadAsync(CancellationToken.None).GetAwaiter().GetResult(); }
    internal CommandProcessor Processor(VehicleCommandHandler handler, TimeSpan? timeout = null) =>
        new(Journal, Clock, Gate, NullLogger<CommandProcessor>.Instance, handler) { Timeout = timeout ?? TimeSpan.FromSeconds(2) };
    internal MqttBridge Bridge(int port = 1883, VehicleCommandHandler? handler = null) => new(
        new("home", "127.0.0.1", port, null, null, false, Directory),
        new(Journal, TimeProvider.System, Gate, NullLogger<CommandProcessor>.Instance, handler),
        NullLogger<MqttBridge>.Instance, TimeProvider.System);
    public void Dispose() { Gate.Dispose(); System.IO.Directory.Delete(Directory, true); }
}
