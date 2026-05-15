namespace Metrics.Client.Configuration;

/// <summary>
/// Configuration options for Prometheus metrics collection.
/// </summary>
public sealed class MetricsOptions
{
    /// <summary>
    /// The configuration section name in appsettings.json.
    /// </summary>
    public const string SectionName = "Metrics";

    /// <summary>
    /// The service name label applied to all metrics.
    /// </summary>
    public string ServiceName { get; set; } = "unknown";

    /// <summary>
    /// Whether Prometheus metrics collection is enabled. Default: true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The path where the Prometheus /metrics endpoint is exposed. Default: /metrics.
    /// </summary>
    public string MetricsPath { get; set; } = "/metrics";
}
