using KQ.Brs.Core;
using KQ.Brs.Core.Domain;
using KQ.Brs.Core.Events;
using KQ.Brs.Core.Persistence;
using KQ.Brs.Core.Reconciliation;
using KQ.Brs.Core.Scanning;
using KQ.Brs.Core.Simulation;

namespace KQ.Brs.Tests;

public sealed class EngineTests : IAsyncLifetime
{
    private const string RealTag = "E2003009281101450600D744";
    private readonly ManualTime _time = new();
    private readonly BrsEventHub _hub = new();
    private readonly List<BrsEvent> _events = new();
    private ReconciliationEngine _engine = null!;

    public async Task InitializeAsync()
    {
        _hub.Subscribe(e => { lock (_events) _events.Add(e); });
        var options = new BrsOptions();
        _engine = new ReconciliationEngine(options, _hub, writer: null, _time);
        await _engine.InitialiseAsync(null, () => ManifestGenerator.Generate(options, _engine.Flight, 504));
        await _engine.SetRampAsync("AK7", armed: true);
    }

    public async Task DisposeAsync() => await _engine.DisposeAsync();

    private BagScan Scan(string epc, string point, int antenna = 0, string reader = "ALR9900") =>
        new(reader, epc, point, antenna, 5000, _time.GetUtcNow());

    private async Task<BagView> Bag(string plate) => (await _engine.SnapshotAsync()).Bags.Single(b => b.Plate == plate);

    [Fact]
    public async Task Manifest_has_700_bags_with_a_real_tag_pool()
    {
        var snapshot = await _engine.SnapshotAsync();

        Assert.Equal(700, snapshot.Totals.Expected);
        Assert.Equal(50, snapshot.Bags.Count(b => b.Source == BagSource.RealPool));
        Assert.Equal(350, snapshot.Messages.Count(m => m.Type == MessageType.BSM));
        Assert.All(snapshot.Bags.Where(b => b.Source == BagSource.RealPool), b => Assert.True(b.AuthorityToLoad));
    }

    [Fact]
    public async Task Real_tag_is_bound_at_the_desk_then_sorted_and_loaded()
    {
        var bound = await _engine.RecordScanAsync(Scan(RealTag, ScanPoint.Desk14));
        Assert.Equal(ScanOutcome.Bound, bound.Outcome);
        Assert.Equal("0706100651", bound.Plate);   // first bag of the real-tag pool
        Assert.Equal(BagStatus.CheckedIn, (await Bag("0706100651")).Status);

        Assert.Equal(ScanOutcome.Matched, (await _engine.RecordScanAsync(Scan(RealTag, ScanPoint.Belt04, 1))).Outcome);

        await _engine.SetRampAsync("AK8", armed: true);
        var load = await _engine.RecordScanAsync(Scan(RealTag, ScanPoint.Ramp, 3));
        Assert.True(load.Decision!.Authorised);
        var bag = await Bag("0706100651");
        Assert.Equal(BagStatus.Loaded, bag.Status);
        Assert.Equal("AK8", bag.Uld);

        var snapshot = await _engine.SnapshotAsync();
        Assert.Contains(snapshot.Messages, m => m.Type == MessageType.BPM && m.Raw.Contains(".U/AK8"));
    }

    [Fact]
    public async Task Earlier_scan_point_never_moves_a_bag_backwards()
    {
        await _engine.RecordScanAsync(Scan(RealTag, ScanPoint.Desk14));
        await _engine.RecordScanAsync(Scan(RealTag, ScanPoint.Belt04, 1));

        var again = await _engine.RecordScanAsync(Scan(RealTag, ScanPoint.Desk14));

        Assert.Equal(ScanOutcome.Duplicate, again.Outcome);
        Assert.Equal(BagStatus.Sorted, (await Bag("0706100651")).Status);
    }

    [Fact]
    public async Task Unknown_tag_at_the_ramp_is_refused_and_raises_an_alarm()
    {
        var result = await _engine.RecordScanAsync(Scan("E200FFFF", ScanPoint.Ramp, 3));

        Assert.False(result.Decision!.Authorised);
        var ex = Assert.Single((await _engine.SnapshotAsync()).Exceptions);
        Assert.Equal(ExceptionType.Unknown, ex.Type);
        Assert.Equal(Severity.Critical, ex.Severity);
    }

