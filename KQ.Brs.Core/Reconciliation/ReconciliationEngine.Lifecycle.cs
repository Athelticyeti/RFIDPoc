using System.Globalization;
using KQ.Brs.Core.Domain;
using KQ.Brs.Core.Events;
using KQ.Brs.Core.Persistence;
using KQ.Brs.Core.TypeB;

namespace KQ.Brs.Core.Reconciliation;

public sealed partial class ReconciliationEngine
{
    // ---------------------------------------------------------------- Type B in (FR-01)

    /// <summary>Parses and applies a raw Type B message (DCS feed or pasted). Returns the plates affected.</summary>
    public Task<IReadOnlyList<string>> ApplyTypeBAsync(string raw) => InvokeAsync(() => ApplyTypeB(raw));

    private IReadOnlyList<string> ApplyTypeB(string raw)
    {
        var message = TypeBParser.Parse(raw);
        switch (message)
        {
            case Bsm bsm:
                LogMessage(MessageDirection.In, MessageType.BSM, bsm.Plates[0].Value, raw);
                return ApplyBsm(bsm);
            case Bpm bpm:
                LogMessage(MessageDirection.In, MessageType.BPM, bpm.Plate.Value, raw);
                return [bpm.Plate.Value];
            case Bum bum:
                LogMessage(MessageDirection.In, MessageType.BUM, bum.Plate.Value, raw);
                return [bum.Plate.Value];
            default:
                return [];
        }
    }

    private IReadOnlyList<string> ApplyBsm(Bsm bsm)
    {
        var plates = new List<string>();
        foreach (var plate in bsm.Plates)
        {
            plates.Add(plate.Value);
            _bags.TryGetValue(plate.Value, out var bag);

            if (bsm.Action == BsmAction.Delete)
            {
                if (bag == null || bag.Deleted) continue;
                bag.Deleted = true;
                _writer?.SaveBag(bag);
                if (bag.Status == BagStatus.Loaded)
                {
                    Record(bag, "BSM-DEL", b => { b.MessageType = MessageType.BSM; b.Raw = bsm.Raw; b.Note = "BSM deleted after loading - offload required"; });
                    RaiseException(bag.Plate.Value, bag.Epc, ExceptionType.Offloaded, Severity.Critical,
                        $"BSM for {bag.Plate} deleted but the bag is loaded in {bag.Uld}. Offload it.", bag.Source != BagSource.Virtual, null);
                }
                else
                {
                    Record(bag, "BSM-DEL", b => { b.MessageType = MessageType.BSM; b.Raw = bsm.Raw; b.NewStatus = BagStatus.Offloaded; b.Note = "BSM deleted (bag offloaded)"; });
                }
                continue;
            }

            if (bag == null)
            {
                bag = new Bag
                {
                    Plate = plate, Flight = bsm.Flight, Destination = bsm.Destination,
                    Surname = bsm.Surname ?? "UNKNOWN", Initial = bsm.Initial ?? "X", Class = bsm.Class,
                    TagType = bsm.Class == CabinClass.Economy ? TagType.Disposable : TagType.Reusable,
                    WeightKg = (bsm.WeightKg ?? 0) / Math.Max(1, bsm.Plates.Count),
                    Source = bsm.Flight == Flight.Designator ? BagSource.Virtual : BagSource.Foreign,
                    AuthorityToLoad = bsm.AuthorityToLoad, PassengerStatus = bsm.PassengerStatus,
                };
                // Every new bag gets a synthetic EPC so the simulator can move it; a desk binding replaces it.
                bag.Epc = SyntheticEpc.For(plate);
                _epcToPlate[bag.Epc] = plate.Value;
                _bags[plate.Value] = bag;
                _writer?.SaveBag(bag);
                Record(bag, "BSM", b => { b.MessageType = MessageType.BSM; b.Raw = bsm.Raw; b.NewStatus = BagStatus.Expected; b.Note = "Bag created from BSM"; });
                continue;
            }

            // CHG (or a repeated original): update the passenger/authority data.
            bag.AuthorityToLoad = bsm.AuthorityToLoad;
            bag.PassengerStatus = bsm.PassengerStatus;
            if (bsm.WeightKg is { } kg) bag.WeightKg = kg / Math.Max(1, bsm.Plates.Count);
            _writer?.SaveBag(bag);
            Record(bag, "BSM-CHG", b => { b.MessageType = MessageType.BSM; b.Raw = bsm.Raw;
                b.Note = $"Authority to load {(bsm.AuthorityToLoad ? "Y" : "N")}, passenger {bsm.PassengerStatus}"; });

            if (bag.Status == BagStatus.Loaded && LoadRefusal(bag) is { } refusal)
            {
                // FR-10: a loaded bag whose passenger won't fly must come off before pushback.
                RaiseException(bag.Plate.Value, bag.Epc, refusal.Type, Severity.Critical,
                    $"{refusal.Reason}: offload {bag.Plate} from {bag.Uld}.", bag.Source != BagSource.Virtual, null);
            }
        }
        return plates;
    }

