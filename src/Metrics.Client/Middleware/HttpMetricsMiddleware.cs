using System.Diagnostics;
using Canary.AspNetCore.Context;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Prometheus;

namespace Metrics.Client.Middleware;

/// <summary>
/// ASP.NET Core middleware that records Prometheus HTTP metrics for every request.
/// Tracks request count, duration histogram, and active (in-flight) requests.
/// </summary>
public sealed class HttpMetricsMiddleware
{
    private readonly RequestDelegate _next;

    /// <summary>
    /// The <c>http_route</c> label value used for any request that did not resolve
    /// to a <see cref="RouteEndpoint"/> — 404s, and anything short-circuited before
    /// routing completed.
    /// </summary>
    /// <remarks>
    /// This constant is the cardinality cap. The previous implementation fell back
    /// to <c>HttpContext.Request.Path</c>, which is the RAW, fully-expanded URL. That
    /// made the label unbounded: every unmatched URL minted a permanent series, and
    /// each one costs ~14 (this counter + 11 histogram buckets + sum + count). It was
    /// not theoretical — live traffic had already leaked GUID-expanded paths such as
    /// <c>/api/v1/admin/attendees/888acadc-…</c>, growing with every attendee created,
    /// and a bot scanning 10k URLs would have added ~140k series to a prod Prometheus
    /// agent that has already OOMKilled once at its 640Mi limit.
    /// The 404 signal itself is not lost — see <see cref="HttpUnmatchedRequestsTotal"/>.
    /// </remarks>
    private const string UnmatchedRoute = "unmatched";

    private static readonly Counter HttpRequestsTotal = Prometheus.Metrics.CreateCounter(
        "http_requests_total",
        "Total number of HTTP requests processed.",
        new CounterConfiguration
        {
            LabelNames = ["app", "method", "http_route", "status_code"]
        });

    /// <summary>
    /// Counts requests that matched no route, WITHOUT recording which URL was asked
    /// for. Deliberately low-cardinality (no route label) so it can never grow: it
    /// preserves the one operationally useful signal from the raw-path fallback —
    /// a 404-rate spike, which is how you notice a client calling a route you renamed
    /// in a deploy — at a fixed cost of one series per (app, method).
    /// </summary>
    private static readonly Counter HttpUnmatchedRequestsTotal = Prometheus.Metrics.CreateCounter(
        "http_unmatched_requests_total",
        "Total number of HTTP requests that did not match any route.",
        new CounterConfiguration
        {
            LabelNames = ["app", "method"]
        });

    /// <summary>
    /// Separate, low-cardinality counter for in-cluster E2E canary traffic.
    /// Deliberately a SEPARATE series (not a <c>canary</c> label on
    /// <see cref="HttpRequestsTotal"/>) to avoid doubling that metric's series,
    /// and deliberately without an <c>endpoint</c> label to keep cardinality
    /// minimal. Incremented ONLY when <see cref="ICanaryRunContext.IsCanary"/>
    /// is true — i.e. the request carried the canary header AND a valid
    /// superUser JWT. Header presence alone never increments it.
    /// Powers the Grafana "Canary Activity" dashboard and lets SLO dashboards
    /// default-exclude canary noise via <c>http_requests_total - on(...) canary_http_requests_total</c>.
    /// </summary>
    private static readonly Counter CanaryHttpRequestsTotal = Prometheus.Metrics.CreateCounter(
        "canary_http_requests_total",
        "Total number of HTTP requests processed that were tagged as in-cluster E2E canary traffic (auth-validated X-Canary-Run-Id).",
        new CounterConfiguration
        {
            LabelNames = ["app", "method", "status_code"]
        });

