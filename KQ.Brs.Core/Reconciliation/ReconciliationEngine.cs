using System.Threading.Channels;
using KQ.Brs.Core.Domain;
using KQ.Brs.Core.Events;
using KQ.Brs.Core.Persistence;
using KQ.Brs.Core.Scanning;
using KQ.Brs.Core.TypeB;

namespace KQ.Brs.Core.Reconciliation;

/// <summary>
/// The BRS core (spec 4.2): matches scans to the KQ-504 manifest, decides loads (FR-05), raises and resolves
/// exceptions (FR-06..08) and emits BPM/BUM. All state is owned by one consumer task (an actor): callers post work
/// through <see cref="InvokeAsync{T}"/>, so there are no locks, and every change is published on the event hub and
/// queued for persistence.
/// </summary>
public sealed partial class ReconciliationEngine : IAsyncDisposable
{
    public const string SimReaderId = "SIM";
    public const string RampHandler = "RAMP-HH1";

    /// <summary>Reader id for a deliberate handheld trigger (manual scan): never ignored by the ramp arm switch.</summary>
    public const string ManualReaderId = "HANDHELD";

    private readonly Channel<Action> _work = Channel.CreateUnbounded<Action>(new() { SingleReader = true });
    private readonly BrsEventHub _hub;
    private readonly PersistenceWriter? _writer;
    private readonly TimeProvider _time;
    private readonly Task _loop;

    private readonly Dictionary<string, Bag> _bags = new();
    private readonly Dictionary<string, string> _epcToPlate = new();
    private readonly Dictionary<string, BagException> _exceptions = new();
    private readonly Dictionary<string, List<BagEvent>> _timeline = new();
    private readonly List<TypeBMessage> _messages = new();
    private readonly HashSet<string> _dirtyBags = new();
    private bool _totalsDirty;
    private int _stray;
    private int _nextForeignSerial = 1;

