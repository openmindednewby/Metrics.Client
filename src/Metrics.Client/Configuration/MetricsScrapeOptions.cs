namespace Metrics.Client.Configuration;

/// <summary>
/// The bearer token a scraper presents on the metrics path (env <c>MetricsScrape__Token</c>); empty refuses every scrape.
/// </summary>
public sealed class MetricsScrapeOptions
{
    /// <summary>
    /// The configuration section name.
    /// </summary>
    public const string SectionName = "MetricsScrape";

    /// <summary>
    /// The expected <c>Authorization: Bearer</c> value. No default, so no build ships a usable credential.
    /// </summary>
    public string Token { get; set; } = string.Empty;
}
