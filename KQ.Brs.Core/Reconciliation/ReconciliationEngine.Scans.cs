using KQ.Brs.Core.Domain;
using KQ.Brs.Core.Events;
using KQ.Brs.Core.Scanning;

namespace KQ.Brs.Core.Reconciliation;

public sealed partial class ReconciliationEngine
{
    private static readonly TimeSpan RescanGrace = TimeSpan.FromSeconds(2);

    /// <summary>Real reads at the ramp antenna are ignored while it is disarmed (bench cross-reads); manual scans never are.</summary>
    private bool RampIgnored(BagScan scan, bool isReal) => isReal && !_rampArmed && scan.ReaderId != ManualReaderId;

    /// <summary>
    /// Records a de-duplicated scan (FR-02/03/04). <paramref name="uld"/> overrides the ramp's selected ULD (used by
    /// the simulator, whose bags go to AK7 or AK8 by class).
    /// </summary>
    public Task<ScanResult> RecordScanAsync(BagScan scan, string? uld = null) => InvokeAsync(() => RecordScan(scan, uld));

    private ScanResult RecordScan(BagScan scan, string? uld)
    {
        var isReal = scan.ReaderId != SimReaderId;
        var point = ScanPoint.Get(scan.ScanPointId);

        if (!_epcToPlate.TryGetValue(scan.Epc, out var plate))
        {
            // FR-02: a tag carrying an encoded licence plate is decoded straight to its bag, with no linking.
            if (!LicencePlateCodec.TryDecode(scan.Epc, out var encoded))
                return Done(UnknownTag(scan, point, isReal), scan, isReal);
            if (!_bags.TryGetValue(encoded.Value, out var encodedBag))
            {
                // A plate with no BSM: never link it to someone else's bag at check-in; elsewhere it's an unknown tag.
                return point.Kind == ScanPointKind.Desk
                    ? Done(new ScanResult(ScanOutcome.Unknown, encoded.Value, $"Tag carries plate {encoded}, but there is no BSM for it"), scan, isReal)
                    : Done(UnknownTag(scan, point, isReal), scan, isReal);
            }
            plate = encoded.Value;
            _epcToPlate[scan.Epc] = plate;
            encodedBag.Epc ??= scan.Epc;
        }

        var bag = _bags[plate];

        // The simulator only moves bags with synthetic EPCs; bags bound to physical tags are never touched by it.
        if (!isReal && !SyntheticEpc.TryDecode(scan.Epc, out _))
            return Done(new ScanResult(ScanOutcome.Ignored, plate, "Not a virtual bag"), scan, isReal);

        // An open alarm for this bag: the next handheld (ramp) scan is the confirming rescan (spec 7.2 step 3).
        // It must come a moment after the alarm, so a simultaneous cross-read on the bench doesn't count.
        if (point.Kind == ScanPointKind.Ramp && OpenAlarm(plate, scan.Epc) is { } alarm && !RampIgnored(scan, isReal))
        {
            if (Now - alarm.RaisedUtc < RescanGrace)
                return Done(new ScanResult(ScanOutcome.Duplicate, plate, "Alarm just raised - rescan again"), scan, isReal);
            return Done(HandleRescan(bag, alarm, scan, isReal, uld ?? _selectedUld), scan, isReal);
        }

        return point.Kind switch
        {
            ScanPointKind.Desk => Done(DeskScan(bag, scan), scan, isReal),
            ScanPointKind.Tunnel => Done(BeltScan(bag, scan, isReal), scan, isReal),
            ScanPointKind.Ramp => Done(RampScan(bag, scan, isReal, uld), scan, isReal),
            _ => Done(new ScanResult(ScanOutcome.Ignored, plate, "Scan point not used in the POC"), scan, isReal),
        };
    }

    private ScanResult Done(ScanResult result, BagScan scan, bool isReal)
    {
        _hub.Publish(new ScanProcessed(scan.Epc, result.Plate, scan.ScanPointId, result.Outcome, result.Detail, isReal, Now));
        return result;
    }

    private BagException? OpenAlarm(string plate, string epc) =>
        _exceptions.Values.FirstOrDefault(e => e.IsOpen && e.Severity == Severity.Critical && (e.Plate == plate || e.Epc == epc));

    // ---------------------------------------------------------------- unknown tags

