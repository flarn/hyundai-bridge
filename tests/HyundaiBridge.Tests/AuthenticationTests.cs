using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HyundaiBridge.Hyundai;
using Microsoft.Extensions.Logging;
using Xunit;

namespace HyundaiBridge.Tests;

public sealed class AuthenticationTests
{
    [Fact]
    public async Task PasswordLoginEncryptsPasswordAndReturnsNormalizedVehicles()
    {
        using var fixture = new Fixture();
        using var rsa = RSA.Create(2048);
        var key = rsa.ExportParameters(false);
        fixture.Http.Enqueue(request =>
        {
            Assert.Contains("client_id=4f4953b5-02e1-4dbc-8599-87e983ee1be5", request.RequestUri!.Query);
            return Response("<html>Login</html>");
        });
        fixture.Http.Enqueue(_ => Response(JsonSerializer.Serialize(new { retValue = new
        {
            kid = "test-key", n = Base64Url(key.Modulus!), e = Base64Url(key.Exponent!)
        }})));
        fixture.Http.Enqueue(async request =>
        {
            Assert.Equal("/auth/account/signin", request.RequestUri!.AbsolutePath);
            var form = await request.Content!.ReadAsStringAsync();
            var fields = form.Split('&').Select(p => p.Split('=', 2))
                .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
            var decrypted = rsa.Decrypt(Convert.FromHexString(fields["password"]), RSAEncryptionPadding.Pkcs1);
            Assert.Equal("test-password", Encoding.UTF8.GetString(decrypted));
            Assert.Equal("true", fields["encryptedPassword"]);
            Assert.Equal("test-key", fields["kid"]);
            var redirect = Response("", HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("https://oneapp.hyundai.com/redirect?code=code%2Bvalue");
            return redirect;
        });
        fixture.Http.Enqueue(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("?code=code%2Bvalue", request.RequestUri!.Query);
            Assert.Equal("com.hyundai.oneapp.eu", request.Headers.GetValues("client-id").Single());
            return Response(Tokens);
        });
        fixture.Http.Enqueue(request =>
        {
            Assert.Equal("cci-access", request.Headers.Authorization!.Parameter);
            Assert.Equal("nonccs", request.Headers.GetValues("Authentication").Single());
            Assert.Equal("exch-access", request.Headers.GetValues("exchangeable-token").Single());
            return Response(Vehicles);
        });

        using var client = fixture.Client();
        var vehicles = await client.GetVehiclesAsync(CancellationToken.None);
        var vehicle = Assert.Single(vehicles);
        Assert.Equal("vehicle-a", vehicle.VehicleId);
        Assert.Equal("SANITIZED-VIN", vehicle.Vin);
        Assert.Equal("KONA Electric", vehicle.Model);
        Assert.Equal("My Hyundai", vehicle.Name);
        var saved = await fixture.Store.LoadAsync(Fixture.AccountHash, CancellationToken.None);
        Assert.NotNull(saved);
        Assert.Equal(fixture.Clock.Now.AddSeconds(3599), saved.ExpiresAt);
        var savedJson = await File.ReadAllTextAsync(Path.Combine(fixture.Directory, "session.json"));
        Assert.DoesNotContain("test-password", savedJson);
        foreach (var secret in new[] { "cci-access", "refresh-secret", "nonccs", "test-password", "code+value" })
            Assert.DoesNotContain(secret, string.Join('\n', fixture.Log.Messages));
        Assert.Equal(0, fixture.Http.Remaining);
        var status = client.Statistics.Snapshot(false);
        Assert.Equal(5, status.RequestCount);
        Assert.Equal(1, status.Logins);
        Assert.Equal("login", status.SessionSource);
        Assert.Equal(saved.ExpiresAt, status.SessionExpiresAt);
        var diagnostics = JsonSerializer.Serialize(status);
        foreach (var secret in new[] { "cci-access", "refresh-secret", "nonccs", "test-password", "code+value", "SANITIZED-VIN", "test@example.invalid" })
            Assert.DoesNotContain(secret, diagnostics);
    }

