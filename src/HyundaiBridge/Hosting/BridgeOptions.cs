using HyundaiBridge.Mqtt;

namespace HyundaiBridge.Hosting;

internal sealed record BridgeOptions(string BridgeId, string Host, int Port, string? Username, string? Password,
    bool Tls, string DataDirectory)
{
    internal static BridgeOptions FromEnvironment()
    {
        var id = Environment.GetEnvironmentVariable("BRIDGE_ID") ?? "home";
        var host = Environment.GetEnvironmentVariable("MQTT_HOST");
        var portText = Environment.GetEnvironmentVariable("MQTT_PORT") ?? "1883";
        var tlsText = Environment.GetEnvironmentVariable("MQTT_TLS") ?? "false";
        var user = Secret("MQTT_USERNAME");
        var pass = Secret("MQTT_PASSWORD");
        var directory = Environment.GetEnvironmentVariable("HYUNDAI_SESSION_DIRECTORY");
        if (!Contract.ValidId(id) || string.IsNullOrWhiteSpace(host) ||
            !int.TryParse(portText, out var port) || port is < 1 or > 65535 ||
            !bool.TryParse(tlsText, out var tls) || string.IsNullOrWhiteSpace(directory) ||
            !Path.IsPathFullyQualified(directory) || (pass is not null && user is null))
            throw new FormatException("Set valid BRIDGE_ID, MQTT_HOST, MQTT_PORT, MQTT_TLS and absolute HYUNDAI_SESSION_DIRECTORY");
        return new(id, host, port, user, pass, tls, directory);
    }

    internal static string? Secret(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        var path = Environment.GetEnvironmentVariable(name + "_FILE");
        if (!string.IsNullOrEmpty(value) && !string.IsNullOrEmpty(path))
            throw new FormatException("Configure either " + name + " or " + name + "_FILE");
        var secret = string.IsNullOrEmpty(path) ? value : File.ReadAllText(path).TrimEnd('\r', '\n');
        return string.IsNullOrEmpty(secret) ? null : secret;
    }
}
