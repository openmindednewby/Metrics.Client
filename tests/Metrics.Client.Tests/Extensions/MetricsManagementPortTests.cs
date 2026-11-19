using System.Net;
using Metrics.Client.Extensions;
using Metrics.Client.Tests.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace Metrics.Client.Tests.Extensions;

[Collection(PrometheusRegistryCollection.Name)]
public sealed class MetricsManagementPortTests
{
    private const string ScrapePath = "/metrics";
    private const int PublicPort = 8080;
    private const int ManagementPort = 8081;

    [Fact]
    public async Task UsePrometheusMetrics_WithManagementPortOnPublicPort_Returns404()
    {
        await using var app = await StartAsync(new(), enableManagementPort: true);

        var response = await GetAsync(app, PublicPort, ScrapePath);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UsePrometheusMetrics_WithManagementPortOnManagementPort_Returns200()
    {
        await using var app = await StartAsync(new(), enableManagementPort: true);

        var response = await GetAsync(app, ManagementPort, ScrapePath);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UsePrometheusMetrics_WithManagementPortSwitchedOnByConfiguration_Returns404OnPublicPort()
    {
        await using var app = await StartAsync(new() { ["Metrics:EnableManagementPort"] = "true" }, enableManagementPort: false);

        var response = await GetAsync(app, PublicPort, ScrapePath);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UsePrometheusMetrics_WithoutManagementPort_Returns200OnPublicPort()
    {
        await using var app = await StartAsync(new(), enableManagementPort: false);

        var response = await GetAsync(app, PublicPort, ScrapePath);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<HttpResponseMessage> GetAsync(WebApplication app, int port, string path)
    {
        using var client = app.GetTestClient();
        client.BaseAddress = new Uri($"http://localhost:{port}/");
        return await client.GetAsync(path);
    }

    private static async Task<WebApplication> StartAsync(Dictionary<string, string?> settings, bool enableManagementPort)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.AddPrometheusMetrics(o =>
        {
            o.ServiceName = $"mgmt-svc-{Guid.NewGuid():N}";
            if (enableManagementPort)
            {
                o.EnableManagementPort = true;
                o.ManagementPort = ManagementPort;
            }
        });

        var app = builder.Build();
        app.UsePrometheusMetrics();
        await app.StartAsync();
        return app;
    }
}