    [Fact]
    public async Task RestartReusesPersistedSessionWithoutLogin()
    {
        using var fixture = new Fixture();
        await fixture.Store.SaveAsync(fixture.Session(), CancellationToken.None);
        for (var i = 0; i < 2; i++)
        {
            fixture.Http.Enqueue(request =>
            {
                Assert.Equal("/domain/api/v1/vehicle/available-vehicles", request.RequestUri!.AbsolutePath);
                return Response(Vehicles);
            });
            using var restarted = fixture.Client();
            Assert.Single(await restarted.GetVehiclesAsync(CancellationToken.None));
            var status = restarted.Statistics.Snapshot(false);
            Assert.Equal("stored", status.SessionSource);
            Assert.Equal(0, status.Logins);
            Assert.Equal(1, status.RequestCount);
        }
        Assert.Equal(2, fixture.Http.RequestCount);
    }

    [Fact]
    public async Task ExpiringSessionRefreshesFullSetAndPersistsRotatedCookie()
    {
        using var fixture = new Fixture();
        var initial = fixture.Session(fixture.Clock.Now.AddSeconds(60));
        await fixture.Store.SaveAsync(initial, CancellationToken.None);
        fixture.Http.Enqueue(async request =>
        {
            Assert.Equal("/domain/api/v2/auth/token-refresh", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("refresh-secret", body.RootElement.GetProperty("refreshToken").GetString());
            Assert.Equal("exch-refresh", body.RootElement.GetProperty("exchangeableRefreshToken").GetString());
            Assert.Equal("nonccs-refresh", body.RootElement.GetProperty("nonCcsRefreshToken").GetString());
            Assert.Equal("id-token", body.RootElement.GetProperty("idToken").GetString());
            var result = Response(Tokens.Replace("refresh-secret", "rotated-refresh"));
            result.Headers.TryAddWithoutValidation("Set-Cookie", "t=rotated-exchangeable; Secure; HttpOnly");
            return result;
        });
        fixture.Http.Enqueue(request =>
        {
            Assert.Equal("rotated-exchangeable", request.Headers.GetValues("exchangeable-token").Single());
            return Response(Vehicles);
        });
        using var client = fixture.Client();
        Assert.Single(await client.GetVehiclesAsync(CancellationToken.None));
        var saved = await fixture.Store.LoadAsync(Fixture.AccountHash, CancellationToken.None);
        Assert.Equal("rotated-refresh", saved!.RefreshToken);
        Assert.Equal("rotated-exchangeable", saved.ExchangeableAccessToken);
        Assert.Equal(initial.DeviceId, saved.DeviceId);
        Assert.Equal(1, client.Statistics.Snapshot(false).Renewals);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task FailedRefreshDoesNotAttemptPasswordLogin(HttpStatusCode status)
    {
        using var fixture = new Fixture();
        await fixture.Store.SaveAsync(fixture.Session(fixture.Clock.Now), CancellationToken.None);
        fixture.Http.Enqueue(_ => Response("upstream text containing secret-value", status));
        using var client = fixture.Client();
        var error = await Assert.ThrowsAsync<HyundaiException>(() => client.GetVehiclesAsync(CancellationToken.None));
        Assert.Equal((int)status, error.StatusCode);
        Assert.DoesNotContain("secret-value", error.Message);
        Assert.Equal(1, fixture.Http.RequestCount);
    }

    [Fact]
    public async Task Discovery401RenewsOnceAndDoesNotLoopOnSecond401()
    {
        using var fixture = new Fixture();
        await fixture.Store.SaveAsync(fixture.Session(), CancellationToken.None);
        fixture.Http.Enqueue(_ => Response("", HttpStatusCode.Unauthorized));
        fixture.Http.Enqueue(_ => Response(Tokens));
        fixture.Http.Enqueue(_ => Response("", HttpStatusCode.Unauthorized));
        using var client = fixture.Client();
        var error = await Assert.ThrowsAsync<HyundaiException>(() => client.GetVehiclesAsync(CancellationToken.None));
        Assert.Equal(401, error.StatusCode);
        Assert.Equal(3, fixture.Http.RequestCount);
    }

    [Fact]
    public async Task Refresh4111PermitsOneFullLoginThenStopsIfItIsRejected()
    {
        using var fixture = new Fixture();
        await fixture.Store.SaveAsync(fixture.Session(fixture.Clock.Now), CancellationToken.None);
        fixture.Http.Enqueue(_ => Response("{\"code\":\"4111\"}"));
        fixture.Http.Enqueue(_ => Response("login"));
        fixture.Http.Enqueue(_ => Response("", HttpStatusCode.ServiceUnavailable));
        using var client = fixture.Client();
        var error = await Assert.ThrowsAsync<HyundaiException>(() => client.GetVehiclesAsync(CancellationToken.None));
        Assert.Equal(503, error.StatusCode);
        Assert.Equal(3, fixture.Http.RequestCount);
    }

    [Fact]
    public async Task RateLimitReturnsRetryAfterAndDoesNotRetry()
    {
        using var fixture = new Fixture();
        await fixture.Store.SaveAsync(fixture.Session(), CancellationToken.None);
        fixture.Http.Enqueue(_ =>
        {
            var response = Response("", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(TimeSpan.FromMinutes(5));
            return response;
        });
        using var client = fixture.Client();
        var error = await Assert.ThrowsAsync<HyundaiException>(() => client.GetVehiclesAsync(CancellationToken.None));
        Assert.Equal(TimeSpan.FromMinutes(5), error.RetryAfter);
        Assert.Equal(1, fixture.Http.RequestCount);
    }

    [Fact]
    public async Task WafBlockStopsBeforeSendingPassword()
    {
        using var fixture = new Fixture();
        fixture.Http.Enqueue(_ => Response("Abusing request"));
        using var client = fixture.Client();
        var error = await Assert.ThrowsAsync<HyundaiException>(() => client.GetVehiclesAsync(CancellationToken.None));
        Assert.Contains("blocked", error.Message);
        Assert.Equal(1, fixture.Http.RequestCount);
    }

    [Fact]
    public async Task AuthorizeFollowsObservedHyundaiWebRedirectBeforeFetchingCertificate()
    {
        using var fixture = new Fixture();
        fixture.Http.Enqueue(_ =>
        {
            var response = Response("", HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://prd.eu-ccapi.hyundai.com:8080/web/v1/user/authorize");
            return response;
        });
        fixture.Http.Enqueue(request =>
        {
            Assert.Equal("prd.eu-ccapi.hyundai.com", request.RequestUri!.Host);
            Assert.Equal(8080, request.RequestUri.Port);
            return Response("login");
        });
        fixture.Http.Enqueue(request =>
        {
            Assert.Equal("/auth/api/v1/accounts/certs", request.RequestUri!.AbsolutePath);
            return Response("[]");
        });
        using var client = fixture.Client();
        var error = await Assert.ThrowsAsync<HyundaiException>(() => client.GetVehiclesAsync(CancellationToken.None));
        Assert.Contains("certificate", error.Message);
        Assert.Equal(3, fixture.Http.RequestCount);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"retValue\":{\"kid\":\"test\",\"n\":null,\"e\":\"AQAB\"}}")]
    public async Task ChangedCertificateSchemaFailsCleanlyBeforePasswordSubmission(string certificate)
    {
        using var fixture = new Fixture();
        fixture.Http.Enqueue(_ => Response("login"));
        fixture.Http.Enqueue(_ => Response(certificate));
        using var client = fixture.Client();
        var error = await Assert.ThrowsAsync<HyundaiException>(() => client.GetVehiclesAsync(CancellationToken.None));
        Assert.Contains("certificate", error.Message);
        Assert.Equal(2, fixture.Http.RequestCount);
    }

    [Fact]
    public async Task ConcurrentDiscoveryDoesNotDuplicateTokenRefresh()
    {
        using var fixture = new Fixture();
        await fixture.Store.SaveAsync(fixture.Session(fixture.Clock.Now), CancellationToken.None);
        fixture.Http.Enqueue(_ => Response(Tokens));
        fixture.Http.Enqueue(_ => Response(Vehicles));
        fixture.Http.Enqueue(_ => Response(Vehicles));
        using var client = fixture.Client();
        await Task.WhenAll(client.GetVehiclesAsync(CancellationToken.None), client.GetVehiclesAsync(CancellationToken.None));
        Assert.Equal(3, fixture.Http.RequestCount);
    }

    [Fact]
    public async Task MalformedRefreshDoesNotFallBackToPasswordLoginOrReplaceSession()
    {
        using var fixture = new Fixture();
        var original = fixture.Session(fixture.Clock.Now);
        await fixture.Store.SaveAsync(original, CancellationToken.None);
        fixture.Http.Enqueue(_ => Response("{\"accessToken\":\"secret-content\",\"expiresIn\":\"changed\"}"));
        using var client = fixture.Client();
        var error = await Assert.ThrowsAsync<HyundaiException>(() => client.GetVehiclesAsync(CancellationToken.None));
        Assert.DoesNotContain("secret-content", error.Message);
        Assert.Equal(1, fixture.Http.RequestCount);
        var saved = await fixture.Store.LoadAsync(Fixture.AccountHash, CancellationToken.None);
        Assert.Equal(original.AccessToken, saved!.AccessToken);
    }

    [Fact]
    public async Task NetworkFailureDuringRefreshDoesNotAttemptPasswordLogin()
    {
        using var fixture = new Fixture();
        await fixture.Store.SaveAsync(fixture.Session(fixture.Clock.Now), CancellationToken.None);
        fixture.Http.Enqueue(_ => Task.FromException<HttpResponseMessage>(new HttpRequestException("upstream unavailable")));
        using var client = fixture.Client();
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetVehiclesAsync(CancellationToken.None));
        Assert.Equal(1, fixture.Http.RequestCount);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(119, true)]
    [InlineData(120, true)]
    [InlineData(121, false)]
    public void ExpiryDecisionUsesCciLifetimeAndMargin(int seconds, bool expected)
    {
        using var fixture = new Fixture();
        Assert.Equal(expected, fixture.Session(fixture.Clock.Now.AddSeconds(seconds)).NeedsRefresh(fixture.Clock.Now));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"expiresIn\":0}")]
    [InlineData("{\"expiresIn\":\"3599\"}")]
    public void ChangedTokenSchemaFailsExplicitly(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Throws<HyundaiException>(() => HyundaiSession.Parse(doc.RootElement, "account", "device", DateTimeOffset.UtcNow));
    }

    internal const string Tokens = """
        {"accessToken":"cci-access","refreshToken":"refresh-secret","nonCcsToken":"nonccs",
         "exchangeableAccessToken":"exch-access","exchangeableRefreshToken":"exch-refresh",
         "nonCcsRefreshToken":"nonccs-refresh","idToken":"id-token","expiresIn":3599}
        """;
    internal const string Vehicles = """
        {"contents":[{"ccspCarId":"vehicle-a","vin":"SANITIZED-VIN",
        "vehicleNameView":"My Hyundai","vehicleModelName":"KONA Electric"}]}
        """;
    internal static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body) };
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

