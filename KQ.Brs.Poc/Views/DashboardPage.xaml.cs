using System.Collections.ObjectModel;
using KQ.Brs.Core.Domain;
using KQ.Brs.Poc.Services;
using KQ.Brs.Poc.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace KQ.Brs.Poc.Views;

public sealed partial class DashboardPage : Page
{
    private static readonly double[] Speeds = [1, 10, 30, 60, 120];
    private readonly ObservableCollection<BagRowViewModel> _filtered = new();
    private AppState State => AppServices.State;
    private bool _subscribed;
    private string? _scrollToPlate;

    public DashboardPage()
    {
        InitializeComponent();
        BagList.ItemsSource = _filtered;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (!_subscribed)
        {
            _subscribed = true;
            State.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(AppState.Totals)) RenderTotals();
                if (args.PropertyName is nameof(AppState.SimRunning) or nameof(AppState.SimStatus)) RenderSim();
                if (args.PropertyName is nameof(AppState.FlightTitle)) FlightLine.Text = State.FlightTitle;
            };
            State.BagsChanged += OnBagsChanged;
            var speed = Array.IndexOf(Speeds, AppServices.Settings.Brs.Simulator.Speed);
            SpeedBox.SelectedIndex = speed >= 0 ? speed : 2;
        }
        FlightLine.Text = State.FlightTitle;
        RenderTotals();
        RenderSim();
        Rebuild();
    }

    private void RenderTotals()
    {
        var t = State.Totals;
        ExpectedTile.Set(t.Expected, 0, $"bags · {t.Offloaded} offloaded");
        CheckInTile.Set(t.CheckInVerified, t.Expected);
        SorterTile.Set(t.SorterVerified, t.Expected);
        LoadedTile.Set(t.Loaded, t.Expected, accent: t.Loaded > 0 && t.Loaded == t.Expected ? Ui.Resource("StatusMatchedBrush") : null);
        ExceptionsTile.Set(t.OpenExceptions, 0, t.OpenExceptions == 0 ? "all clear" : "need attention",
            t.OpenExceptions > 0 ? Ui.Resource("StatusExceptionBrush") : Ui.Resource("StatusMatchedBrush"));

        LaneDesk.Text = $"{t.CheckInVerified}";
        LaneBelt.Text = $"{t.SorterVerified}";
        LaneAk7.Text = $"{t.LoadedAk7} bags";
        LaneAk8.Text = $"{t.LoadedAk8} bags";
        LaneOffloaded.Text = $"{t.Offloaded}";
        LaneReal.Text = $"{t.RealBound} / {t.RealPool}";
        LaneStray.Text = $"{t.StrayReads}";
    }

    private void RenderSim()
    {
        // The virtual-bags simulator only matters for the generated 700-bag flight; hide it for real-tag demos.
        var demoBags = AppServices.Settings.GenerateDemoBags;
        SimButton.Visibility = SpeedBox.Visibility = SimStatusText.Visibility = demoBags ? Visibility.Visible : Visibility.Collapsed;
        RealTagsFilter.Visibility = demoBags ? Visibility.Visible : Visibility.Collapsed;
        if (!demoBags && FilterBar.SelectedItem == RealTagsFilter) FilterBar.SelectedItem = FilterBar.Items[0];
        SimStatusText.Text = State.SimStatus;
        SimLabel.Text = State.SimRunning ? "Pause virtual bags" : AppServices.Simulator.Pending > 0 ? "Resume virtual bags" : "Start virtual bags";
        SimIcon.Glyph = State.SimRunning ? "" : "";
    }

    private async void Sim_Click(object sender, RoutedEventArgs e)
    {
        if (AppServices.Simulator.IsRunning) { AppServices.Simulator.Pause(); return; }
        if ((await AppServices.Engine.VirtualBagsAsync()).Count == 0 && AppServices.Simulator.Pending == 0)
        {
            State.ShowToast("No virtual bags", "This flight only has the passengers you added. To simulate a full flight, switch on Settings → Generate 700 demo bags and reset the demo.", isError: true);
            return;
        }
        await AppServices.Simulator.StartAsync();
    }

    private void Speed_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SpeedBox.SelectedIndex < 0) return;
        var s = AppServices.Settings;
        AppServices.ApplySettings(s with { Brs = s.Brs with { Simulator = s.Brs.Simulator with { Speed = Speeds[SpeedBox.SelectedIndex] } } });
    }

    private async void AddPassenger_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddPassengerDialog { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || dialog.Plates.Count == 0) return;

        // Keep the list as it is and scroll to the new bag once it arrives (the engine publishes it on the next UI tick).
        _scrollToPlate = dialog.Plates[0];
        ScrollToPending();

        var plates = dialog.Plates.Count == 1 ? dialog.Plates[0] : $"{dialog.Plates[0]}–{dialog.Plates[^1]}";
        if (AppServices.Reader.Reader == null)
        {
            State.ShowToast("Passenger added",
                $"{dialog.PassengerName}: {dialog.Plates.Count} bag(s), {plates}. The reader isn't connected, so the next tag held at check-in will be linked to their bag.");
            return;
        }

        // Write each bag's licence plate onto its tag: the POC's "print the bag tag" at check-in.
        var written = 0;
        for (var i = 0; i < dialog.Plates.Count; i++)
        {
            var write = new WriteTagDialog(dialog.Plates[i], dialog.PassengerName, i + 1, dialog.Plates.Count) { XamlRoot = XamlRoot };
            if (await write.ShowAsync() == ContentDialogResult.Primary && write.Result != null) written++;
        }
        var skipped = dialog.Plates.Count - written;
        State.ShowToast("Passenger added",
            $"{dialog.PassengerName}: {plates}. {written} tag(s) written" +
            (skipped > 0 ? $"; {skipped} bag(s) will be linked to the next plain tag held at check-in." : ". Hold the tag at check-in to confirm it."));
    }

    private async void PrePushback_Click(object sender, RoutedEventArgs e)
    {
        var report = await AppServices.Engine.PrePushbackAsync(raiseExceptions: false);
        var dialog = new PrePushbackDialog(report) { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var raised = await AppServices.Engine.PrePushbackAsync(raiseExceptions: true);
            State.ShowToast("Pre-pushback", $"{raised.ExceptionsRaised} NotLoaded exception(s) raised.");
        }
    }

    // ---------------------------------------------------------------- filtering

    private void Filter_Changed(object sender, object e)
    {
        // Raised while the page is still being built (initial SelectedIndex); wait until it is ready.
        if (_subscribed) Rebuild();
    }

    private void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (_subscribed) Rebuild();
    }

    private Func<BagRowViewModel, bool> Predicate()
    {
        var tag = FilterBar.SelectedItem?.Tag as string ?? "All";
        var uld = UldFilter.SelectedIndex switch { 1 => "AK7", 2 => "AK8", _ => null };
        var q = SearchBox.Text.Trim();
        return row =>
        {
            var b = row.Bag;
            var ok = tag switch
            {
                "Real" => row.IsReal,
                "Progress" => b.Status is BagStatus.CheckedIn or BagStatus.Sorted,
                "Loaded" => b.Status == BagStatus.Loaded,
                "Exception" => b.OpenException != null,
                "Expected" => b.Status == BagStatus.Expected && !b.Deleted,
                "Foreign" => row.IsOtherFlight,
                _ => !row.IsOtherFlight || b.OpenException != null,
            };
            if (!ok) return false;
            if (uld != null && b.Uld != uld) return false;
            return q.Length == 0
                || b.Plate.Contains(q, StringComparison.OrdinalIgnoreCase)
                || b.PassengerName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (b.Epc?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
        };
    }

    private void Rebuild()
    {
        var match = Predicate();
        _filtered.Clear();
        foreach (var row in State.Bags.Where(match).OrderBy(r => r.Plate, StringComparer.Ordinal))
            _filtered.Add(row);
        CountText.Text = $"{_filtered.Count:N0} bags";
    }

    /// <summary>Keeps the filtered list in step with live changes without rebuilding it (no flicker, keeps scroll).</summary>
    private void OnBagsChanged(IReadOnlyCollection<BagRowViewModel> changed)
    {
        if (changed.Count > 300) { Rebuild(); return; }
        var match = Predicate();
        foreach (var row in changed)
        {
            var index = _filtered.IndexOf(row);
            var wanted = match(row);
            if (wanted && index < 0)
            {
                var at = 0;
                while (at < _filtered.Count && string.CompareOrdinal(_filtered[at].Plate, row.Plate) < 0) at++;
                _filtered.Insert(at, row);
            }
            else if (!wanted && index >= 0)
            {
                _filtered.RemoveAt(index);
            }
        }
        CountText.Text = $"{_filtered.Count:N0} bags";
        ScrollToPending();
    }

    private void ScrollToPending()
    {
        if (_scrollToPlate == null) return;
        var row = _filtered.FirstOrDefault(r => r.Plate == _scrollToPlate);
        if (row == null) return;   // not in the list yet, or hidden by the current filter
        _scrollToPlate = null;
        BagList.ScrollIntoView(row, ScrollIntoViewAlignment.Leading);
    }

    private async void Bag_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is BagRowViewModel row)
            await BagDetailDialog.ShowForAsync(row.Plate, XamlRoot);
    }
}
