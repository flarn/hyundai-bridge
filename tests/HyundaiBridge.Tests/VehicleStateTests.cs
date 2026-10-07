using System.Text.Json;
using System.Net;
using System.Text;
using HyundaiBridge.Domain;
using HyundaiBridge.Hyundai;
using Xunit;

namespace HyundaiBridge.Tests;

public sealed class VehicleStateTests
{
    internal static VehicleMetadata Vehicle => new("vehicle-a", "SANITIZED-VIN", "My Hyundai", "Hyundai", VehicleCapabilities.None);
    internal const string Payload = """
        {"lastUpdateTime":"1791376200000","state":{"Vehicle":{
          "Date":"20261007180000.000","Offset":2,
          "Drivetrain":{"Odometer":12345,"FuelSystem":{"DTE":{"Total":300,"Unit":1}}},
          "Green":{"BatteryManagement":{"BatteryRemain":{"Ratio":73}},
            "ChargingInformation":{"ConnectorFastening":{"State":1},"Charging":{"RemainTime":25},"TargetSoC":{"Standard":80,"Quick":90}}},
          "Cabin":{"HVAC":{"Row1":{"Driver":{"Temperature":{"Value":21,"Unit":0}}},"OutsideTemperature":{"Value":46.4,"Unit":1}},
            "Door":{"Row1":{"Driver":{"Lock":0},"Passenger":{"Lock":0}},"Row2":{"Left":{"Lock":0},"Right":{"Lock":0}}}},
          "Location":{"GeoCoord":{"Latitude":0,"Longitude":0}}
        }}}
        """;

    [Fact]
    public void Ccs2ValuesMapWithUnitsAndSeparateObservationTimes()
    {
        using var document = JsonDocument.Parse(Payload);
        var now = DateTimeOffset.Parse("2026-10-07T19:00:00Z");
        var state = VehicleStateParser.Parse(document.RootElement, Vehicle, now);
        Assert.Equal(73, state.BatteryPercent);
        Assert.Equal(300, state.EstimatedRangeKm);
        Assert.Equal(12345, state.OdometerKm);
        Assert.True(state.IsCharging); Assert.True(state.IsPluggedIn); Assert.True(state.IsLocked);
        Assert.Equal(80, state.AcChargeLimitPercent); Assert.Equal(90, state.DcChargeLimitPercent);
        Assert.Equal(21, state.TargetTemperatureCelsius);
        Assert.Equal(8, state.OutsideTemperatureCelsius!.Value, 6);
        Assert.Null(state.CabinTemperatureCelsius); Assert.Null(state.IsClimateOn);
        Assert.Equal(0, state.Latitude); Assert.Equal(0, state.Longitude);
        Assert.Equal(DateTimeOffset.Parse("2026-10-07T16:00:00Z"), state.VehicleUpdatedAt);
        Assert.Equal(now, state.BridgeUpdatedAt);
    }

    [Fact]
    public void MissingAndSentinelValuesDoNotInventState()
    {
        using var document = JsonDocument.Parse("""
            {"state":{"Vehicle":{"Green":{"BatteryManagement":{"BatteryRemain":{"Ratio":255}}},
             "Cabin":{"Door":{"Row1":{"Driver":{"Lock":0}}}},"Location":{"GeoCoord":{"Latitude":91,"Longitude":18}}}}}
            """);
        var state = VehicleStateParser.Parse(document.RootElement, Vehicle, DateTimeOffset.UtcNow);
        Assert.Null(state.BatteryPercent); Assert.Null(state.IsLocked); Assert.Null(state.IsCharging);
        Assert.Null(state.VehicleUpdatedAt); Assert.Null(state.Latitude); Assert.Null(state.Longitude);
        Assert.Null(state.AuxiliaryBatteryPercent); Assert.Null(state.IsFrontLeftDoorOpen);
        Assert.Null(state.IsTrunkOpen); Assert.Null(state.IsHoodOpen);
        Assert.Null(state.IsChargePortOpen); Assert.Null(state.IsSunroofOpen);
        Assert.Null(state.ChargingPowerKw); Assert.Null(state.RemainingChargeTimeMinutes);
    }

