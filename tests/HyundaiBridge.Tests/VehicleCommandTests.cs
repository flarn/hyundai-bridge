using System.Net;
using System.Text.Json;
using HyundaiBridge.Domain;
using HyundaiBridge.Hyundai;
using Xunit;
using static HyundaiBridge.Tests.AuthenticationTests;

namespace HyundaiBridge.Tests;

public sealed class VehicleCommandTests
{
    private const string Discovery = """
        {"contents":[{"ccspCarId":"vehicle-a","vin":"SANITIZED-VIN","vehicleModelName":"Example EV","isEv":true,"ccs2ProtocolSupport":2}]}
        """;
    private static string CcsToken => "header." + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"uid\":\"user-secret\"}"))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature";
    private const string PinResponse = """{"isMatched":true,"controlTokenInfo":{"controlToken":"control-secret","expiresTime":600}}""";
    private const string Accepted = """{"metaInfo":{"retCode":"S"},"data":{"SID":"operation-a"}}""";
    private static string State(string date = "20261006120000.000") => JsonSerializer.Serialize(new
    {
        metaInfo = new { retCode = "S" },
        data = new { state = new { Vehicle = new { Date = date, Offset = 0,
            Green = new { BatteryManagement = new { BatteryRemain = new { Ratio = 73 } } } } } }
    });
    private static string Result(string status, string sid = "operation-a") => JsonSerializer.Serialize(new
    { metaInfo = new { retCode = "S" }, data = new { pollingState = status, SID = sid } });

    private static async Task<HyundaiClient> Ready(Fixture f, string? pin = "1234")
    {
        using var tokens = JsonDocument.Parse(Tokens);
        await f.Store.SaveAsync(HyundaiSession.Parse(tokens.RootElement, Fixture.AccountHash,
            "01234567-89ab-cdef-0123-456789abcdef", f.Clock.Now), CancellationToken.None);
        f.Http.Enqueue(_ => Response(Discovery));
        var client = f.Client(pin);
        await client.GetBridgeSnapshotAsync(CancellationToken.None);
        return client;
    }
    private static void Exchange(Fixture f) => f.Http.Enqueue(_ => Response(JsonSerializer.Serialize(new { accessToken = CcsToken, expiresTime = 86400 })));

    [Theory]
    [InlineData(null, true)]
    [InlineData("1234", true)]
    [InlineData("1234", false)]
    public async Task DiscoveryAdvertisesOnlyRequestedAndApplicableCommands(string? pin, bool electric)
    {
        using var f = new Fixture();
        using var tokens = JsonDocument.Parse(Tokens);
        await f.Store.SaveAsync(HyundaiSession.Parse(tokens.RootElement, Fixture.AccountHash,
            "01234567-89ab-cdef-0123-456789abcdef", f.Clock.Now), CancellationToken.None);
        f.Http.Enqueue(_ => Response(Discovery.Replace("\"isEv\":true", "\"isEv\":" + (electric ? "true" : "false"))));
        using var client = f.Client(pin);
        var vehicle = Assert.Single((await client.GetBridgeSnapshotAsync(CancellationToken.None)).Vehicles);
        Assert.Contains("refresh", vehicle.Capabilities.Commands);
        Assert.DoesNotContain("lock", vehicle.Capabilities.Commands);
        Assert.DoesNotContain("unlock", vehicle.Capabilities.Commands);
        Assert.DoesNotContain("charge-limit", vehicle.Capabilities.Commands);
        Assert.Equal(pin is not null, vehicle.Capabilities.Commands.Contains("climate/start"));
        Assert.Equal(pin is not null && electric, vehicle.Capabilities.Commands.Contains("charging/start"));
        if (pin is not null)
        {
            Assert.Contains("targetTemperatureCelsius", vehicle.Capabilities.StateFields);
            Assert.True(vehicle.Capabilities.Climate!.SupportsDefrost);
        }
        Assert.Equal(1, f.Http.RequestCount); // Advertising does not authenticate PIN or send any control.
    }

    [Theory]
    [InlineData("climate/start", "temperature", "start", true)]
    [InlineData("climate/stop", "temperature", "stop", false)]
    [InlineData("charging/start", "charge", "start", false)]
    [InlineData("charging/stop", "charge", "stop", false)]
    public async Task RequestedControlsUsePinHeadersAndWaitForResult(string action, string endpoint, string operation, bool defrost)
    {
        using var f = new Fixture(); using var client = await Ready(f);
        Exchange(f);
        f.Http.Enqueue(async request =>
        {
            Assert.Equal("/domain/api/v1/auth/pin", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("1234", body.RootElement.GetProperty("pin").GetString());
            return Response(PinResponse);
        });
        var accepted = false;
        f.Http.Enqueue(async request =>
        {
            Assert.False(accepted);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.EndsWith("/" + endpoint, request.RequestUri!.AbsolutePath);
            Assert.Equal("control-secret", request.Headers.Authorization!.Parameter);
            Assert.Equal("Bearer control-secret", request.Headers.GetValues("AuthorizationCCSP").Single());
            Assert.Equal("2", request.Headers.GetValues("Ccuccs2protocolsupport").Single());
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(operation, body.RootElement.GetProperty("command").GetString());
            if (action == "climate/start")
            {
                Assert.Equal("21.5", body.RootElement.GetProperty("hvacTemp").GetString());
                Assert.Equal("C", body.RootElement.GetProperty("tempUnit").GetString());
                Assert.Equal(1, body.RootElement.GetProperty("hvacTempType").GetInt32());
                Assert.True(body.RootElement.GetProperty("windshieldFrontDefogState").GetBoolean());
            }
            return Response(Accepted, HttpStatusCode.Accepted);
        });
        f.Http.Enqueue(request =>
        {
            Assert.True(accepted);
            Assert.Equal("?path=gspa/v1/remote/vehicles", request.RequestUri!.Query);
            Assert.Equal(CcsToken, request.Headers.Authorization!.Parameter);
            Assert.False(request.Headers.Contains("AuthorizationCCSP"));
            return Response(Result("SUCCESS"), HttpStatusCode.Accepted);
        });
        f.Http.Enqueue(_ => Response(State()));
        var state = await client.ExecuteCommandAsync(new("vehicle-a", Guid.NewGuid(), action, 21.5, defrost),
            () => { accepted = true; return Task.CompletedTask; }, CancellationToken.None);
        Assert.True(accepted); Assert.Equal(73, state!.BatteryPercent); Assert.Equal(0, f.Http.Remaining);
        foreach (var secret in new[] { "1234", "control-secret", "user-secret" })
            Assert.DoesNotContain(secret, string.Join('\n', f.Log.Messages));
    }

    [Fact]
    public async Task RejectedPinIsNeverAutomaticallyRetried()
    {
        using var f = new Fixture(); using var client = await Ready(f);
        Exchange(f); f.Http.Enqueue(_ => Response("{\"isMatched\":false,\"controlTokenInfo\":null}"));
        for (var i = 0; i < 2; i++)
            await Assert.ThrowsAsync<HyundaiException>(() => client.ExecuteCommandAsync(new("vehicle-a", Guid.NewGuid(), "charging/start"),
                () => throw new Exception("Must not accept"), CancellationToken.None));
        Assert.Equal(3, f.Http.RequestCount); Assert.Equal(0, f.Http.Remaining);
    }

    [Theory]
    [InlineData("{\"metaInfo\":{\"retCode\":\"F\"},\"data\":{}}", false)]
    [InlineData("{\"metaInfo\":{\"retCode\":\"S\"},\"data\":{}}", true)]
    [InlineData("{}", false)]
    public async Task RejectionOrMissingHandleCannotBecomeCompletion(string response, bool expectAccepted)
    {
        using var f = new Fixture(); using var client = await Ready(f);
        Exchange(f); f.Http.Enqueue(_ => Response(PinResponse)); f.Http.Enqueue(_ => Response(response));
        var accepted = false;
        await Assert.ThrowsAsync<HyundaiException>(() => client.ExecuteCommandAsync(new("vehicle-a", Guid.NewGuid(), "charging/stop"),
            () => { accepted = true; return Task.CompletedTask; }, CancellationToken.None));
        Assert.Equal(expectAccepted, accepted); Assert.Equal(0, f.Http.Remaining);
    }

    [Theory]
    [InlineData("FAILURE")]
    [InlineData("TIMEOUT")]
    [InlineData("NEW-UNKNOWN-STATE")]
    public async Task TerminalFailureAndUnknownStatusDoNotCompleteOrResend(string status)
    {
        using var f = new Fixture(); using var client = await Ready(f);
        Exchange(f); f.Http.Enqueue(_ => Response(PinResponse)); f.Http.Enqueue(_ => Response(Accepted));
        f.Http.Enqueue(_ => Response(Result(status)));
        await Assert.ThrowsAsync<HyundaiException>(() => client.ExecuteCommandAsync(new("vehicle-a", Guid.NewGuid(), "charging/stop"),
            () => Task.CompletedTask, CancellationToken.None));
        Assert.Equal(5, f.Http.RequestCount); Assert.Equal(0, f.Http.Remaining);
    }

    [Fact]
    public async Task RefreshUsesBearerAndRequiresAdvancedVehicleTimestamp()
    {
        using var f = new Fixture(); using var client = await Ready(f, null);
        Exchange(f); f.Http.Enqueue(_ => Response(State()));
        f.Http.Enqueue(async request =>
        {
            Assert.EndsWith("/prewakeup", request.RequestUri!.AbsolutePath);
            Assert.Equal(CcsToken, request.Headers.Authorization!.Parameter);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("prewakeup", body.RootElement.GetProperty("action").GetString());
            return Response(Accepted, HttpStatusCode.Accepted);
        });
        f.Http.Enqueue(_ => Response(State()));
        f.Http.Enqueue(_ => Response(State("20261006120100.000")));
        var state = await client.ExecuteCommandAsync(new("vehicle-a", Guid.NewGuid(), "refresh"),
            () => Task.CompletedTask, CancellationToken.None);
        Assert.Equal(DateTimeOffset.Parse("2026-10-06T12:01:00Z"), state!.VehicleUpdatedAt);
        Assert.Equal(6, f.Http.RequestCount); Assert.Equal(0, f.Http.Remaining);
    }

    [Fact]
    public async Task PinTokenIsReusedBetweenCommands()
    {
        using var f = new Fixture(); using var client = await Ready(f);
        Exchange(f); f.Http.Enqueue(_ => Response(PinResponse));
        for (var i = 0; i < 2; i++)
        {
            f.Http.Enqueue(_ => Response(Accepted));
            f.Http.Enqueue(_ => Response(Result("SUCCESS")));
            f.Http.Enqueue(_ => Response(State()));
            await client.ExecuteCommandAsync(new("vehicle-a", Guid.NewGuid(), "charging/stop"),
                () => Task.CompletedTask, CancellationToken.None);
        }
        Assert.Equal(9, f.Http.RequestCount); Assert.Equal(0, f.Http.Remaining);
    }

    [Fact]
    public async Task RateLimitBlocksFurtherRequestsForAccountWithoutResendingControl()
    {
        using var f = new Fixture(); using var client = await Ready(f);
        Exchange(f); f.Http.Enqueue(_ => Response(PinResponse));
        f.Http.Enqueue(_ => { var response = Response("", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(TimeSpan.FromHours(1)); return response; });
        for (var i = 0; i < 2; i++)
            await Assert.ThrowsAsync<HyundaiException>(() => client.ExecuteCommandAsync(new("vehicle-a", Guid.NewGuid(), "charging/start"),
                () => throw new Exception("Must not accept"), CancellationToken.None));
        Assert.Equal(4, f.Http.RequestCount); Assert.Equal(0, f.Http.Remaining);
    }

    [Fact]
    public async Task CanceledAcceptedCommandDoesNotResend()
    {
        using var f = new Fixture(); using var client = await Ready(f);
        Exchange(f); f.Http.Enqueue(_ => Response(PinResponse)); f.Http.Enqueue(_ => Response(Accepted));
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ExecuteCommandAsync(new("vehicle-a", Guid.NewGuid(), "charging/start"),
            () => { cancel.Cancel(); return Task.CompletedTask; }, cancel.Token));
        Assert.Equal(4, f.Http.RequestCount); Assert.Equal(0, f.Http.Remaining);
    }

    [Fact]
    public async Task UnsupportedAndUnrequestedControlsNeverReachHyundai()
    {
        using var f = new Fixture(); using var client = await Ready(f, null);
        foreach (var action in new[] { "lock", "unlock", "charge-limit", "climate/start", "charging/start" })
            await Assert.ThrowsAsync<HyundaiException>(() => client.ExecuteCommandAsync(new("vehicle-a", Guid.NewGuid(), action, 21),
                () => Task.CompletedTask, CancellationToken.None));
        Assert.Equal(1, f.Http.RequestCount);
    }
}
