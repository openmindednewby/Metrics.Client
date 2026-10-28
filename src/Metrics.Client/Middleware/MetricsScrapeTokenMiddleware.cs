using System.Security.Cryptography;
using System.Text;
using Metrics.Client.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Metrics.Client.Middleware;

internal sealed class MetricsScrapeTokenMiddleware
{
    private const string BearerPrefix = "Bearer ";

    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<MetricsScrapeOptions> _options;
    private readonly ILogger<MetricsScrapeTokenMiddleware> _logger;
    private readonly PathString _metricsPath;

    public MetricsScrapeTokenMiddleware(
        RequestDelegate next,
        IOptionsMonitor<MetricsScrapeOptions> options,
        ILogger<MetricsScrapeTokenMiddleware> logger,
        string metricsPath)
    {
        _next = next;
        _options = options;
        _logger = logger;
        _metricsPath = new PathString(metricsPath);
    }

    public Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments(_metricsPath, StringComparison.OrdinalIgnoreCase))
            return _next(context);

        var expected = _options.CurrentValue.Token;
        if (string.IsNullOrWhiteSpace(expected))
        {
            _logger.LogWarning("Rejected a metrics scrape: no MetricsScrape:Token is configured");
            return Reject(context);
        }

        var presented = ReadBearerToken(context);
        return presented is not null && TokensMatch(presented, expected) ? _next(context) : Reject(context);
    }

    private static string? ReadBearerToken(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var token = header[BearerPrefix.Length..].Trim();
        return token.Length == 0 ? null : token;
    }

    private static bool TokensMatch(string presented, string expected) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(expected));

    private static Task Reject(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }
}
