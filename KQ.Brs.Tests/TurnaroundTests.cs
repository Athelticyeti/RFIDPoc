using KQ.Brs.Core.Reports;

namespace KQ.Brs.Tests;

public class TurnaroundTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);

    // Baseline: 101 bags in 50 min = 30 s between bags.
    private static readonly TurnaroundBaseline Baseline = new("KQ-504 on 25 Sep", 101, 50);

    [Fact]
    public void Slower_loading_than_the_baseline_extends_turnaround()
    {
        // 11 bags in 6 min = 36 s between bags: 6 s slower, so 100 gaps × 6 s = 10 min longer for the baseline flight.
        var t = new TurnaroundComparison(Baseline, 11, Start, Start.AddMinutes(6));

        Assert.Equal(30, Baseline.SecondsPerBag);
        Assert.Equal(36, t.SecondsPerBag);
        Assert.Equal(6, t.DeltaSecondsPerBag!.Value, 6);
        Assert.Equal(TimeSpan.FromMinutes(10), t.ProjectedImpact);
        Assert.True(t.Extended);
    }

    [Fact]
    public void Faster_loading_does_not_extend_turnaround()
    {
        var t = new TurnaroundComparison(Baseline, 11, Start, Start.AddMinutes(4));   // 24 s between bags

        Assert.False(t.Extended);
        Assert.Equal(TimeSpan.FromMinutes(-10), t.ProjectedImpact);
    }

    [Fact]
    public void Needs_a_baseline_and_two_loads_to_judge()
    {
        Assert.Null(new TurnaroundComparison(null, 11, Start, Start.AddMinutes(6)).Extended);
        Assert.Null(new TurnaroundComparison(Baseline, 1, Start, Start).Extended);
        Assert.Null(new TurnaroundComparison(Baseline, 0, null, null).Extended);
    }
}
