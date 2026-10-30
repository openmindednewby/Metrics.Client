using Prometheus;

namespace Metrics.Client.Labels;

/// <summary>
/// Creates metrics on the default Prometheus registry after <see cref="MetricLabelGuard"/> accepts their labels.
/// </summary>
public static class GuardedMetrics
{
    public static Counter CreateCounter(string name, string help, CounterConfiguration? configuration = null)
    {
        MetricLabelGuard.EnsureAllowed(name, configuration?.LabelNames);
        return Prometheus.Metrics.CreateCounter(name, help, configuration);
    }

    public static Gauge CreateGauge(string name, string help, GaugeConfiguration? configuration = null)
    {
        MetricLabelGuard.EnsureAllowed(name, configuration?.LabelNames);
        return Prometheus.Metrics.CreateGauge(name, help, configuration);
    }

    public static Histogram CreateHistogram(string name, string help, HistogramConfiguration? configuration = null)
    {
        MetricLabelGuard.EnsureAllowed(name, configuration?.LabelNames);
        return Prometheus.Metrics.CreateHistogram(name, help, configuration);
    }

    public static Summary CreateSummary(string name, string help, SummaryConfiguration? configuration = null)
    {
        MetricLabelGuard.EnsureAllowed(name, configuration?.LabelNames);
        return Prometheus.Metrics.CreateSummary(name, help, configuration);
    }
}
