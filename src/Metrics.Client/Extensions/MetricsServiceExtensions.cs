using Metrics.Client.Configuration;
using Metrics.Client.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Prometheus;

namespace Metrics.Client.Extensions;

/// <summary>
/// Extension methods for configuring Prometheus application metrics.
/// </summary>
public static class MetricsServiceExtensions
{
    /// <summary>
    /// Adds Prometheus metrics collection to the application.
    /// Registers HTTP metrics middleware and configures the /metrics scrape endpoint.
    /// When <see cref="MetricsOptions.Enabled"/> is false (<c>Metrics:Enabled=false</c>)
    /// nothing is set up: <see cref="UsePrometheusMetrics"/> then adds no middleware and
    /// maps no scrape endpoint, so the host needs no Prometheus at all.
    /// </summary>
    /// <typeparam name="TBuilder">The host builder type.</typeparam>
    /// <param name="builder">The host application builder.</param>
    /// <param name="configure">Optional action to configure metrics options.</param>
    /// <returns>The builder for chaining.</returns>
    public static TBuilder AddPrometheusMetrics<TBuilder>(
        this TBuilder builder,
        Action<MetricsOptions>? configure = null)
        where TBuilder : IHostApplicationBuilder
    {
        var options = new MetricsOptions();

        // Bind from configuration first
        builder.Configuration
            .GetSection(MetricsOptions.SectionName)
            .Bind(options);

        // Apply programmatic overrides
        configure?.Invoke(options);

        // Read back by UsePrometheusMetrics to decide whether to wire anything.
        builder.Services.AddSingleton(options);

        // Suppress the default prometheus-net collectors (process/runtime). Called on the
        // disabled path too, so an off switch leaves no collector behind.
        Prometheus.Metrics.SuppressDefaultMetrics();

        if (!options.Enabled)
            return builder;

        // Store service name for the middleware. Touching the middleware type is what
        // creates its static counters, so the disabled path above must not reach here.
        HttpMetricsMiddleware.ServiceName = options.ServiceName;

        return builder;
    }

    /// <summary>
    /// Adds Prometheus HTTP metrics middleware and maps the /metrics scrape endpoint.
    /// Call this after <see cref="AddPrometheusMetrics{TBuilder}"/> in the builder phase.
    /// The /metrics endpoint is marked AllowAnonymous so Prometheus scrapes are not
    /// rejected by a global authorization fallback policy. The endpoint is served at
    /// <see cref="MetricsOptions.MetricsPath"/>. When metrics are disabled this is a no-op.
    /// Called without <see cref="AddPrometheusMetrics{TBuilder}"/>, the defaults apply
    /// (enabled, <c>/metrics</c>), as before.
    /// </summary>
    /// <param name="app">The web application.</param>
    /// <returns>The app for chaining.</returns>
    /// <remarks>
    /// <para>
    /// prometheus-net's own <c>app.UseHttpMetrics()</c> is deliberately NOT registered
    /// here. It used to be, alongside <see cref="HttpMetricsMiddleware"/>, so every
    /// request was measured TWICE — and because both write to the metric name
    /// <c>http_request_duration_seconds</c> under incompatible label schemas
    /// (<c>code,method,endpoint</c> vs ours), the result was ~2x the series AND a
    /// silent double-count in any query that summed the metric.
    /// </para>
    /// <para>
    /// Ours is kept because it is strictly more capable: bounded route-template
    /// labels, the canary counter, and the unmatched-route cap. Before removing the
    /// built-in, every consumer class was checked for use of the metrics it uniquely
    /// provided (<c>http_requests_received_total</c>, <c>http_requests_in_progress</c>):
    /// 30 live Grafana dashboards, the PrometheusRule CRs, the Prometheus
    /// recording/alerting rules and the in-repo dashboards — zero references in all four.
    /// </para>
    /// </remarks>
    public static WebApplication UsePrometheusMetrics(this WebApplication app)
    {
        var options = app.Services.GetService<MetricsOptions>() ?? new MetricsOptions();
        if (!options.Enabled)
            return app;

        app.UseMiddleware<HttpMetricsMiddleware>();
        app.MapMetrics(ResolvePath(options)).AllowAnonymous();

        return app;
    }

    private static string ResolvePath(MetricsOptions options) =>
        string.IsNullOrWhiteSpace(options.MetricsPath) ? DefaultMetricsPath : options.MetricsPath;

    private const string DefaultMetricsPath = "/metrics";
}
