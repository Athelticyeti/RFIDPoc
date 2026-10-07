using KQ.Brs.Core;
using KQ.Brs.Core.Domain;
using KQ.Brs.Core.Events;
using KQ.Brs.Core.Persistence;
using KQ.Brs.Core.Reconciliation;
using KQ.Brs.Core.Reports;
using KQ.Brs.Core.Scanning;
using KQ.Brs.Core.Simulation;
using Microsoft.Data.Sqlite;

namespace KQ.Brs.Tests;

public sealed class PersistenceAndSimulatorTests : IDisposable
{
    private const string RealTag = "E2003009281101450600D744";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kqbrs-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private BrsStore NewStore()
    {
        var store = new BrsStore(Path.Combine(_dir, "brs.db"));
        store.EnsureSchema();
        return store;
    }

    private static async Task<(ReconciliationEngine Engine, PersistenceWriter Writer)> StartAsync(BrsStore store, BrsOptions options)
    {
        var writer = new PersistenceWriter(store);
        var engine = new ReconciliationEngine(options, new BrsEventHub(), writer);
        var stored = store.Load();
        await engine.InitialiseAsync(stored, () => ManifestGenerator.Generate(options, engine.Flight, 504));
        await engine.SetRampAsync("AK7", armed: true);
        return (engine, writer);
    }

    [Fact]
    public async Task Event_log_is_append_only()
    {
        var store = NewStore();
        var (engine, writer) = await StartAsync(store, new BrsOptions());
        await writer.FlushAsync();
        await engine.DisposeAsync();
        await writer.DisposeAsync();

        using var c = store.Open();
        using var update = c.CreateCommand();
        update.CommandText = "UPDATE bag_events SET note = 'tampered'";
        Assert.Throws<SqliteException>(() => update.ExecuteNonQuery());
        using var delete = c.CreateCommand();
        delete.CommandText = "DELETE FROM bag_events";
        Assert.Throws<SqliteException>(() => delete.ExecuteNonQuery());
    }

    [Fact]
    public async Task Restart_rebuilds_the_same_state_by_replaying_events()
    {
        var store = NewStore();
        var options = new BrsOptions();
        var (engine, writer) = await StartAsync(store, options);
        await engine.RecordScanAsync(new BagScan("R", RealTag, ScanPoint.Desk14, 0, 1, DateTimeOffset.UtcNow));
        await engine.RecordScanAsync(new BagScan("R", RealTag, ScanPoint.Belt04, 1, 1, DateTimeOffset.UtcNow));
        await engine.RecordScanAsync(new BagScan("R", "E200FFFF", ScanPoint.Ramp, 3, 1, DateTimeOffset.UtcNow));
        var before = await engine.SnapshotAsync();
        await writer.FlushAsync();
        await engine.DisposeAsync();
        await writer.DisposeAsync();

        var (engine2, writer2) = await StartAsync(store, options);
        var after = await engine2.SnapshotAsync();

        Assert.Equal(before.Totals, after.Totals);
        Assert.Equal(BagStatus.Sorted, after.Bags.Single(b => b.Plate == "0706100651").Status);
        Assert.Equal("0706100651", await engine2.PlateForEpcAsync(RealTag));
        Assert.Single(after.Exceptions);
        await engine2.DisposeAsync();
        await writer2.DisposeAsync();
    }

