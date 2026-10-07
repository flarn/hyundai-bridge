using System.Net;
using System.Text.Json;
using HyundaiBridge.Hosting;
using HyundaiBridge.Hyundai;
using HyundaiBridge.Mqtt;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using var logs = LoggerFactory.Create(builder => builder.AddJsonConsole(options =>
{
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
    options.UseUtcTimestamp = true;
}).AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace));
var logger = logs.CreateLogger("HyundaiBridge");
if (args.Length > 1 || (args.Length == 1 && args[0] is not ("--discover" or "--serve")))
{
    logger.LogError("Usage: HyundaiBridge [--serve|--discover]");
    return 2;
}
string? username, password;
BridgeOptions? bridgeOptions = null;
var discovery = args.Length == 1 && args[0] == "--discover";
try
{
    username = BridgeOptions.Secret("HYUNDAI_USERNAME");
    password = BridgeOptions.Secret("HYUNDAI_PASSWORD");
    if (!discovery) bridgeOptions = BridgeOptions.FromEnvironment();
}
catch (Exception error) when (error is FormatException or IOException or UnauthorizedAccessException)
{
    logger.LogError("Invalid bridge configuration: {FailureType}; check environment and secret files", error.GetType().Name);
    return 2;
}
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
    if (!discovery)
    {
        if (OperatingSystem.IsWindows()) throw new IOException("Bridge storage requires macOS or Linux");
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var lease = new FileStream(Path.Combine(directory, "bridge.lock"), new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
        var journal = new CommandJournal(directory);
        await journal.LoadAsync(stopping.Token);
        using var backendGate = new SemaphoreSlim(1, 1);
        var processor = new CommandProcessor(journal, TimeProvider.System, backendGate, logs.CreateLogger<CommandProcessor>());
        using var mqtt = new MqttBridge(bridgeOptions!, processor, logs.CreateLogger<MqttBridge>(), TimeProvider.System);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
            builder.WebHost.UseUrls("http://127.0.0.1:8080");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<ILoggerFactory>(logs);
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(15));
        builder.Services.AddSingleton<IHostedService>(new BridgeWorker(mqtt, client.GetBridgeSnapshotAsync,
            backendGate, TimeProvider.System, logs.CreateLogger<BridgeWorker>(), client.Statistics, client.GetStateAsync));
        await using var host = builder.Build();
        StatusPage.Map(host, client.Statistics, () => mqtt.IsConnected);
        await host.RunAsync(stopping.Token);
        return 0;
    }
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