    [Fact]
    public async Task Ramp_refuses_a_bag_that_was_never_checked_in()
    {
        // Simulate a virtual bag (synthetic EPC) going straight to the ramp.
        var plate = "0706100001";
        var result = await _engine.RecordScanAsync(new BagScan(ReconciliationEngine.SimReaderId, SyntheticEpc.For(new LicencePlate(plate)), ScanPoint.Ramp, 3, null, _time.GetUtcNow()), "AK7");

        Assert.False(result.Decision!.Authorised);
        Assert.Equal("Not checked in at the desk", result.Decision.Reason);
        Assert.Equal(BagStatus.Expected, (await Bag(plate)).Status);
    }

    [Fact]
    public async Task Misroute_is_caught_at_the_belt_and_closed_by_a_ramp_rescan()
    {
        await _engine.RecordScanAsync(Scan(RealTag, ScanPoint.Desk14));
        var foreignPlate = await _engine.FlagForeignAsync(RealTag);

        var belt = await _engine.RecordScanAsync(Scan(RealTag, ScanPoint.Belt04, 1));
        Assert.Equal(ScanOutcome.WrongFlight, belt.Outcome);
        var alarm = Assert.Single((await _engine.SnapshotAsync()).Exceptions, e => e.IsOpen());
        Assert.Equal(ExceptionType.WrongFlight, alarm.Type);
        Assert.Equal(foreignPlate, alarm.Plate);

        // A cross-read straight after the alarm doesn't count; a later handheld rescan does.
        Assert.Equal(ScanOutcome.Duplicate, (await _engine.RecordScanAsync(Scan(RealTag, ScanPoint.Ramp, 3))).Outcome);
        _time.Advance(TimeSpan.FromSeconds(5));
        await _engine.RecordScanAsync(Scan(RealTag, ScanPoint.Ramp, 3));

        var resolved = (await _engine.SnapshotAsync()).Exceptions.Single(e => e.Id == alarm.Id);
        Assert.Equal(ExceptionState.Resolved, resolved.State);
        Assert.Contains("re-routed to KQ412", resolved.ResolutionNote);
        Assert.NotEqual(BagStatus.Loaded, (await Bag(foreignPlate)).Status);
        Assert.Contains(_events, e => e is AlarmCleared);

        // Undo returns the tag to its KQ-504 bag.
        Assert.Equal("0706100651", await _engine.UnflagForeignAsync(RealTag));
        Assert.Equal("0706100651", await _engine.PlateForEpcAsync(RealTag));
    }

    [Fact]
    public async Task Override_needs_a_reason_and_is_audited()
    {
        await _engine.RecordScanAsync(Scan("E200FFFF", ScanPoint.Ramp, 3));
        var ex = (await _engine.SnapshotAsync()).Exceptions.Single();

        await Assert.ThrowsAsync<ArgumentException>(() => _engine.OverrideAsync(ex.Id, "SUP1", " "));
        Assert.True(await _engine.OverrideAsync(ex.Id, "SUP1", "Tag destroyed, bag re-tagged"));

        var after = (await _engine.SnapshotAsync()).Exceptions.Single();
        Assert.True(after.Overridden);
        Assert.Equal("SUP1", after.ResolvedBy);
    }

    [Fact]
    public async Task Deleted_bsm_offloads_the_bag_and_the_belt_flags_it()
    {
        await _engine.RecordScanAsync(Scan(RealTag, ScanPoint.Desk14));
        var bag = await Bag("0706100651");
        await _engine.ApplyTypeBAsync(KQ.Brs.Core.TypeB.TypeBBuilder.Bsm(KQ.Brs.Core.TypeB.BsmAction.Delete, "NBO", _engine.Flight, "EBB",
            bag.Class, new LicencePlate(bag.Plate), 1, 20, true, PassengerStatus.CheckedIn, "1A", 1, "X", "Y"));

        Assert.Equal(BagStatus.Offloaded, (await Bag("0706100651")).Status);
        var belt = await _engine.RecordScanAsync(Scan(RealTag, ScanPoint.Belt04, 1));
        Assert.Equal(ScanOutcome.Offloaded, belt.Outcome);
        Assert.Equal(699, (await _engine.SnapshotAsync()).Totals.Expected);
    }

