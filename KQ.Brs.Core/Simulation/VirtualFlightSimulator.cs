using System.Globalization;
using KQ.Brs.Core.Domain;
using KQ.Brs.Core.Events;
using KQ.Brs.Core.Persistence;
using KQ.Brs.Core.Reconciliation;
using KQ.Brs.Core.Scanning;
using KQ.Brs.Core.TypeB;

namespace KQ.Brs.Core.Simulation;

/// <summary>
/// Moves the virtual bags through Desk 14 → Belt 04 → Ramp on a virtual clock (speed × real time), alongside the
/// real tags, and injects the faults the POC must handle: passengers not boarding, deleted BSMs, KQ-412 misroutes,
/// missed belt reads, no-shows at the ramp and stray tags. Bags already part-way through (after a restart) continue
/// from their current status. Scans go straight to the engine with reader id SIM.
/// </summary>
public sealed class VirtualFlightSimulator(ReconciliationEngine engine, PersistenceWriter? writer, Func<SimulatorOptions> options)
{
    private static readonly TimeSpan CheckInSpread = TimeSpan.FromMinutes(40);

    private readonly PriorityQueue<Func<Task>, TimeSpan> _agenda = new();
    private readonly Lock _lock = new();
    private CancellationTokenSource? _run;
    private Random _random = new(1);
    private int _foreignSerial;

    public bool IsRunning => _run != null;
    public TimeSpan VirtualTime { get; private set; }
    public int Pending { get { lock (_lock) return _agenda.Count; } }

    /// <summary>Raised on a background thread when running state or progress changes.</summary>
    public event Action? Changed;

    /// <summary>Raised on a background thread if a simulated action fails.</summary>
    public event Action<string>? Error;

    public async Task StartAsync()
    {
        if (_run != null) return;
        lock (_lock)
        {
            if (_agenda.Count > 0)
            {
                // Resume a paused run.
                _run = new CancellationTokenSource();
            }
        }
        if (_run == null)
        {
            await BuildAgendaAsync().ConfigureAwait(false);
            _run = new CancellationTokenSource();
        }
        _ = RunAsync(_run.Token);
        Changed?.Invoke();
    }

    public void Pause()
    {
        _run?.Cancel();
        _run = null;
        Changed?.Invoke();
    }