    [Fact]
    public void AuxiliaryBatteryAndEachOpeningMapIndependentlyIntoTheContract()
    {
        using var document = JsonDocument.Parse("""
            {"state":{"Vehicle":{"Electronics":{"Battery":{"Level":83}},
             "Cabin":{"Door":{"Row1":{"Driver":{"Open":1},"Passenger":{"Open":0}},
              "Row2":{"Left":{"Open":0},"Right":{"Open":1}}}},
             "Body":{"Trunk":{"Open":1},"Hood":{"Open":0}}}}}
            """);
        var state = VehicleStateParser.Parse(document.RootElement, Vehicle, DateTimeOffset.UtcNow);
        Assert.Equal(83, state.AuxiliaryBatteryPercent);
        Assert.True(state.IsFrontLeftDoorOpen); Assert.False(state.IsFrontRightDoorOpen);
        Assert.False(state.IsRearLeftDoorOpen); Assert.True(state.IsRearRightDoorOpen);
        Assert.True(state.IsTrunkOpen); Assert.False(state.IsHoodOpen);
        using var normalized = JsonDocument.Parse(HyundaiBridge.Mqtt.Contract.Serialize(state));
        Assert.Equal(83, normalized.RootElement.GetProperty("auxiliaryBatteryPercent").GetInt32());
        Assert.True(normalized.RootElement.GetProperty("isFrontLeftDoorOpen").GetBoolean());
        Assert.False(normalized.RootElement.GetProperty("isHoodOpen").GetBoolean());
    }

    [Fact]
    public void UnsupportedAuxiliaryBatteryAndOpeningValuesStayUnknown()
    {
        using var document = JsonDocument.Parse("""
            {"state":{"Vehicle":{"Electronics":{"Battery":{"Level":255}},
             "Cabin":{"Door":{"Row1":{"Driver":{"Open":2},"Passenger":{"Open":1}}}},
             "Body":{"Trunk":{"Open":null},"Hood":{"Open":"unknown"}}}}}
            """);
        var state = VehicleStateParser.Parse(document.RootElement, Vehicle, DateTimeOffset.UtcNow);
        Assert.Null(state.AuxiliaryBatteryPercent); Assert.Null(state.IsFrontLeftDoorOpen);
        Assert.True(state.IsFrontRightDoorOpen); Assert.Null(state.IsRearLeftDoorOpen);
        Assert.Null(state.IsTrunkOpen); Assert.Null(state.IsHoodOpen);
        Assert.Throws<FormatException>(() => HyundaiBridge.Mqtt.Contract.ValidateState(state with { AuxiliaryBatteryPercent = 101 }));
    }

