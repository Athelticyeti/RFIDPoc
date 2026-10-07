using CommunityToolkit.Mvvm.ComponentModel;
using KQ.Brs.Core.Scanning;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace KQ.Brs.Poc.ViewModels;

/// <summary>One antenna's live activity: reads per second (with a 60 s sparkline), unique tags, last read.</summary>
public sealed partial class AntennaCardViewModel(int antenna) : ObservableObject
{
    public const double SparkWidth = 240, SparkHeight = 44;
    private readonly Queue<double> _history = new(Enumerable.Repeat(0.0, 60));
    private long _lastReads;

    public int Antenna { get; } = antenna;
    public string Title => $"Antenna {Antenna}";

    [ObservableProperty] public partial string ScanPointName { get; set; } = "";
    [ObservableProperty] public partial bool Enabled { get; set; } = true;
    [ObservableProperty] public partial string ReadsPerSecond { get; set; } = "0";
    [ObservableProperty] public partial string Unique { get; set; } = "0";
    [ObservableProperty] public partial string Total { get; set; } = "0";
    [ObservableProperty] public partial string LastEpc { get; set; } = "-";
    [ObservableProperty] public partial string Rssi { get; set; } = "-";
    [ObservableProperty] public partial PointCollection Spark { get; set; } = new();

    public static double Dim(bool enabled) => enabled ? 1 : 0.45;

    /// <summary>Called once a second.</summary>
    public void Sample(AntennaStats stats, string scanPointName, bool enabled)
    {
        var reads = stats.Reads;
        var perSecond = Math.Max(0, reads - _lastReads);
        _lastReads = reads;
        _history.Enqueue(perSecond);
        while (_history.Count > 60) _history.Dequeue();

        ScanPointName = scanPointName;
        Enabled = enabled;
        ReadsPerSecond = perSecond.ToString("N0");
        Unique = stats.UniqueEpcs.Count.ToString("N0");
        Total = reads.ToString("N0");
        LastEpc = stats.LastEpc is { } e ? "…" + e[^10..] : "-";
        Rssi = stats.LastRssi is { } r ? $"{r:N0} (max {stats.MaxRssi:N0})" : "-";

        var max = Math.Max(5, _history.Max());
        var points = new PointCollection();
        var i = 0;
        foreach (var v in _history)
            points.Add(new Point(i++ * SparkWidth / 59, SparkHeight - v / max * (SparkHeight - 2)));
        Spark = points;
    }
}
