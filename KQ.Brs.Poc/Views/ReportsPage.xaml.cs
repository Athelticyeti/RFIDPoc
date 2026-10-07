using System.Diagnostics;
using KQ.Brs.Core.Domain;
using KQ.Brs.Core.Reports;
using KQ.Brs.Poc.Services;
using KQ.Brs.Poc.ViewModels;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;

namespace KQ.Brs.Poc.Views;

public sealed partial class ReportsPage : Page
{
    private EvaluationReport? _report;

    public ReportsPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e) => await LoadAsync();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        Busy.IsActive = true;
        try
        {
            await AppServices.Writer.FlushAsync();
            _report = await Task.Run(() => EvaluationReportBuilder.Build(AppServices.Store));
            Render(_report);
        }
        catch (Exception ex)
        {
            AppServices.State.ShowToast("Report", ex.Message, isError: true);
        }
        finally
        {
            Busy.IsActive = false;
        }
    }

    private void Render(EvaluationReport r)
    {
        Body.Children.Clear();
        Generated.Text = $"Generated {r.GeneratedUtc.ToLocalTime():HH:mm:ss} from the audit trail. Target read rate > 99 % at every fixed scan point.";

        // Summary tiles
        var tiles = new Grid { ColumnSpacing = 12 };
        string throughput = r.BagsPerMinute is { } bpm ? $"{bpm:F1}" : "-";
        var summary = new (string Caption, string Value, string Sub)[]
        {
            ("Bags loaded", r.Loaded.ToString("N0"), "real and virtual"),
            ("Throughput", throughput, "bags / min, first check-in to last load (simulator time runs faster)"),
            ("Exceptions raised", r.Exceptions.Sum(e => e.Raised).ToString("N0"), $"{r.Exceptions.Sum(e => e.Open)} still open"),
            ("Reader went offline", r.ReaderOfflineEvents.ToString("N0"), "times (auto-reconnect)"),
        };
        for (var i = 0; i < summary.Length; i++)
        {
            tiles.ColumnDefinitions.Add(new ColumnDefinition());
            var card = Card(new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    Caption(summary[i].Caption),
                    new TextBlock { Text = summary[i].Value, FontSize = 30, FontWeight = FontWeights.SemiBold },
                    Caption(summary[i].Sub),
                },
            });
            Grid.SetColumn(card, i);
            tiles.Children.Add(card);
        }
        Body.Children.Add(tiles);

        // Read rate per scan point
        var rates = new StackPanel { Spacing = 10 };
        rates.Children.Add(Header("Read rate per scan point", "A miss = the bag was seen at a later point but not here."));
        if (r.ScanPoints.Count == 0) rates.Children.Add(Caption("No scans yet. Start the virtual bags or present real tags."));
        foreach (var p in r.ScanPoints)
            rates.Children.Add(RateRow($"{ScanPoint.Get(p.ScanPoint).Name} · {(p.Real ? "real tags" : "virtual")}", p.Rate, $"{p.Read} read, {p.Missed} missed", p.Real));
        Body.Children.Add(Card(rates));

        // Antennas
        var antennas = new StackPanel { Spacing = 6 };
        antennas.Children.Add(Header("Antennas", "From de-duplicated scan windows: how often each antenna took part, and its strongest signal."));
        antennas.Children.Add(Table(["Antenna", "Source", "Scan windows", "Reads", "Reads / window", "Max RSSI"],
            r.Antennas.Select(a => new[] { $"{a.Antenna}", a.Real ? "Real" : "Virtual", $"{a.Windows:N0}", $"{a.Reads:N0}", $"{a.AvgReadsPerWindow:F1}", a.MaxRssi is { } m ? $"{m:N0}" : "-" })));
        Body.Children.Add(Card(antennas));

        // Exceptions
        var exceptions = new StackPanel { Spacing = 6 };
        exceptions.Children.Add(Header("Exceptions and time to resolve", null));
        exceptions.Children.Add(Table(["Type", "Raised", "Open", "Overridden", "Median resolve", "Max resolve"],
            r.Exceptions.Select(e => new[] { e.Type.ToString(), $"{e.Raised}", $"{e.Open}", $"{e.Overridden}", Fmt(e.MedianResolve), Fmt(e.MaxResolve) })));
        Body.Children.Add(Card(exceptions));

        if (r.MissedAtBelt.Count > 0)
        {
            var missed = new StackPanel { Spacing = 4 };
            missed.Children.Add(Header("Real-tag bags missed at Belt 04", "Loaded without a tunnel read: check antenna placement and attenuation (spec 3.4.4)."));
            missed.Children.Add(new TextBlock { Text = string.Join("   ", r.MissedAtBelt), FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"), TextWrapping = TextWrapping.Wrap });
            Body.Children.Add(Card(missed));
        }
    }

    private static string Fmt(TimeSpan? t) => t is { } v ? Ui.Duration(v) : "-";

    private static Border Card(UIElement content) => new() { Style = (Style)Application.Current.Resources["CardStyle"], Child = content };

    private static TextBlock Caption(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap,
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Foreground = Ui.Resource("TextFillColorSecondaryBrush"),
    };

    private static StackPanel Header(string title, string? sub)
    {
        var p = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 0, 6) };
        p.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        if (sub != null) p.Children.Add(Caption(sub));
        return p;
    }

    /// <summary>Label, a bar with the 99 % target marked, and the figure.</summary>
    private static Grid RateRow(string label, double rate, string detail, bool real)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });

        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontWeight = real ? FontWeights.SemiBold : FontWeights.Normal });

        var track = new Grid { Height = 12, VerticalAlignment = VerticalAlignment.Center };
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, rate), GridUnitType.Star) });
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, 1 - rate), GridUnitType.Star) });
        var background = new Rectangle { Fill = Ui.Resource("LaneBrush"), RadiusX = 6, RadiusY = 6 };
        Grid.SetColumnSpan(background, 2);
        track.Children.Add(background);
        track.Children.Add(new Rectangle { Fill = Ui.Resource(rate >= 0.99 ? "StatusMatchedBrush" : rate >= 0.95 ? "StatusInScanBrush" : "StatusExceptionBrush"), RadiusX = 6, RadiusY = 6 });
        Grid.SetColumn(track, 1);
        grid.Children.Add(track);

        var value = new TextBlock { Text = $"{rate:P1}", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(value, 2);
        grid.Children.Add(value);
        var sub = Caption(detail);
        sub.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(sub, 3);
        grid.Children.Add(sub);
        return grid;
    }

    private static Grid Table(string[] headers, IEnumerable<string[]> rows)
    {
        var grid = new Grid { ColumnSpacing = 24, RowSpacing = 6 };
        foreach (var _ in headers) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var c = 0; c < headers.Length; c++)
        {
            var h = new TextBlock { Text = headers[c].ToUpperInvariant(), Style = (Style)Application.Current.Resources["ColumnHeaderStyle"] };
            Grid.SetColumn(h, c);
            grid.Children.Add(h);
        }
        var r = 1;
        foreach (var row in rows)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var c = 0; c < row.Length; c++)
            {
                var t = new TextBlock { Text = row[c] };
                Grid.SetRow(t, r);
                Grid.SetColumn(t, c);
                grid.Children.Add(t);
            }
            r++;
        }
        if (r == 1)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var none = Caption("None yet.");
            Grid.SetRow(none, 1);
            Grid.SetColumnSpan(none, headers.Length);
            grid.Children.Add(none);
        }
        return grid;
    }

    private async void ExportMd_Click(object sender, RoutedEventArgs e) => await ExportAsync("md", r => r.ToMarkdown());

    private async void ExportCsv_Click(object sender, RoutedEventArgs e) => await ExportAsync("csv", r => r.ToCsv());

    private async Task ExportAsync(string extension, Func<EvaluationReport, string> render)
    {
        if (_report == null) await LoadAsync();
        if (_report == null) return;
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "KQ-BRS-Reports");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"KQ504-evaluation-{DateTime.Now:yyyyMMdd-HHmmss}.{extension}");
        await File.WriteAllTextAsync(file, render(_report));
        AppServices.State.ShowToast("Report exported", file);
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
    }
}