internal sealed class Fixture : IDisposable
{
    internal static string AccountHash => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("test@example.invalid")));
    internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "hyundai-tests-" + Guid.NewGuid().ToString("N"));
    internal ScriptedHttp Http { get; } = new();
    internal FixedClock Clock { get; } = new();
    internal CapturedLog Log { get; } = new();
    internal SessionStore Store { get; }
    private readonly HttpClient transport;

    internal Fixture()
    {
        Store = new SessionStore(Directory);
        transport = new HttpClient(Http);
    }
    internal HyundaiClient Client() => new(transport, Store, "test@example.invalid", "test-password", Log, Clock);
    internal HyundaiSession Session(DateTimeOffset? expires = null)
    {
        using var document = JsonDocument.Parse(AuthenticationTests.Tokens);
        return HyundaiSession.Parse(document.RootElement, AccountHash, "device-uuid",
            (expires ?? Clock.Now.AddHours(1)).AddSeconds(-3599));
    }
    public void Dispose()
    {
        transport.Dispose();
        if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
    }
}

internal sealed class FixedClock : TimeProvider
{
    internal DateTimeOffset Now { get; set; } = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class ScriptedHttp : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>> steps = new();
    internal int RequestCount { get; private set; }
    internal int Remaining => steps.Count;
    internal void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> step) => steps.Enqueue(r => Task.FromResult(step(r)));
    internal void Enqueue(Func<HttpRequestMessage, Task<HttpResponseMessage>> step) => steps.Enqueue(step);
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequestCount++;
        Assert.NotEmpty(steps);
        return steps.Dequeue()(request);
    }
}

internal sealed class CapturedLog : ILogger<HyundaiClient>
{
    internal List<string> Messages { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => true;
    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        => Messages.Add(formatter(state, error));
}
