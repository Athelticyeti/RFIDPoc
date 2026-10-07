using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using KQ.Brs.Core.Domain;
using KQ.Brs.Core.Events;
using KQ.Brs.Core.Reconciliation;
using KQ.Brs.Poc.Services;
using Microsoft.UI.Dispatching;

namespace KQ.Brs.Poc.ViewModels;

/// <summary>
/// UI-thread model of the flight. Engine events arrive on background threads and are only queued; a 100 ms
/// DispatcherQueueTimer drains the queue, coalesces bag updates by plate and updates rows in place, so even a
/// simulator at 60× or a busy reader never floods the UI thread.
/// </summary>
public sealed partial class AppState : ObservableObject
{
    private readonly ConcurrentQueue<BrsEvent> _queue = new();
    private readonly ConcurrentQueue<ReaderHealth> _health = new();
    private readonly Dictionary<string, BagRowViewModel> _bagsByPlate = new();
    private readonly Dictionary<string, ExceptionRowViewModel> _exceptionsById = new();
    private readonly Dictionary<string, MessageRowViewModel> _messagesById = new();
    private readonly HashSet<string> _alarmed = new();
    private readonly Dictionary<string, DateTimeOffset> _unlinkedToasts = new();
    private readonly DispatcherQueueTimer _timer;
    private DateTimeOffset _lastSlowRefresh;
    private Func<Task<EngineSnapshot>>? _snapshot;

    public AppState(DispatcherQueue dispatcher)
    {
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(100);
        _timer.Tick += (_, _) => Drain();
        _timer.Start();
    }

    public ObservableCollection<BagRowViewModel> Bags { get; } = new();
    public ObservableCollection<ExceptionRowViewModel> Exceptions { get; } = new();
    public ObservableCollection<MessageRowViewModel> Inbox { get; } = new();
    public ObservableCollection<MessageRowViewModel> Outbox { get; } = new();
    public ObservableCollection<DecisionViewModel> Decisions { get; } = new();
    public ObservableCollection<ScanLogViewModel> ScanLog { get; } = new();