    private ScanResult UnknownTag(BagScan scan, ScanPoint point, bool isReal)
    {
        switch (point.Kind)
        {
            case ScanPointKind.Desk when isReal:
                return BindAtDesk(scan);

            case ScanPointKind.Ramp when RampIgnored(scan, isReal):
                return new ScanResult(ScanOutcome.Ignored, null, "Loading antenna (3) not armed");

            case ScanPointKind.Ramp:
            {
                // FR-05 needs a positive match, so an unknown tag at the ramp is always an alarm.
                if (_exceptions.Values.FirstOrDefault(e => e.IsOpen && e.Type == ExceptionType.Unknown && e.Epc == scan.Epc) is { } open)
                {
                    ResolveException(open, isReal ? RampHandler : "SIM-HANDLER", "Unknown tag set aside for investigation and rescanned", overridden: false);
                    return new ScanResult(ScanOutcome.Unknown, null, "Unknown tag set aside");
                }
                var ex = RaiseException(UnknownPlate, scan.Epc, ExceptionType.Unknown, Severity.Critical,
                    $"Tag {Short(scan.Epc)} is not on any manifest. Do not load.", isReal, scan.ScanPointId);
                var decision = new LoadDecision(false, "Unknown tag - no BSM for this bag", scan.Epc, null, _selectedUld, null, null,
                    isReal, ExceptionView.From(ex), Now);
                _hub.Publish(new LoadDecided(decision));
                return new ScanResult(ScanOutcome.Unknown, null, decision.Reason, decision);
            }

            default:
                // Stray tag (spec 3.4.3): counted so it can be diagnosed, no alarm unless configured.
                _stray++;
                _writer?.SetCounter("stray", _stray);
                _totalsDirty = true;
                if (point.Kind == ScanPointKind.Tunnel && Options.RaiseUnknownAtBelt)
                    RaiseException(UnknownPlate, scan.Epc, ExceptionType.Unknown, Severity.Warning,
                        $"Unknown tag {Short(scan.Epc)} seen at {point.Name}.", isReal, scan.ScanPointId);
                return new ScanResult(ScanOutcome.Unknown, null, "Stray tag (not bound to a bag)");
        }
    }

    /// <summary>
    /// FR-02 in the POC: the first time a physical tag is seen at the desk it is bound to the next bag reserved for
    /// real tags, as if the agent had just printed that bag's tag.
    /// </summary>
    private ScanResult BindAtDesk(BagScan scan)
    {
        // Passengers added during the demo come first, in the order they were added.
        var bag = _bags.Values
            .Where(b => b.Source == BagSource.RealPool && b.Epc == null && !b.Deleted && b.Flight == Flight.Designator)
            .OrderBy(b => IsAddedPassengerBag(b) ? 0 : 1).ThenBy(b => b.Plate.Value).FirstOrDefault()
            // Pool used up: take a virtual bag the simulator hasn't touched yet.
            ?? _bags.Values
                .Where(b => b.Source == BagSource.Virtual && b.Status == BagStatus.Expected && !b.Deleted && b.Flight == Flight.Designator)
                .OrderByDescending(b => b.Plate.Value).FirstOrDefault();

        if (bag == null)
            return new ScanResult(ScanOutcome.Unknown, null, "No passenger is waiting for a bag tag. Add the passenger first, then hold the tag at check-in again.");

        if (bag.Epc != null) _epcToPlate.Remove(bag.Epc);   // drop the synthetic EPC of a converted virtual bag
        bag.Source = BagSource.RealPool;
        bag.Epc = scan.Epc;
        _epcToPlate[scan.Epc] = bag.Plate.Value;
        _writer?.SaveBag(bag);
        _writer?.SaveBinding(scan.Epc, bag.Plate.Value, Now);

        Record(bag, "DeskBind", b => { b.Scan(scan); b.Outcome = ScanOutcome.Bound; b.NewStatus = BagStatus.CheckedIn; b.Note = $"Tag {scan.Epc} bound to bag"; });
        SendBpm(bag, ScanPoint.Desk14, null);
        _hub.Publish(new TagBound(scan.Epc, bag.Plate.Value, bag.PassengerName));
        return new ScanResult(ScanOutcome.Bound, bag.Plate.Value, $"Tag bound to {bag.Plate} ({bag.PassengerName})");
    }

