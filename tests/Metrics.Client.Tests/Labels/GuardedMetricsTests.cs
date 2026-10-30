using Metrics.Client.Labels;
using Metrics.Client.Tests.Middleware;
using Prometheus;
using Shouldly;

namespace Metrics.Client.Tests.Labels;

[Collection(PrometheusRegistryCollection.Name)]
public sealed class GuardedMetricsTests
{
    [Theory]
    [InlineData("user_id")]
    [InlineData("userid")]
    [InlineData("userId")]
    [InlineData("user")]
    [InlineData("email")]
    [InlineData("request_id")]
    [InlineData("trace_id")]
    [InlineData("span_id")]
    [InlineData("correlation_id")]
    [InlineData("path")]
    [InlineData("url")]
    [InlineData("raw_path")]
    [InlineData("query")]
    [InlineData("ip")]
    [InlineData("client_ip")]
    [InlineData("user_agent")]
    [InlineData("message")]
    [InlineData("error_message")]
    public void CreateCounter_WithBannedLabel_ThrowsNamingLabelAndStandard(string label)
    {
        var name = UniqueName("orders_total");

        var exception = Should.Throw<MetricLabelNotAllowedException>(
            () => GuardedMetrics.CreateCounter(name, "Orders.", new CounterConfiguration { LabelNames = ["service", label] }));

        exception.Message.ShouldContain($"'{label}'");
        exception.Message.ShouldContain(name);
        exception.Message.ShouldContain("observability-pillars.md");
        exception.LabelName.ShouldBe(label);
    }

    [Fact]
    public void CreateCounter_WithTenantOnHttpMetric_Throws()
    {
        var name = UniqueName("http_orders_total");

        var exception = Should.Throw<MetricLabelNotAllowedException>(
            () => GuardedMetrics.CreateCounter(name, "Orders.", new CounterConfiguration { LabelNames = ["service", "tenant"] }));

        exception.LabelName.ShouldBe("tenant");
    }

    [Fact]
    public void CreateCounter_WithTenantOnBusinessMetric_Registers()
    {
        var name = UniqueName("orders_total");

        var counter = GuardedMetrics.CreateCounter(name, "Orders.", new CounterConfiguration { LabelNames = ["service", "tenant"] });

        counter.LabelNames.ShouldBe(["service", "tenant"]);
    }

    [Fact]
    public void CreateGauge_WithNineLabels_ThrowsNamingTheLimit()
    {
        var name = UniqueName("queue_depth");
        string[] labels = ["l1", "l2", "l3", "l4", "l5", "l6", "l7", "l8", "l9"];

        var exception = Should.Throw<MetricLabelNotAllowedException>(
            () => GuardedMetrics.CreateGauge(name, "Depth.", new GaugeConfiguration { LabelNames = labels }));

        exception.Message.ShouldContain("9");
        exception.Message.ShouldContain("8");
    }

    [Fact]
    public void CreateGauge_WithEightLabels_Registers()
    {
        var name = UniqueName("queue_depth");
        string[] labels = ["l1", "l2", "l3", "l4", "l5", "l6", "l7", "l8"];

        var gauge = GuardedMetrics.CreateGauge(name, "Depth.", new GaugeConfiguration { LabelNames = labels });

        gauge.LabelNames.Length.ShouldBe(8);
    }

    [Fact]
    public void CreateHistogram_WithBannedLabel_Throws()
    {
        var name = UniqueName("job_duration_seconds");

        Should.Throw<MetricLabelNotAllowedException>(
            () => GuardedMetrics.CreateHistogram(name, "Duration.", new HistogramConfiguration { LabelNames = ["trace_id"] }));
    }

    [Fact]
    public void CreateSummary_WithBannedLabel_Throws()
    {
        var name = UniqueName("job_size_bytes");

        Should.Throw<MetricLabelNotAllowedException>(
            () => GuardedMetrics.CreateSummary(name, "Size.", new SummaryConfiguration { LabelNames = ["email"] }));
    }

    [Fact]
    public void CreateCounter_WithAllowedLabels_Registers()
    {
        var name = UniqueName("http_jobs_total");

        var counter = GuardedMetrics.CreateCounter(
            name, "Jobs.", new CounterConfiguration { LabelNames = ["service", "method", "route", "status_code"] });

        counter.LabelNames.ShouldBe(["service", "method", "route", "status_code"]);
    }

    [Fact]
    public void CreateCounter_WithoutConfiguration_Registers()
    {
        var name = UniqueName("ticks_total");

        var counter = GuardedMetrics.CreateCounter(name, "Ticks.");

        counter.LabelNames.ShouldBeEmpty();
    }

    private static string UniqueName(string stem) => $"{stem}_{Guid.NewGuid():N}";
}
