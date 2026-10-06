using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HyundaiBridge.Domain;
using Microsoft.Extensions.Logging;

namespace HyundaiBridge.Hyundai;

internal sealed class HyundaiClient(HttpClient http, SessionStore store, string username, string password,
    ILogger<HyundaiClient> logger, TimeProvider time) : IDisposable
{
    private const string Idp = "https://idpconnect-eu.hyundai.com";
    private const string Cci = "https://cci-api-eu.hyundai.com/domain/api/";
    private const string ClientId = "4f4953b5-02e1-4dbc-8599-87e983ee1be5";
    private const string RedirectUri = "https://oneapp.hyundai.com/redirect";
    private const string MobileAgent = "Mozilla/5.0 (Linux; Android 4.1.1; Galaxy Nexus Build/JRO03C) " +
        "AppleWebKit/535.19 (KHTML, like Gecko) Chrome/18.0.1025.166 Mobile Safari/535.19_CCS_APP_AOS";
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string accountHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(username)));
    private HyundaiSession? session;
    private bool loaded;

    internal async Task<IReadOnlyList<Vehicle>> GetVehiclesAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!loaded)
            {
                session = await store.LoadAsync(accountHash, cancellationToken);
                loaded = true;
            }
            await EnsureSessionAsync(forceRefresh: false, cancellationToken);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var request = CciRequest(HttpMethod.Get, "v1/vehicle/available-vehicles?detail=true", session!);
                using var response = await http.SendAsync(request, cancellationToken);
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    await EnsureSessionAsync(forceRefresh: true, cancellationToken);
                    continue;
                }
                var data = await ReadJsonAsync(response, "Vehicle discovery", cancellationToken);
                var vehicles = VehicleParser.Parse(data);
                logger.LogInformation("Vehicle discovery completed: {VehicleCount} vehicles", vehicles.Count);
                return vehicles;
            }
            throw new HyundaiException("Vehicle discovery failed after credential renewal.");
        }
        finally { gate.Release(); }
    }

    private async Task EnsureSessionAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        if (session is not null && !forceRefresh && !session.NeedsRefresh(time.GetUtcNow())) return;
        HyundaiSession next;
        if (session is null)
        {
            next = await LoginAsync(Guid.NewGuid().ToString(), cancellationToken);
            await store.SaveAsync(next, cancellationToken);
            session = next;
            logger.LogInformation("Authenticated with Hyundai");
            return;
        }
        try
        {
            using var request = CciRequest(HttpMethod.Post, "v2/auth/token-refresh", session);
            request.Content = JsonContent.Create(session.RefreshPayload());
            using var response = await http.SendAsync(request, cancellationToken);
            var data = await ReadJsonAsync(response, "Token refresh", cancellationToken);
            next = HyundaiSession.Parse(data, accountHash, session.DeviceId, time.GetUtcNow(), session);
            // The app may rotate its exchangeable token in the t cookie.
            if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
            {
                var rotated = cookies.Select(x => x.Split(';', 2)[0])
                    .FirstOrDefault(x => x.StartsWith("t=", StringComparison.Ordinal));
                if (rotated is { Length: > 2 })
                    next.ExchangeableAccessToken = rotated[2..];
            }
        }
        catch (HyundaiException error) when (error.StatusCode == 401)
        {
            logger.LogInformation("Hyundai refresh credentials expired; authenticating once");
            next = await LoginAsync(session.DeviceId, cancellationToken);
            await store.SaveAsync(next, cancellationToken);
            session = next;
            logger.LogInformation("Authenticated with Hyundai");
            return;
        }
        // Persist the rotated full set before reporting success.
        await store.SaveAsync(next, cancellationToken);
        session = next;
        logger.LogInformation("Hyundai token refreshed");
    }

    private async Task<HyundaiSession> LoginAsync(string deviceId, CancellationToken cancellationToken)
    {
        var authorize = new Uri(Idp + "/auth/api/v2/user/oauth2/authorize?response_type=code&client_id=" +
            ClientId + "&redirect_uri=" + Uri.EscapeDataString(RedirectUri) + "&lang=en&state=ccsp&country=de");
        await AuthorizeAsync(authorize, cancellationToken);
        using var certRequest = IdpRequest(HttpMethod.Get, Idp + "/auth/api/v1/accounts/certs");
        using var certResponse = await http.SendAsync(certRequest, cancellationToken);
        var certData = await ReadJsonAsync(certResponse, "Authentication certificate", cancellationToken);
        if (certData.ValueKind != JsonValueKind.Object ||
            !certData.TryGetProperty("retValue", out var jwk) || jwk.ValueKind != JsonValueKind.Object)
            throw new HyundaiException("Hyundai authentication certificate schema changed.");
        string encrypted;
        string kid;
        try
        {
            kid = jwk.GetProperty("kid").GetString() ?? throw new FormatException();
            using var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters
            {
                Modulus = DecodeBase64Url(jwk.GetProperty("n").GetString() ?? throw new FormatException()),
                Exponent = DecodeBase64Url(jwk.GetProperty("e").GetString() ?? throw new FormatException())
            });
            var plaintext = Encoding.UTF8.GetBytes(password);
            try { encrypted = Convert.ToHexStringLower(rsa.Encrypt(plaintext, RSAEncryptionPadding.Pkcs1)); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        catch (Exception error) when (error is FormatException or CryptographicException or
            KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            throw new HyundaiException("Hyundai authentication certificate is invalid or unsupported.");
        }

        using var signin = IdpRequest(HttpMethod.Post, Idp + "/auth/account/signin");
        signin.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId, ["encryptedPassword"] = "true", ["password"] = encrypted,
            ["redirect_uri"] = RedirectUri, ["scope"] = "", ["nonce"] = "", ["state"] = "ccsp",
            ["username"] = username, ["connector_session_key"] = "", ["kid"] = kid, ["_csrf"] = ""
        });
        using var signinResponse = await http.SendAsync(signin, cancellationToken);
        if (signinResponse.StatusCode != HttpStatusCode.Found)
        {
            CheckHttpStatus(signinResponse, "Authentication signin");
            throw new HyundaiException("Hyundai signin did not return an authorization redirect. Check credentials or changed authentication flow.");
        }
        var location = signinResponse.Headers.Location;
        if (location is null) throw new HyundaiException("Hyundai signin returned no authorization redirect.");
        if (!location.IsAbsoluteUri) location = new Uri(new Uri(Idp), location);
        var code = QueryValue(location.Query, "code");
        if (string.IsNullOrWhiteSpace(code))
        {
            if (location.AbsolutePath.Contains("/web/v1/user/authorization", StringComparison.Ordinal))
                throw new HyundaiException("Account consent is required. Accept the terms in the official MyHyundai app/browser, then retry.");
            throw new HyundaiException("Hyundai did not authorize the account. Check credentials, consent or changed authentication flow.");
        }
        using var exchange = CciRequest(HttpMethod.Post, "v1/auth/token?code=" + Uri.EscapeDataString(code), null, deviceId);
        exchange.Content = new ByteArrayContent([]);
        using var exchangeResponse = await http.SendAsync(exchange, cancellationToken);
        var data = await ReadJsonAsync(exchangeResponse, "Authentication token exchange", cancellationToken);
        return HyundaiSession.Parse(data, accountHash, deviceId, time.GetUtcNow());
    }

    private async Task AuthorizeAsync(Uri uri, CancellationToken cancellationToken)
    {
        // HttpClient redirects are disabled so signin codes are never followed.
        // Only the authorize GET follows the known Hyundai identity hosts.
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            using var request = IdpRequest(HttpMethod.Get, uri.AbsoluteUri);
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or
                HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                if (location is null) break;
                uri = new Uri(uri, location);
                if (uri.Scheme != "https" || uri.Host is not
                    ("idpconnect-eu.hyundai.com" or "eu-account.hyundai.com" or "prd.eu-ccapi.hyundai.com"))
                    throw new HyundaiException("Hyundai authorization redirected to an unexpected host.");
                continue;
            }
            CheckHttpStatus(response, "Authentication authorize");
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (body.Contains("abusing", StringComparison.OrdinalIgnoreCase) ||
                (uri.AbsolutePath == "/error" && QueryValue(uri.Query, "status") == "400"))
                throw new HyundaiException("Hyundai authorization was blocked by the server. Do not repeatedly retry login.");
            return;
        }
        throw new HyundaiException("Hyundai authorization exceeded its redirect limit.");
    }

    private static HttpRequestMessage IdpRequest(HttpMethod method, string uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", MobileAgent);
        return request;
    }

    private static HttpRequestMessage CciRequest(HttpMethod method, string path, HyundaiSession? credentials, string? deviceId = null)
    {
        var request = new HttpRequestMessage(method, Cci + path);
        var headers = new Dictionary<string, string>
        {
            ["client-id"] = "com.hyundai.oneapp.eu", ["client-name"] = "hyundai", ["client-version"] = "1.3.3",
            ["client-os-code"] = "ios", ["client-os-version"] = "18.7", ["client-device-model"] = "iPhone",
            ["client-device-id"] = credentials?.DeviceId ?? deviceId!, ["client-notification-provider-type"] = "APNS",
            ["locale"] = "SV", ["timezone"] = "+00:00", ["Accept"] = "application/json",
            ["Accept-Language"] = "sv", ["User-Agent"] = "okhttp/3.12.0"
        };
        if (credentials is not null)
        {
            headers["Authorization"] = "Bearer " + credentials.AccessToken;
            headers["Authentication"] = credentials.NonCcsToken;
            headers["exchangeable-token"] = credentials.ExchangeableAccessToken;
            headers["non-ccs-token"] = credentials.NonCcsToken;
        }
        foreach (var header in headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return request;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        JsonElement data;
        try
        {
            using var document = JsonDocument.Parse(body);
            data = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            CheckHttpStatus(response, operation);
            throw new HyundaiException(operation + " returned non-JSON data; the Hyundai API may have changed.");
        }
        if (data.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "code", "resCode", "retCode" })
            {
                if (!data.TryGetProperty(key, out var value)) continue;
                var code = value.ToString();
                if (code == "4111") throw new HyundaiException("Hyundai credentials expired.", 401);
            }
        }
        CheckHttpStatus(response, operation);
        if (data.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            throw new HyundaiException(operation + " returned an unsupported JSON schema.");
        return data;
    }

    private static void CheckHttpStatus(HttpResponseMessage response, string operation)
    {
        if (response.IsSuccessStatusCode) return;
        var retryAfter = response.Headers.RetryAfter?.Delta;
        if (retryAfter is null && response.Headers.RetryAfter?.Date is { } date)
            retryAfter = date - DateTimeOffset.UtcNow;
        throw new HyundaiException(operation + " failed with HTTP " + (int)response.StatusCode + ".",
            (int)response.StatusCode, retryAfter);
    }

    private static byte[] DecodeBase64Url(string text)
    {
        var value = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(value.PadRight((value.Length + 3) / 4 * 4, '='));
    }

    private static string? QueryValue(string query, string key)
    {
        foreach (var part in query.TrimStart('?').Split('&'))
        {
            var pair = part.Split('=', 2);
            if (Uri.UnescapeDataString(pair[0]) == key && pair.Length == 2)
                return Uri.UnescapeDataString(pair[1].Replace('+', ' '));
        }
        return null;
    }

    public void Dispose() => gate.Dispose();
}