    // ---------------------------------------------------------------- startup / reset

    /// <summary>
    /// Loads persisted state, or creates the flight from the generated BSMs when there is none, then applies any
    /// kept tag bindings. Publishes <see cref="StateReloaded"/>.
    /// </summary>
    public Task InitialiseAsync(StoredState? stored, Func<IEnumerable<string>> manifestBsms) => InvokeAsync(() =>
    {
        _bags.Clear(); _epcToPlate.Clear(); _exceptions.Clear(); _timeline.Clear(); _messages.Clear();
        _stray = 0; _nextForeignSerial = 1;

        if (stored is { Bags.Count: > 0 })
        {
            Rehydrate(stored);
        }
        else
        {
            foreach (var raw in manifestBsms())
                ApplyTypeB(raw);
            AssignRealPool();
            if (stored != null)
                ApplyBindings(stored.Bindings);
        }

        _dirtyBags.Clear();
        _totalsDirty = true;
        _hub.Publish(new StateReloaded());
    });

    /// <summary>The last N bags of the flight are reserved for real tags (no synthetic EPC, never simulated).</summary>
    private void AssignRealPool()
    {
        foreach (var bag in _bags.Values.Where(b => b.Flight == Flight.Designator)
                     .OrderByDescending(b => b.Plate.Value).Take(Options.RealTagPoolSize))
        {
            if (bag.Epc != null) _epcToPlate.Remove(bag.Epc);
            bag.Epc = null;
            bag.Source = BagSource.RealPool;
            _writer?.SaveBag(bag);
        }
    }

    private void ApplyBindings(Dictionary<string, string> bindings)
    {
        foreach (var (epc, plate) in bindings)
        {
            if (!_bags.TryGetValue(plate, out var bag)) continue;
            if (bag.Epc != null) _epcToPlate.Remove(bag.Epc);
            bag.Epc = epc;
            bag.Source = BagSource.RealPool;
            _epcToPlate[epc] = plate;
            _writer?.SaveBag(bag);
        }
    }