    [Fact]
    public async Task Tunnel_scan_keeps_the_antenna_that_saw_it_most_and_the_strongest_rssi()
    {
        var store = NewStore();
        var options = new BrsOptions();
        var (engine, writer) = await StartAsync(store, options);
        await engine.RecordScanAsync(new BagScan("R", RealTag, ScanPoint.Desk14, 0, 1, DateTimeOffset.UtcNow));

        // Left antenna sees it first and weakly; the right antenna sees it most and strongest.
        var dedupe = new ScanDeduplicator(TimeSpan.FromSeconds(3));
        ScanWindowSummary? closed = null;
        dedupe.Closed += s => closed = s;
        KQ.Rfid.Alien.TagRead Read(int antenna, double rssi) => new("R", RealTag, antenna, rssi, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var scan = dedupe.Process(Read(1, 1_800), ScanPoint.Belt04)!;
        foreach (var rssi in new[] { 21_000.0, 22_684, 19_500 }) Assert.Null(dedupe.Process(Read(2, rssi), ScanPoint.Belt04));

        await engine.RecordScanAsync(scan);
        var first = (await engine.TimelineAsync("0706100651")).Single(e => e.Kind == "Sorted");
        Assert.Equal(1, first.Antenna);   // the decision is made on the first read

        dedupe.CloseAll();
        Assert.Equal(2, closed!.BestAntenna);
        writer.SaveScanWindow(closed);
        await engine.CompleteScanAsync(closed);
        var sorted = (await engine.TimelineAsync("0706100651")).Single(e => e.Kind == "Sorted");
        Assert.Equal(2, sorted.Antenna);
        Assert.Equal(22_684, sorted.Rssi);

        await writer.FlushAsync();
        await engine.DisposeAsync();
        await writer.DisposeAsync();

        var (engine2, writer2) = await StartAsync(store, options);
        var reloaded = (await engine2.TimelineAsync("0706100651")).Single(e => e.Kind == "Sorted");
        Assert.Equal(2, reloaded.Antenna);
        Assert.Equal(22_684, reloaded.Rssi);
        await engine2.DisposeAsync();
        await writer2.DisposeAsync();
    }

    [Fact]
    public async Task Reset_can_keep_tag_bindings()
    {
        var store = NewStore();
        var options = new BrsOptions();
        var (engine, writer) = await StartAsync(store, options);
        await engine.RecordScanAsync(new BagScan("R", RealTag, ScanPoint.Desk14, 0, 1, DateTimeOffset.UtcNow));
        await writer.FlushAsync();
        await engine.DisposeAsync();
        await writer.DisposeAsync();

        store.Reset(keepBindings: true);
        var (engine2, writer2) = await StartAsync(store, options);

        Assert.Equal("0706100651", await engine2.PlateForEpcAsync(RealTag));
        Assert.Equal(BagStatus.Expected, (await engine2.SnapshotAsync()).Bags.Single(b => b.Plate == "0706100651").Status);
        await engine2.DisposeAsync();
        await writer2.DisposeAsync();
    }

    [Fact]
    public async Task Simulated_flight_completes_and_reconciles()
    {
        var store = NewStore();
        var options = new BrsOptions { Simulator = new SimulatorOptions { Speed = 2000 } };
        var (engine, writer) = await StartAsync(store, options);
        var sim = new VirtualFlightSimulator(engine, writer, () => options.Simulator);
        var errors = new List<string>();
        sim.Error += errors.Add;

        await sim.StartAsync();
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while ((sim.IsRunning || sim.Pending > 0) && DateTime.UtcNow < deadline) await Task.Delay(100);

        Assert.Empty(errors);
        Assert.Equal(0, sim.Pending);
        var s = await engine.SnapshotAsync();
        var virtualBags = s.Bags.Where(b => b.Source == BagSource.Virtual && b.Flight == "KQ504").ToList();

        // Every virtual bag ends loaded, offloaded, or (no-show / missed) still checked in.
        Assert.All(virtualBags, b => Assert.NotEqual(BagStatus.Expected, b.Status));
        Assert.Equal(options.Simulator.DeletedBsms + options.Simulator.PaxNotBoarded, virtualBags.Count(b => b.Status == BagStatus.Offloaded));
        Assert.Equal(options.Simulator.Misroutes, s.Exceptions.Count(e => e.Type == ExceptionType.WrongFlight));
        Assert.DoesNotContain(s.Exceptions, e => e.Severity == Severity.Critical && e.State != ExceptionState.Resolved);
        Assert.True(s.Totals.Loaded > 600, $"loaded {s.Totals.Loaded}");
        Assert.All(s.Bags.Where(b => b.Source == BagSource.RealPool), b => Assert.Equal(BagStatus.Expected, b.Status));

        await writer.FlushAsync();
        var report = EvaluationReportBuilder.Build(store);
        var belt = report.ScanPoints.Single(p => p.ScanPoint == ScanPoint.Belt04 && !p.Real);
        Assert.InRange(belt.Rate, 0.95, 1.0);
        Assert.Contains(report.Antennas, a => a.Antenna == 1 && !a.Real);

        await engine.DisposeAsync();
        await writer.DisposeAsync();
    }
}
