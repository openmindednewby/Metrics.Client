using System.Text;
using Metrics.Client.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Metrics.Client.Tests.Middleware;

/// <summary>
/// Contract tests for WHERE <c>UsePrometheusMetrics()</c> must sit in the
/// middleware pipeline.
/// </summary>
/// <remarks>
/// <para>
/// THE DEFECT (found live in production, 2026-08). Nine of eleven backend
/// services registered <c>app.UsePrometheusMetrics()</c> BELOW
/// <c>app.UseAuthentication()</c> / <c>app.UseAuthorization()</c> and below
/// <c>app.UseRateLimiter()</c>. ASP.NET Core middleware is a NESTED pipeline,
/// not a list of observers: a request short-circuited by auth (401/403) or by
/// the rate limiter (429) never reaches anything registered below it. So every
/// 401, 403 and 429 across those nine services went completely uncounted —
/// precisely the requests you most want to see.
/// </para>
/// <para>
/// Nothing looked wrong. The package was referenced, <c>/metrics</c> returned
/// 200, the Prometheus target read <c>up</c>, and the dashboards rendered. The
/// only symptom was an absence: <c>scrape_samples_scraped{job="agora-api"}</c>
/// sat at 0 while the kefi-api control read 1656, during a window in which nine
/// real 401 requests hit the Agora pod.
/// </para>
/// <para>
/// WHY A REAL PIPELINE. These tests build an <see cref="ApplicationBuilder"/>
/// via <c>UseMiddleware</c>, exactly as
/// <see cref="HttpMetricsMiddlewarePipelineTests"/> does, because ordering is a
/// property of the pipeline and cannot exist in a test that calls
/// <c>InvokeAsync</c> directly on a hand-constructed context. A direct-invoke
/// test records the request unconditionally and would pass against both the
/// correct and the broken arrangement — which is how the defect survived a
/// green suite in the first place.
/// </para>
/// <para>
/// WHAT THEY LOCK DOWN. Each test builds the SAME pipeline twice, differing in
/// nothing but the position of the metrics middleware relative to a
/// short-circuiting one, and asserts the resulting registry contents diverge.
/// The pair is the point: the "above" case alone would still pass if the
/// middleware simply recorded everything, and the "below" case alone could be
/// silent because the harness is broken rather than because ordering matters.
/// <see cref="MetricsBelowShortCircuit_WhenRequestIsNotRejected_StillRecordsIt"/>
/// is the control that rules the second possibility out.
/// </para>
/// <para>
/// The middleware writes to the process-wide
/// <c>Prometheus.Metrics.DefaultRegistry</c> and reads a STATIC
/// <c>HttpMetricsMiddleware.ServiceName</c>, so this class joins
/// <see cref="PrometheusRegistryCollection"/> (serializing it against the other
/// middleware test classes) and uses a unique app name per pipeline as a label
/// discriminator.
/// </para>
/// </remarks>
[Collection(PrometheusRegistryCollection.Name)]
public sealed class HttpMetricsMiddlewareOrderingTests
{
    private const string RequestsMetricName = "http_requests_total";
    private const string DurationMetricName = "http_request_duration_seconds";

    private const int Unauthorized = StatusCodes.Status401Unauthorized;
    private const int Forbidden = StatusCodes.Status403Forbidden;
    private const int TooManyRequests = StatusCodes.Status429TooManyRequests;