    // ---------------------------------------------------------------- desk / belt / ramp

    private ScanResult DeskScan(Bag bag, BagScan scan)
    {
        if (bag.Flight != Flight.Designator)
        {
            Record(bag, "DeskScan", b => { b.Scan(scan); b.Outcome = ScanOutcome.WrongFlight; b.Note = $"Bag for {bag.Flight} seen at {Flight} desk"; });
            return new ScanResult(ScanOutcome.WrongFlight, bag.Plate.Value, $"Bag belongs to {bag.Flight}");
        }
        if (bag.Status != BagStatus.Expected || bag.Deleted)
            return new ScanResult(ScanOutcome.Duplicate, bag.Plate.Value, $"Already {bag.Status}");

        Record(bag, "DeskCheck", b => { b.Scan(scan); b.Outcome = ScanOutcome.Matched; b.NewStatus = BagStatus.CheckedIn; b.Note = "Tag confirmed against BSM"; });
        SendBpm(bag, ScanPoint.Desk14, null);
        return new ScanResult(ScanOutcome.Matched, bag.Plate.Value, "Checked in: tag matches BSM");
    }

    private ScanResult BeltScan(Bag bag, BagScan scan, bool isReal)
    {
        if (bag.Flight != Flight.Designator)
        {
            // FR-07: a bag tagged for another flight in the KQ-504 lane is an immediate WrongFlight alarm.
            Record(bag, "BeltScan", b => { b.Scan(scan); b.Outcome = ScanOutcome.WrongFlight; b.Note = $"{bag.Flight} bag in {Flight} lane"; });
            RaiseException(bag.Plate.Value, scan.Epc, ExceptionType.WrongFlight, Severity.Critical,
                $"Bag {bag.Plate} belongs to {bag.Flight} to {bag.Destination}, seen in the {Flight} lane. Remove it.", isReal, scan.ScanPointId);
            return new ScanResult(ScanOutcome.WrongFlight, bag.Plate.Value, $"Wrong flight: belongs to {bag.Flight}");
        }
        if (bag.Deleted || bag.Status == BagStatus.Offloaded)
        {
            Record(bag, "BeltScan", b => { b.Scan(scan); b.Outcome = ScanOutcome.Offloaded; });
            RaiseException(bag.Plate.Value, scan.Epc, ExceptionType.Offloaded, Severity.Critical,
                $"Bag {bag.Plate} was offloaded (BSM deleted) but is on the belt. Remove it.", isReal, scan.ScanPointId);
            return new ScanResult(ScanOutcome.Offloaded, bag.Plate.Value, "Offloaded bag on the belt");
        }
        if (bag.Status >= BagStatus.Sorted)
        {
            Record(bag, "BeltScan", b => { b.Scan(scan); b.Outcome = ScanOutcome.Duplicate; b.Note = $"Already {bag.Status}"; });
            return new ScanResult(ScanOutcome.Duplicate, bag.Plate.Value, $"Already {bag.Status}");
        }

        Record(bag, "Sorted", b => { b.Scan(scan); b.Outcome = ScanOutcome.Matched; b.MessageType = MessageType.BPM; b.NewStatus = BagStatus.Sorted;
            b.Raw = SendBpm(bag, ScanPoint.Belt04, null); });
        return new ScanResult(ScanOutcome.Matched, bag.Plate.Value, "Sorted: on the KQ-504 manifest");
    }

    private ScanResult RampScan(Bag bag, BagScan scan, bool isReal, string? uldOverride)
    {
        if (RampIgnored(scan, isReal))
            return new ScanResult(ScanOutcome.Ignored, bag.Plate.Value, "Loading antenna (3) not armed");
        if (bag.Status == BagStatus.Loaded)
            return new ScanResult(ScanOutcome.Duplicate, bag.Plate.Value, $"Already loaded in {bag.Uld}");

        var decision = AuthoriseLoad(bag, uldOverride ?? _selectedUld, scan, isReal, isReal ? RampHandler : "SIM-HANDLER");
        return new ScanResult(decision.Authorised ? ScanOutcome.Matched : OutcomeFor(decision), bag.Plate.Value, decision.Reason, decision);
    }

