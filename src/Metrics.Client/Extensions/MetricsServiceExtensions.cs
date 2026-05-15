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
    public static WebApplication UsePrometheusMetrics(this WebApplication app)
    {
        app.UseMiddleware<HttpMetricsMiddleware>();
        app.UseHttpMetrics();
        app.MapMetrics().AllowAnonymous();

        return app;
    }
}
