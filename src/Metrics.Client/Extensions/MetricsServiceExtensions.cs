using Metrics.Client.Configuration;
using Metrics.Client.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
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

        // Store service name for the middleware
        HttpMetricsMiddleware.ServiceName = options.ServiceName;

        // Suppress the default prometheus-net metrics server (we use ASP.NET endpoint mapping)
        Prometheus.Metrics.SuppressDefaultMetrics();

        return builder;
    }

    /// <summary>
    /// Adds Prometheus HTTP metrics middleware and maps the /metrics scrape endpoint.
    /// Call this after <see cref="AddPrometheusMetrics{TBuilder}"/> in the builder phase.
    /// The /metrics endpoint is marked AllowAnonymous so Prometheus scrapes are not
    /// rejected by a global authorization fallback policy.
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
        app.UseMiddleware<HttpMetricsMiddleware>();
        app.MapMetrics().AllowAnonymous();

        return app;
    }
}