    private static ScanOutcome OutcomeFor(LoadDecision d) => d.Exception?.Type switch
    {
        ExceptionType.WrongFlight => ScanOutcome.WrongFlight,
        ExceptionType.Offloaded => ScanOutcome.Offloaded,
        _ => ScanOutcome.NotAuthorised,
    };

    /// <summary>The refusal (type, reason) under the FR-05 rules, or null if the bag may be loaded.</summary>
    private (ExceptionType Type, string Reason)? LoadRefusal(Bag bag)
    {
        var rules = Options.LoadRules;
        if (bag.Flight != Flight.Designator)
            return (ExceptionType.WrongFlight, $"Bag belongs to {bag.Flight} to {bag.Destination}");
        if (bag.Deleted || bag.Status == BagStatus.Offloaded)
            return (ExceptionType.Offloaded, "Bag was offloaded (BSM deleted)");
        if (rules.RequirePassengerNotNoShow && bag.PassengerStatus == PassengerStatus.NotBoarded)
            return (ExceptionType.PaxNotBoarded, $"Passenger {bag.PassengerName} did not board");
        if (rules.RequireAuthorityToLoad && !bag.AuthorityToLoad)
            return (ExceptionType.NotAuthorised, "No authority to load (ticket not valid)");
        if (rules.RequireDeskCheckIn && bag.Status < BagStatus.CheckedIn)
            return (ExceptionType.NotAuthorised, "Not checked in at the desk");
        if (rules.RequireBeltScan && bag.Status < BagStatus.Sorted)
            return (ExceptionType.NotAuthorised, "No sorter (belt) scan");
        return null;
    }

    /// <summary>FR-04/05: decide a load at the ramp and record it either way.</summary>
    private LoadDecision AuthoriseLoad(Bag bag, string uld, BagScan scan, bool isReal, string handler)
    {
        if (LoadRefusal(bag) is { } refusal)
        {
            var belongsTo = bag.Flight != Flight.Designator ? $"{bag.Flight} to {bag.Destination}" : null;
            var ex = RaiseException(bag.Plate.Value, bag.Epc, refusal.Type, Severity.Critical,
                $"DO NOT LOAD {bag.Plate} ({bag.PassengerName}): {refusal.Reason}.", isReal, scan.ScanPointId);
            Record(bag, "LoadRefused", b => { b.Scan(scan); b.Uld = uld; b.Handler = handler; b.LoadAuthorised = false;
                b.Outcome = refusal.Type == ExceptionType.WrongFlight ? ScanOutcome.WrongFlight : ScanOutcome.NotAuthorised; b.Note = refusal.Reason; });
            var refused = new LoadDecision(false, refusal.Reason, scan.Epc, bag.Plate.Value, uld, bag.PassengerName, belongsTo, isReal, ExceptionView.From(ex), Now);
            _hub.Publish(new LoadDecided(refused));
            return refused;
        }

        if (bag.Status < BagStatus.Sorted)
        {
            // A missed belt read: the bag may still load (FR-05 passed) but it's reported (spec 3.4, read rate).
            var warning = RaiseException(bag.Plate.Value, bag.Epc, ExceptionType.MissingAtSorter, Severity.Warning,
                $"Bag {bag.Plate} reached the ramp without a Belt 04 scan (missed read).", isReal, scan.ScanPointId);
            warning.State = ExceptionState.Acknowledged;
            warning.AcknowledgedUtc = Now;
            _writer?.SaveException(warning);
            _hub.Publish(new ExceptionUpdated(ExceptionView.From(warning)));
        }

        Record(bag, "Loaded", b => { b.Scan(scan); b.Uld = uld; b.Handler = handler; b.LoadAuthorised = true; b.Outcome = ScanOutcome.Matched;
            b.MessageType = MessageType.BPM; b.NewStatus = BagStatus.Loaded; b.Raw = SendBpm(bag, ScanPoint.Ramp, uld); });
        var ok = new LoadDecision(true, $"Load into {uld}", scan.Epc, bag.Plate.Value, uld, bag.PassengerName, null, isReal, null, Now);
        _hub.Publish(new LoadDecided(ok));
        return ok;
    }

