using Canary.AspNetCore.Context;
using Canary.AspNetCore.Extensions;
using Metrics.Client.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Metrics.Client.Tests.Middleware;

/// <summary>
/// Regression tests for the Canary.AspNetCore hard-dependency footgun.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="HttpMetricsMiddleware"/> used to declare
/// <c>InvokeAsync(HttpContext, ICanaryRunContext)</c>. ASP.NET Core resolves
/// non-first <c>InvokeAsync</c> parameters with <c>GetRequiredService</c> BEFORE
/// the method body runs, and <c>AddPrometheusMetrics()</c> never registered
/// <see cref="ICanaryRunContext"/> — only <c>AddCanaryAuth()</c> did. A service
/// that wired metrics but not canary therefore threw
/// <see cref="InvalidOperationException"/> on EVERY request, including
/// <c>/health/live</c> and <c>/metrics</c>, because the skip logic sat AFTER the
/// resolution point.
/// </para>
/// <para>
/// The defect was invisible to <see cref="HttpMetricsMiddlewareTests"/> because
/// those tests call <c>InvokeAsync</c> directly with a hand-constructed context,
/// bypassing middleware activation entirely. These tests instead build a real
/// <see cref="ApplicationBuilder"/> pipeline via <c>UseMiddleware</c>, which uses
/// the same reflection-based activation as production, so the resolution actually
/// happens. Against the unfixed middleware every test in this class throws.
/// </para>
/// </remarks>
[Collection(PrometheusRegistryCollection.Name)]
public sealed class HttpMetricsMiddlewarePipelineTests
{
    // -----------------------------------------------------------------------
    // The defect: no Canary.AspNetCore registration at all.
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/metrics")]
    [InlineData("/api/v1/templates")]
    public async Task Pipeline_WithoutCanaryRegistration_RequestSucceeds(string path)
    {
        HttpMetricsMiddleware.ServiceName = UniqueServiceName();
        var pipeline = BuildPipeline(registerCanary: false, out var provider);
        using (provider)
        {
            var context = BuildContext(path, provider);

            // Before the fix this threw:
            // "Unable to resolve service for type 'Canary.AspNetCore.Context.ICanaryRunContext'
            //  while attempting to Invoke middleware 'Metrics.Client.Middleware.HttpMetricsMiddleware'."
            await Should.NotThrowAsync(() => pipeline(context));

            context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        }
    }

    [Fact]
    public async Task Pipeline_WithoutCanaryRegistration_StillCallsNext()
    {
        HttpMetricsMiddleware.ServiceName = UniqueServiceName();
        var nextWasCalled = false;
        var pipeline = BuildPipeline(
            registerCanary: false,
            out var provider,
            next: _ =>
            {
                nextWasCalled = true;
                return Task.CompletedTask;
            });

        using (provider)
        {
            await pipeline(BuildContext("/api/v1/templates", provider));
        }

        nextWasCalled.ShouldBeTrue();
    }

    // -----------------------------------------------------------------------
    // Backwards compatibility: services that DO wire canary are unaffected.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Pipeline_WithCanaryRegistration_ResolvesTheScopedCanaryContext()
    {
        HttpMetricsMiddleware.ServiceName = UniqueServiceName();
        var pipeline = BuildPipeline(registerCanary: true, out var provider);

        using (provider)
        {
            using var scope = provider.CreateScope();
            var context = BuildContext("/api/v1/templates", scope.ServiceProvider);

            await Should.NotThrowAsync(() => pipeline(context));

            // The middleware must see the SAME scoped instance that
            // CanaryAuthMiddleware mutates — i.e. resolving from RequestServices
            // is equivalent to the old parameter injection, which resolved from
            // this same scope.
            var resolved = scope.ServiceProvider.GetRequiredService<ICanaryRunContext>();
            var concrete = scope.ServiceProvider.GetRequiredService<CanaryRunContext>();
            resolved.ShouldBeSameAs(concrete);
        }
    }

    [Fact]
    public async Task Pipeline_WithCanaryRegistration_ActivatedContextIsVisibleToMiddleware()
    {
        HttpMetricsMiddleware.ServiceName = UniqueServiceName();
        var pipeline = BuildPipeline(registerCanary: true, out var provider);

        using (provider)
        {
            using var scope = provider.CreateScope();

            // Simulate CanaryAuthMiddleware having tagged the request.
            scope.ServiceProvider
                .GetRequiredService<CanaryRunContext>()
                .Activate("a1b2c3d4-1234-5678-9abc-def012345678");

            var context = BuildContext("/api/v1/templates", scope.ServiceProvider);
            await pipeline(context);

            scope.ServiceProvider
                .GetRequiredService<ICanaryRunContext>()
                .IsCanary
                .ShouldBeTrue();
        }
    }

    // -----------------------------------------------------------------------
    // Builders.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds a real middleware pipeline through <c>UseMiddleware</c>, which uses
    /// the same reflection-based activation (and therefore the same DI parameter
    /// resolution) as a production ASP.NET Core host.
    /// </summary>
    private static RequestDelegate BuildPipeline(
        bool registerCanary,
        out ServiceProvider provider,
        RequestDelegate? next = null)
    {
        var services = new ServiceCollection();

        if (registerCanary)
        {
            services.AddCanaryAuth(
                new ConfigurationBuilder().AddInMemoryCollection().Build());
        }

        provider = services.BuildServiceProvider();

        var app = new ApplicationBuilder(provider);
        app.UseMiddleware<HttpMetricsMiddleware>();
        app.Run(next ?? (_ => Task.CompletedTask));

        return app.Build();
    }

    private static HttpContext BuildContext(string path, IServiceProvider requestServices)
    {
        var ctx = new DefaultHttpContext { RequestServices = requestServices };
        ctx.Request.Path = path;
        ctx.Request.Method = "GET";
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        return ctx;
    }

    private static string UniqueServiceName() => $"test-svc-{Guid.NewGuid():N}";
}
