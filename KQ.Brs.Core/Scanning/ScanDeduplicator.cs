using KQ.Rfid.Alien;

namespace KQ.Brs.Core.Scanning;

/// <summary>A de-duplicated scan of one tag at one scan point (spec 3.4). Idempotency key = reader + EPC + window start.</summary>
public sealed record BagScan(string ReaderId, string Epc, string ScanPointId, int Antenna, double? Rssi, DateTimeOffset WindowStartUtc)
{
    public string IdempotencyKey => KeyFor(ReaderId, Epc, WindowStartUtc);

    public static string KeyFor(string readerId, string epc, DateTimeOffset windowStartUtc) =>
        $"{readerId}|{epc}|{windowStartUtc.ToUnixTimeMilliseconds()}";
}

/// <summary>
/// A closed scan window: the antenna that saw the tag most and the strongest RSSI (spec 3.4 step 1). These complete
/// the window's <see cref="BagScan"/> (same <see cref="ScanKey"/>) and feed the evaluation report.
/// </summary>
public sealed record ScanWindowSummary(
    string ReaderId, string Epc, string ScanPointId, DateTimeOffset StartUtc, DateTimeOffset EndUtc,
    int Reads, int BestAntenna, double? MaxRssi, IReadOnlyDictionary<int, int> ReadsPerAntenna)
{
    public string ScanKey => BagScan.KeyFor(ReaderId, Epc, StartUtc);
}

/// <summary>
/// Collapses raw reads into one <see cref="BagScan"/> per EPC per scan point (spec 3.4.1). The first read opens a
/// window and is emitted immediately (so the decision is made within the 1 s latency target); later reads extend the
/// window; after a quiet period the window closes and a summary with the best antenna and strongest RSSI is emitted. Not thread-safe: one pipeline task owns it.
/// </summary>
public sealed class ScanDeduplicator(TimeSpan window, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<(string Epc, string ScanPoint), Window> _open = new();

    public TimeSpan WindowLength { get; set; } = window;

    /// <summary>
    /// A tag that never leaves the field would otherwise stay in one window forever and never be re-evaluated
    /// (e.g. after it is bound at the desk). After this long, its next read starts a new scan.
    /// </summary>
    public TimeSpan MaxWindow { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Returns a scan if this read opens a new window, otherwise null (a duplicate).</summary>
    public BagScan? Process(TagRead read, string scanPointId)
    {
        var now = _time.GetUtcNow();
        var key = (read.Epc, scanPointId);
        if (_open.TryGetValue(key, out var w) && now - w.LastUtc < WindowLength && now - w.StartUtc < MaxWindow)
        {
            w.Add(read, now);
            return null;
        }

        if (w != null)
        {
            _open.Remove(key);
            Closed?.Invoke(w.Summarise());
        }

        w = new Window(read.ReaderId, read.Epc, scanPointId, now);
        w.Add(read, now);
        _open[key] = w;
        return new BagScan(read.ReaderId, read.Epc, scanPointId, read.Antenna, read.Rssi, now);
    }

    /// <summary>Raised (on the caller's thread) when a window closes.</summary>
    public event Action<ScanWindowSummary>? Closed;

    /// <summary>Closes windows that have been quiet for the window length. Call periodically.</summary>
    public void Tick()
    {
        var now = _time.GetUtcNow();
        foreach (var (key, w) in _open.Where(kv => now - kv.Value.LastUtc >= WindowLength).ToList())
        {
            _open.Remove(key);
            Closed?.Invoke(w.Summarise());
        }
    }

    /// <summary>Forgets an EPC's open windows so its next read counts as a new scan (the ramp "Rescan" button).</summary>
    public void Reset(string epc) => CloseWhere(kv => kv.Key.Epc == epc);

    /// <summary>Forgets all open windows at a scan point (e.g. when the ramp antenna is armed).</summary>
    public void ResetScanPoint(string scanPointId) => CloseWhere(kv => kv.Key.ScanPoint == scanPointId);

    /// <summary>Closes every open window (when reading stops), so their summaries are recorded.</summary>
    public void CloseAll() => CloseWhere(_ => true);

    private void CloseWhere(Func<KeyValuePair<(string Epc, string ScanPoint), Window>, bool> match)
    {
        foreach (var (key, w) in _open.Where(match).ToList())
        {
            _open.Remove(key);
            Closed?.Invoke(w.Summarise());
        }
    }

    private sealed class Window(string readerId, string epc, string scanPoint, DateTimeOffset start)
    {
        private readonly Dictionary<int, int> _perAntenna = new();
        private double? _maxRssi;
        private int _reads;

        public DateTimeOffset StartUtc { get; } = start;
        public DateTimeOffset LastUtc { get; private set; } = start;

        public void Add(TagRead read, DateTimeOffset now)
        {
            _reads++;
            _perAntenna[read.Antenna] = _perAntenna.GetValueOrDefault(read.Antenna) + 1;
            if (read.Rssi is { } r && (_maxRssi == null || r > _maxRssi)) _maxRssi = r;
            LastUtc = now;
        }

        public ScanWindowSummary Summarise() => new(readerId, epc, scanPoint, StartUtc, LastUtc, _reads,
            _perAntenna.OrderByDescending(kv => kv.Value).First().Key, _maxRssi, _perAntenna);
    }
}
