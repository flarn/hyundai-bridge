using System.Text.Json;
using HyundaiBridge.Domain;

namespace HyundaiBridge.Hyundai;

internal static class VehicleParser
{
    internal static IReadOnlyList<Vehicle> Parse(JsonElement data)
    {
        JsonElement entries;
        if (data.ValueKind == JsonValueKind.Array) entries = data;
        else if (data.ValueKind == JsonValueKind.Object &&
            (data.TryGetProperty("contents", out entries) || data.TryGetProperty("vehicles", out entries))) { }
        else throw new HyundaiException("Hyundai discovery returned an unsupported vehicle envelope.");

        var list = entries.ValueKind switch
        {
            JsonValueKind.Array => entries.EnumerateArray().ToArray(),
            JsonValueKind.Object => [entries],
            _ => throw new HyundaiException("Hyundai discovery returned an unsupported vehicle list.")
        };
        var vehicles = new List<Vehicle>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in list)
        {
            if (entry.ValueKind != JsonValueKind.Object)
                throw new HyundaiException("Hyundai discovery returned an invalid vehicle entry.");
            var ccsp = entry.TryGetProperty("ccspVehicle", out var nested) ? nested : default;
            var id = Text(entry, "ccspCarId") ?? Text(ccsp, "carId") ?? Text(entry, "vehicleId");
            if (string.IsNullOrWhiteSpace(id) || !ids.Add(id))
                throw new HyundaiException("Hyundai discovery returned missing or duplicate vehicle identifiers.");
            vehicles.Add(new Vehicle(id, Text(entry, "vin"),
                Text(entry, "vehicleNameView") ?? Text(entry, "nickname") ?? Text(entry, "vehicleName"),
                Text(entry, "vehicleModelName") ?? Text(entry, "modelName")));
        }
        return vehicles;
    }

    private static string? Text(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value) ||
            value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new HyundaiException("Hyundai discovery returned an unsupported metadata field.");
        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
