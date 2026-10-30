namespace Metrics.Client.Labels;

/// <summary>
/// <c>tenant.Bounded()</c> idiom for business-counter label values.
/// </summary>
public static class BoundedLabelExtensions
{
    /// <summary>
    /// Returns <paramref name="value"/> while <paramref name="label"/> (default <see cref="BoundedLabel.Shared"/>) has room, else <c>other</c>.
    /// </summary>
    public static string Bounded(this string? value, BoundedLabel? label = null) =>
        (label ?? BoundedLabel.Shared).Bound(value);
}
