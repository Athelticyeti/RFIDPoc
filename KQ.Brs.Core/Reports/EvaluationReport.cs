using System.Globalization;
using System.Text;
using System.Text.Json;
using KQ.Brs.Core.Domain;
using KQ.Brs.Core.Persistence;
using KQ.Brs.Core.Reconciliation;

namespace KQ.Brs.Core.Reports;

public sealed record ScanPointRate(string ScanPoint, bool Real, int Read, int Missed)
{
    public double Rate => Read + Missed == 0 ? 0 : (double)Read / (Read + Missed);
}

public sealed record AntennaRate(int Antenna, bool Real, int Windows, int Reads, double AvgReadsPerWindow, double? MaxRssi);

public sealed record ExceptionStats(ExceptionType Type, int Raised, int Open, int Overridden, TimeSpan? MedianResolve, TimeSpan? MaxResolve);

/// <summary>
/// A comparable flight loaded without RFID, entered by hand (spec 9.1: turnaround not extended compared with a baseline
/// flight). Loading time runs from the first bag into the hold to the last.
/// </summary>
public sealed record TurnaroundBaseline(string Flight, int Bags, double LoadingMinutes)
{
    /// <summary>Average time between one bag being loaded and the next.</summary>
    public double? SecondsPerBag => Bags >= 2 && LoadingMinutes > 0 ? LoadingMinutes * 60 / (Bags - 1) : null;
}

/// <summary>
/// Turnaround impact (FR-13): the POC's loading pace with real tags against the baseline flight. Only real-tag loads
/// count, because the simulator runs faster than real time.
/// </summary>
public sealed record TurnaroundComparison(TurnaroundBaseline? Baseline, int Loaded, DateTimeOffset? FirstLoad, DateTimeOffset? LastLoad)
{
    public TimeSpan? LoadingTime => FirstLoad is { } a && LastLoad is { } b ? b - a : null;

    /// <summary>Average time between one bag being loaded and the next (needs at least two loads).</summary>
    public double? SecondsPerBag => Loaded >= 2 && LoadingTime is { TotalSeconds: > 0 } t ? t.TotalSeconds / (Loaded - 1) : null;

    /// <summary>Positive: the POC loads each bag this much slower than the baseline.</summary>
    public double? DeltaSecondsPerBag => SecondsPerBag is { } poc && Baseline?.SecondsPerBag is { } baseline ? poc - baseline : null;

    /// <summary>The difference scaled to the baseline flight: how much longer (positive) or shorter loading it would take.</summary>
    public TimeSpan? ProjectedImpact => DeltaSecondsPerBag is { } d && Baseline is { } b ? TimeSpan.FromSeconds(d * (b.Bags - 1)) : null;

    /// <summary>The verdict for spec 9.1, or null while there's nothing to compare.</summary>
    public bool? Extended => DeltaSecondsPerBag is { } d ? d > 0 : null;
}

