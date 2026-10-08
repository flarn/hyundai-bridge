using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HyundaiBridge.Domain;
using HyundaiBridge.Hosting;
using Microsoft.Extensions.Logging;

namespace HyundaiBridge.Hyundai;

internal sealed class HyundaiClient(HttpClient http, SessionStore store, string username, string password,
    ILogger<HyundaiClient> logger, TimeProvider time, string? pin = null) : IDisposable
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
    private readonly GspaStamp stamp = new();
    private readonly Dictionary<string, string> protocols = new();
    private string? ccsToken, ccsUserId, controlToken;
    private DateTimeOffset controlExpiresAt, apiNotBefore;
    private bool pinRejected;
    private readonly HashSet<string> electricVehicles = new();
    private readonly Dictionary<string, VehicleMetadata> knownVehicles = new();
    private readonly Dictionary<string, VehicleState> lastStates = new();
    private DateTimeOffset ccsExpiresAt;
    internal BridgeStatistics Statistics { get; } = new(time);

    internal async Task<IReadOnlyList<Vehicle>> GetVehiclesAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureSessionAsync(forceRefresh: false, cancellationToken);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var request = CciRequest(HttpMethod.Get, "v1/vehicle/available-vehicles?detail=true", session!);
                using var response = await SendAsync(request, "discovery", cancellationToken);
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    await EnsureSessionAsync(forceRefresh: true, cancellationToken);
                    continue;
                }
                var data = await ReadJsonAsync(response, "Vehicle discovery", cancellationToken);
                var vehicles = VehicleParser.Parse(data);
                var entries = data.ValueKind == JsonValueKind.Array ? data :
                    data.TryGetProperty("contents", out var contents) ? contents : data.GetProperty("vehicles");
                foreach (var entry in entries.ValueKind == JsonValueKind.Array ? entries.EnumerateArray().ToArray() : [entries])
                {
                    var id = VehicleStateParser.At(entry, "ccspCarId");
                    if (id.ValueKind != JsonValueKind.String) id = VehicleStateParser.At(entry, "ccspVehicle.carId");
                    if (id.ValueKind != JsonValueKind.String) id = VehicleStateParser.At(entry, "vehicleId");
                    var protocol = VehicleStateParser.At(entry, "ccs2ProtocolSupport");
                    if (protocol.ValueKind == JsonValueKind.Undefined) protocol = VehicleStateParser.At(entry, "ccu_ccs2_protocol_support");
                    var fuel = TextOrNull(VehicleStateParser.At(entry, "fuelType")) ?? TextOrNull(VehicleStateParser.At(entry, "engineFuelCode"));
                    var carType = TextOrNull(VehicleStateParser.At(entry, "ccspVehicle.carType"));
                    if (VehicleStateParser.At(entry, "isEv").ValueKind == JsonValueKind.True ||
                        fuel is "EV" or "PHEV" or "HEV+PHEV" || carType is "EV" or "ELEC" or "PHEV")
                        electricVehicles.Add(id.GetString()!);
                    protocols[id.GetString()!] = protocol.ValueKind == JsonValueKind.Number ? protocol.ToString() :
                        VehicleStateParser.At(entry, "isCcs").ValueKind == JsonValueKind.True &&
                        VehicleStateParser.At(entry, "isCcsOpen").ValueKind == JsonValueKind.True ? "2" : "0";
                }
                logger.LogInformation("Vehicle discovery completed: {VehicleCount} vehicles", vehicles.Count);
                return vehicles;
            }
            throw new HyundaiException("Vehicle discovery failed after credential renewal.");
        }
        finally { gate.Release(); }
    }

    internal async Task<BridgeSnapshot> GetBridgeSnapshotAsync(CancellationToken cancellationToken)
    {
        var discovered = await GetVehiclesAsync(cancellationToken);
        var vehicles = new List<VehicleMetadata>();
        foreach (var vehicle in discovered)
        {
            if (string.IsNullOrWhiteSpace(vehicle.Vin))
            {
                logger.LogWarning("Discovered vehicle omitted from bridge metadata because VIN is missing");
                continue;
            }
            // Supported climate fields remain nullable until an actual observation.
            var supported = protocols.GetValueOrDefault(vehicle.VehicleId, "0") != "0";
            var commands = supported ? string.IsNullOrEmpty(pin) ? new[] { "refresh" } :
                electricVehicles.Contains(vehicle.VehicleId)
                    ? new[] { "refresh", "climate/start", "climate/stop", "charging/start", "charging/stop" }
                    : new[] { "refresh", "climate/start", "climate/stop" } : [];
            var capabilities = new VehicleCapabilities(
                commands.Contains("climate/start") ? ["isClimateOn", "targetTemperatureCelsius", "isDefrostOn"] : [], commands,
                commands.Contains("climate/start") ? new(17, 27, 0.5, true) : null);
            var metadata = new VehicleMetadata(vehicle.VehicleId, vehicle.Vin, vehicle.Name, vehicle.Model, capabilities);
            knownVehicles[vehicle.VehicleId] = metadata;
            vehicles.Add(metadata);
        }
        return new(vehicles, []);
    }

    internal async Task<VehicleState> GetStateAsync(VehicleMetadata vehicle, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureSessionAsync(false, cancellationToken);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await EnsureCcsAsync(cancellationToken);
                using var request = GspaRequest(HttpMethod.Get, vehicle.VehicleId,
                    "status/vehicles/" + Uri.EscapeDataString(vehicle.VehicleId) + "/stored-status");
                using var response = await SendAsync(request, "stored-status", cancellationToken);
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    ccsToken = null;
                    await EnsureSessionAsync(true, cancellationToken);
                    continue;
                }
                var data = await ReadJsonAsync(response, "Cached vehicle status", cancellationToken);
                if (VehicleStateParser.At(data, "metaInfo.retCode").ToString() != "S")
                    throw new HyundaiException("Hyundai cached vehicle status was rejected by the backend.");
                Statistics.VehicleResponseReceived(vehicle.VehicleId, vehicle.Model, StatusResponseRedactor.Redact(data));
                var state = VehicleStateParser.Parse(VehicleStateParser.At(data, "data"), vehicle, time.GetUtcNow());
                logger.LogInformation("Cached vehicle state retrieved");
                lastStates[vehicle.VehicleId] = state;
                return state;
            }
            throw new HyundaiException("Cached vehicle state failed after credential renewal.");
        }
        finally { gate.Release(); }
    }

    private HttpRequestMessage GspaRequest(HttpMethod method, string vehicleId, string path, bool control = false)
    {
        var now = time.GetUtcNow();
        var requestId = GspaStamp.RequestId(session!.DeviceId, now);
        var request = new HttpRequestMessage(method, "https://gspa-ccs-eu.hyundai.com/gspa/v1/" + path);
        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer " + (control ? controlToken : ccsToken),
            ["ccsp-service-id"] = "6d477c38-3ca4-4cf3-9557-2a1929a94654",
            ["ccsp-application-id"] = "6d477c38-3ca4-4cf3-9557-2a1929a94654",
            ["ccsp-device-id"] = session.DeviceId, ["X-Device-Id"] = session.DeviceId,
            ["Ccuccs2protocolsupport"] = protocols.GetValueOrDefault(vehicleId, "0"),
            ["client-id"] = ClientId, ["client-name"] = "hyundai", ["client-version"] = "1.3.3",
            ["client-os-code"] = "AOS", ["client-os-version"] = "14", ["Language"] = "sv",
            ["User-Agent"] = "okhttp/3.12.0", ["Accept"] = "application/json",
            ["X-Request-Id"] = requestId, ["X-Stamp"] = stamp.Compute(requestId, now.ToUnixTimeSeconds(), ccsUserId!)
        };
        if (control) headers["AuthorizationCCSP"] = "Bearer " + controlToken;
        foreach (var header in headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return request;
    }

    internal async Task<VehicleState?> ExecuteCommandAsync(VehicleCommand command, Func<Task> reportAccepted,
        CancellationToken cancellationToken)
    {
        if (!knownVehicles.TryGetValue(command.VehicleId, out var vehicle))
            throw new HyundaiException("Unknown vehicle.");
        // This allowlist is independent of MQTT validation: no unrequested control can reach Hyundai.
        if (!vehicle.Capabilities.Commands.Contains(command.Command))
            throw new HyundaiException("Vehicle command is not supported or PIN is not configured.");
        Mqtt.Contract.Validate(command, vehicle);
        var refresh = command.Command == "refresh";
        var baseline = lastStates.GetValueOrDefault(command.VehicleId);
        if (refresh && baseline?.VehicleUpdatedAt is null)
            baseline = await GetStateAsync(vehicle, cancellationToken);
        if (refresh && baseline?.VehicleUpdatedAt is null)
            throw new HyundaiException("Cannot verify refreshed state without a vehicle timestamp.");
        string? sid;
        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureSessionAsync(false, cancellationToken);
            await EnsureCcsAsync(cancellationToken);
            if (!refresh) await EnsureControlTokenAsync(cancellationToken);
            var endpoint = command.Command switch
            {
                "refresh" => "prewakeup",
                "climate/start" or "climate/stop" => "temperature",
                _ => "charge"
            };
            using var request = GspaRequest(HttpMethod.Post, vehicle.VehicleId,
                "remote/vehicles/" + Uri.EscapeDataString(vehicle.VehicleId) + "/" + endpoint, !refresh);
            object body = command.Command switch
            {
                "refresh" => new { action = "prewakeup" },
                "climate/start" => new
                {
                    command = "start", hvacTemp = command.TemperatureCelsius!.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
                    tempUnit = "C", hvacTempType = 1, windshieldFrontDefogState = command.Defrost ?? false
                },
                "climate/stop" or "charging/stop" => new { command = "stop" },
                _ => new { command = "start" }
            };
            request.Content = JsonContent.Create(body);
            using var response = await SendAsync(request, "command-" + command.Command, cancellationToken);
            var data = await ReadJsonAsync(response, "Vehicle command", cancellationToken);
            RequireGspaSuccess(data);
            sid = TextOrNull(VehicleStateParser.At(data, "data.SID"))
                ?? TextOrNull(VehicleStateParser.At(data, "data.svcSID"));
        }
        finally { gate.Release(); }
        await reportAccepted();
        if (!refresh && string.IsNullOrWhiteSpace(sid))
            throw new HyundaiException("Command accepted without a result handle; outcome is unknown.");
        // Twelve spaced reads at most; the transport also imposes its 90-second budget.
        for (var attempt = 0; attempt < 12; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), time, cancellationToken);
            if (refresh)
            {
                var state = await GetStateAsync(vehicle, cancellationToken);
                if (state.VehicleUpdatedAt > baseline!.VehicleUpdatedAt) return state;
                continue;
            }
            string status;
            await gate.WaitAsync(cancellationToken);
            try
            {
                await EnsureSessionAsync(false, cancellationToken);
                await EnsureCcsAsync(cancellationToken);
                using var request = GspaRequest(HttpMethod.Get, vehicle.VehicleId,
                    "status/vehicles/" + Uri.EscapeDataString(vehicle.VehicleId) + "/update-status?path=gspa/v1/remote/vehicles");
                using var response = await SendAsync(request, "command-status", cancellationToken);
                var data = await ReadJsonAsync(response, "Vehicle command result", cancellationToken);
                RequireGspaSuccess(data);
                var resultSid = TextOrNull(VehicleStateParser.At(data, "data.SID"))
                    ?? TextOrNull(VehicleStateParser.At(data, "data.svcSID"));
                status = resultSid is not null && resultSid != sid ? "WAIT" :
                    VehicleStateParser.At(data, "data.pollingState").ToString();
            }
            finally { gate.Release(); }
            if (status == "SUCCESS")
            {
                try { return await GetStateAsync(vehicle, cancellationToken); }
                catch (Exception error) when (error is HyundaiException or HttpRequestException)
                {
                    logger.LogWarning("Command completed but cached state could not be read: {FailureType}", error.GetType().Name);
                    return null;
                }
            }
            if (status is "FAILURE" or "TIMEOUT") throw new HyundaiException("Vehicle command did not complete.");
            if (status != "WAIT") throw new HyundaiException("Unknown vehicle command result; outcome is unknown.");
        }
        throw new HyundaiException("Vehicle command result timed out; outcome is unknown.");
    }

    private async Task EnsureControlTokenAsync(CancellationToken cancellationToken)
    {
        if (pinRejected) throw new HyundaiException("PIN verification was rejected; check configuration and restart before retrying.");
        if (controlToken is not null && controlExpiresAt > time.GetUtcNow().AddSeconds(30)) return;
        if (string.IsNullOrEmpty(pin)) throw new HyundaiException("Configure HYUNDAI_PIN for remote controls.");
        using var request = CciRequest(HttpMethod.Post, "v1/auth/pin", session!);
        request.Content = JsonContent.Create(new { pin });
        using var response = await SendAsync(request, "control-auth", cancellationToken);
        var data = await ReadJsonAsync(response, "Remote control authentication", cancellationToken);
        if (VehicleStateParser.At(data, "isMatched").ValueKind != JsonValueKind.True)
        {
            pinRejected = true;
            throw new HyundaiException("PIN verification was rejected; no automatic PIN retries will be made.");
        }
        var token = VehicleStateParser.At(data, "controlTokenInfo.controlToken");
        var expiry = VehicleStateParser.At(data, "controlTokenInfo.expiresTime");
        if (token.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(token.GetString()) ||
            expiry.ValueKind != JsonValueKind.Number || !expiry.TryGetInt32(out var seconds) || seconds <= 0)
            throw new HyundaiException("Unsupported control-token response.");
        controlToken = token.GetString();
        controlExpiresAt = time.GetUtcNow().AddSeconds(seconds);
    }

    private static string? TextOrNull(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void RequireGspaSuccess(JsonElement data)
    {
        if (VehicleStateParser.At(data, "metaInfo.retCode").ToString() != "S")
            throw new HyundaiException("Hyundai rejected the operation.");
    }

    private async Task EnsureCcsAsync(CancellationToken cancellationToken)
    {
        if (ccsToken is not null && ccsExpiresAt > time.GetUtcNow().AddMinutes(2)) return;
        using var request = CciRequest(HttpMethod.Post, "v1/auth/token-exchange?serviceType=CCS", session!);
        request.Content = new ByteArrayContent([]);
        using var response = await SendAsync(request, "ccs-exchange", cancellationToken);
        var data = await ReadJsonAsync(response, "Vehicle token exchange", cancellationToken);
        var access = VehicleStateParser.At(data, "accessToken");
        if (access.ValueKind != JsonValueKind.String) access = VehicleStateParser.At(data, "ccsAccessToken");
        if (access.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(access.GetString()))
            throw new HyundaiException("Hyundai vehicle token exchange returned no token.");
        var token = access.GetString()!;
        var uid = JwtClaim(token, "uid") ?? JwtClaim(session!.IdToken, "sub");
        if (string.IsNullOrWhiteSpace(uid)) throw new HyundaiException("Hyundai vehicle token has no supported user identifier.");
        var ttl = VehicleStateParser.At(data, "expiresTime");
        if (!int.TryParse(ttl.ToString(), out var seconds) || seconds <= 0)
            throw new HyundaiException("Hyundai vehicle token has an unsupported expiry schema.");
        ccsToken = token; ccsUserId = uid; ccsExpiresAt = time.GetUtcNow().AddSeconds(seconds);
        logger.LogInformation("Hyundai vehicle token exchanged");
    }

    private static string? JwtClaim(string token, string claim)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2) return null;
            using var document = JsonDocument.Parse(DecodeBase64Url(parts[1]));
            var value = VehicleStateParser.At(document.RootElement, claim);
            return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (Exception error) when (error is FormatException or JsonException) { return null; }
    }

    private async Task EnsureSessionAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        if (!loaded)
        {
            session = await store.LoadAsync(accountHash, cancellationToken);
            if (session is not null) Statistics.SessionUpdated("stored", session.ExpiresAt);
            loaded = true;
        }
        if (session is not null && !forceRefresh && !session.NeedsRefresh(time.GetUtcNow())) return;
        controlToken = null;
        HyundaiSession next;
        if (session is null)
        {
            next = await LoginAsync(Guid.NewGuid().ToString(), cancellationToken);
            await store.SaveAsync(next, cancellationToken);
            session = next;
            Statistics.SessionUpdated("login", next.ExpiresAt);
            logger.LogInformation("Authenticated with Hyundai");
            return;
        }
        try
        {
            using var request = CciRequest(HttpMethod.Post, "v2/auth/token-refresh", session);
            request.Content = JsonContent.Create(session.RefreshPayload());
            using var response = await SendAsync(request, "refresh", cancellationToken);
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
            Statistics.SessionUpdated("login", next.ExpiresAt);
            logger.LogInformation("Authenticated with Hyundai");
            return;
        }
        // Persist the rotated full set before reporting success.
        await store.SaveAsync(next, cancellationToken);
        session = next;
        Statistics.SessionUpdated("refresh", next.ExpiresAt);
        logger.LogInformation("Hyundai token refreshed");
    }

    private async Task<HyundaiSession> LoginAsync(string deviceId, CancellationToken cancellationToken)
    {
        var authorize = new Uri(Idp + "/auth/api/v2/user/oauth2/authorize?response_type=code&client_id=" +
            ClientId + "&redirect_uri=" + Uri.EscapeDataString(RedirectUri) + "&lang=en&state=ccsp&country=de");
        await AuthorizeAsync(authorize, cancellationToken);
        using var certRequest = IdpRequest(HttpMethod.Get, Idp + "/auth/api/v1/accounts/certs");
        using var certResponse = await SendAsync(certRequest, "certificate", cancellationToken);
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
        using var signinResponse = await SendAsync(signin, "signin", cancellationToken);
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
        using var exchangeResponse = await SendAsync(exchange, "exchange", cancellationToken);
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
            using var response = await SendAsync(request, "authorize", cancellationToken);
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

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string operation,
        CancellationToken cancellationToken)
    {
        if (apiNotBefore > time.GetUtcNow())
            throw new HyundaiException("Hyundai API cooldown is active.", 429, apiNotBefore - time.GetUtcNow());
        var started = time.GetTimestamp();
        int? status = null;
        string? failure = null;
        try
        {
            var response = await http.SendAsync(request, cancellationToken);
            status = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var delay = response.Headers.RetryAfter?.Delta ??
                    (response.Headers.RetryAfter?.Date - time.GetUtcNow()) ?? TimeSpan.FromMinutes(10);
                apiNotBefore = time.GetUtcNow() + (delay > TimeSpan.FromMinutes(10) ? delay : TimeSpan.FromMinutes(10));
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized) controlToken = null;
            return response;
        }
        catch (OperationCanceledException)
        {
            failure = cancellationToken.IsCancellationRequested ? "cancelled" : "timeout";
            throw;
        }
        catch (HttpRequestException) { failure = "network"; throw; }
        finally { Statistics.RequestCompleted(operation, status, time.GetElapsedTime(started), failure); }
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
