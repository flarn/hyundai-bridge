using System.Text.Json;
using System.Text.RegularExpressions;
using HyundaiBridge.Domain;

namespace HyundaiBridge.Mqtt;

internal static partial class Contract
{
    internal const string Prefix = "hyundai/v1";
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static readonly string[] Commands = ["refresh", "lock", "unlock", "climate/start", "climate/stop",
        "charging/start", "charging/stop", "charge-limit"];

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex TopicPattern();
    internal static bool ValidId(string? value) => value is not null && TopicPattern().IsMatch(value);
    internal static string Serialize<T>(T data) => JsonSerializer.Serialize(data, Json);
    internal static string Manifest(string bridgeId, BridgeSnapshot snapshot) =>
        Serialize(new { schemaVersion = 1, bridgeId, name = "Hyundai Bridge", vehicles = snapshot.Vehicles });

    internal static VehicleCommand ParseCommand(string topic, ReadOnlyMemory<byte> payload)
    {
        if (payload.Length > 4096) throw new FormatException("Command payload too large");
        var parts = topic.Split('/');
        if (parts.Length < 5 || parts[0] != "hyundai" || parts[1] != "v1" ||
            !ValidId(parts[2]) || parts[3] != "command") throw new FormatException("Invalid command topic");
        var action = string.Join('/', parts.Skip(4));
        if (!Commands.Contains(action)) throw new FormatException("Unknown command");
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var data = doc.RootElement;
            if (data.ValueKind != JsonValueKind.Object ||
                data.EnumerateObject().Select(x => x.Name).Distinct().Count() != data.EnumerateObject().Count())
                throw new FormatException("Invalid command document");
            if (!Guid.TryParse(data.GetProperty("commandId").GetString(), out var id) || id == Guid.Empty)
                throw new FormatException("A nonempty command UUID is required");
            string[] fields = action switch
            {
                "climate/start" => ["commandId", "temperatureCelsius", "defrost"],
                "charge-limit" => ["commandId", "acPercent", "dcPercent"],
                _ => ["commandId"]
            };
            if (data.EnumerateObject().Any(x => !fields.Contains(x.Name))) throw new FormatException("Unexpected command argument");
            double? temperature = null;
            bool? defrost = null;
            int? ac = null, dc = null;
            if (action == "climate/start")
            {
                temperature = data.GetProperty("temperatureCelsius").GetDouble();
                if (!double.IsFinite(temperature.Value)) throw new FormatException("Invalid temperature");
                if (data.TryGetProperty("defrost", out var preset)) defrost = preset.GetBoolean();
            }
            if (action == "charge-limit")
            {
                ac = data.GetProperty("acPercent").GetInt32();
                dc = data.GetProperty("dcPercent").GetInt32();
                if (ac is < 0 or > 100 || dc is < 0 or > 100) throw new FormatException("Invalid charge percentage");
            }
            return new(parts[2], id, action, temperature, defrost, ac, dc);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or OverflowException)
        {
            throw new FormatException("Malformed command", error);
        }
    }

    internal static void Validate(VehicleCommand command, VehicleMetadata vehicle)
    {
        if (!vehicle.Capabilities.Commands.Contains(command.Command)) throw new FormatException("Vehicle does not support this command");
        if (command.Command == "climate/start")
        {
            var bounds = vehicle.Capabilities.Climate;
            if (bounds is null || command.TemperatureCelsius is not { } value ||
                !Within(value, bounds.MinTemperatureCelsius, bounds.MaxTemperatureCelsius, bounds.TemperatureStepCelsius))
                throw new FormatException("Temperature is outside supported values");
            if (command.Defrost == true && !bounds.SupportsDefrost) throw new FormatException("Defrost is not supported");
        }
        if (command.Command == "charge-limit")
        {
            var bounds = vehicle.Capabilities.ChargeLimits;
            if (bounds is null || command.AcPercent is not { } ac || command.DcPercent is not { } dc ||
                !Within(ac, bounds.MinPercent, bounds.MaxPercent, bounds.StepPercent) ||
                !Within(dc, bounds.MinPercent, bounds.MaxPercent, bounds.StepPercent))
                throw new FormatException("Charge limit is outside supported values");
        }
    }

    private static bool Within(double value, double min, double max, double step) =>
        double.IsFinite(value) && step > 0 && value >= min && value <= max &&
        Math.Abs((value - min) / step - Math.Round((value - min) / step)) < 0.000001;

    internal static void ValidateState(VehicleState state)
    {
        foreach (var percentage in new[] { state.BatteryPercent, state.AuxiliaryBatteryPercent, state.AcChargeLimitPercent, state.DcChargeLimitPercent })
            if (percentage is < 0 or > 100) throw new FormatException("Invalid normalized percentage");
        foreach (var distance in new[] { state.EstimatedRangeKm, state.OdometerKm })
            if (distance < 0) throw new FormatException("Invalid normalized distance");
        if (state.Latitude is { } lat && Math.Abs(lat) > 90 || state.Longitude is { } lon && Math.Abs(lon) > 180)
            throw new FormatException("Invalid normalized coordinates");
        foreach (var measurement in new[] { state.ChargingPowerKw, state.RemainingChargeTimeMinutes })
            if (measurement < 0) throw new FormatException("Invalid normalized charging measurement");
        // Serialization refuses NaN/infinity; do it before replacing cache data.
        _ = Serialize(state);
    }
}
