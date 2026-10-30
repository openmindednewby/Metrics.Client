namespace Metrics.Client.Labels;

/// <summary>
/// Rejects metric label sets that would leak identifiers or explode cardinality (observability-pillars.md §7).
/// </summary>
public static class MetricLabelGuard
{
    public const int MaxLabelsPerMetric = 8;

    private const string StandardReference =
        "See BaseClient/docs/code-standards/observability-pillars.md §7 \"Few metric labels\"; put it in logs or span attributes instead.";

    private const string HttpMetricPrefix = "http_";

    private static readonly HashSet<string> BannedLabels = new(StringComparer.Ordinal)
    {
        "userid", "user", "email", "requestid", "traceid", "spanid", "correlationid",
        "path", "url", "rawpath", "query", "ip", "clientip", "useragent", "message", "errormessage",
    };

    private static readonly HashSet<string> TenantLabels = new(StringComparer.Ordinal) { "tenant", "tenantid" };

    /// <summary>
    /// Throws <see cref="MetricLabelNotAllowedException"/> when <paramref name="labelNames"/> breaks the label rules.
    /// </summary>
    public static void EnsureAllowed(string metricName, IReadOnlyCollection<string>? labelNames)
    {
        if (labelNames is null)
            return;

        if (labelNames.Count > MaxLabelsPerMetric)
            throw new MetricLabelNotAllowedException(
                metricName,
                null,
                $"Metric '{metricName}' declares {labelNames.Count} labels; the limit is {MaxLabelsPerMetric}. {StandardReference}");

        var isHttpMetric = metricName.StartsWith(HttpMetricPrefix, StringComparison.OrdinalIgnoreCase);
        var banned = labelNames.FirstOrDefault(label => IsBanned(Normalize(label), isHttpMetric));
        if (banned is not null)
            throw new MetricLabelNotAllowedException(
                metricName,
                banned,
                $"Metric '{metricName}' declares banned label '{banned}'. {StandardReference}");
    }

    private static bool IsBanned(string normalized, bool isHttpMetric) =>
        BannedLabels.Contains(normalized) || (isHttpMetric && TenantLabels.Contains(normalized));

    private static string Normalize(string label) =>
        label.Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
}
