using System.Collections.Concurrent;
using KQ.Brs.Core.Domain;
using KQ.Brs.Core.Persistence;
using KQ.Brs.Core.Reconciliation;
using KQ.Rfid.Alien;

namespace KQ.Brs.Core.Scanning;

/// <summary>Live per-antenna figures for the Live reads page.</summary>
public sealed class AntennaStats
{
    private long _reads;
    public int Antenna { get; init; }
    public long Reads => Interlocked.Read(ref _reads);
    public string? LastEpc { get; private set; }
    public double? LastRssi { get; private set; }
    public double MaxRssi { get; private set; }
    public DateTimeOffset? LastReadUtc { get; private set; }
    public ConcurrentDictionary<string, byte> UniqueEpcs { get; } = new();

    internal void Add(TagRead read)
    {
        Interlocked.Increment(ref _reads);
        LastEpc = read.Epc;
        LastRssi = read.Rssi;
        if (read.Rssi > MaxRssi) MaxRssi = read.Rssi.Value;
        LastReadUtc = read.LastSeenUtc;
        UniqueEpcs.TryAdd(read.Epc, 0);
    }
}

/// <summary>
/// Reader → scan points → engine. Maps each read's antenna to a scan point (Settings), drops reads below the
/// antenna's minimum RSSI (cross-read control on the bench), de-duplicates into BagScans and posts them to the engine
/// in order. Runs on its own task; raw reads are also queued for the UI.
/// </summary>
public sealed class ReadPipeline(ReconciliationEngine engine, PersistenceWriter? writer, Func<BrsOptions> options)
{
    private readonly Lock _lock = new();
    private ScanDeduplicator? _dedupe;
    private long _filtered;

    public IReadOnlyDictionary<int, AntennaStats> Antennas { get; } =
        Enumerable.Range(0, 4).ToDictionary(a => a, a => new AntennaStats { Antenna = a });

    /// <summary>Recent raw reads for the Live reads page (the UI drains it).</summary>
    public ConcurrentQueue<TagRead> RecentReads { get; } = new();

    public long FilteredReads => Interlocked.Read(ref _filtered);

    /// <summary>Raised on the pipeline task if posting to the engine fails.</summary>
    public event Action<string>? Error;

    public async Task RunAsync(IRfidReader reader, CancellationToken ct)
    {
        var dedupe = new ScanDeduplicator(options().DedupeWindow);
        dedupe.Closed += summary =>
        {
            writer?.SaveScanWindow(summary);
            _ = CompleteScanAsync(summary);
        };
        lock (_lock) _dedupe = dedupe;

        using var ticker = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        var tick = TickAsync(ticker, dedupe, ct);
        try
        {
            await foreach (var read in reader.ReadsAsync(ct).ConfigureAwait(false))
            {
                if (Antennas.TryGetValue(read.Antenna, out var stats)) stats.Add(read);
                RecentReads.Enqueue(read);
                while (RecentReads.Count > 2000) RecentReads.TryDequeue(out _);

                var o = options();
                var assignment = o.Antennas.FirstOrDefault(a => a.Antenna == read.Antenna);
                if (assignment is not { Enabled: true } || (read.Rssi ?? double.MaxValue) < assignment.MinRssi)
                {
                    Interlocked.Increment(ref _filtered);
                    continue;
                }

                BagScan? scan;
                lock (_lock)
                {
                    dedupe.WindowLength = o.DedupeWindow;
                    scan = dedupe.Process(read, assignment.ScanPointId);
                }
                if (scan == null) continue;

                try
                {
                    var result = await engine.RecordScanAsync(scan).ConfigureAwait(false);
                    // A newly bound tag may already be sitting at the belt/ramp antennas (seen there while still
                    // unknown): forget those windows so its next read there is evaluated as a bag.
                    if (result.Outcome == ScanOutcome.Bound)
                        lock (_lock) dedupe.Reset(scan.Epc);
                }
                catch (Exception ex) { Error?.Invoke($"Scan of {scan.Epc} failed: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (_lock)
            {
                dedupe.CloseAll();
                _dedupe = null;
            }
            await tick.ConfigureAwait(false);
        }
    }

    private async Task CompleteScanAsync(ScanWindowSummary summary)
    {
        try { await engine.CompleteScanAsync(summary).ConfigureAwait(false); }
        catch (ObjectDisposedException) { }   // shutting down
        catch (Exception ex) { Error?.Invoke($"Completing the scan of {summary.Epc} failed: {ex.Message}"); }
    }

    /// <summary>Re-evaluates every tag at a scan point on its next read (e.g. the ramp antenna was just armed).</summary>
    public void ResetScanPoint(string scanPointId)
    {
        lock (_lock) _dedupe?.ResetScanPoint(scanPointId);
    }

    private async Task TickAsync(PeriodicTimer ticker, ScanDeduplicator dedupe, CancellationToken ct)
    {
        try
        {
            while (await ticker.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                lock (_lock) dedupe.Tick();
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Lets a tag that is still in the field be scanned again (the ramp "Rescan" button).</summary>
    public void ResetTag(string epc)
    {
        lock (_lock) _dedupe?.Reset(epc);
    }
}
