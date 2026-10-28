using System.Net;
using Metrics.Client.Configuration;
using Metrics.Client.Extensions;
using Metrics.Client.Tests.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace Metrics.Client.Tests.Extensions;

[Collection(PrometheusRegistryCollection.Name)]
public sealed class MetricsScrapeTokenTests
{
    private const string ScrapePath = "/metrics";
    private const string ProbePath = "/api/ping";
    private const string Credential = "scrape-value-one";

    [Fact]
    public async Task UsePrometheusMetrics_WithGuardOnAndMatchingBearer_Returns200()
    {
        await using var app = await StartAsync(new() { ["MetricsScrape:Token"] = Credential }, requireToken: true);

        var response = await SendAsync(app, ScrapePath, $"Bearer {Credential}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UsePrometheusMetrics_WithGuardOnAndNoAuthorization_Returns401()
    {
        await using var app = await StartAsync(new() { ["MetricsScrape:Token"] = Credential }, requireToken: true);

        var response = await SendAsync(app, ScrapePath, authorization: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().ShouldBe("Bearer");
    }

    [Fact]
    public async Task UsePrometheusMetrics_WithGuardOnAndWrongBearer_Returns401()
    {
        await using var app = await StartAsync(new() { ["MetricsScrape:Token"] = Credential }, requireToken: true);

        var response = await SendAsync(app, ScrapePath, "Bearer scrape-value-two");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UsePrometheusMetrics_WithGuardOnAndNoTokenConfigured_Returns401EvenWithBearer()
    {
        await using var app = await StartAsync(new(), requireToken: true);

        var response = await SendAsync(app, ScrapePath, "Bearer ");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UsePrometheusMetrics_WithGuardOnAndNoAuthorization_LeavesOtherPathsOpen()
    {
        await using var app = await StartAsync(new() { ["MetricsScrape:Token"] = Credential }, requireToken: true);

        var response = await SendAsync(app, ProbePath, authorization: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UsePrometheusMetrics_WithGuardSwitchedOnByConfiguration_Returns401WithoutBearer()
    {
        await using var app = await StartAsync(
            new() { ["Metrics:RequireScrapeToken"] = "true", ["MetricsScrape:Token"] = Credential },
            requireToken: false);

        var response = await SendAsync(app, ScrapePath, authorization: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UsePrometheusMetrics_WithGuardOffAndTokenConfigured_StaysAnonymous()
    {
        await using var app = await StartAsync(new() { ["MetricsScrape:Token"] = Credential }, requireToken: false);

        var response = await SendAsync(app, ScrapePath, authorization: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<HttpResponseMessage> SendAsync(WebApplication app, string path, string? authorization)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (authorization is not null)
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return await app.GetTestClient().SendAsync(request);
    }

    private static async Task<WebApplication> StartAsync(Dictionary<string, string?> settings, bool requireToken)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.AddPrometheusMetrics(o =>
        {
            o.ServiceName = $"scrape-svc-{Guid.NewGuid():N}";
            if (requireToken)
                o.RequireScrapeToken = true;
        });

        var app = builder.Build();
        app.UsePrometheusMetrics();
        app.MapGet(ProbePath, () => "pong");
        await app.StartAsync();
        return app;
    }
}
