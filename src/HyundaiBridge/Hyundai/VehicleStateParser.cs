using System.Globalization;
using System.Text.Json;
using HyundaiBridge.Domain;

namespace HyundaiBridge.Hyundai;

internal static class VehicleStateParser
{
    internal static VehicleState Parse(JsonElement payload, VehicleMetadata vehicle, DateTimeOffset now)
    {
        var state = At(payload, "state.Vehicle");
        if (state.ValueKind != JsonValueKind.Object || !state.EnumerateObject().Any())
            throw new HyundaiException("Hyundai stored status has an unsupported state schema.");
        var locks = new[] { "Row1.Driver", "Row1.Passenger", "Row2.Left", "Row2.Right" }
            .Select(door => Bool(At(state, "Cabin.Door." + door + ".Lock"))).ToArray();
        // CCS2 0 means locked, 1 unlocked. Unknown doors do not imply unlocked.
        bool? locked = locks.Any(x => x == true) ? false : locks.All(x => x == false) ? true : null;
        var range = Number(At(state, "Drivetrain.FuelSystem.DTE.Total"));
        var unit = Number(At(state, "Drivetrain.FuelSystem.DTE.Unit"));
        range = unit switch { 1 => range, 2 or 3 => range * 1.609344, _ => null };
        var remaining = Number(At(state, "Green.ChargingInformation.Charging.RemainTime"));
        var target = Temperature(At(state, "Cabin.HVAC.Row1.Driver.Temperature"));
        var pressureUnit = Number(At(state, "Chassis.Axle.Tire.PressureUnit"));
        var fanSpeed = Number(At(state, "Cabin.HVAC.Row1.Driver.Blower.SpeedLevel"));
        int? fanLevel = fanSpeed is >= 0 and <= int.MaxValue && fanSpeed == Math.Truncate(fanSpeed.Value)
            ? (int)fanSpeed.Value : null;
        var energy = At(state, "Green.BatteryManagement.BatteryRemain");
        var latitude = Number(At(state, "Location.GeoCoord.Latitude"));
        var longitude = Number(At(state, "Location.GeoCoord.Longitude"));
        if (latitude is not (>= -90 and <= 90) || longitude is not (>= -180 and <= 180))
            latitude = longitude = null;
        return new VehicleState
        {
            VehicleId = vehicle.VehicleId, Vin = vehicle.Vin,
            // v1 uses whole percentages; omit the fractional part, matching the observed app display.
            BatteryPercent = Number(At(state, "Green.BatteryManagement.BatteryRemain.Ratio")) is >= 0 and <= 100 and var soc
                ? (int)Math.Truncate(soc) : null,
            AuxiliaryBatteryPercent = Percent(At(state, "Electronics.Battery.Level")),
            EstimatedRangeKm = range >= 0 ? range : null,
            FrontLeftTirePressureBar = PressureBar(At(state, "Chassis.Axle.Row1.Left.Tire.Pressure"), pressureUnit),
            FrontRightTirePressureBar = PressureBar(At(state, "Chassis.Axle.Row1.Right.Tire.Pressure"), pressureUnit),
            RearLeftTirePressureBar = PressureBar(At(state, "Chassis.Axle.Row2.Left.Tire.Pressure"), pressureUnit),
            RearRightTirePressureBar = PressureBar(At(state, "Chassis.Axle.Row2.Right.Tire.Pressure"), pressureUnit),
            IsTirePressureLow = Bool(At(state, "Chassis.Axle.Tire.PressureLow")),
            IsCabinFanOn = fanLevel is { } level ? level > 0 : null,
            CabinFanSpeedLevel = fanLevel,
            BatteryMinTemperatureCelsius = Number(At(state, "Green.BatteryManagement.Temperature.Min.Raw")),
            BatteryMaxTemperatureCelsius = Number(At(state, "Green.BatteryManagement.Temperature.Max.Raw")),
            // Reported remaining energy, not a claim of usable energy or battery health.
            BatteryEnergyKwh = At(energy, "Unit").ValueKind == JsonValueKind.String && At(energy, "Unit").GetString() == "kJ"
                && Number(At(energy, "Value")) is >= 0 and var kilojoules ? kilojoules / 3600 : null,
            IsFrontLeftWindowOpen = Bool(At(state, "Cabin.Window.Row1.Driver.Open")),
            IsFrontRightWindowOpen = Bool(At(state, "Cabin.Window.Row1.Passenger.Open")),
            IsRearLeftWindowOpen = Bool(At(state, "Cabin.Window.Row2.Left.Open")),
            IsRearRightWindowOpen = Bool(At(state, "Cabin.Window.Row2.Right.Open")),
            IsCharging = remaining >= 0 ? remaining > 0 : null,
            IsPluggedIn = Bool(At(state, "Green.ChargingInformation.ConnectorFastening.State")),
            IsLocked = locked,
            IsChargePortOpen = Number(At(state, "Green.ChargingDoor.State")) switch { 0 or 2 => false, 1 => true, _ => null },
            IsSunroofOpen = Bool(At(state, "Body.Sunroof.Glass.Open")),
            ChargingPowerKw = Number(At(state, "Green.Electric.SmartGrid.RealTimePower")) is >= 0 and var power ? power : null,
            RemainingChargeTimeMinutes = remaining >= 0 ? remaining : null,
            IsFrontLeftDoorOpen = Bool(At(state, "Cabin.Door.Row1.Driver.Open")),
            IsFrontRightDoorOpen = Bool(At(state, "Cabin.Door.Row1.Passenger.Open")),
            IsRearLeftDoorOpen = Bool(At(state, "Cabin.Door.Row2.Left.Open")),
            IsRearRightDoorOpen = Bool(At(state, "Cabin.Door.Row2.Right.Open")),
            IsTrunkOpen = Bool(At(state, "Body.Trunk.Open")),
            IsHoodOpen = Bool(At(state, "Body.Hood.Open")),
            AcChargeLimitPercent = Percent(At(state, "Green.ChargingInformation.TargetSoC.Standard")),
            DcChargeLimitPercent = Percent(At(state, "Green.ChargingInformation.TargetSoC.Quick")),
            TargetTemperatureCelsius = target,
            // Observed EU remote-climate codes: 0 off, 1 on. Other codes remain unknown.
            IsClimateOn = Bool(At(state, "Green.Electric.Climate.RemoteClimateDetails")),
            // A set temperature does not establish measured cabin temperature or HVAC action.
            IsDefrostOn = Number(At(state, "Body.Windshield.Front.Defog.State")) switch { 0 or 2 => false, 1 => true, _ => null },
            OutsideTemperatureCelsius = Temperature(At(state, "Cabin.HVAC.OutsideTemperature")),
            Latitude = latitude, Longitude = longitude,
            OdometerKm = Number(At(state, "Drivetrain.Odometer")) is >= 0 and var odo ? odo : null,
            VehicleUpdatedAt = Timestamp(At(state, "Date"), Number(At(state, "Offset")))
                ?? Timestamp(At(payload, "lastUpdateTime"), 0),
            BridgeUpdatedAt = now
        };
    }

