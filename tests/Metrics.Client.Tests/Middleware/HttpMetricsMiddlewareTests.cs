using System.Text;
using Canary.AspNetCore.Context;
using Metrics.Client.Extensions;
using Metrics.Client.Middleware;
using Microsoft.AspNetCore.Http;
using Prometheus;
using Shouldly;

namespace Metrics.Client.Tests.Middleware;

/// <summary>
/// Contract tests for <see cref="HttpMetricsMiddleware"/>, focused on the
/// <c>canary_http_requests_total</c> counter added for the e2e-multi-environment
/// effort. The spec: the canary counter increments ONLY for auth-validated
/// canary requests (<see cref="ICanaryRunContext.IsCanary"/> == true), NOT for
/// mere header presence, and it follows the same <c>/metrics</c> + <c>/health</c>
/// skip logic as the existing HTTP metrics.
/// </summary>
/// <remarks>
/// The middleware writes to the process-wide default Prometheus registry. To
/// stay deterministic against that shared state, every test uses a unique
/// service name as a label discriminator and asserts against the scraped
/// registry text. <see cref="AddPrometheusMetrics{TBuilder}"/> is not used —
/// the tests set <see cref="HttpMetricsMiddleware.ServiceName"/> equivalently
/// by constructing the labels directly, mirroring how the production
/// extension wires it.
/// </remarks>
public sealed class HttpMetricsMiddlewareTests
{
    private const string CanaryMetricName = "canary_http_requests_total";

    [Fact]
    public async Task Invoke_CanaryRequest_IncrementsCanaryCounter()
    {
        var serviceName = UniqueServiceName();
        HttpMetricsMiddleware.ServiceName = serviceName;
        var context = BuildContext(path: "/api/v1/templates", method: "GET", statusCode: 200);
        var canary = ActivatedCanaryContext();
        var middleware = BuildMiddleware();

        await middleware.InvokeAsync(context, canary);

        var scrape = await ScrapeAsync();
        CanaryCounterValue(scrape, serviceName, "GET", "200").ShouldBe(1);
    }

    [Fact]
    public async Task Invoke_NonCanaryRequest_DoesNotIncrementCanaryCounter()
    {
        var serviceName = UniqueServiceName();
        HttpMetricsMiddleware.ServiceName = serviceName;
        var context = BuildContext(path: "/api/v1/templates", method: "GET", statusCode: 200);
        var canary = new CanaryRunContext(); // IsCanary == false
        var middleware = BuildMiddleware();

        await middleware.InvokeAsync(context, canary);

        var scrape = await ScrapeAsync();
        // The series must not exist at all for this unique service name.
        CanaryCounterValue(scrape, serviceName, "GET", "200").ShouldBe(0);
    }

    [Fact]
    public async Task Invoke_CanaryRequest_OnMetricsPath_IsSkipped()
    {
        var serviceName = UniqueServiceName();
        HttpMetricsMiddleware.ServiceName = serviceName;
        var context = BuildContext(path: "/metrics", method: "GET", statusCode: 200);
        var canary = ActivatedCanaryContext();
        var middleware = BuildMiddleware();

        await middleware.InvokeAsync(context, canary);

        var scrape = await ScrapeAsync();
        CanaryCounterValue(scrape, serviceName, "GET", "200").ShouldBe(0);
    }

    [Fact]
    public async Task Invoke_CanaryRequest_OnHealthPath_IsSkipped()
    {
        var serviceName = UniqueServiceName();
        HttpMetricsMiddleware.ServiceName = serviceName;
        var context = BuildContext(path: "/health/live", method: "GET", statusCode: 200);
        var canary = ActivatedCanaryContext();
        var middleware = BuildMiddleware();

        await middleware.InvokeAsync(context, canary);

        var scrape = await ScrapeAsync();
        CanaryCounterValue(scrape, serviceName, "GET", "200").ShouldBe(0);
    }