    /// <summary>Stops and forgets the schedule (used by Reset demo).</summary>
    public void Stop()
    {
        Pause();
        lock (_lock) _agenda.Clear();
        VirtualTime = TimeSpan.Zero;
        Changed?.Invoke();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        var last = DateTimeOffset.UtcNow;
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var now = DateTimeOffset.UtcNow;
                VirtualTime += (now - last) * Math.Max(0.1, options().Speed);
                last = now;

                while (true)
                {
                    Func<Task>? action;
                    lock (_lock)
                    {
                        if (!_agenda.TryPeek(out action, out var due) || due > VirtualTime) break;
                        _agenda.Dequeue();
                    }
                    try { await action().ConfigureAwait(false); }
                    catch (Exception ex) { Error?.Invoke(ex.Message); }
                }

                if (Pending == 0)
                {
                    _run = null;
                    Changed?.Invoke();
                    return;
                }
                Changed?.Invoke();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void At(TimeSpan when, Func<Task> action)
    {
        lock (_lock) _agenda.Enqueue(action, when);
    }

    private TimeSpan Between(double minMinutes, double maxMinutes) =>
        TimeSpan.FromMinutes(minMinutes + _random.NextDouble() * (maxMinutes - minMinutes));

    private async Task BuildAgendaAsync()
    {
        var o = options();
        _random = new Random(o.Seed);
        VirtualTime = TimeSpan.Zero;
        var bags = await engine.VirtualBagsAsync().ConfigureAwait(false);
        var active = bags.Where(b => !b.Deleted && b.Status is not (BagStatus.Loaded or BagStatus.Offloaded)).ToList();

        // Pick fault bags deterministically from those still at the start of their journey.
        var fresh = active.Where(b => b.Status == BagStatus.Expected && b.AuthorityToLoad).OrderBy(_ => _random.Next()).ToList();
        var notBoarded = fresh.Take(o.PaxNotBoarded).Select(b => b.Plate).ToHashSet();
        var deleted = fresh.Skip(o.PaxNotBoarded).Take(o.DeletedBsms).Select(b => b.Plate).ToHashSet();

        foreach (var bag in active)
        {
            var desk = bag.Status == BagStatus.Expected ? Between(0, CheckInSpread.TotalMinutes) : TimeSpan.Zero;
            var belt = desk + (bag.Status < BagStatus.Sorted ? Between(2, 6) : TimeSpan.Zero);
            var ramp = belt + Between(3, 10);
            var plate = bag.Plate;
            var uld = bag.Class == CabinClass.Economy ? "AK8" : "AK7";

            if (bag.Status == BagStatus.Expected)
                At(desk, () => ScanAsync(plate, ScanPoint.Desk14, null));

            if (deleted.Contains(plate))
            {
                // DCS deletes the BSM before the belt; the bag still travels, the belt catches it, a handler removes it.
                At(desk + TimeSpan.FromMinutes(1), () => SendBsmChangeAsync(plate, BsmAction.Delete));
                At(belt, () => ScanAsync(plate, ScanPoint.Belt04, null));
                At(belt + Between(0.3, 2), () => engine.SimulateRescanAsync(plate));
                continue;
            }

            if (bag.Status < BagStatus.Sorted && _random.NextDouble() >= o.MissedBeltReadRate)
                At(belt, () => ScanAsync(plate, ScanPoint.Belt04, null));

            if (notBoarded.Contains(plate))
            {
                // Passenger doesn't board: CHG with status N; the ramp refuses; the handler offloads after a while.
                At(belt + TimeSpan.FromSeconds(30), () => SendBsmChangeAsync(plate, BsmAction.Change));
                At(ramp, () => ScanAsync(plate, ScanPoint.Ramp, uld));
                At(ramp + Between(0.3, 2), () => engine.SimulateRescanAsync(plate));
                continue;
            }

            if (!bag.AuthorityToLoad)
            {
                // Ticket problem: refused at the ramp; the handler sets the bag aside and rescans it.
                At(ramp, () => ScanAsync(plate, ScanPoint.Ramp, uld));
                At(ramp + Between(0.3, 2), () => engine.SimulateRescanAsync(plate));
            }
            else if (_random.NextDouble() >= o.NoShowAtRampRate)
            {
                At(ramp, () => ScanAsync(plate, ScanPoint.Ramp, uld));
            }
        }

        // KQ-412 bags that end up in the KQ-504 lane (FR-07).
        for (var i = 0; i < o.Misroutes; i++)
        {
            var when = Between(10, 40);
            At(when, async () =>
            {
                var plate = await CreateMisrouteAsync().ConfigureAwait(false);
                At(VirtualTime + TimeSpan.FromSeconds(5), () => ScanAsync(plate, ScanPoint.Belt04, null));
                At(VirtualTime + Between(0.5, 2.5), () => engine.SimulateRescanAsync(plate));
            });
        }

        // Stray tags seen by the tunnel (not bag tags).
        for (var i = 0; i < o.StrayTags; i++)
        {
            var epc = $"DEAD{_random.Next():X8}{_random.Next():X8}{i:X4}";
            At(Between(5, 50), () => engine.RecordScanAsync(new BagScan(ReconciliationEngine.SimReaderId, epc, ScanPoint.Belt04, 1, 2000, DateTimeOffset.UtcNow)));
        }
    }

    private async Task ScanAsync(string plate, string scanPoint, string? uld)
    {
        var epc = SyntheticEpc.For(new LicencePlate(plate));
        var (antennas, best) = SyntheticAntennaReads(scanPoint);
        var rssi = Math.Round(Math.Max(500, 9000 + NextGaussian() * 1500), 1);
        var now = DateTimeOffset.UtcNow;
        await engine.RecordScanAsync(new BagScan(ReconciliationEngine.SimReaderId, epc, scanPoint, best, rssi, now), uld).ConfigureAwait(false);
        writer?.SaveScanWindow(new ScanWindowSummary(ReconciliationEngine.SimReaderId, epc, scanPoint, now, now.AddMilliseconds(400),
            antennas.Values.Sum(), best, rssi, antennas));
    }

    /// <summary>Per-antenna read counts that look like a real tunnel (2 antennas, each reading ~96-97 % of bags).</summary>
    private (Dictionary<int, int> Reads, int Best) SyntheticAntennaReads(string scanPoint)
    {
        var reads = new Dictionary<int, int>();
        switch (scanPoint)
        {
            case ScanPoint.Desk14: reads[0] = _random.Next(1, 4); break;
            case ScanPoint.Ramp: reads[3] = _random.Next(1, 4); break;
            default:
                if (_random.NextDouble() < 0.97) reads[1] = _random.Next(1, 7);
                if (_random.NextDouble() < 0.96) reads[2] = _random.Next(1, 7);
                if (reads.Count == 0) reads[_random.Next(1, 3)] = 1;
                break;
        }
        return (reads, reads.OrderByDescending(kv => kv.Value).First().Key);
    }

    private double NextGaussian() =>
        Math.Sqrt(-2.0 * Math.Log(1.0 - _random.NextDouble())) * Math.Sin(2.0 * Math.PI * _random.NextDouble());

    private async Task SendBsmChangeAsync(string plate, BsmAction action)
    {
        var bag = (await engine.SnapshotAsync().ConfigureAwait(false)).Bags.FirstOrDefault(b => b.Plate == plate);
        if (bag == null) return;
        var name = bag.PassengerName.Split(". ");
        var raw = TypeBBuilder.Bsm(action, engine.Options.Station, engine.Flight, engine.Options.Destination, bag.Class,
            new LicencePlate(plate), 1, bag.WeightKg, bag.AuthorityToLoad,
            action == BsmAction.Change ? PassengerStatus.NotBoarded : bag.PassengerStatus, "00A", 0, name[^1], name[0]);
        await engine.ApplyTypeBAsync(raw).ConfigureAwait(false);
    }

    private async Task<string> CreateMisrouteAsync()
    {
        var serial = Interlocked.Increment(ref _foreignSerial);
        var plate = new LicencePlate($"07068{serial:D5}");
        var flight = new FlightKey("KQ", 412, engine.Flight.Date);
        var raw = TypeBBuilder.Bsm(BsmAction.Original, engine.Options.Station, flight, "DAR", CabinClass.Economy, plate, 1,
            21, true, PassengerStatus.CheckedIn, "22B", serial, "HASSAN", "M");
        await engine.ApplyTypeBAsync(raw).ConfigureAwait(false);
        return plate.Value;
    }

    public override string ToString() => $"Sim {VirtualTime.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)}, {Pending} pending";
}