/// <summary>Evaluation report (FR-13): read rates, missed reads, exceptions and time to resolve, throughput, turnaround.</summary>
public sealed record EvaluationReport(
    DateTimeOffset GeneratedUtc,
    IReadOnlyList<ScanPointRate> ScanPoints,
    IReadOnlyList<AntennaRate> Antennas,
    IReadOnlyList<string> MissedAtBelt,
    IReadOnlyList<ExceptionStats> Exceptions,
    int Loaded,
    DateTimeOffset? FirstCheckIn,
    DateTimeOffset? LastLoad,
    int ReaderOfflineEvents,
    int ReaderOnlineEvents,
    TurnaroundComparison Turnaround)
{
    public double? BagsPerMinute => FirstCheckIn is { } a && LastLoad is { } b && b > a ? Loaded / (b - a).TotalMinutes : null;

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# KQ-504 POC evaluation report");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Generated {GeneratedUtc.ToLocalTime():yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        sb.AppendLine("## Read rate per scan point");
        sb.AppendLine("| Scan point | Source | Read | Missed | Rate |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var s in ScanPoints)
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {s.ScanPoint} | {(s.Real ? "Real tags" : "Virtual")} | {s.Read} | {s.Missed} | {s.Rate:P1} |");
        sb.AppendLine();
        sb.AppendLine("## Antennas");
        sb.AppendLine("| Antenna | Source | Scan windows | Reads | Reads/window | Max RSSI |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var a in Antennas)
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {a.Antenna} | {(a.Real ? "Real" : "Virtual")} | {a.Windows} | {a.Reads} | {a.AvgReadsPerWindow:F1} | {a.MaxRssi:F0} |");
        sb.AppendLine();
        sb.AppendLine("## Exceptions");
        sb.AppendLine("| Type | Raised | Open | Overridden | Median resolve | Max resolve |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var e in Exceptions)
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {e.Type} | {e.Raised} | {e.Open} | {e.Overridden} | {Fmt(e.MedianResolve)} | {Fmt(e.MaxResolve)} |");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Loaded: {Loaded}. Throughput: {(BagsPerMinute is { } r ? r.ToString("F1", CultureInfo.InvariantCulture) + " bags/min" : "n/a")}.");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Reader went offline {ReaderOfflineEvents} time(s).");
        sb.AppendLine();
        sb.AppendLine("## Turnaround vs baseline flight");
        var t = Turnaround;
        if (t.Baseline is { } b)
        {
            sb.AppendLine("| Flight | Bags loaded | Loading time | Time per bag |");
            sb.AppendLine("|---|---|---|---|");
            sb.AppendLine(CultureInfo.InvariantCulture, $"| Baseline: {b.Flight} (no RFID) | {b.Bags} | {Fmt(TimeSpan.FromMinutes(b.LoadingMinutes))} | {Secs(b.SecondsPerBag)} |");
            sb.AppendLine(CultureInfo.InvariantCulture, $"| This POC (real tags) | {t.Loaded} | {Fmt(t.LoadingTime)} | {Secs(t.SecondsPerBag)} |");
            sb.AppendLine();
        }
        sb.AppendLine(TurnaroundVerdict());
        if (MissedAtBelt.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Missed belt reads");
            foreach (var p in MissedAtBelt) sb.AppendLine(CultureInfo.InvariantCulture, $"- {p}");
        }
        return sb.ToString();
    }

    public string ToCsv()
    {
        var sb = new StringBuilder("section,key,source,value1,value2,value3\n");
        foreach (var s in ScanPoints)
            sb.AppendLine(CultureInfo.InvariantCulture, $"scanpoint,{s.ScanPoint},{(s.Real ? "real" : "virtual")},{s.Read},{s.Missed},{s.Rate:F4}");
        foreach (var a in Antennas)
            sb.AppendLine(CultureInfo.InvariantCulture, $"antenna,{a.Antenna},{(a.Real ? "real" : "virtual")},{a.Windows},{a.Reads},{a.MaxRssi:F1}");
        foreach (var e in Exceptions)
            sb.AppendLine(CultureInfo.InvariantCulture, $"exception,{e.Type},all,{e.Raised},{e.Open},{e.MedianResolve?.TotalSeconds:F0}");
        foreach (var p in MissedAtBelt)
            sb.AppendLine(CultureInfo.InvariantCulture, $"missed,{p},belt,,,");
        var t = Turnaround;
        if (t.Baseline is { } b)
            sb.AppendLine(CultureInfo.InvariantCulture, $"turnaround,baseline,{Csv(b.Flight)},{b.Bags},{b.LoadingMinutes * 60:F0},{b.SecondsPerBag:F1}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"turnaround,poc,real,{t.Loaded},{t.LoadingTime?.TotalSeconds:F0},{t.SecondsPerBag:F1}");
        return sb.ToString();
    }

    /// <summary>One line on spec 9.1 (turnaround not extended), or what is still needed to judge it.</summary>
    public string TurnaroundVerdict()
    {
        var t = Turnaround;
        if (t.Baseline?.SecondsPerBag == null) return "No baseline flight entered yet: add one to compare turnaround (spec 9.1).";
        if (t.SecondsPerBag == null) return "Load at least two bags with real tags to compare against the baseline.";
        var delta = t.DeltaSecondsPerBag!.Value;
        var impact = t.ProjectedImpact!.Value;
        return t.Extended == true
            ? $"Turnaround extended: {delta.ToString("F1", CultureInfo.InvariantCulture)} s slower per bag, about {Fmt(impact)} longer to load {t.Baseline.Bags} bags."
            : $"Turnaround not extended: {(-delta).ToString("F1", CultureInfo.InvariantCulture)} s faster per bag, about {Fmt(-impact)} shorter to load {t.Baseline.Bags} bags.";
    }

    private static string Secs(double? s) => s is { } v ? v.ToString("F1", CultureInfo.InvariantCulture) + " s" : "-";

    private static string Csv(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    private static string Fmt(TimeSpan? t) => t is { } v ? (v.TotalMinutes >= 1 ? $"{v.TotalMinutes:F1} min" : $"{v.TotalSeconds:F0} s") : "-";
}

public static class EvaluationReportBuilder
{
    /// <summary>Builds the report from the database (read-only; safe to run on a background thread).</summary>
    public static EvaluationReport Build(BrsStore store, TurnaroundBaseline? baseline = null)
    {
        using var c = store.Open();

        // Which plates each scan point saw, split by real vs simulated reader.
        var seen = new Dictionary<(string Point, bool Real), HashSet<string>>();
        var loaded = new HashSet<string>();
        var sourceOf = new Dictionary<string, bool>(); // plate → real
        DateTimeOffset? firstCheckIn = null, lastLoad = null;
        var realLoads = new Dictionary<string, DateTimeOffset>();   // plate → its first load with a real tag (turnaround)
        BrsStore.Query(c, "SELECT plate, kind, scan_point, reader_id, occurred_utc FROM bag_events WHERE scan_point IS NOT NULL", r =>
        {
            var plate = r.GetString(0);
            var kind = r.GetString(1);
            var real = !r.IsDBNull(3) && r.GetString(3) != ReconciliationEngine.SimReaderId;
            var t = BrsStore.ParseTime(r.GetString(4));
            sourceOf.TryAdd(plate, real);
            var key = (r.GetString(2), real);
            if (!seen.TryGetValue(key, out var set)) seen[key] = set = new();
            set.Add(plate);
            if (kind == "Loaded") { loaded.Add(plate); if (lastLoad == null || t > lastLoad) lastLoad = t; }
            if (kind == "Loaded" && real && (!realLoads.TryGetValue(plate, out var at) || t < at)) realLoads[plate] = t;
            if (kind is "DeskBind" or "DeskCheck" && (firstCheckIn == null || t < firstCheckIn)) firstCheckIn = t;
        });

        // A bag missed at a scan point = it reached a later point without being read there.
        var rates = new List<ScanPointRate>();
        var missedAtBelt = new List<string>();
        foreach (var real in new[] { true, false })
        {
            var desk = seen.GetValueOrDefault((ScanPoint.Desk14, real)) ?? [];
            var belt = seen.GetValueOrDefault((ScanPoint.Belt04, real)) ?? [];
            var ramp = seen.GetValueOrDefault((ScanPoint.Ramp, real)) ?? [];
            var beltMissed = loaded.Where(p => sourceOf.GetValueOrDefault(p) == real && !belt.Contains(p)).ToList();
            if (real) missedAtBelt.AddRange(beltMissed);
            var deskMissed = belt.Concat(ramp).Distinct().Count(p => !desk.Contains(p));
            if (desk.Count + belt.Count + ramp.Count == 0) continue;
            rates.Add(new ScanPointRate(ScanPoint.Desk14, real, desk.Count, deskMissed));
            rates.Add(new ScanPointRate(ScanPoint.Belt04, real, belt.Count, beltMissed.Count));
            rates.Add(new ScanPointRate(ScanPoint.Ramp, real, ramp.Count, 0));
        }

        // Per-antenna participation from the de-duplicated scan windows.
        var antennas = new Dictionary<(int, bool), (int Windows, int Reads, double? Max)>();
        BrsStore.Query(c, "SELECT reader_id, reads_per_antenna, best_antenna, max_rssi FROM scan_windows", r =>
        {
            var real = r.GetString(0) != ReconciliationEngine.SimReaderId;
            var per = JsonSerializer.Deserialize<Dictionary<int, int>>(r.GetString(1)) ?? [];
            double? max = r.IsDBNull(3) ? null : r.GetDouble(3);
            foreach (var (antenna, reads) in per)
            {
                var cur = antennas.GetValueOrDefault((antenna, real));
                var best = r.GetInt32(2) == antenna ? max : null;
                antennas[(antenna, real)] = (cur.Windows + 1, cur.Reads + reads, Max(cur.Max, best));
            }
        });

        var exceptions = new List<(ExceptionType Type, ExceptionState State, bool Overridden, TimeSpan? Took)>();
        BrsStore.Query(c, "SELECT type, state, overridden, raised_utc, resolved_utc FROM exceptions", r =>
            exceptions.Add(((ExceptionType)r.GetInt32(0), (ExceptionState)r.GetInt32(1), r.GetInt32(2) != 0,
                r.IsDBNull(4) ? null : BrsStore.ParseTime(r.GetString(4)) - BrsStore.ParseTime(r.GetString(3)))));

        int offline = 0, online = 0;
        BrsStore.Query(c, "SELECT status FROM reader_log", r =>
        {
            if (r.GetString(0) == "Offline") offline++;
            if (r.GetString(0) is "Online" or "Streaming") online++;
        });

        return new EvaluationReport(
            DateTimeOffset.UtcNow,
            rates,
            antennas.OrderBy(kv => kv.Key.Item2 ? 0 : 1).ThenBy(kv => kv.Key.Item1)
                .Select(kv => new AntennaRate(kv.Key.Item1, kv.Key.Item2, kv.Value.Windows, kv.Value.Reads,
                    kv.Value.Windows == 0 ? 0 : (double)kv.Value.Reads / kv.Value.Windows, kv.Value.Max)).ToList(),
            missedAtBelt,
            exceptions.GroupBy(e => e.Type).OrderBy(g => g.Key).Select(g =>
            {
                var took = g.Where(e => e.Took != null).Select(e => e.Took!.Value).OrderBy(t => t).ToList();
                return new ExceptionStats(g.Key, g.Count(), g.Count(e => e.State == ExceptionState.Open), g.Count(e => e.Overridden),
                    took.Count == 0 ? null : took[took.Count / 2], took.Count == 0 ? null : took[^1]);
            }).ToList(),
            loaded.Count, firstCheckIn, lastLoad, offline, online,
            new TurnaroundComparison(baseline, realLoads.Count,
                realLoads.Count == 0 ? null : realLoads.Values.Min(), realLoads.Count == 0 ? null : realLoads.Values.Max()));
    }

    private static double? Max(double? a, double? b) => a == null ? b : b == null ? a : Math.Max(a.Value, b.Value);
}