    [Fact]
    public async Task Invoke_CanaryRequest_LabelsByMethodAndStatusCode()
    {
        var serviceName = UniqueServiceName();
        HttpMetricsMiddleware.ServiceName = serviceName;
        var canary = ActivatedCanaryContext();
        var middleware = BuildMiddleware();

        await middleware.InvokeAsync(
            BuildContext(path: "/api/v1/orders", method: "POST", statusCode: 201), canary);
        await middleware.InvokeAsync(
            BuildContext(path: "/api/v1/orders/9", method: "DELETE", statusCode: 404), canary);
        await middleware.InvokeAsync(
            BuildContext(path: "/api/v1/orders/1", method: "POST", statusCode: 201), canary);

        var scrape = await ScrapeAsync();
        CanaryCounterValue(scrape, serviceName, "POST", "201").ShouldBe(2);
        CanaryCounterValue(scrape, serviceName, "DELETE", "404").ShouldBe(1);
        // Endpoint is intentionally NOT a label on the canary counter — two
        // different POST paths collapse into one series, keeping cardinality
        // minimal. (The general http_requests_total DOES carry endpoint; we
        // only assert the absence of the label on canary_http_requests_total.)
        CanaryMetricLines(scrape).ShouldAllBe(line => !line.Contains("endpoint="));
    }

    [Fact]
    public async Task Invoke_CanaryRequest_StillCallsNext()
    {
        HttpMetricsMiddleware.ServiceName = UniqueServiceName();
        var nextWasCalled = false;
        var middleware = new HttpMetricsMiddleware(_ =>
        {
            nextWasCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(
            BuildContext(path: "/api/v1/x", method: "GET", statusCode: 200),
            ActivatedCanaryContext());

        nextWasCalled.ShouldBeTrue();
    }

    // ---------------------------------------------------------------------------
    // Builders + scrape helpers.
    // ---------------------------------------------------------------------------

    private static HttpMetricsMiddleware BuildMiddleware()
    {
        return new HttpMetricsMiddleware(_ => Task.CompletedTask);
    }

    private static CanaryRunContext ActivatedCanaryContext()
    {
        var ctx = new CanaryRunContext();
        ctx.Activate("a1b2c3d4-1234-5678-9abc-def012345678");
        return ctx;
    }

    private static HttpContext BuildContext(string path, string method, int statusCode)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Request.Method = method;
        ctx.Response.StatusCode = statusCode;
        return ctx;
    }

    private static string UniqueServiceName() => $"test-svc-{Guid.NewGuid():N}";

    private static async Task<string> ScrapeAsync()
    {
        using var stream = new MemoryStream();
        await Prometheus.Metrics.DefaultRegistry.CollectAndExportAsTextAsync(stream);
        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    /// <summary>
    /// Parses the scraped registry text for the
    /// <c>canary_http_requests_total</c> sample matching the given labels.
    /// Returns 0 when the series is absent.
    /// </summary>
    private static double CanaryCounterValue(
        string scrape, string service, string method, string statusCode)
    {
        foreach (var line in scrape.Split('\n'))
        {
            if (!line.StartsWith(CanaryMetricName + "{", StringComparison.Ordinal))
                continue;
            if (!line.Contains($"service=\"{service}\"", StringComparison.Ordinal))
                continue;
            if (!line.Contains($"method=\"{method}\"", StringComparison.Ordinal))
                continue;
            if (!line.Contains($"status_code=\"{statusCode}\"", StringComparison.Ordinal))
                continue;

            var value = line[(line.LastIndexOf(' ') + 1)..].Trim();
            return double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        return 0;
    }

    /// <summary>
    /// Returns the scraped sample lines for <c>canary_http_requests_total</c>
    /// (excluding HELP/TYPE comment lines).
    /// </summary>
    private static IEnumerable<string> CanaryMetricLines(string scrape)
    {
        return scrape
            .Split('\n')
            .Where(line => line.StartsWith(CanaryMetricName + "{", StringComparison.Ordinal));
    }
}
