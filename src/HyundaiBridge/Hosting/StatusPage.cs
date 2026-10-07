using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace HyundaiBridge.Hosting;

internal static class StatusPage
{
    internal static void Map(WebApplication app, BridgeStatistics statistics, Func<bool> mqttConnected)
    {
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.ContentSecurityPolicy =
                "default-src 'none'; style-src 'self'; script-src 'self'; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            await next(context);
        });
        app.MapGet("/", () => Results.Content(Asset("index.html"), "text/html; charset=utf-8"));
        app.MapGet("/app.css", () => Results.Content(Asset("app.css"), "text/css; charset=utf-8"));
        app.MapGet("/app.js", () => Results.Content(Asset("app.js"), "text/javascript; charset=utf-8"));
        app.MapGet("/api/status", () => Results.Json(statistics.Snapshot(mqttConnected())));
    }

    private static string Asset(string name)
    {
        using var stream = typeof(StatusPage).Assembly.GetManifestResourceStream("HyundaiBridge.Hosting.Web." + name)
            ?? throw new InvalidOperationException("Missing status page asset");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
