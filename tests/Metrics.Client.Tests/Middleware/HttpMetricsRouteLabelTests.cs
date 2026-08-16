using System.Text;
using Metrics.Client.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Metrics.Client.Tests.Middleware;

/// <summary>
/// Contract tests for the BOUNDEDNESS of the <c>http_route</c> label.
/// </summary>
/// <remarks>
/// <para>
/// These exist because the previous implementation fell back to the raw request
/// path whenever a request matched no route, which made the label unbounded. That
/// was not caught by the existing suite: every test asserted the middleware ran and
/// that labels were PRESENT, never that the label set stayed FINITE. Live traffic
/// had already leaked GUID-expanded paths into Prometheus as a result.
/// </para>
/// <para>
/// <see cref="ManyDistinctUnmatchedPaths_CollapseToASingleSeries"/> is the test that
/// encodes the actual defect — it fails loudly against the old behaviour and is the
/// reason this file exists.
/// </para>
/// <para>
/// The middleware writes to the process-wide default Prometheus registry, so every
/// test uses a unique service name as a label discriminator and asserts against the
/// scraped registry text.
/// </para>
/// </remarks>
[Collection(PrometheusRegistryCollection.Name)]
public sealed class HttpMetricsRouteLabelTests
{
    private const string RequestsMetricName = "http_requests_total";
    private const string UnmatchedMetricName = "http_unmatched_requests_total";

    [Fact]
    public async Task MatchedRoute_RecordsTheTemplate_NotTheExpandedPath()
    {
        var app = UniqueAppName();
        HttpMetricsMiddleware.ServiceName = app;
        var middleware = BuildMiddleware();

        await middleware.InvokeAsync(BuildContext(
            path: "/api/v1/orders/e62a3fe4-88e9-431b-8f9e-2270314049a3",
            routeTemplate: "/api/v1/orders/{id}"));

        var lines = MetricLines(await ScrapeAsync(), RequestsMetricName, app);
        lines.ShouldContain(line => line.Contains("http_route=\"/api/v1/orders/{id}\""));
        lines.ShouldAllBe(line => !line.Contains("e62a3fe4"));
    }

    [Fact]
    public async Task UnmatchedRequest_RecordsTheUnmatchedLiteral_NotTheRawPath()
    {
        var app = UniqueAppName();
        HttpMetricsMiddleware.ServiceName = app;
        var middleware = BuildMiddleware();

        await middleware.InvokeAsync(BuildContext(path: "/wp-admin/setup-config.php"));

        var lines = MetricLines(await ScrapeAsync(), RequestsMetricName, app);
        lines.ShouldContain(line => line.Contains("http_route=\"unmatched\""));
        lines.ShouldAllBe(line => !line.Contains("wp-admin"));
    }

    /// <summary>
    /// The regression test for the leak. Under the old raw-path fallback each of
    /// these URLs minted its own series; the label must now be finite regardless of
    /// how many distinct unmatched URLs arrive.
    /// </summary>
    [Fact]
    public async Task ManyDistinctUnmatchedPaths_CollapseToASingleSeries()
    {
        const int DistinctPaths = 200;
        var app = UniqueAppName();
        HttpMetricsMiddleware.ServiceName = app;
        var middleware = BuildMiddleware();

        for (var i = 0; i < DistinctPaths; i++)
            await middleware.InvokeAsync(BuildContext(path: $"/scan/{Guid.NewGuid():N}"));

        var lines = MetricLines(await ScrapeAsync(), RequestsMetricName, app);
        lines.Count.ShouldBe(1);
        lines[0].ShouldContain("http_route=\"unmatched\"");
    }

    [Fact]
    public async Task UnmatchedRequest_IncrementsTheUnmatchedCounter()
    {
        var app = UniqueAppName();
        HttpMetricsMiddleware.ServiceName = app;
        var middleware = BuildMiddleware();

        await middleware.InvokeAsync(BuildContext(path: "/nope"));
        await middleware.InvokeAsync(BuildContext(path: "/also-nope"));

        var scrape = await ScrapeAsync();
        CounterValue(scrape, UnmatchedMetricName, app, "GET").ShouldBe(2);
    }

    [Fact]
    public async Task MatchedRequest_DoesNotIncrementTheUnmatchedCounter()
    {
        var app = UniqueAppName();
        HttpMetricsMiddleware.ServiceName = app;
        var middleware = BuildMiddleware();

        await middleware.InvokeAsync(BuildContext(
            path: "/api/v1/orders/7", routeTemplate: "/api/v1/orders/{id}"));

        var scrape = await ScrapeAsync();
        CounterValue(scrape, UnmatchedMetricName, app, "GET").ShouldBe(0);
    }

    /// <summary>
    /// The unmatched counter must never carry a route label — that would reintroduce
    /// exactly the unbounded growth it exists to avoid.
    /// </summary>
    [Fact]
    public async Task UnmatchedCounter_CarriesNoRouteLabel()
    {
        var app = UniqueAppName();
        HttpMetricsMiddleware.ServiceName = app;
        var middleware = BuildMiddleware();

        await middleware.InvokeAsync(BuildContext(path: "/some/unmatched/path"));

        var lines = MetricLines(await ScrapeAsync(), UnmatchedMetricName, app);
        lines.ShouldNotBeEmpty();
        lines.ShouldAllBe(line => !line.Contains("http_route="));
    }

    // ---------------------------------------------------------------------------
    // Builders + scrape helpers.
    // ---------------------------------------------------------------------------

    private static HttpMetricsMiddleware BuildMiddleware() => new(_ => Task.CompletedTask);

    /// <summary>
    /// Builds a request context. When <paramref name="routeTemplate"/> is supplied
    /// the context carries a matched <see cref="RouteEndpoint"/> (as routing would
    /// have set); when it is null the request matched nothing, which is the 404 case.
    /// </summary>
    private static HttpContext BuildContext(
        string path, string? routeTemplate = null, string method = "GET", int statusCode = 404)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Request.Method = method;
        ctx.Response.StatusCode = statusCode;
        ctx.RequestServices = new ServiceCollection().BuildServiceProvider();

        if (routeTemplate is not null)
        {
            ctx.SetEndpoint(new RouteEndpoint(
                _ => Task.CompletedTask,
                RoutePatternFactory.Parse(routeTemplate),
                order: 0,
                new EndpointMetadataCollection(),
                displayName: routeTemplate));
        }

        return ctx;
    }

    private static string UniqueAppName() => $"test-svc-{Guid.NewGuid():N}";

    private static async Task<string> ScrapeAsync()
    {
        using var stream = new MemoryStream();
        await Prometheus.Metrics.DefaultRegistry.CollectAndExportAsTextAsync(stream);
        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    /// <summary>
    /// Returns the scraped sample lines for the given metric belonging to the given
    /// app label (excluding HELP/TYPE comment lines).
    /// </summary>
    private static List<string> MetricLines(string scrape, string metricName, string app)
    {
        return scrape
            .Split('\n')
            .Where(line => line.StartsWith(metricName + "{", StringComparison.Ordinal))
            .Where(line => line.Contains($"app=\"{app}\"", StringComparison.Ordinal))
            .ToList();
    }

    private static double CounterValue(string scrape, string metricName, string app, string method)
    {
        foreach (var line in MetricLines(scrape, metricName, app))
        {
            if (!line.Contains($"method=\"{method}\"", StringComparison.Ordinal))
                continue;

            var value = line[(line.LastIndexOf(' ') + 1)..].Trim();
            return double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        return 0;
    }
}
