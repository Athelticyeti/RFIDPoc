using KQ.Brs.Core.Scanning;
using KQ.Rfid.Alien;

namespace KQ.Brs.Tests;

/// <summary>A clock tests can move by hand.</summary>
public sealed class ManualTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 2, 8, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

public class DeduplicatorTests
{
    private static TagRead Read(string epc, int antenna, double rssi) =>
        new("R1", epc, antenna, rssi, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Fact]
    public void First_read_is_emitted_immediately_and_repeats_are_collapsed()
    {
        var time = new ManualTime();
        var dedupe = new ScanDeduplicator(TimeSpan.FromSeconds(3), time);

        Assert.NotNull(dedupe.Process(Read("A", 1, 100), "BELT04"));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(dedupe.Process(Read("A", 2, 300), "BELT04"));
        time.Advance(TimeSpan.FromSeconds(2.5));   // window slides: still within 3 s of the last read
        Assert.Null(dedupe.Process(Read("A", 2, 200), "BELT04"));
    }

    [Fact]
    public void Same_tag_at_another_scan_point_is_a_separate_scan()
    {
        var dedupe = new ScanDeduplicator(TimeSpan.FromSeconds(3), new ManualTime());
        Assert.NotNull(dedupe.Process(Read("A", 0, 1), "DESK14"));
        Assert.NotNull(dedupe.Process(Read("A", 1, 1), "BELT04"));
    }

    [Fact]
    public void Quiet_window_closes_with_a_per_antenna_summary()
    {
        var time = new ManualTime();
        var dedupe = new ScanDeduplicator(TimeSpan.FromSeconds(3), time);
        ScanWindowSummary? closed = null;
        dedupe.Closed += s => closed = s;

        dedupe.Process(Read("A", 1, 100), "BELT04");
        dedupe.Process(Read("A", 2, 900), "BELT04");
        dedupe.Process(Read("A", 2, 400), "BELT04");
        time.Advance(TimeSpan.FromSeconds(3.1));
        dedupe.Tick();

        Assert.NotNull(closed);
        Assert.Equal(3, closed.Reads);
        Assert.Equal(2, closed.BestAntenna);
        Assert.Equal(900, closed.MaxRssi);
        Assert.NotNull(dedupe.Process(Read("A", 1, 100), "BELT04"));   // a new window
    }

    [Fact]
    public void Reset_lets_a_held_tag_scan_again()
    {
        var dedupe = new ScanDeduplicator(TimeSpan.FromSeconds(3), new ManualTime());
        dedupe.Process(Read("A", 3, 1), "RAMP");
        Assert.Null(dedupe.Process(Read("A", 3, 1), "RAMP"));
        dedupe.Reset("A");
        Assert.NotNull(dedupe.Process(Read("A", 3, 1), "RAMP"));
    }
}

public class DeduplicatorMaxWindowTests
{
    [Fact]
    public void A_tag_that_never_leaves_the_field_is_rescanned_after_the_max_window()
    {
        var time = new ManualTime();
        var dedupe = new ScanDeduplicator(TimeSpan.FromSeconds(3), time) { MaxWindow = TimeSpan.FromSeconds(15) };
        var read = new KQ.Rfid.Alien.TagRead("R1", "A", 1, 1, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        Assert.NotNull(dedupe.Process(read, "BELT04"));
        for (var i = 0; i < 14; i++) { time.Advance(TimeSpan.FromSeconds(1)); Assert.Null(dedupe.Process(read, "BELT04")); }
        time.Advance(TimeSpan.FromSeconds(1.5));
        Assert.NotNull(dedupe.Process(read, "BELT04"));
    }
}
