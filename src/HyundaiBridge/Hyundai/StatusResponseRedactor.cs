using System.Text.Json;
using System.Text.Json.Nodes;

namespace HyundaiBridge.Hyundai;

internal static class StatusResponseRedactor
{
    internal static JsonElement Redact(JsonElement response)
    {
        var node = JsonNode.Parse(response.GetRawText());
        Mask(node);
        return JsonSerializer.SerializeToElement(node);
    }

    private static void Mask(JsonNode? node)
    {
        if (node is JsonObject obj)
            foreach (var (key, value) in obj.ToArray())
            {
                var name = key.ToLowerInvariant();
                if (name is "location" or "geocoord" or "latitude" or "longitude" or "vin" or "serialnumber" or "username" or "email" ||
                    name is "id" or "vehicleid" or "deviceid" or "userid" or "useruid" or "accountid" or "requestid" or "msgid" ||
                    key.EndsWith("Id", StringComparison.Ordinal) || key.EndsWith("ID", StringComparison.Ordinal) ||
                    name.EndsWith("_id", StringComparison.Ordinal) || name.Contains("token", StringComparison.Ordinal) || name.Contains("password", StringComparison.Ordinal))
                    obj[key] = "[maskerat]";
                else Mask(value);
            }
        else if (node is JsonArray array)
            foreach (var value in array) Mask(value);
    }
}