    internal static JsonElement At(JsonElement element, string path)
    {
        foreach (var name in path.Split('.'))
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element)) return default;
        return element;
    }

    private static double? Number(JsonElement element)
    {
        double value;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out value) && double.IsFinite(value)) return value;
        if (element.ValueKind == JsonValueKind.String && double.TryParse(element.GetString(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out value) && double.IsFinite(value)) return value;
        return null;
    }
    private static double? PressureBar(JsonElement element, double? unit)
    {
        var raw = Number(element);
        if (raw is null or < 0 or 255) return null; // 255 is the CCS2 no-reading sentinel.
        return unit switch { 0 => raw * 0.0689475729, 1 => raw * 0.05, 2 => raw * 0.1, _ => null };
    }
    private static int? Percent(JsonElement element) => Number(element) is >= 0 and <= 100 and var n && n == Math.Truncate(n) ? (int)n : null;
    private static bool? Bool(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => true, JsonValueKind.False => false,
        _ => Number(element) switch { 0 => false, 1 => true, _ => null }
    };
    private static double? Temperature(JsonElement element)
    {
        var value = Number(At(element, "Value"));
        return Number(At(element, "Unit")) switch { 0 => value, 1 => (value - 32) * 5 / 9, _ => null };
    }
    private static DateTimeOffset? Timestamp(JsonElement element, double? offset)
    {
        var text = element.ValueKind is JsonValueKind.String or JsonValueKind.Number ? element.ToString() : null;
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (text.Length == 13 && long.TryParse(text, out var milliseconds))
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        if (DateTime.TryParseExact(text, new[] { "yyyyMMddHHmmss", "yyyyMMddHHmmss.FFF" }, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date))
        {
            if (offset is null or < -14 or > 14 || offset * 60 != Math.Truncate(offset.Value * 60)) return null;
            return new DateTimeOffset(date, TimeSpan.FromHours(offset.Value)).ToUniversalTime();
        }
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var parsed) ? parsed.ToUniversalTime() : null;
    }
}
