using Metrics.Client.Labels;
using Shouldly;

namespace Metrics.Client.Tests.Labels;

public sealed class BoundedLabelTests
{
    [Fact]
    public void Bound_WhenUnderCapacity_ReturnsValue()
    {
        var label = new BoundedLabel(capacity: 2);

        var bound = label.Bound("tenant-a");

        bound.ShouldBe("tenant-a");
    }

    [Fact]
    public void Bound_WhenCapacityReached_ReturnsOther()
    {
        var label = new BoundedLabel(capacity: 2);
        label.Bound("tenant-a");
        label.Bound("tenant-b");

        var bound = label.Bound("tenant-c");

        bound.ShouldBe("other");
    }

    [Fact]
    public void Bound_WhenCapacityReachedAndValueAlreadyAdmitted_ReturnsValue()
    {
        var label = new BoundedLabel(capacity: 2);
        label.Bound("tenant-a");
        label.Bound("tenant-b");

        var bound = label.Bound("tenant-a");

        bound.ShouldBe("tenant-a");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Bound_WithMissingValue_ReturnsOther(string? value)
    {
        var label = new BoundedLabel(capacity: 2);

        var bound = label.Bound(value);

        bound.ShouldBe("other");
    }

    [Fact]
    public void Bound_WithDefaultCapacity_AdmitsFirstHundredValues()
    {
        var label = new BoundedLabel();
        for (var i = 0; i < 100; i++)
            label.Bound($"tenant-{i}");

        var bound = label.Bound("tenant-100");

        bound.ShouldBe("other");
    }

    [Fact]
    public void Bound_WhenCalledConcurrently_AdmitsExactlyCapacityValues()
    {
        var label = new BoundedLabel(capacity: 50);
        var values = Enumerable.Range(0, 1000).Select(i => $"tenant-{i}").ToArray();

        var results = values.AsParallel().Select(label.Bound).ToArray();

        results.Where(r => r != BoundedLabel.Overflow).Distinct().Count().ShouldBe(50);
    }

    [Fact]
    public void Constructor_WithZeroCapacity_Throws()
    {
        var create = () => new BoundedLabel(capacity: 0);

        Should.Throw<ArgumentOutOfRangeException>(create);
    }

    [Fact]
    public void Bounded_WithDistinctLabels_KeepsSeparateBudgets()
    {
        var first = new BoundedLabel(capacity: 1);
        var second = new BoundedLabel(capacity: 1);
        first.Bound("tenant-a");

        var bound = "tenant-b".Bounded(second);

        bound.ShouldBe("tenant-b");
    }
}
