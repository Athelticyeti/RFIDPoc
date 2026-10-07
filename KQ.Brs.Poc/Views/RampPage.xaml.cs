using System.Collections.ObjectModel;
using System.Collections.Specialized;
using KQ.Brs.Core.Domain;
using KQ.Brs.Core.Reconciliation;
using KQ.Brs.Core.Scanning;
using KQ.Brs.Poc.Services;
using KQ.Brs.Poc.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace KQ.Brs.Poc.Views;

public sealed partial class RampPage : Page
{
    private readonly ObservableCollection<DecisionViewModel> _realDecisions = new();
    private bool _ready;
    private bool _subscribed;

    public RampPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        var state = AppServices.State;
        // The real/virtual filter only matters when the simulator has virtual bags.
        RealOnly.Visibility = AppServices.Settings.GenerateDemoBags ? Visibility.Visible : Visibility.Collapsed;
        if (!AppServices.Settings.GenerateDemoBags) RealOnly.IsOn = false;
        if (!_subscribed)
        {
            _subscribed = true;
            state.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(AppState.LastDecision)) RenderDecision(); };
            state.Decisions.CollectionChanged += Decisions_Changed;
        }

        // Re-read the ULD and armed state every visit: Settings can arm the loading antenna while this page is cached.
        _ready = false;   // don't echo these back to the engine through Ramp_Changed
        var snapshot = await AppServices.Engine.SnapshotAsync();
        UldSelector.SelectedIndex = snapshot.SelectedUld == "AK8" ? 1 : 0;
        ArmToggle.IsOn = snapshot.RampArmed;
        DisarmedInfo.IsOpen = !snapshot.RampArmed;
        _ready = true;
        DecisionList.ItemsSource = RealOnly.IsOn ? _realDecisions : state.Decisions;
        RenderDecision();
    }

    private void Decisions_Changed(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems == null) return;
        foreach (DecisionViewModel d in e.NewItems)
        {
            if (!d.Decision.IsReal) continue;
            _realDecisions.Insert(0, d);
            if (_realDecisions.Count > 60) _realDecisions.RemoveAt(_realDecisions.Count - 1);
        }
    }

    private void RealOnly_Toggled(object sender, RoutedEventArgs e)
    {
        if (_ready) DecisionList.ItemsSource = RealOnly.IsOn ? _realDecisions : AppServices.State.Decisions;
    }

    private async void Ramp_Changed(object sender, object e)
    {
        DisarmedInfo.IsOpen = !ArmToggle.IsOn;
        if (!_ready) return;
        var uld = UldSelector.SelectedIndex == 1 ? "AK8" : "AK7";
        await AppServices.Engine.SetRampAsync(uld, ArmToggle.IsOn);
        // Tags already lying in the ramp field were ignored while disarmed: evaluate them on their next read.
        if (ArmToggle.IsOn) AppServices.Pipeline.ResetScanPoint(ScanPoint.Ramp);
    }

    private void RenderDecision()
    {
        var d = AppServices.State.LastDecision;
        if (d == null)
        {
            DecisionIconBack.Background = Ui.Resource("LaneBrush");
            DecisionIcon.Glyph = "";
            DecisionIcon.Foreground = Ui.Resource("TextFillColorSecondaryBrush");
            DecisionTitle.Text = "Waiting for a scan";
            DecisionTitle.ClearValue(TextBlock.ForegroundProperty);
            DecisionPlate.Text = DecisionPax.Text = DecisionMeta.Text = "";
            DecisionReason.Text = "Present a bag tag to antenna 3, or use the manual scan below.";
            return;
        }
        var brush = Ui.DecisionBrush(d.Authorised);
        DecisionIconBack.Background = brush;
        DecisionIcon.Glyph = d.Glyph;
        DecisionIcon.Foreground = Ui.Resource("TextOnAccentFillColorPrimaryBrush");
        DecisionTitle.Text = d.Title;
        DecisionTitle.Foreground = brush;
        DecisionPlate.Text = d.Plate;
        DecisionPax.Text = d.Passenger;
        DecisionReason.Text = d.Reason;
        DecisionMeta.Text = $"{d.Time} · {d.SourceText}";
    }

    // ---------------------------------------------------------------- manual scan

    private void Manual_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        var q = sender.Text.Trim();
        sender.ItemsSource = q.Length < 3 ? null : AppServices.State.Bags
            .Where(b => b.Plate.Contains(q, StringComparison.OrdinalIgnoreCase) || (b.Bag.Epc?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
            .Take(8).Select(b => b.Plate).ToList();
    }

    private async void Manual_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) =>
        await ScanAsync((args.ChosenSuggestion as string) ?? sender.Text);

    private async void ManualScan_Click(object sender, RoutedEventArgs e) => await ScanAsync(ManualBox.Text);

    /// <summary>Scans a plate or EPC at the ramp, through the same engine path as antenna 3.</summary>
    private async Task ScanAsync(string input)
    {
        ManualError.Text = "";
        var q = input.Trim().Replace(" ", "");
        if (q.Length == 0) return;

        string epc;
        if (LicencePlate.TryParse(q, out var plate))
        {
            var bag = AppServices.State.FindBag(plate.Value);
            if (bag == null) { ManualError.Text = $"No bag {plate} on any manifest."; return; }
            if (bag.Bag.Epc == null) { ManualError.Text = $"Bag {plate} has no tag yet (bind a real tag at the desk first)."; return; }
            epc = bag.Bag.Epc;
        }
        else
        {
            epc = q.ToUpperInvariant();
        }

        var readerId = SyntheticEpc.TryDecode(epc, out _) ? ReconciliationEngine.SimReaderId : ReconciliationEngine.ManualReaderId;
        var uld = UldSelector.SelectedIndex == 1 ? "AK8" : "AK7";
        AppServices.Pipeline.ResetTag(epc);
        var result = await AppServices.Engine.RecordScanAsync(new BagScan(readerId, epc, ScanPoint.Ramp, 3, null, DateTimeOffset.UtcNow),
            readerId == ReconciliationEngine.SimReaderId ? uld : null);
        if (result.Decision == null) ManualError.Text = result.Detail;
        ManualBox.Text = "";
    }
}