    [Theory]
    [InlineData(80.5, 80)]
    [InlineData(79.9, 79)]
    [InlineData(100, 100)]
    public void FractionalSocUsesWholePercentageWithoutRoundingUp(double ratio, int expected)
    {
        using var document = JsonDocument.Parse(Payload.Replace("\"Ratio\":73", "\"Ratio\":" + ratio.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(expected, VehicleStateParser.Parse(document.RootElement, Vehicle, DateTimeOffset.UtcNow).BatteryPercent);
    }

    [Theory]
    [InlineData(1, 300)]
    [InlineData(2, 482.8032)]
    [InlineData(3, 482.8032)]
    public void RangeUsesDeclaredUnit(int unit, double expected)
    {
        using var document = JsonDocument.Parse(Payload.Replace("\"Total\":300,\"Unit\":1", "\"Total\":300,\"Unit\":" + unit));
        Assert.Equal(expected, VehicleStateParser.Parse(document.RootElement, Vehicle, DateTimeOffset.UtcNow).EstimatedRangeKm!.Value, 6);
    }

    [Fact]
    public void UnknownRangeUnitDoesNotInventKilometers()
    {
        using var document = JsonDocument.Parse(Payload.Replace("\"Total\":300,\"Unit\":1", "\"Total\":300,\"Unit\":255"));
        Assert.Null(VehicleStateParser.Parse(document.RootElement, Vehicle, DateTimeOffset.UtcNow).EstimatedRangeKm);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(255, null)]
    public void ChargePortUsesVerifiedStates(int code, bool? expected)
    {
        using var document = JsonDocument.Parse("{\"state\":{\"Vehicle\":{\"Green\":{\"ChargingDoor\":{\"State\":" + code + "}}}}}");
        Assert.Equal(expected, VehicleStateParser.Parse(document.RootElement, Vehicle, DateTimeOffset.UtcNow).IsChargePortOpen);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, null)]
    public void SunroofUsesOnlyVerifiedOpenCodes(int code, bool? expected)
    {
        using var document = JsonDocument.Parse("{\"state\":{\"Vehicle\":{\"Body\":{\"Sunroof\":{\"Glass\":{\"Open\":" + code + "}}}}}}");
        Assert.Equal(expected, VehicleStateParser.Parse(document.RootElement, Vehicle, DateTimeOffset.UtcNow).IsSunroofOpen);
    }

    [Theory]
    [InlineData(58.7, 25)]
    [InlineData(0, 0)]
    [InlineData(-1, -1)]
    public void ChargingMeasurementsKeepUnitsAndRejectSentinels(double power, double minutes)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new { state = new { Vehicle = new {
            Green = new { Electric = new { SmartGrid = new { RealTimePower = power } },
                ChargingInformation = new { Charging = new { RemainTime = minutes } } }
        } } });
        using var document = JsonDocument.Parse(json);
        var state = VehicleStateParser.Parse(document.RootElement, Vehicle, DateTimeOffset.UtcNow);
        Assert.Equal(power >= 0 ? power : (double?)null, state.ChargingPowerKw);
        Assert.Equal(minutes >= 0 ? minutes : (double?)null, state.RemainingChargeTimeMinutes);
        using var normalized = JsonDocument.Parse(HyundaiBridge.Mqtt.Contract.Serialize(state));
        if (power >= 0) Assert.Equal(power, normalized.RootElement.GetProperty("chargingPowerKw").GetDouble());
        else Assert.Equal(JsonValueKind.Null, normalized.RootElement.GetProperty("chargingPowerKw").ValueKind);
        Assert.Throws<FormatException>(() => HyundaiBridge.Mqtt.Contract.ValidateState(state with { ChargingPowerKw = -1 }));
        Assert.Throws<FormatException>(() => HyundaiBridge.Mqtt.Contract.ValidateState(state with { RemainingChargeTimeMinutes = -1 }));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"state\":{\"Vehicle\":{}}}")]
    public void ChangedEnvelopeIsAnError(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<HyundaiException>(() => VehicleStateParser.Parse(document.RootElement, Vehicle, DateTimeOffset.UtcNow));
    }

    private static string CcsToken => "header." + Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"uid\":\"user-secret\"}")).TrimEnd('=') + ".signature";

    [Fact]
    public async Task CachedStatusUsesCcsHeadersAndReusesTokenWithoutRediscoveryOrWake()
    {
        using var fixture = new Fixture();
        using var tokens = JsonDocument.Parse(AuthenticationTests.Tokens);
        await fixture.Store.SaveAsync(HyundaiSession.Parse(tokens.RootElement, Fixture.AccountHash,
            "01234567-89ab-cdef-0123-456789abcdef", fixture.Clock.Now), CancellationToken.None);
        fixture.Http.Enqueue(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/domain/api/v1/auth/token-exchange", request.RequestUri!.AbsolutePath);
            Assert.Equal("?serviceType=CCS", request.RequestUri.Query);
            return AuthenticationTests.Response(JsonSerializer.Serialize(new { accessToken = CcsToken, expiresTime = 86400 }));
        });
        for (var i = 0; i < 2; i++)
            fixture.Http.Enqueue(request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("gspa-ccs-eu.hyundai.com", request.RequestUri!.Host);
                Assert.Equal("/gspa/v1/status/vehicles/vehicle-a/stored-status", request.RequestUri.AbsolutePath);
                Assert.Equal(CcsToken, request.Headers.Authorization!.Parameter);
                var id = request.Headers.GetValues("X-Request-Id").Single();
                Assert.Equal(new GspaStamp().Compute(id, fixture.Clock.Now.ToUnixTimeSeconds(), "user-secret"), request.Headers.GetValues("X-Stamp").Single());
                return AuthenticationTests.Response("{\"metaInfo\":{\"retCode\":\"S\"},\"data\":" + Payload + "}");
            });
        using var client = fixture.Client();
        await client.GetStateAsync(Vehicle, CancellationToken.None);
        fixture.Clock.Now += TimeSpan.FromMinutes(10);
        var state = await client.GetStateAsync(Vehicle, CancellationToken.None);
        Assert.Equal(73, state.BatteryPercent);
        Assert.Equal(3, fixture.Http.RequestCount);
        Assert.Equal(0, fixture.Http.Remaining);
        Assert.DoesNotContain("user-secret", string.Join('\n', fixture.Log.Messages));
    }

    [Fact]
    public async Task Status401RenewsOnceAndNeverLoopsOrLogsResponseSecrets()
    {
        using var fixture = new Fixture();
        using var tokens = JsonDocument.Parse(AuthenticationTests.Tokens);
        await fixture.Store.SaveAsync(HyundaiSession.Parse(tokens.RootElement, Fixture.AccountHash,
            "01234567-89ab-cdef-0123-456789abcdef", fixture.Clock.Now), CancellationToken.None);
        var exchange = JsonSerializer.Serialize(new { accessToken = CcsToken, expiresTime = 86400 });
        fixture.Http.Enqueue(_ => AuthenticationTests.Response(exchange));
        fixture.Http.Enqueue(_ => AuthenticationTests.Response("upstream-secret", HttpStatusCode.Unauthorized));
        fixture.Http.Enqueue(_ => AuthenticationTests.Response(AuthenticationTests.Tokens));
        fixture.Http.Enqueue(_ => AuthenticationTests.Response(exchange));
        fixture.Http.Enqueue(_ => AuthenticationTests.Response("upstream-secret", HttpStatusCode.Unauthorized));
        using var client = fixture.Client();
        var error = await Assert.ThrowsAsync<HyundaiException>(() => client.GetStateAsync(Vehicle, CancellationToken.None));
        Assert.Equal(401, error.StatusCode);
        Assert.Equal(5, fixture.Http.RequestCount);
        Assert.DoesNotContain("upstream-secret", error.Message);
        Assert.DoesNotContain("upstream-secret", string.Join('\n', fixture.Log.Messages));
    }

    [Theory]
    [InlineData("000102030405060708090a0b0c0d0e0f", "d8d51c3015970d3570b211312783d83b")]
    [InlineData("5554535251504f4e4d4c4b4a49484746", "f49c54a7a4b0906609dac73880c4df2c")]
    public void HyundaiStampMatchesUpstreamProtocolVectors(string input, string expected)
        => Assert.Equal(expected, Convert.ToHexStringLower(new GspaStamp().EncryptBlock(Convert.FromHexString(input))));

    [Fact]
    public void StampFeedbackMatchesFixedUpstreamVector()
    {
        var stamp = new GspaStamp().Compute("0TSID123456789", 1748523600, "testuser");
        Assert.Equal("740f3b0febde3f415273415f5d9f6af84d1a8a256d16fa075bccad8507adce048100", Convert.ToHexStringLower(Convert.FromBase64String(stamp)));
        var id = Convert.FromBase64String(GspaStamp.RequestId("01234567-89ab-cdef-0123-456789abcdef", DateTimeOffset.Parse("2025-06-01T12:00:00Z")) + "=");
        Assert.Equal(14, id.Length);
        Assert.Equal("0123456789abcdef", Convert.ToHexStringLower(id.AsSpan(5, 8)));
        Assert.Equal(6, id[13]);
    }
}