    /// <summary>
    /// Spec 7.2 steps 3-4: after an alarm the handler deals with the bag and scans it again. If the bag can now be
    /// loaded it is; otherwise the rescan confirms the corrective action (removed, offloaded) and closes the alarm.
    /// </summary>
    private ScanResult HandleRescan(Bag bag, BagException alarm, BagScan scan, bool isReal, string uld)
    {
        var handler = isReal ? RampHandler : "SIM-HANDLER";
        if (LoadRefusal(bag) == null)
        {
            ResolveException(alarm, handler, "Rescan OK - bag now authorised and loaded", overridden: false);
            var decision = AuthoriseLoad(bag, uld, scan, isReal, handler);
            return new ScanResult(ScanOutcome.Matched, bag.Plate.Value, decision.Reason, decision);
        }

        string note;
        switch (alarm.Type)
        {
            case ExceptionType.WrongFlight:
                note = $"Bag removed from {Flight} lane and re-routed to {bag.Flight}; rescanned";
                Record(bag, "Rescan", b => { b.Scan(scan); b.Handler = handler; b.Note = note; });
                break;
            case ExceptionType.PaxNotBoarded:
            case ExceptionType.Offloaded:
                note = bag.Status == BagStatus.Loaded ? $"Bag offloaded from {bag.Uld}; rescanned" : "Bag set aside for offload; rescanned";
                Record(bag, "Offloaded", b => { b.Scan(scan); b.Handler = handler; b.NewStatus = BagStatus.Offloaded; b.Note = note;
                    b.MessageType = MessageType.BUM; b.Raw = SendBum(bag, alarm.Type == ExceptionType.PaxNotBoarded ? "PAX NOT BOARDED" : "BSM DELETED"); });
                bag.Uld = null;
                break;
            default:
                note = "Bag held aside (not authorised); rescanned";
                Record(bag, "Rescan", b => { b.Scan(scan); b.Handler = handler; b.Note = note; });
                break;
        }
        ResolveException(alarm, handler, note, overridden: false);
        return new ScanResult(ScanOutcome.Matched, bag.Plate.Value, note);
    }

    /// <summary>Simulator hook: the virtual handler rescans a bag with an open alarm.</summary>
    public Task<bool> SimulateRescanAsync(string plate) => InvokeAsync(() =>
    {
        if (!_bags.TryGetValue(plate, out var bag) || OpenAlarm(plate, bag.Epc ?? "") is not { } alarm) return false;
        var scan = new BagScan(SimReaderId, bag.Epc ?? SyntheticEpc.For(bag.Plate), ScanPoint.Ramp, 3, null, Now);
        var result = HandleRescan(bag, alarm, scan, isReal: false, bag.Uld ?? (bag.Class == CabinClass.Economy ? "AK8" : "AK7"));
        _hub.Publish(new ScanProcessed(scan.Epc, plate, ScanPoint.Ramp, result.Outcome, result.Detail, false, Now));
        return true;
    });

    // ---------------------------------------------------------------- FR-10

    public Task<PrePushbackReport> PrePushbackAsync(bool raiseExceptions) => InvokeAsync(() =>
    {
        var ours = _bags.Values.Where(b => b.Flight == Flight.Designator && !b.Deleted).ToList();
        var notLoaded = ours.Where(b => b.Status is BagStatus.CheckedIn or BagStatus.Sorted && b.PassengerStatus != PassengerStatus.NotBoarded).ToList();
        var offload = ours.Where(b => b.Status == BagStatus.Loaded && (b.PassengerStatus == PassengerStatus.NotBoarded || !b.AuthorityToLoad)).ToList();
        var raised = 0;
        if (raiseExceptions)
        {
            foreach (var bag in notLoaded)
            {
                RaiseException(bag.Plate.Value, bag.Epc, ExceptionType.NotLoaded, Severity.Warning,
                    $"Bag {bag.Plate} ({bag.PassengerName}) checked in but not loaded before pushback.", bag.Source != BagSource.Virtual, null);
                raised++;
            }
        }
        return new PrePushbackReport(
            notLoaded.Select(b => BagView.From(b, OpenExceptionFor(b.Plate.Value))).ToList(),
            ours.Count(b => b.Status == BagStatus.Expected),
            offload.Select(b => BagView.From(b, OpenExceptionFor(b.Plate.Value))).ToList(),
            raised,
            ours.Count(b => b.Status == BagStatus.Loaded),
            ours.Count);
    });

    private static string Short(string epc) => epc.Length > 8 ? "…" + epc[^8..] : epc;
}
