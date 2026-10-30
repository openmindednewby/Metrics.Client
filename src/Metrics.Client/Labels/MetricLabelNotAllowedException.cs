namespace Metrics.Client.Labels;

/// <summary>
/// Thrown when a metric is registered with a banned label or too many labels (observability-pillars.md §7).
/// </summary>
public sealed class MetricLabelNotAllowedException : InvalidOperationException
{
    public MetricLabelNotAllowedException(string metricName, string? labelName, string message)
        : base(message)
    {
        MetricName = metricName;
        LabelName = labelName;
    }

    public string MetricName { get; }

    public string? LabelName { get; }
}