    private static readonly Histogram HttpRequestDurationSeconds = Prometheus.Metrics.CreateHistogram(
        "http_request_duration_seconds",
        "Duration of HTTP requests in seconds.",
        new HistogramConfiguration
        {
            LabelNames = ["app", "method", "http_route", "status_code"],
            Buckets = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10]
        });

    private static readonly Gauge HttpRequestsInFlight = Prometheus.Metrics.CreateGauge(
        "http_requests_in_flight",
        "Number of HTTP requests currently being processed.",
        new GaugeConfiguration
        {
            LabelNames = ["app"]
        });

    /// <summary>
    /// The service name used as label value. Set during registration.
    /// </summary>
    internal static string ServiceName { get; set; } = "unknown";

    /// <summary>
    /// Initializes a new instance of the <see cref="HttpMetricsMiddleware"/> class.
    /// </summary>
    public HttpMetricsMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    /// <summary>
    /// Processes the HTTP request, recording metrics before and after execution.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <remarks>
    /// <para>
    /// <see cref="ICanaryRunContext"/> is resolved from
    /// <see cref="HttpContext.RequestServices"/> INSIDE this method rather than
    /// declared as a DI-injected <c>InvokeAsync</c> parameter. ASP.NET Core
    /// resolves such parameters with <c>GetRequiredService</c> BEFORE the method
    /// body runs, which made <c>Canary.AspNetCore</c> a hard, unstated
    /// requirement of this package: a service that called
    /// <c>AddPrometheusMetrics()</c> without also calling <c>AddCanaryAuth()</c>
    /// threw <see cref="InvalidOperationException"/> on EVERY request — including
    /// <c>/health/live</c> and <c>/metrics</c>, because the skip logic below sits
    /// after the resolution point. Resolving here, null-tolerantly and after the
    /// skips, makes the canary integration genuinely optional.
    /// </para>
    /// <para>
    /// For services that DO call <c>AddCanaryAuth()</c> the behaviour is
    /// unchanged: parameter injection resolved from this same
    /// <see cref="HttpContext.RequestServices"/> scope, so the instance obtained
    /// here is the identical scoped <c>CanaryRunContext</c> the middleware
    /// previously received. It is populated by <c>CanaryAuthMiddleware</c>, which
    /// runs INSIDE this middleware (<c>UsePrometheusMetrics()</c> is registered
    /// before <c>UseCanaryAuth()</c>), so by the time the <c>finally</c> block
    /// reads it, canary tagging has already happened for this request.
    /// </para>
    /// </remarks>
    public async Task InvokeAsync(HttpContext context)
    {
        // Skip metrics endpoint itself to avoid recursion
        if (context.Request.Path.StartsWithSegments("/metrics"))
        {
            await _next(context);
            return;
        }

        // Skip health check endpoints to reduce cardinality
        if (context.Request.Path.StartsWithSegments("/health"))
        {
            await _next(context);
            return;
        }

        // Resolved AFTER the /metrics + /health skips and null-tolerantly, so a
        // consumer that has not wired Canary.AspNetCore still gets a working
        // service (and working health probes) rather than a 500 on every request.
        var canaryContext = context.RequestServices?.GetService<ICanaryRunContext>();

        var stopwatch = Stopwatch.StartNew();
        HttpRequestsInFlight.WithLabels(ServiceName).Inc();

        try
        {
            await _next(context);
        }
        finally
        {
            stopwatch.Stop();
            HttpRequestsInFlight.WithLabels(ServiceName).Dec();

            var method = context.Request.Method;
            var route = ResolveRoute(context);
            var statusCode = context.Response.StatusCode.ToString();
            var duration = stopwatch.Elapsed.TotalSeconds;

            HttpRequestsTotal
                .WithLabels(ServiceName, method, route, statusCode)
                .Inc();

            HttpRequestDurationSeconds
                .WithLabels(ServiceName, method, route, statusCode)
                .Observe(duration);

            // The route label is capped at the literal "unmatched", so the URL that
            // was actually requested is deliberately NOT recorded. This counter keeps
            // the rate visible without the cardinality.
            if (string.Equals(route, UnmatchedRoute, StringComparison.Ordinal))
            {
                HttpUnmatchedRequestsTotal
                    .WithLabels(ServiceName, method)
                    .Inc();
            }

            // Separate canary counter — incremented ONLY for auth-validated
            // canary requests, NOT mere header presence. No endpoint label,
            // so this stays low-cardinality. Follows the same /metrics + /health
            // skip logic above (those paths return early before reaching here).
            if (canaryContext?.IsCanary == true)
            {
                CanaryHttpRequestsTotal
                    .WithLabels(ServiceName, method, statusCode)
                    .Inc();
            }
        }
    }

    /// <summary>
    /// Resolves the BOUNDED route label for a request: the route template when the
    /// request matched one, otherwise <see cref="UnmatchedRoute"/>.
    /// </summary>
    /// <remarks>
    /// The route template (e.g. <c>/api/v1/templates/{id}</c>) is what keeps this
    /// label finite — 50,000 requests for 50,000 different ids collapse into one
    /// series. There is deliberately NO raw-path fallback: a request that matched no
    /// route has no template, and substituting the literal URL is precisely what made
    /// this label unbounded. See <see cref="UnmatchedRoute"/>.
    /// </remarks>
    private static string ResolveRoute(HttpContext context)
    {
        if (context.GetEndpoint() is RouteEndpoint routeEndpoint)
            return routeEndpoint.RoutePattern.RawText ?? UnmatchedRoute;

        return UnmatchedRoute;
    }
}