    public ReconciliationEngine(BrsOptions options, BrsEventHub hub, PersistenceWriter? writer = null, TimeProvider? time = null)
    {
        Options = options;
        _hub = hub;
        _writer = writer;
        _time = time ?? TimeProvider.System;
        Flight = options.FlightKey(DateOnly.FromDateTime(_time.GetLocalNow().DateTime));
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>Settings; replaced as a whole from the UI (read on the engine thread).</summary>
    public BrsOptions Options { get; set; }

    public FlightKey Flight { get; }

    // Engine-thread state that the UI changes through commands.
    private string _selectedUld = "AK7";
    // Starts disarmed: on a bench, tags lying near the ramp antenna would otherwise raise Unknown alarms at once.
    private bool _rampArmed;
    private bool _outboxPaused;

    // ------------------------------------------------------------------ plumbing

    /// <summary>Runs <paramref name="action"/> on the engine thread, then publishes the resulting changes.</summary>
    public Task<T> InvokeAsync<T>(Func<T> action)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_work.Writer.TryWrite(() =>
            {
                try { tcs.TrySetResult(action()); }
                catch (Exception ex) { tcs.TrySetException(ex); }
                finally { PublishChanges(); }
            }))
            tcs.TrySetException(new ObjectDisposedException(nameof(ReconciliationEngine)));
        return tcs.Task;
    }

    public Task InvokeAsync(Action action) => InvokeAsync(() => { action(); return true; });

    private async Task LoopAsync()
    {
        await foreach (var work in _work.Reader.ReadAllAsync().ConfigureAwait(false))
            work();
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    private void Touch(Bag bag)
    {
        _dirtyBags.Add(bag.Plate.Value);
        _totalsDirty = true;
    }

    private void PublishChanges()
    {
        foreach (var plate in _dirtyBags)
        {
            if (_bags.TryGetValue(plate, out var bag))
                _hub.Publish(new BagUpdated(BagView.From(bag, OpenExceptionFor(plate))));
        }
        _dirtyBags.Clear();
        if (_totalsDirty)
        {
            _totalsDirty = false;
            _hub.Publish(new TotalsUpdated(ComputeTotals()));
        }
    }

    // ------------------------------------------------------------------ queries

    public Task<EngineSnapshot> SnapshotAsync() => InvokeAsync(() => new EngineSnapshot(
        Flight,
        _bags.Values.OrderBy(b => b.Plate.Value).Select(b => BagView.From(b, OpenExceptionFor(b.Plate.Value))).ToList(),
        _exceptions.Values.OrderByDescending(e => e.RaisedUtc).Select(ExceptionView.From).ToList(),
        _messages.ToList(),
        ComputeTotals(),
        _selectedUld, _rampArmed, _outboxPaused));

    public Task<IReadOnlyList<BagEvent>> TimelineAsync(string plate) => InvokeAsync<IReadOnlyList<BagEvent>>(() =>
        _timeline.TryGetValue(plate, out var list) ? list.ToList() : []);

    /// <summary>Plate a tag is bound to (real binding or synthetic), if any.</summary>
    public Task<string?> PlateForEpcAsync(string epc) => InvokeAsync(() => _epcToPlate.GetValueOrDefault(epc));

    /// <summary>Virtual bags of this flight, for the simulator to schedule.</summary>
    public Task<IReadOnlyList<BagView>> VirtualBagsAsync() => InvokeAsync<IReadOnlyList<BagView>>(() =>
        _bags.Values.Where(b => b.Source == BagSource.Virtual && b.Flight == Flight.Designator)
            .OrderBy(b => b.Plate.Value).Select(b => BagView.From(b, null)).ToList());

    private ExceptionType? OpenExceptionFor(string plate)
    {
        ExceptionType? worst = null;
        var worstSeverity = Severity.Info;
        foreach (var e in _exceptions.Values)
        {
            if (e.Plate != plate || !e.IsOpen) continue;
            if (worst == null || e.Severity > worstSeverity) { worst = e.Type; worstSeverity = e.Severity; }
        }
        return worst;
    }

    private FlightTotals ComputeTotals()
    {
        int expected = 0, checkedIn = 0, sorted = 0, loaded = 0, offloaded = 0, ak7 = 0, ak8 = 0, realBound = 0, realPool = 0;
        foreach (var b in _bags.Values)
        {
            if (b.Flight != Flight.Designator) continue;
            if (b.Deleted || b.Status == BagStatus.Offloaded) { offloaded++; continue; }
            expected++;
            if (b.Status is >= BagStatus.CheckedIn and < BagStatus.Offloaded) checkedIn++;
            if (b.Status is >= BagStatus.Sorted and < BagStatus.Offloaded) sorted++;
            if (b.Status == BagStatus.Loaded)
            {
                loaded++;
                if (b.Uld == "AK7") ak7++;
                else if (b.Uld == "AK8") ak8++;
            }
            if (b.Source == BagSource.RealPool) { realPool++; if (b.Epc != null) realBound++; }
        }
        var open = _exceptions.Values.Count(e => e.State == ExceptionState.Open);
        return new FlightTotals(expected, checkedIn, sorted, loaded, offloaded, open, ak7, ak8, _stray, realBound, realPool);
    }

    // ------------------------------------------------------------------ ramp context

    public Task SetRampAsync(string uld, bool armed) => InvokeAsync(() =>
    {
        _selectedUld = uld;
        _rampArmed = armed;
    });

    /// <summary>
    /// Completes a scan once its de-duplication window closes (spec 3.4 step 1): the events recorded for it now show
    /// the antenna that saw the tag most and the strongest RSSI, not just the first read. The decision itself was
    /// already made on the first read. On disk the event row is completed through scan_windows (bag_events stays
    /// append-only).
    /// </summary>
    public Task CompleteScanAsync(ScanWindowSummary w) => InvokeAsync(() =>
    {
        if (_epcToPlate.GetValueOrDefault(w.Epc) is not { } plate || !_timeline.TryGetValue(plate, out var list)) return;
        var key = w.ScanKey;
        var changed = false;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (list[i].ScanKey != key || list[i].ScanPoint != w.ScanPointId) continue;
            list[i] = list[i] with { Antenna = w.BestAntenna, Rssi = w.MaxRssi ?? list[i].Rssi };
            changed = true;
        }
        if (changed && _bags.TryGetValue(plate, out var bag)) Touch(bag);
    });

    /// <summary>Arms or disarms the loading antenna, keeping the selected ULD.</summary>
    public Task SetRampArmedAsync(bool armed) => InvokeAsync(() => _rampArmed = armed);

    // ------------------------------------------------------------------ events, messages, exceptions

    private BagEvent Record(Bag bag, string kind, Action<BagEventBuilder>? configure = null)
    {
        var builder = new BagEventBuilder();
        configure?.Invoke(builder);
        var e = new BagEvent
        {
            Id = BagEvent.NewId(), Plate = bag.Plate.Value, Flight = bag.Flight, Kind = kind, OccurredUtc = Now,
            ScanPoint = builder.ScanPoint, MessageType = builder.MessageType, ReaderId = builder.ReaderId,
            Antenna = builder.Antenna, Rssi = builder.Rssi, Outcome = builder.Outcome, LoadAuthorised = builder.LoadAuthorised,
            Uld = builder.Uld, Handler = builder.Handler, NewStatus = builder.NewStatus, RawMessage = builder.Raw, Note = builder.Note, ScanKey = builder.ScanKey,
        };

        if (e.NewStatus is { } status) bag.Status = status;
        if (e.ScanPoint != null)
        {
            bag.LastScanPoint = e.ScanPoint;
            bag.LastSeenUtc = e.OccurredUtc;
        }
        if (kind == "Loaded") bag.Uld = e.Uld;

        if (!_timeline.TryGetValue(bag.Plate.Value, out var list)) _timeline[bag.Plate.Value] = list = new();
        list.Add(e);
        _writer?.AppendEvent(e);
        Touch(bag);
        return e;
    }

    private sealed class BagEventBuilder
    {
        public string? ScanPoint;
        public MessageType? MessageType;
        public string? ReaderId;
        public int? Antenna;
        public double? Rssi;
        public ScanOutcome? Outcome;
        public bool? LoadAuthorised;
        public string? Uld;
        public string? Handler;
        public BagStatus? NewStatus;
        public string? Raw;
        public string? Note;
        public string? ScanKey;

        public BagEventBuilder Scan(BagScan s)
        {
            ScanKey = s.IdempotencyKey;
            ScanPoint = s.ScanPointId;
            ReaderId = s.ReaderId;
            Antenna = s.Antenna;
            Rssi = s.Rssi;
            return this;
        }
    }

    private TypeBMessage LogMessage(MessageDirection direction, MessageType type, string? plate, string raw)
    {
        var now = Now;
        // Outbound messages wait in the outbox while "MQ" is down (at-least-once delivery demo).
        var sent = direction == MessageDirection.In || !_outboxPaused ? now : (DateTimeOffset?)null;
        var message = new TypeBMessage(BagEvent.NewId(), direction, type, plate, raw, now, sent);
        _messages.Add(message);
        if (_messages.Count > 3000) _messages.RemoveRange(0, 500);
        _writer?.SaveMessage(message);
        _hub.Publish(new MessageLogged(message));
        return message;
    }

    private string SendBpm(Bag bag, string scanPoint, string? uld)
    {
        var raw = TypeBBuilder.Bpm(Options.Station, scanPoint, Now, FlightOf(bag), bag.Destination, bag.Class, bag.Plate, uld);
        LogMessage(MessageDirection.Out, MessageType.BPM, bag.Plate.Value, raw);
        return raw;
    }

    private string SendBum(Bag bag, string reason)
    {
        var raw = TypeBBuilder.Bum(Options.Station, FlightOf(bag), bag.Destination, bag.Plate, reason);
        LogMessage(MessageDirection.Out, MessageType.BUM, bag.Plate.Value, raw);
        return raw;
    }

    private FlightKey FlightOf(Bag bag) =>
        bag.Flight == Flight.Designator ? Flight : new FlightKey(bag.Flight[..2], int.Parse(bag.Flight[2..]), Flight.Date);

    /// <summary>Simulate MQ outage: outbound messages queue until resumed, then all are sent (at least once).</summary>
    public Task<int> SetOutboxPausedAsync(bool paused) => InvokeAsync(() =>
    {
        _outboxPaused = paused;
        if (paused) return 0;
        var flushed = 0;
        for (var i = 0; i < _messages.Count; i++)
        {
            if (_messages[i].SentUtc != null) continue;
            _messages[i] = _messages[i] with { SentUtc = Now };
            _writer?.SaveMessage(_messages[i]);
            _hub.Publish(new MessageLogged(_messages[i]));
            flushed++;
        }
        return flushed;
    });

    private BagException RaiseException(string plate, string? epc, ExceptionType type, Severity severity, string message,
        bool isReal, string? scanPoint)
    {
        // One open exception per bag (or unknown tag) and type.
        var existing = _exceptions.Values.FirstOrDefault(e => e.IsOpen && e.Type == type &&
            (plate != UnknownPlate ? e.Plate == plate : e.Epc == epc));
        if (existing != null) return existing;

        var ex = new BagException
        {
            Id = BagEvent.NewId(), Plate = plate, Epc = epc, Type = type, Severity = severity, RaisedUtc = Now,
            Message = message, Source = isReal ? BagSource.RealPool : BagSource.Virtual, ScanPoint = scanPoint,
        };
        _exceptions[ex.Id] = ex;
        _writer?.SaveException(ex);
        _hub.Publish(new ExceptionUpdated(ExceptionView.From(ex)));
        if (_bags.TryGetValue(plate, out var bag)) Touch(bag);
        _totalsDirty = true;
        return ex;
    }

    private void ResolveException(BagException ex, string by, string note, bool overridden)
    {
        if (!ex.IsOpen) return;
        ex.State = ExceptionState.Resolved;
        ex.ResolvedUtc = Now;
        ex.ResolvedBy = by;
        ex.ResolutionNote = note;
        ex.Overridden = overridden;
        _writer?.SaveException(ex);
        _hub.Publish(new ExceptionUpdated(ExceptionView.From(ex)));
        if (ex.Severity == Severity.Critical) _hub.Publish(new AlarmCleared(ex.Id, overridden ? "Supervisor override" : note));
        if (_bags.TryGetValue(ex.Plate, out var bag)) Touch(bag);
        _totalsDirty = true;
    }

    public const string UnknownPlate = "UNKNOWN";

    public Task<bool> AcknowledgeAsync(string exceptionId) => InvokeAsync(() =>
    {
        if (!_exceptions.TryGetValue(exceptionId, out var ex) || ex.State != ExceptionState.Open) return false;
        ex.State = ExceptionState.Acknowledged;
        ex.AcknowledgedUtc = Now;
        _writer?.SaveException(ex);
        _hub.Publish(new ExceptionUpdated(ExceptionView.From(ex)));
        _totalsDirty = true;
        return true;
    });

    /// <summary>Closes an exception without a rescan. Critical (alarm) exceptions need a supervisor and a reason (spec 8, audit).</summary>
    public Task<bool> OverrideAsync(string exceptionId, string supervisor, string reason) => InvokeAsync(() =>
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A reason is required for an override.");
        if (!_exceptions.TryGetValue(exceptionId, out var ex) || !ex.IsOpen) return false;
        ResolveException(ex, supervisor, $"Supervisor override: {reason}", overridden: true);
        if (_bags.TryGetValue(ex.Plate, out var bag))
            Record(bag, "Override", b => { b.Handler = supervisor; b.Note = $"{ex.Type} overridden: {reason}"; });
        return true;
    });

    /// <summary>Resolves a non-critical exception (warnings) with a note.</summary>
    public Task<bool> ResolveAsync(string exceptionId, string handler, string note) => InvokeAsync(() =>
    {
        if (!_exceptions.TryGetValue(exceptionId, out var ex) || !ex.IsOpen) return false;
        if (ex.Severity == Severity.Critical)
            throw new InvalidOperationException("Critical exceptions are resolved by a rescan or a supervisor override.");
        ResolveException(ex, handler, note, overridden: false);
        if (_bags.TryGetValue(ex.Plate, out var bag))
            Record(bag, "ExceptionResolved", b => { b.Handler = handler; b.Note = $"{ex.Type}: {note}"; });
        return true;
    });
}