    [Fact]
    public async Task Added_passenger_gets_the_next_real_tags_at_the_desk()
    {
        var plates = await _engine.AddPassengerAsync("Otie-no", "a", CabinClass.Business, bags: 2, weightKg: 40);

        Assert.Equal(["0706200001", "0706200002"], plates);
        var snapshot = await _engine.SnapshotAsync();
        Assert.Equal(702, snapshot.Totals.Expected);
        Assert.Contains(snapshot.Messages, m => m.Type == MessageType.BSM && m.Raw.Contains(".P/1OTIENO/A"));
        var added = snapshot.Bags.Where(b => plates.Contains(b.Plate)).ToList();
        Assert.All(added, b => Assert.Equal(BagSource.RealPool, b.Source));
        Assert.All(added, b => Assert.Null(b.Epc));   // no synthetic EPC, so the simulator can't move them

        // The next two physical tags go to the new passenger, ahead of the existing real-tag pool.
        Assert.Equal("0706200001", (await _engine.RecordScanAsync(Scan(RealTag, ScanPoint.Desk14))).Plate);
        Assert.Equal("0706200002", (await _engine.RecordScanAsync(Scan("E2003009281101520630D3DF", ScanPoint.Desk14))).Plate);
        Assert.Equal("0706100651", (await _engine.RecordScanAsync(Scan("E2003009281101380600D728", ScanPoint.Desk14))).Plate);

        // A second passenger continues the serial.
        Assert.Equal(["0706200003"], await _engine.AddPassengerAsync("Smith", "J", CabinClass.Economy, 1, 20));
        await Assert.ThrowsAsync<ArgumentException>(() => _engine.AddPassengerAsync("123", "J", CabinClass.Economy, 1, 20));
    }

    [Fact]
    public async Task Outbox_holds_messages_while_mq_is_down_then_sends_them()
    {
        await _engine.SetOutboxPausedAsync(true);
        await _engine.RecordScanAsync(Scan(RealTag, ScanPoint.Desk14));
        Assert.Contains((await _engine.SnapshotAsync()).Messages, m => m.Direction == MessageDirection.Out && m.SentUtc == null);

        Assert.True(await _engine.SetOutboxPausedAsync(false) >= 1);
        Assert.DoesNotContain((await _engine.SnapshotAsync()).Messages, m => m.SentUtc == null);
    }
}

internal static class ExceptionViewExtensions
{
    public static bool IsOpen(this ExceptionView e) => e.State != ExceptionState.Resolved;
}

public sealed class EmptyFlightTests
{
    [Fact]
    public async Task Empty_flight_only_has_the_passengers_you_add()
    {
        await using var engine = new ReconciliationEngine(new BrsOptions(), new BrsEventHub());
        await engine.InitialiseAsync(null, () => []);
        Assert.Equal(0, (await engine.SnapshotAsync()).Totals.Expected);

        var tag = "E2003009281101450600D744";
        var unlinked = await engine.RecordScanAsync(new BagScan("R", tag, ScanPoint.Desk14, 0, 1, DateTimeOffset.UtcNow));
        Assert.Equal(ScanOutcome.Unknown, unlinked.Outcome);
        Assert.Contains("Add the passenger first", unlinked.Detail);

        await engine.AddPassengerAsync("Wanjiru", "S", CabinClass.Economy, 1, 18);
        var bound = await engine.RecordScanAsync(new BagScan("R", tag, ScanPoint.Desk14, 0, 1, DateTimeOffset.UtcNow));
        Assert.Equal(ScanOutcome.Bound, bound.Outcome);
        Assert.Equal("0706200001", bound.Plate);
        Assert.Equal(1, (await engine.SnapshotAsync()).Totals.Expected);
    }
}

public sealed class TagWritingTests
{
    private static async Task<ReconciliationEngine> EmptyFlightAsync()
    {
        var engine = new ReconciliationEngine(new BrsOptions(), new BrsEventHub());
        await engine.InitialiseAsync(null, () => []);
        return engine;
    }

    private static BagScan Desk(string epc) => new("R", epc, ScanPoint.Desk14, 0, 1, DateTimeOffset.UtcNow);

    [Fact]
    public void Codec_round_trips_a_licence_plate()
    {
        var epc = LicencePlateCodec.Encode(new LicencePlate("0706200001"));
        Assert.Equal("BA6007062000010000000000", epc);
        Assert.True(LicencePlateCodec.TryDecode(epc, out var plate));
        Assert.Equal("0706200001", plate.Value);
        Assert.False(LicencePlateCodec.TryDecode("E2003009281101450600D744", out _));
    }

    [Fact]
    public async Task Encoded_tag_is_decoded_and_confirmed_at_check_in()
    {
        await using var engine = await EmptyFlightAsync();
        await engine.AddPassengerAsync("Otieno", "A", CabinClass.Economy, 2, 40);   // 0706200001, 0706200002

        // A tag carrying the SECOND bag's plate checks in that bag, even though the first bag is next in line.
        var result = await engine.RecordScanAsync(Desk(LicencePlateCodec.Encode(new LicencePlate("0706200002"))));

        Assert.Equal(ScanOutcome.Matched, result.Outcome);
        Assert.Equal("0706200002", result.Plate);
        var bags = (await engine.SnapshotAsync()).Bags;
        Assert.Equal(BagStatus.CheckedIn, bags.Single(b => b.Plate == "0706200002").Status);
        Assert.Equal(BagStatus.Expected, bags.Single(b => b.Plate == "0706200001").Status);
    }

