using System.Text.Json;

namespace HyundaiBridge.Hyundai;

// A class rather than a record: auto-generated ToString must not print tokens.
internal sealed class HyundaiSession
{
    public required string AccountHash { get; init; }
    public required string DeviceId { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required string AccessToken { get; init; }
    public required string RefreshToken { get; init; }
    public required string NonCcsToken { get; init; }
    public required string ExchangeableAccessToken { get; set; }
    public string ExchangeableRefreshToken { get; init; } = "";
    public string NonCcsRefreshToken { get; init; } = "";
    public string IdToken { get; init; } = "";

    internal bool NeedsRefresh(DateTimeOffset now) => ExpiresAt <= now.AddMinutes(2);

    internal Dictionary<string, string> RefreshPayload() => new()
    {
        ["accessToken"] = AccessToken,
        ["refreshToken"] = RefreshToken,
        ["nonCcsToken"] = NonCcsToken,
        ["nonCcsRefreshToken"] = NonCcsRefreshToken,
        ["exchangeableAccessToken"] = ExchangeableAccessToken,
        ["exchangeableRefreshToken"] = ExchangeableRefreshToken,
        ["idToken"] = IdToken
    };

    internal static HyundaiSession Parse(JsonElement data, string accountHash, string deviceId,
        DateTimeOffset now, HyundaiSession? previous = null)
    {
        if (data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("expiresIn", out var expiry) || expiry.ValueKind != JsonValueKind.Number ||
            !expiry.TryGetInt32(out var lifetime) || lifetime <= 0)
            throw new HyundaiException("Hyundai token response has an unsupported expiry schema.");

        string Value(string name, string? fallback, bool required = false)
        {
            string? value = fallback;
            if (data.TryGetProperty(name, out var field))
            {
                if (field.ValueKind != JsonValueKind.String)
                    throw new HyundaiException("Hyundai token response has an unsupported field schema.");
                value = field.GetString();
            }
            if (required && string.IsNullOrWhiteSpace(value))
                throw new HyundaiException("Hyundai token response is missing required credentials.");
            return value ?? "";
        }

        return new HyundaiSession
        {
            AccountHash = accountHash, DeviceId = deviceId, ExpiresAt = now.AddSeconds(lifetime),
            AccessToken = Value("accessToken", null, true),
            RefreshToken = Value("refreshToken", previous?.RefreshToken, true),
            NonCcsToken = Value("nonCcsToken", previous?.NonCcsToken, true),
            ExchangeableAccessToken = Value("exchangeableAccessToken", previous?.ExchangeableAccessToken, true),
            ExchangeableRefreshToken = Value("exchangeableRefreshToken", previous?.ExchangeableRefreshToken),
            NonCcsRefreshToken = Value("nonCcsRefreshToken", previous?.NonCcsRefreshToken),
            IdToken = Value("idToken", previous?.IdToken)
        };
    }
}
