using System.Collections.Concurrent;

namespace Metrics.Client.Labels;

/// <summary>
/// Admits the first <see cref="DefaultCapacity"/> distinct label values and maps the rest to <see cref="Overflow"/>.
/// </summary>
public sealed class BoundedLabel
{
    public const int DefaultCapacity = 100;

    public const string Overflow = "other";

    private readonly ConcurrentDictionary<string, byte> _admitted = new(StringComparer.Ordinal);
    private readonly int _capacity;
    private readonly Lock _gate = new();

    public BoundedLabel(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    public static BoundedLabel Shared { get; } = new();

    public string Bound(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return Overflow;

        if (_admitted.ContainsKey(value))
            return value;

        lock (_gate)
        {
            if (_admitted.ContainsKey(value))
                return value;

            if (_admitted.Count >= _capacity)
                return Overflow;

            _admitted[value] = 0;
            return value;
        }
    }
}