    [Fact]
    public async Task Encoded_plate_without_a_BSM_is_rejected_and_never_linked()
    {
        await using var engine = await EmptyFlightAsync();
        await engine.AddPassengerAsync("Otieno", "A", CabinClass.Economy, 1, 20);

        var result = await engine.RecordScanAsync(Desk(LicencePlateCodec.Encode(new LicencePlate("0706999999"))));

        Assert.Equal(ScanOutcome.Unknown, result.Outcome);
        Assert.Contains("no BSM", result.Detail);
        Assert.Null((await engine.SnapshotAsync()).Bags.Single().Epc);   // the waiting bag is untouched
    }

    [Fact]
    public async Task Tag_owner_is_known_for_linked_and_encoded_tags()
    {
        await using var engine = await EmptyFlightAsync();
        await engine.AddPassengerAsync("Crompton", "S", CabinClass.Economy, 1, 20);   // 0706200001
        await engine.AddPassengerAsync("Crompton", "C", CabinClass.Economy, 1, 20);   // 0706200002
        const string factoryEpc = "E2003009281100490600D5C4";
        await engine.RecordScanAsync(Desk(factoryEpc));                                  // linked to S. Crompton

        Assert.Equal("0706200001", (await engine.TagOwnerAsync(factoryEpc))?.Plate);
        Assert.Equal("S. CROMPTON", (await engine.TagOwnerAsync(factoryEpc))?.PassengerName);
        Assert.Equal("0706200002", (await engine.TagOwnerAsync(LicencePlateCodec.Encode(new LicencePlate("0706200002"))))?.Plate);
        Assert.Null(await engine.TagOwnerAsync("E200FFFFFFFFFFFFFFFFFFFF"));                       // blank tag: free to write
        Assert.Null(await engine.TagOwnerAsync(LicencePlateCodec.Encode(new LicencePlate("0706999999"))));   // plate not on manifest
    }

    [Fact]
    public async Task Refused_write_is_in_both_bags_audit_trail()
    {
        await using var engine = await EmptyFlightAsync();
        await engine.AddPassengerAsync("Crompton", "S", CabinClass.Economy, 1, 20);   // 0706200001
        await engine.AddPassengerAsync("Crompton", "C", CabinClass.Economy, 1, 20);   // 0706200002
        const string tag = "BA6007062000010000000000";

        await engine.RecordTagWriteRefusedAsync("0706200001", tag, "0706200002", "ALR9900-192.168.0.161");

        var owner = (await engine.TimelineAsync("0706200001")).Single(e => e.Kind == "TagWriteRefused");
        Assert.Contains("C. CROMPTON's bag 0706200002", owner.Note);
        Assert.Equal("ALR9900-192.168.0.161", owner.ReaderId);
        var attempted = (await engine.TimelineAsync("0706200002")).Single(e => e.Kind == "TagWriteRefused");
        Assert.Contains("already belongs to S. CROMPTON's bag 0706200001", attempted.Note);
        Assert.Null((await engine.SnapshotAsync()).Bags.Single(b => b.Plate == "0706200002").Epc);   // nothing changed hands
    }

    [Fact]
    public async Task Writing_a_tag_moves_it_off_the_bag_it_was_linked_to()
    {
        await using var engine = await EmptyFlightAsync();
        await engine.AddPassengerAsync("First", "A", CabinClass.Economy, 1, 20);    // 0706200001
        await engine.AddPassengerAsync("Second", "B", CabinClass.Economy, 1, 20);   // 0706200002
        const string factoryEpc = "E2003009281101450600D744";
        Assert.Equal("0706200001", (await engine.RecordScanAsync(Desk(factoryEpc))).Plate);   // linked to the first bag

        // The same physical tag is re-written for the second passenger.
        var newEpc = LicencePlateCodec.Encode(new LicencePlate("0706200002"));
        await engine.RecordTagWrittenAsync("0706200002", factoryEpc, newEpc);

        var bags = (await engine.SnapshotAsync()).Bags;
        Assert.Null(bags.Single(b => b.Plate == "0706200001").Epc);
        Assert.Equal(newEpc, bags.Single(b => b.Plate == "0706200002").Epc);
        Assert.Null(await engine.PlateForEpcAsync(factoryEpc));
        Assert.Equal("0706200002", (await engine.RecordScanAsync(Desk(newEpc))).Plate);
    }
}
