using System.Net;
using System.Text.Json;
using HyundaiBridge.Hosting;
using HyundaiBridge.Hyundai;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace HyundaiBridge.Tests;

public sealed class StatusPageTests
{
    [Fact]
    public void StoredStatusResponseKeepsDataButMasksIdentifiersTokensAndLocation()
    {
        using var document = JsonDocument.Parse("""
            {"metaInfo":{"retCode":"S","msgId":"private-id"},"data":{"vin":"private-vin",
            "nested":[{"accessToken":"private-token","password":"private-password"}],"WasherFluid":{"Level":1},
            "state":{"Vehicle":{"Green":{"BatteryManagement":{"BatteryRemain":{"Ratio":80.5}}},
            "Location":{"GeoCoord":{"Latitude":59.123456,"Longitude":18.123456}}}}}}
            """);
        var response = StatusResponseRedactor.Redact(document.RootElement);
        var stats = new BridgeStatistics(new FixedClock());
        stats.VehicleResponseReceived("private-vehicle-id", "IONIQ 9", response);
        var snapshot = stats.Snapshot(true);
        var json = JsonSerializer.Serialize(snapshot);
        foreach (var secret in new[] { "private-id", "private-vin", "private-token", "private-password", "private-vehicle-id", "59.123456", "18.123456" })
            Assert.DoesNotContain(secret, json);
        Assert.Equal(80.5, response.GetProperty("data").GetProperty("state").GetProperty("Vehicle")
            .GetProperty("Green").GetProperty("BatteryManagement").GetProperty("BatteryRemain").GetProperty("Ratio").GetDouble());
        Assert.Equal("private-vin", document.RootElement.GetProperty("data").GetProperty("vin").GetString());
        Assert.Equal(1, response.GetProperty("data").GetProperty("WasherFluid").GetProperty("Level").GetInt32());
        stats.VehicleResponseReceived("private-vehicle-id", "IONIQ 9", JsonSerializer.SerializeToElement(new { updated = true }));
        Assert.Single(stats.Snapshot(false).VehicleResponses!);
        Assert.Equal(80.5, snapshot.VehicleResponses![0].Response.GetProperty("data").GetProperty("state").GetProperty("Vehicle")
            .GetProperty("Green").GetProperty("BatteryManagement").GetProperty("BatteryRemain").GetProperty("Ratio").GetDouble());
        Assert.Equal(0, stats.Snapshot(false).RequestCount);
    }

    [Fact]
    public void StatisticsKeepBoundedDetachedHistoryAndDistinguishCancellationFromFailures()
    {
        var clock = new FixedClock();
        var stats = new BridgeStatistics(clock);
        Assert.Null(stats.Snapshot(false).ApiReachable);
        for (var i = 0; i < 25; i++)
        {
            clock.Now += TimeSpan.FromSeconds(1);
            stats.RequestCompleted("discovery", 200, TimeSpan.FromMilliseconds(100), null);
        }
        var snapshot = stats.Snapshot(true);
        Assert.Equal(25, snapshot.RequestCount);
        Assert.Equal(20, snapshot.RecentRequests.Count);
        Assert.Equal(clock.Now, snapshot.RecentRequests[0].At);
        stats.RequestCompleted("discovery", 429, TimeSpan.FromMilliseconds(100), null);
        stats.RequestCompleted("refresh", null, TimeSpan.FromMilliseconds(100), "timeout");
        stats.RequestCompleted("refresh", null, TimeSpan.FromMilliseconds(100), "cancelled");
        var after = stats.Snapshot(false);
        Assert.Equal(2, after.RequestFailures);
        Assert.Equal(1, after.RateLimitedResponses);
        Assert.Equal(100, after.AverageDurationMs);
        Assert.Equal(25, snapshot.RequestCount);
        Assert.All(snapshot.RecentRequests, request => Assert.Equal(200, request.StatusCode));
    }

    [Fact]
    public async Task RateLimitAndNetworkFailureProduceSafeDiagnosticMetadata()
    {
        using var fixture = new Fixture();
        await fixture.Store.SaveAsync(fixture.Session(), CancellationToken.None);
        fixture.Http.Enqueue(_ => AuthenticationTests.Response("secret response body", HttpStatusCode.TooManyRequests));
        fixture.Http.Enqueue(_ => Task.FromException<HttpResponseMessage>(
            new HttpRequestException("https://upstream.invalid/?token=secret-token")));
        using var client = fixture.Client();
        await Assert.ThrowsAsync<HyundaiBridge.Hyundai.HyundaiException>(() => client.GetVehiclesAsync(CancellationToken.None));
        await Assert.ThrowsAsync<HyundaiBridge.Hyundai.HyundaiException>(() => client.GetVehiclesAsync(CancellationToken.None));
        Assert.Equal(1, fixture.Http.RequestCount);
        fixture.Clock.Now += TimeSpan.FromMinutes(10);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetVehiclesAsync(CancellationToken.None));
        var stats = client.Statistics.Snapshot(false);
        Assert.Equal(2, stats.RequestCount);
        Assert.Equal(2, stats.RequestFailures);
        Assert.Equal(1, stats.RateLimitedResponses);
        Assert.Equal("network", stats.RecentRequests[0].Failure);
        Assert.Equal(429, stats.RecentRequests[1].StatusCode);
        var json = JsonSerializer.Serialize(stats);
        foreach (var value in new[] { "secret", "test@example.invalid", "test-password", "device-uuid", "cci-access", "refresh-secret" })
            Assert.DoesNotContain(value, json);
    }

    [Fact]
    public async Task StatusHttpRoutesServeAssetsAndSnapshotsWithoutTriggeringOperations()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        var stats = new BridgeStatistics(new FixedClock());
        stats.PollSucceeded(0);
        stats.SessionUpdated("stored", DateTimeOffset.UtcNow.AddHours(1));
        StatusPage.Map(app, stats, () => true);
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        try
        {
            using var status = await http.GetAsync("/api/status");
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);
            Assert.True(status.Headers.CacheControl!.NoStore);
            Assert.Contains("default-src 'none'", status.Headers.GetValues("Content-Security-Policy").Single());
            using var data = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
            Assert.True(data.RootElement.GetProperty("mqttConnected").GetBoolean());
            Assert.True(data.RootElement.GetProperty("apiReachable").GetBoolean());
            Assert.Equal(0, data.RootElement.GetProperty("vehicleCount").GetInt32());
            Assert.Equal(0, data.RootElement.GetProperty("requestCount").GetInt64());
            Assert.Equal(0, stats.Snapshot(false).RequestCount);
            Assert.Contains("Koll på anslutningen", await http.GetStringAsync("/"));
            Assert.Contains("Senaste bilstatus · JSON", await http.GetStringAsync("/"));
            Assert.Contains("connection-grid", await http.GetStringAsync("/app.css"));
            Assert.Contains("fetch('/api/status'", await http.GetStringAsync("/app.js"));
            using var command = await http.PostAsync("/api/status", new StringContent("{}"));
            Assert.Equal(HttpStatusCode.MethodNotAllowed, command.StatusCode);
            using var unknown = await http.GetAsync("/session.json");
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }
        finally { await app.StopAsync(); }
    }
}