    // -----------------------------------------------------------------------
    // The core contract: above records the rejection, below records nothing.
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(Unauthorized)]   // UseAuthentication() rejects
    [InlineData(Forbidden)]      // UseAuthorization() rejects
    [InlineData(TooManyRequests)] // UseRateLimiter() rejects
    public async Task MetricsAboveShortCircuit_WhenRequestIsRejected_RecordsItWithThatStatus(
        int rejectionStatus)
    {
        var app = UniqueAppName();

        await RunPipelineAsync(app, metricsFirst: true, rejectWith: rejectionStatus);

        var lines = await MetricLinesAsync(RequestsMetricName, app);

        lines.ShouldNotBeEmpty(
            $"metrics registered ABOVE the middleware that returns {rejectionStatus} " +
            "must observe the rejected request");
        lines.ShouldContain(line => line.Contains($"status_code=\"{rejectionStatus}\""));
    }

    [Theory]
    [InlineData(Unauthorized)]
    [InlineData(Forbidden)]
    [InlineData(TooManyRequests)]
    public async Task MetricsBelowShortCircuit_WhenRequestIsRejected_RecordsNothing(
        int rejectionStatus)
    {
        var app = UniqueAppName();

        await RunPipelineAsync(app, metricsFirst: false, rejectWith: rejectionStatus);

        // This is the production defect, encoded. The middleware is registered,
        // the package is referenced, the pipeline runs — and the request leaves
        // no trace whatsoever, because it was short-circuited above.
        (await MetricLinesAsync(RequestsMetricName, app)).ShouldBeEmpty(
            $"a request rejected with {rejectionStatus} never reaches metrics " +
            "registered below the rejecting middleware — this is the defect");
        (await MetricLinesAsync(DurationMetricName, app)).ShouldBeEmpty();
    }

    // -----------------------------------------------------------------------
    // The control. Without this, "below records nothing" is not evidence of
    // anything: a broken harness would produce the same silence.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task MetricsBelowShortCircuit_WhenRequestIsNotRejected_StillRecordsIt()
    {
        var app = UniqueAppName();

        // Identical pipeline, identical (below) position — only the gate lets
        // the request through this time.
        await RunPipelineAsync(app, metricsFirst: false, rejectWith: null);

        var lines = await MetricLinesAsync(RequestsMetricName, app);

        lines.ShouldNotBeEmpty(
            "the below-registered middleware IS wired and DOES record requests that " +
            "reach it; the silence in the rejection tests is caused by ordering, not " +
            "by a broken pipeline");
        lines.ShouldContain(line => line.Contains("status_code=\"200\""));
    }

    // -----------------------------------------------------------------------
    // The side-by-side statement of why ordering matters. Same pipeline shape,
    // same request, same rejection — the ONLY difference is registration order.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SamePipeline_DifferingOnlyInMetricsPosition_ProducesDivergentMetrics()
    {
        var above = UniqueAppName();
        var below = UniqueAppName();

        await RunPipelineAsync(above, metricsFirst: true, rejectWith: Unauthorized);
        await RunPipelineAsync(below, metricsFirst: false, rejectWith: Unauthorized);

        var scrape = await ScrapeAsync();

        MetricLines(scrape, RequestsMetricName, above)
            .ShouldContain(line => line.Contains("status_code=\"401\""));

        MetricLines(scrape, RequestsMetricName, below)
            .ShouldBeEmpty(
                "one line of registration order is the entire difference between " +
                "seeing every 401 and seeing none of them");
    }

    /// <summary>
    /// Auth rejects some requests and lets others through. Registered below, the
    /// metrics do not merely lose volume — they report a 100% success rate for a
    /// service that is rejecting half its traffic. Silent under-counting is worse
    /// than no metric at all, because the dashboard looks healthy.
    /// </summary>
    [Fact]
    public async Task MetricsBelowShortCircuit_WithMixedTraffic_ReportsOnlyTheSuccesses()
    {
        var app = UniqueAppName();

        await RunPipelineAsync(app, metricsFirst: false, rejectWith: Unauthorized);
        await RunPipelineAsync(app, metricsFirst: false, rejectWith: null);

        var lines = await MetricLinesAsync(RequestsMetricName, app);

        lines.ShouldContain(line => line.Contains("status_code=\"200\""));
        lines.ShouldAllBe(line => !line.Contains("status_code=\"401\""));
    }

    // -----------------------------------------------------------------------
    // Builders + scrape helpers.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds and runs a real middleware pipeline of the shape every service has:
    /// a metrics middleware and a middleware that can short-circuit the request
    /// (standing in for <c>UseAuthentication</c>/<c>UseAuthorization</c>/
    /// <c>UseRateLimiter</c>), with only their relative order varying.
    /// </summary>
    /// <param name="appName">Unique service name, used as the <c>app</c> label.</param>
    /// <param name="metricsFirst">
    /// <c>true</c> registers metrics ABOVE the gate (correct); <c>false</c>
    /// registers it BELOW (the production defect).
    /// </param>
    /// <param name="rejectWith">
    /// Status the gate short-circuits with, or <c>null</c> to let the request through.
    /// </param>
    private static async Task RunPipelineAsync(string appName, bool metricsFirst, int? rejectWith)
    {
        HttpMetricsMiddleware.ServiceName = appName;

        using var provider = new ServiceCollection().BuildServiceProvider();
        var builder = new ApplicationBuilder(provider);

        if (metricsFirst)
        {
            builder.UseMiddleware<HttpMetricsMiddleware>();
            UseShortCircuitGate(builder, rejectWith);
        }
        else
        {
            UseShortCircuitGate(builder, rejectWith);
            builder.UseMiddleware<HttpMetricsMiddleware>();
        }

        builder.Run(ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });

        await builder.Build()(BuildContext(provider));
    }

    /// <summary>
    /// Stands in for the real gates. The behaviour that matters is the one they
    /// all share: on rejection they set a status and RETURN WITHOUT CALLING NEXT,
    /// so everything below them in the pipeline is skipped entirely.
    /// </summary>
    private static void UseShortCircuitGate(IApplicationBuilder builder, int? rejectWith)
    {
        builder.Use(async (ctx, next) =>
        {
            if (rejectWith.HasValue)
            {
                ctx.Response.StatusCode = rejectWith.Value;
                return;
            }

            await next(ctx);
        });
    }

    private static HttpContext BuildContext(IServiceProvider requestServices)
    {
        var ctx = new DefaultHttpContext { RequestServices = requestServices };
        ctx.Request.Path = "/api/v1/templates";
        ctx.Request.Method = "GET";
        return ctx;
    }

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

    private static async Task<List<string>> MetricLinesAsync(string metricName, string app)
        => MetricLines(await ScrapeAsync(), metricName, app);

    private static string UniqueAppName() => $"test-svc-{Guid.NewGuid():N}";
}
