using System.Net;
using System.Text;
using Metrics.Client.Configuration;
using Metrics.Client.Extensions;
using Metrics.Client.Middleware;
using Metrics.Client.Tests.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace Metrics.Client.Tests.Extensions;

/// <summary>
/// OBS-1a "Make Metrics.Client / Logging.Client switchable": <c>Metrics:Enabled</c> and
/// <c>Metrics:MetricsPath</c> used to be bound and never read, so a consumer (ProovID, a
/// client with no Prometheus) could not turn the package off. These tests drive a real
/// host through <c>AddPrometheusMetrics</c> + <c>UsePrometheusMetrics</c>.
/// </summary>
[Collection(PrometheusRegistryCollection.Name)]
public sealed class MetricsSwitchTests
{
    private const string DefaultPath = "/metrics";
    private const string ProbePath = "/api/ping";

    [Fact]
    public async Task UsePrometheusMetrics_WhenDisabledByConfiguration_DoesNotMapScrapeEndpoint()
    {
        await using var app = await StartAsync(new() { ["Metrics:Enabled"] = "false" });

        var response = await app.GetTestClient().GetAsync(DefaultPath);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UsePrometheusMetrics_WhenDisabledInCode_DoesNotRecordRequests()
    {
        const string sentinel = "sentinel-before-disabled-host";
        HttpMetricsMiddleware.ServiceName = sentinel;
        var disabledName = UniqueServiceName();

        await using var app = await StartAsync(
            new(),
            o =>
            {
                o.Enabled = false;
                o.ServiceName = disabledName;
            });
        var response = await app.GetTestClient().GetAsync(ProbePath);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        HttpMetricsMiddleware.ServiceName.ShouldBe(sentinel);
        var scrape = await ScrapeDefaultRegistryAsync();
        scrape.ShouldNotContain(disabledName);
    }

    [Fact]
    public async Task UsePrometheusMetrics_WithCustomPath_ServesOnlyTheConfiguredPath()
    {
        HttpMetricsMiddleware.ServiceName = UniqueServiceName();
        await using var app = await StartAsync(new() { ["Metrics:MetricsPath"] = "/internal/metrics" });
        var client = app.GetTestClient();

        (await client.GetAsync("/internal/metrics")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync(DefaultPath)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UsePrometheusMetrics_WithDefaults_ServesScrapeAndRecordsRequests()
    {
        var name = UniqueServiceName();
        await using var app = await StartAsync(new(), o => o.ServiceName = name);
        var client = app.GetTestClient();

        await client.GetAsync(ProbePath);
        var scrape = await client.GetStringAsync(DefaultPath);

        scrape.ShouldContain(name);
    }

    [Fact]
    public async Task UsePrometheusMetrics_WithoutAddPrometheusMetrics_KeepsDefaultEndpoint()
    {
        await using var app = await StartAsync(new(), configure: null, callAdd: false);

        var response = await app.GetTestClient().GetAsync(DefaultPath);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<WebApplication> StartAsync(
        Dictionary<string, string?> settings,
        Action<MetricsOptions>? configure = null,
        bool callAdd = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(settings);
        if (callAdd)
            builder.AddPrometheusMetrics(configure);

        var app = builder.Build();
        app.UsePrometheusMetrics();
        app.MapGet(ProbePath, () => "pong");
        await app.StartAsync();
        return app;
    }

    private static async Task<string> ScrapeDefaultRegistryAsync()
    {
        using var stream = new MemoryStream();
        await Prometheus.Metrics.DefaultRegistry.CollectAndExportAsTextAsync(stream);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string UniqueServiceName() => $"switch-svc-{Guid.NewGuid():N}";
}