    [ObservableProperty] public partial string FlightTitle { get; set; } = "KQ-504";
    [ObservableProperty] public partial FlightTotals Totals { get; set; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    [ObservableProperty] public partial ReaderHealth Health { get; set; } = new(SupervisorState.Disconnected, "Not connected");
    [ObservableProperty] public partial DecisionViewModel? LastDecision { get; set; }
    [ObservableProperty] public partial bool SimRunning { get; set; }
    [ObservableProperty] public partial string SimStatus { get; set; } = "Simulator idle";
    [ObservableProperty] public partial bool OutboxPaused { get; set; }
    [ObservableProperty] public partial int QueuedMessages { get; set; }

    /// <summary>Raised on the UI thread for bags whose data changed this tick (pages refresh their filters).</summary>
    public event Action<IReadOnlyCollection<BagRowViewModel>>? BagsChanged;

    /// <summary>A critical exception on a real tag: lock the ramp screen (FR-06).</summary>
    public event Action<ExceptionRowViewModel>? AlarmRaised;

    public event Action<string>? AlarmCleared;

    /// <summary>Short notifications (title, message, isError).</summary>
    public event Action<string, string, bool>? Toast;

    public void Post(BrsEvent e) => _queue.Enqueue(e);

    public void Post(ReaderHealth health) => _health.Enqueue(health);

    public void ShowToast(string title, string message, bool isError = false) => Toast?.Invoke(title, message, isError);

    /// <summary>Loads everything from the engine (startup, reset).</summary>
    public async Task LoadAsync(Func<Task<EngineSnapshot>> snapshot)
    {
        _snapshot = snapshot;
        var s = await snapshot();
        _queue.Clear();
        _bagsByPlate.Clear(); _exceptionsById.Clear(); _messagesById.Clear(); _alarmed.Clear();
        Bags.Clear(); Exceptions.Clear(); Inbox.Clear(); Outbox.Clear(); Decisions.Clear(); ScanLog.Clear();

        FlightTitle = $"{s.Flight} · {s.Flight.Date:dd MMM yyyy} · NBO → EBB";
        foreach (var bag in s.Bags) AddBag(bag);
        foreach (var e in s.Exceptions.OrderByDescending(e => e.RaisedUtc)) { var row = new ExceptionRowViewModel(e); _exceptionsById[e.Id] = row; Exceptions.Add(row); }
        foreach (var m in s.Messages.TakeLast(1000).Reverse()) AddMessage(m, atTop: false);
        Totals = s.Totals;
        OutboxPaused = s.OutboxPaused;
        QueuedMessages = Outbox.Count(m => m.IsQueued);
        BagsChanged?.Invoke(Bags);
    }

    public BagRowViewModel? FindBag(string plate) => _bagsByPlate.GetValueOrDefault(plate);

    private BagRowViewModel AddBag(BagView bag)
    {
        var row = new BagRowViewModel(bag);
        _bagsByPlate[bag.Plate] = row;
        Bags.Add(row);
        return row;
    }

    private void AddMessage(TypeBMessage m, bool atTop)
    {
        var target = m.Direction == MessageDirection.In ? Inbox : Outbox;
        var row = new MessageRowViewModel(m);
        _messagesById[m.Id] = row;
        if (atTop) target.Insert(0, row); else target.Add(row);
        while (target.Count > 1000) { _messagesById.Remove(target[^1].Id); target.RemoveAt(target.Count - 1); }
    }

    private bool _stopped;

    /// <summary>Stops all UI updates (window closing): bound controls must not be touched once the window is gone.</summary>
    public void Stop()
    {
        _stopped = true;
        _timer.Stop();
    }

    private void Drain()
    {
        if (_stopped) return;
        while (_health.TryDequeue(out var h)) Health = h;

        var bagUpdates = new Dictionary<string, BagView>();
        FlightTotals? totals = null;
        var reload = false;
        var processed = 0;
        while (processed++ < 5000 && _queue.TryDequeue(out var e))
        {
            switch (e)
            {
                case BagUpdated u: bagUpdates[u.Bag.Plate] = u.Bag; break;
                case TotalsUpdated t: totals = t.Totals; break;
                case ExceptionUpdated x: ApplyException(x.Exception); break;
                case MessageLogged m: ApplyMessage(m.Message); break;
                case LoadDecided d:
                    var vm = new DecisionViewModel(d.Decision);
                    if (d.Decision.IsReal || LastDecision == null || !LastDecision.Decision.IsReal) LastDecision = vm;
                    Decisions.Insert(0, vm);
                    if (Decisions.Count > 60) Decisions.RemoveAt(Decisions.Count - 1);
                    break;
                case ScanProcessed sp when sp.IsReal:
                    ScanLog.Insert(0, new ScanLogViewModel(sp, sp.Plate is { } p ? FindBag(p)?.Passenger : null));
                    if (ScanLog.Count > 300) ScanLog.RemoveAt(ScanLog.Count - 1);
                    // A tag at check-in with nobody waiting for it: tell the operator (at most every 15 s per tag).
                    if (AppServices.Settings.ShowUnlinkedTagAlerts && sp.ScanPointId == ScanPoint.Desk14 && sp.Outcome == ScanOutcome.Unknown &&
                        (!_unlinkedToasts.TryGetValue(sp.Epc, out var last) || sp.Utc - last > TimeSpan.FromSeconds(15)))
                    {
                        _unlinkedToasts[sp.Epc] = sp.Utc;
                        Toast?.Invoke("Tag not linked", $"Tag …{sp.Epc[^6..]}: {sp.Detail}", true);
                    }
                    break;
                case TagBound b:
                    Toast?.Invoke("Bag tag printed", $"Tag …{b.Epc[^6..]} bound to {b.Plate} ({b.PassengerName}) at Desk 14", false);
                    break;
                case AlarmCleared c:
                    _alarmed.Remove(c.ExceptionId);
                    AlarmCleared?.Invoke(c.ExceptionId);
                    break;
                case StateReloaded:
                    reload = true;
                    break;
            }
        }

        if (reload && _snapshot != null)
        {
            _ = LoadAsync(_snapshot);
            return;
        }

        if (bagUpdates.Count > 0)
        {
            var changed = new List<BagRowViewModel>(bagUpdates.Count);
            foreach (var bag in bagUpdates.Values)
            {
                if (_bagsByPlate.TryGetValue(bag.Plate, out var row)) row.Update(bag);
                else row = AddBag(bag);
                changed.Add(row);
            }
            BagsChanged?.Invoke(changed);
        }
        if (totals != null) Totals = totals;

        // Once a second: age the IN SCAN highlights, "x s ago" texts and exception ages.
        var now = DateTimeOffset.UtcNow;
        if (now - _lastSlowRefresh > TimeSpan.FromSeconds(1))
        {
            _lastSlowRefresh = now;
            var refreshed = new List<BagRowViewModel>();
            foreach (var row in Bags)
            {
                if (!row.IsRecentlySeen(now)) continue;
                var before = row.Badge;
                row.Refresh(now);
                if (row.Badge != before) refreshed.Add(row);
            }
            if (refreshed.Count > 0) BagsChanged?.Invoke(refreshed);
            foreach (var x in Exceptions) if (x.IsOpen) x.Refresh(now);
        }
    }

    private void ApplyException(ExceptionView e)
    {
        if (_exceptionsById.TryGetValue(e.Id, out var row)) row.Update(e);
        else
        {
            row = new ExceptionRowViewModel(e);
            _exceptionsById[e.Id] = row;
            Exceptions.Insert(0, row);
        }

        if (e.State == ExceptionState.Resolved)
        {
            if (_alarmed.Remove(e.Id)) AlarmCleared?.Invoke(e.Id);
        }
        else if (e.Severity == Severity.Critical && e.Source != BagSource.Virtual && _alarmed.Add(e.Id))
        {
            AlarmRaised?.Invoke(row);
        }
    }

    private void ApplyMessage(TypeBMessage m)
    {
        if (_messagesById.TryGetValue(m.Id, out var existing))
        {
            // Sent after a queue (MQ back up): replace the row so its state text updates.
            var list = m.Direction == MessageDirection.In ? Inbox : Outbox;
            var index = list.IndexOf(existing);
            existing.Message = m;
            if (index >= 0) list[index] = new MessageRowViewModel(m);
            _messagesById[m.Id] = index >= 0 ? list[index] : existing;
        }
        else
        {
            AddMessage(m, atTop: true);
        }
        QueuedMessages = Outbox.Count(r => r.IsQueued);
    }
}
