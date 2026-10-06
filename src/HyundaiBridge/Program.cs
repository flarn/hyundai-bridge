using System.Net;
using System.Text.Json;
using HyundaiBridge.Hyundai;
using Microsoft.Extensions.Logging;

using var logs = LoggerFactory.Create(builder => builder.AddJsonConsole(options =>
{
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
    options.UseUtcTimestamp = true;
}).AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace));
var logger = logs.CreateLogger("HyundaiBridge");
if (args.Length != 1 || args[0] != "--discover")
{
    logger.LogError("Usage: HyundaiBridge --discover");
    return 2;
}
var username = Environment.GetEnvironmentVariable("HYUNDAI_USERNAME");
var password = Environment.GetEnvironmentVariable("HYUNDAI_PASSWORD");
var directory = Environment.GetEnvironmentVariable("HYUNDAI_SESSION_DIRECTORY");
var region = Environment.GetEnvironmentVariable("HYUNDAI_REGION") ?? "EU";
if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password) ||
    string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory) ||
    !string.Equals(region, "EU", StringComparison.OrdinalIgnoreCase))
{
    logger.LogError("Set HYUNDAI_USERNAME, HYUNDAI_PASSWORD, HYUNDAI_REGION=EU and an absolute HYUNDAI_SESSION_DIRECTORY");
    return 2;
}
using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };
using var handler = new HttpClientHandler
{
    AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer()
};
using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(35) };
using var client = new HyundaiClient(http, new SessionStore(directory), username, password,
    logs.CreateLogger<HyundaiClient>(), TimeProvider.System);
try
{
    var vehicles = await client.GetVehiclesAsync(stopping.Token);
    Console.WriteLine(JsonSerializer.Serialize(vehicles, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    return vehicles.Count == 0 ? 4 : 0;
}
catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return 130; }
catch (HyundaiException error)
{
    logger.LogError("Hyundai operation failed: {Reason}; HTTP {StatusCode}; retry after {RetryAfter}",
        error.Message, error.StatusCode, error.RetryAfter);
    return 3;
}
catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IOException or
    UnauthorizedAccessException or FormatException)
{
    // Exception messages/inner exceptions can contain request URLs with auth codes.
    logger.LogError("Hyundai operation failed: {FailureType}; check network connectivity and private session storage", error.GetType().Name);
    return 3;
}
