using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace KQ.Brs.Poc.ViewModels;

/// <summary>Small formatting and brush helpers used from x:Bind function bindings.</summary>
public static class Ui
{
    public static string Ago(TimeSpan t) => t.TotalSeconds < 1 ? "now"
        : t.TotalSeconds < 60 ? $"{t.TotalSeconds:F0} s ago"
        : t.TotalMinutes < 60 ? $"{t.TotalMinutes:F0} min ago"
        : $"{t.TotalHours:F0} h ago";

    public static string Duration(TimeSpan t) => t.TotalSeconds < 60 ? $"{t.TotalSeconds:F0} s"
        : t.TotalMinutes < 60 ? $"{t.TotalMinutes:F1} min"
        : $"{t.TotalHours:F1} h";

    /// <summary>
    /// Where an antenna sits, e.g. "Check-in · Desk 14". When two antennas share a scan point (the tunnel), they are
    /// told apart as left and right.
    /// </summary>
    public static string AntennaName(KQ.Brs.Core.BrsOptions options, int antenna)
    {
        var assignment = options.Antennas.FirstOrDefault(a => a.Antenna == antenna);
        if (assignment == null) return "Unassigned";
        var name = KQ.Brs.Core.Domain.ScanPoint.Get(assignment.ScanPointId).Name;
        var sharing = options.Antennas.Where(a => a.ScanPointId == assignment.ScanPointId).Select(a => a.Antenna).Order().ToList();
        return sharing.Count == 2 ? $"{name} ({(sharing[0] == antenna ? "left" : "right")})" : name;
    }

    public static Brush Resource(string key) => (Brush)Application.Current.Resources[key];

    public static Brush BadgeBrush(BadgeKind kind) => Resource(kind switch
    {
        BadgeKind.Matched => "StatusMatchedBrush",
        BadgeKind.InScan => "StatusInScanBrush",
        BadgeKind.Exception => "StatusExceptionBrush",
        BadgeKind.Missing or BadgeKind.Held => "StatusMissingBrush",
        BadgeKind.Offloaded => "StatusOffloadedBrush",
        _ => "StatusExpectedBrush",
    });

    public static Brush StageBrush(bool done) => Resource(done ? "StatusMatchedBrush" : "ControlStrokeColorDefaultBrush");

    public static Brush SeverityBrush(bool critical) => Resource(critical ? "StatusExceptionBrush" : "StatusInScanBrush");

    public static Brush QueuedBrush(bool queued) => Resource(queued ? "StatusInScanBrush" : "TextFillColorSecondaryBrush");

    public static Brush DecisionBrush(bool authorised) => Resource(authorised ? "StatusMatchedBrush" : "StatusExceptionBrush");

    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static double Percent(int part, int whole) => whole == 0 ? 0 : 100.0 * part / whole;

    public static string PercentText(int part, int whole) => whole == 0 ? "0 %" : $"{100.0 * part / whole:F0} %";
}