    /// <summary>Rebuilds state from the store. Status, ULD and last scan are derived by replaying the event log.</summary>
    private void Rehydrate(StoredState stored)
    {
        foreach (var bag in stored.Bags)
        {
            _bags[bag.Plate.Value] = bag;
            if (bag.Source != BagSource.RealPool && !stored.Bindings.ContainsValue(bag.Plate.Value))
            {
                bag.Epc = SyntheticEpc.For(bag.Plate);
                _epcToPlate[bag.Epc] = bag.Plate.Value;
            }
            if (bag.Flight != Flight.Designator && bag.Plate.Value.StartsWith("07069", StringComparison.Ordinal) &&
                int.TryParse(bag.Plate.Value[5..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var serial))
                _nextForeignSerial = Math.Max(_nextForeignSerial, serial + 1);
        }
        foreach (var (epc, plate) in stored.Bindings)
        {
            if (!_bags.TryGetValue(plate, out var bag)) continue;
            bag.Epc = epc;
            _epcToPlate[epc] = plate;
        }
        foreach (var e in stored.Events)
        {
            if (!_bags.TryGetValue(e.Plate, out var bag)) continue;
            if (e.NewStatus is { } status) bag.Status = status;
            if (e.ScanPoint != null) { bag.LastScanPoint = e.ScanPoint; bag.LastSeenUtc = e.OccurredUtc; }
            if (e.Kind == "Loaded") bag.Uld = e.Uld;
            if (e.Kind == "Offloaded") bag.Uld = null;
            if (!_timeline.TryGetValue(e.Plate, out var list)) _timeline[e.Plate] = list = new();
            list.Add(e);
        }
        foreach (var ex in stored.Exceptions) _exceptions[ex.Id] = ex;
        _messages.AddRange(stored.Messages);
        _stray = stored.StrayReads;
    }

    // ---------------------------------------------------------------- passengers added during the demo

    /// <summary>Plates for passengers added during the demo: 07062 + 5-digit serial (outside the generated manifest).</summary>
    public const string AddedPassengerPrefix = "07062";

    private static bool IsAddedPassengerBag(Bag bag) => bag.Plate.Value.StartsWith(AddedPassengerPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Adds a passenger to KQ-504 as the DCS would, with a BSM (FR-01), and reserves their bags for real tags: the next
    /// tags presented at Desk 14 are bound to these bags, ahead of the rest of the real-tag pool. Returns the new plates.
    /// </summary>
    /// <param name="minSerial">Lowest plate serial to use, so plates keep counting up after the database is cleaned and
    /// never repeat (tags written earlier must not match a new passenger).</param>
    public Task<IReadOnlyList<string>> AddPassengerAsync(string surname, string initial, CabinClass cls, int bags, int weightKg,
        bool authorityToLoad = true, int minSerial = 1) => InvokeAsync<IReadOnlyList<string>>(() =>
    {
        // Type B fields are letters only (no '/' or line breaks).
        var name = new string(surname.ToUpperInvariant().Where(char.IsAsciiLetterUpper).ToArray());
        var first = initial.ToUpperInvariant().FirstOrDefault(char.IsAsciiLetterUpper);
        if (name.Length == 0) throw new ArgumentException("Enter the passenger's surname (letters only).");
        if (first == default) throw new ArgumentException("Enter the passenger's initial.");
        if (bags is < 1 or > 9) throw new ArgumentException("A passenger can check in 1 to 9 bags.");

        var serial = _bags.Keys
            .Where(p => p.StartsWith(AddedPassengerPrefix, StringComparison.Ordinal))
            .Select(p => int.Parse(p[AddedPassengerPrefix.Length..], CultureInfo.InvariantCulture))
            .DefaultIfEmpty(0).Max() + 1;
        serial = Math.Max(serial, minSerial);
        var firstPlate = new LicencePlate($"{AddedPassengerPrefix}{serial:D5}");

        var raw = TypeBBuilder.Bsm(BsmAction.Original, Options.Station, Flight, Options.Destination, cls, firstPlate, bags,
            Math.Max(1, weightKg), authorityToLoad, PassengerStatus.CheckedIn, "", serial, name, first.ToString());
        var plates = ApplyTypeB(raw);

        foreach (var plate in plates)
        {
            var bag = _bags[plate];
            if (bag.Epc != null) _epcToPlate.Remove(bag.Epc);   // drop the synthetic EPC: this bag waits for a real tag
            bag.Epc = null;
            bag.Source = BagSource.RealPool;
            _writer?.SaveBag(bag);
            Touch(bag);
        }
        return plates;
    });

    /// <summary>
    /// The bag a physical tag currently belongs to (linked, or carrying an encoded plate on the manifest), or null if
    /// it belongs to no bag. Used to stop a tag that is already on one passenger's bag being re-written for another.
    /// </summary>
    public Task<BagView?> TagOwnerAsync(string epc) => InvokeAsync<BagView?>(() =>
    {
        var plate = _epcToPlate.GetValueOrDefault(epc)
                    ?? (LicencePlateCodec.TryDecode(epc, out var encoded) && _bags.ContainsKey(encoded.Value) ? encoded.Value : null);
        return plate != null && _bags.TryGetValue(plate, out var bag) ? BagView.From(bag, OpenExceptionFor(plate)) : null;
    });

    /// <summary>
    /// FR-12 audit: someone tried to write a tag that already belongs to another passenger's bag, and the write was
    /// refused. Recorded on both bags: the one that owns the tag and the one it was going to be used for.
    /// </summary>
    public Task RecordTagWriteRefusedAsync(string ownerPlate, string epc, string attemptedPlate, string device) => InvokeAsync(() =>
    {
        _bags.TryGetValue(ownerPlate, out var owner);
        _bags.TryGetValue(attemptedPlate, out var attempted);
        if (owner != null)
            Record(owner, "TagWriteRefused", b =>
            {
                b.ReaderId = device;
                b.Note = $"Write refused at check-in: tried to re-use this bag's tag {epc} for {attempted?.PassengerName ?? "another passenger"}'s bag {attemptedPlate}";
            });
        if (attempted != null)
            Record(attempted, "TagWriteRefused", b =>
            {
                b.ReaderId = device;
                b.Note = $"Write refused at check-in: tag {epc} already belongs to {owner?.PassengerName ?? "another passenger"}'s bag {ownerPlate}";
            });
    });

    /// <summary>
    /// Records that a physical tag has just been written with this bag's licence plate (the POC's "tag printed at
    /// check-in"). The tag's old EPC no longer exists, so any bag it was linked to loses its tag. The next read at
    /// check-in decodes the plate and confirms it against the BSM (FR-02).
    /// </summary>
    public Task RecordTagWrittenAsync(string plate, string oldEpc, string newEpc) => InvokeAsync(() =>
    {
        if (!_bags.TryGetValue(plate, out var bag)) throw new InvalidOperationException($"No bag {plate} on the manifest.");

        if (_epcToPlate.Remove(oldEpc, out var previousPlate) && previousPlate != plate && _bags.TryGetValue(previousPlate, out var previous))
        {
            previous.Epc = null;
            _writer?.SaveBag(previous);
            Record(previous, "TagUnbound", b => b.Note = $"Tag {oldEpc} was re-written with plate {plate}; this bag has no tag now");
        }
        _writer?.DeleteBinding(oldEpc);

        if (bag.Epc != null && bag.Epc != newEpc) _epcToPlate.Remove(bag.Epc);
        bag.Epc = newEpc;
        bag.Source = BagSource.RealPool;
        _epcToPlate[newEpc] = plate;
        _writer?.SaveBag(bag);
        _writer?.SaveBinding(newEpc, plate, Now);
        Record(bag, "TagWritten", b => b.Note = $"Licence plate written to tag at check-in (was {oldEpc}, now {newEpc})");
    });

    // ---------------------------------------------------------------- misroute demo (spec 7.2)

    /// <summary>
    /// Turns a real tag into a bag for another flight (default KQ-412 to DAR) by sending that flight's BSM and moving
    /// the tag's binding to it. The KQ-504 bag it came from goes back to Expected.
    /// </summary>
    public Task<string> FlagForeignAsync(string epc, string flight = "KQ412", string destination = "DAR") => InvokeAsync(() =>
    {
        if (!_epcToPlate.TryGetValue(epc, out var plate)) throw new InvalidOperationException("Tag is not bound to a bag.");
        var original = _bags[plate];
        if (original.Flight != Flight.Designator) throw new InvalidOperationException($"Tag is already on {original.Flight}.");

        var serial = _nextForeignSerial++;
        var foreignPlate = new LicencePlate($"07069{serial:D5}");
        var key = new FlightKey(flight[..2], int.Parse(flight[2..], CultureInfo.InvariantCulture), Flight.Date);
        string[] names = ["OMONDI/P", "ACHIENG/R", "KAMAU/D", "WANJIRU/S", "OTIENO/B"];
        var name = names[serial % names.Length].Split('/');
        var raw = TypeBBuilder.Bsm(BsmAction.Original, Options.Station, key, destination, CabinClass.Economy, foreignPlate, 1, 18,
            true, PassengerStatus.CheckedIn, "31C", serial, name[0], name[1]);
        ApplyTypeB(raw);

        var foreign = _bags[foreignPlate.Value];
        if (foreign.Epc != null) _epcToPlate.Remove(foreign.Epc);
        foreign.Epc = epc;
        foreign.OriginalPlate = original.Plate;
        foreign.Status = BagStatus.CheckedIn;
        _epcToPlate[epc] = foreign.Plate.Value;
        original.Epc = null;
        _writer?.SaveBag(foreign);
        _writer?.SaveBag(original);
        _writer?.SaveBinding(epc, foreign.Plate.Value, Now);

        Record(original, "TagUnbound", b => { b.NewStatus = BagStatus.Expected; b.Note = $"Tag moved to {flight} bag {foreignPlate} (misroute demo)"; });
        Record(foreign, "TagBound", b => { b.NewStatus = BagStatus.CheckedIn; b.Note = $"Tag {epc} now a {flight} bag (misroute demo)"; });
        return foreign.Plate.Value;
    });

    /// <summary>Undoes <see cref="FlagForeignAsync"/>: deletes the foreign BSM and gives the tag back to its KQ-504 bag.</summary>
    public Task<string?> UnflagForeignAsync(string epc) => InvokeAsync<string?>(() =>
    {
        if (!_epcToPlate.TryGetValue(epc, out var plate)) return null;
        var foreign = _bags[plate];
        if (foreign.OriginalPlate is not { } originalPlate || !_bags.TryGetValue(originalPlate.Value, out var original)) return null;

        var key = FlightOf(foreign);
        ApplyTypeB(TypeBBuilder.Bsm(BsmAction.Delete, Options.Station, key, foreign.Destination, foreign.Class, foreign.Plate, 1,
            foreign.WeightKg, true, PassengerStatus.CheckedIn, "31C", 1, foreign.Surname, foreign.Initial));

        foreach (var ex in _exceptions.Values.Where(e => e.IsOpen && e.Plate == foreign.Plate.Value).ToList())
            ResolveException(ex, "DEMO", "Misroute demo undone", overridden: false);

        foreign.Epc = null;
        original.Epc = epc;
        _epcToPlate[epc] = original.Plate.Value;
        _writer?.SaveBag(foreign);
        _writer?.SaveBag(original);
        _writer?.SaveBinding(epc, original.Plate.Value, Now);
        Record(original, "TagRebound", b => { b.NewStatus = BagStatus.Expected; b.Note = "Tag returned to this bag (misroute demo undone)"; });
        return original.Plate.Value;
    });

    // ---------------------------------------------------------------- reader status (audit)

    public void LogReaderStatus(string status, string? reason) => _writer?.LogReaderStatus(Now, status, reason);

    public async ValueTask DisposeAsync()
    {
        _work.Writer.TryComplete();
        await _loop.ConfigureAwait(false);
    }
}
