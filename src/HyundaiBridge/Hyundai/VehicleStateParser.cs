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
        var latitude = Number(At(state, "Location.GeoCoord.Latitude"));
        var longitude = Number(At(state, "Location.GeoCoord.Longitude"));
        if (latitude is not (>= -90 and <= 90) || longitude is not (>= -180 and <= 180))
            latitude = longitude = null;
        return new VehicleState
        {
            VehicleId = vehicle.VehicleId, Vin = vehicle.Vin,
            // v1 uses whole percentages; round fractional SoC without treating it as unsupported.
            BatteryPercent = Number(At(state, "Green.BatteryManagement.BatteryRemain.Ratio")) is >= 0 and <= 100 and var soc
                ? (int)Math.Round(soc, MidpointRounding.AwayFromZero) : null,
            AuxiliaryBatteryPercent = Percent(At(state, "Electronics.Battery.Level")),
            EstimatedRangeKm = range >= 0 ? range : null,
            IsCharging = remaining >= 0 ? remaining > 0 : null,
            IsPluggedIn = Bool(At(state, "Green.ChargingInformation.ConnectorFastening.State")),
            IsLocked = locked,
            IsFrontLeftDoorOpen = Bool(At(state, "Cabin.Door.Row1.Driver.Open")),
            IsFrontRightDoorOpen = Bool(At(state, "Cabin.Door.Row1.Passenger.Open")),
            IsRearLeftDoorOpen = Bool(At(state, "Cabin.Door.Row2.Left.Open")),
            IsRearRightDoorOpen = Bool(At(state, "Cabin.Door.Row2.Right.Open")),
            IsTrunkOpen = Bool(At(state, "Body.Trunk.Open")),
            IsHoodOpen = Bool(At(state, "Body.Hood.Open")),
            AcChargeLimitPercent = Percent(At(state, "Green.ChargingInformation.TargetSoC.Standard")),
            DcChargeLimitPercent = Percent(At(state, "Green.ChargingInformation.TargetSoC.Quick")),
            TargetTemperatureCelsius = target,
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
