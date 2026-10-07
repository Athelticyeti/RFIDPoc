using KQ.Brs.Core.Domain;
using KQ.Brs.Core.TypeB;
using KQ.Brs.Poc.Services;
using KQ.Brs.Poc.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace KQ.Brs.Poc.Views;

public sealed partial class MessagesPage : Page
{
    private bool _subscribed;

    public MessagesPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var state = AppServices.State;
        if (!_subscribed)
        {
            _subscribed = true;
            state.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(AppState.QueuedMessages)) RenderQueue();
                if (args.PropertyName == nameof(AppState.OutboxPaused)) MqToggle.IsOn = !state.OutboxPaused;
            };
        }
        MqToggle.IsOn = !state.OutboxPaused;
        ShowBox();
        RenderQueue();
    }

    private void RenderQueue()
    {
        var queued = AppServices.State.QueuedMessages;
        QueuedText.Text = queued.ToString("N0");
        OutboxItem.Text = queued > 0 ? $"Outbox (BPM, BUM) · {queued} queued" : "Outbox (BPM, BUM)";
    }

    private void BoxBar_Changed(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => ShowBox();

    private void ShowBox()
    {
        if (!_subscribed) return;
        List.ItemsSource = BoxBar.SelectedItem == OutboxItem ? AppServices.State.Outbox : AppServices.State.Inbox;
    }

    private async void Mq_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_subscribed || MqToggle.IsOn == !AppServices.State.OutboxPaused) return;
        var paused = !MqToggle.IsOn;
        AppServices.State.OutboxPaused = paused;
        var flushed = await AppServices.Engine.SetOutboxPausedAsync(paused);
        if (!paused && flushed > 0)
            AppServices.State.ShowToast("MQ link restored", $"{flushed} queued message(s) sent from the outbox (at-least-once delivery).");
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (List.SelectedItem is not MessageRowViewModel row) return;
        RawText.Text = row.Message.Raw;
        ParsedText.Text = Describe(row.Message.Raw);
    }

    /// <summary>Parses the message again and lists its key fields, to show the parser at work.</summary>
    private static string Describe(string raw)
    {
        try
        {
            return TypeBParser.Parse(raw) switch
            {
                Bsm b => $"BSM {(b.Action == BsmAction.Original ? "" : b.Action.ToString().ToUpperInvariant())} · {b.Flight}/{b.FlightDate} {b.Station} → {b.Destination} · {b.Class}\n" +
                         $"{b.Plates.Count} plate(s): {string.Join(", ", b.Plates.Select(p => p.Value))}\n" +
                         $"Passenger {b.Initial}. {b.Surname} · {b.PassengerStatus} · authority to load {(b.AuthorityToLoad ? "Y" : "N")} · {b.WeightKg} kg",
                Bpm p => $"BPM · {p.Plate} processed at {p.ScanPoint} {p.ProcessedUtc.ToLocalTime():HH:mm:ss}{(p.Uld != null ? $" into {p.Uld}" : "")} · {p.Flight} → {p.Destination}",
                Bum u => $"BUM · {u.Plate} unloaded from {u.Flight} · {u.Reason}",
                _ => "",
            };
        }
        catch (Exception ex)
        {
            return $"Could not parse: {ex.Message}";
        }
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var plates = await AppServices.Engine.ApplyTypeBAsync(PasteBox.Text.Trim());
            ApplyResult.Text = $"Applied to {string.Join(", ", plates)}";
            ApplyResult.Foreground = Ui.Resource("StatusMatchedBrush");
        }
        catch (Exception ex)
        {
            ApplyResult.Text = ex.Message;
            ApplyResult.Foreground = Ui.Resource("SystemFillColorCriticalBrush");
        }
    }

    private void Example_Click(object sender, RoutedEventArgs e)
    {
        // A passenger in the real-tag pool doesn't board: their loaded bag must come off (FR-10).
        var bag = AppServices.State.Bags.FirstOrDefault(b => b.IsReal && b.Bag.Epc != null && b.Bag.Flight == "KQ504")
                  ?? AppServices.State.Bags.First(b => b.Bag.Flight == "KQ504");
        var name = bag.Passenger.Split(". ");
        PasteBox.Text = TypeBBuilder.Bsm(BsmAction.Change, AppServices.Settings.Brs.Station, AppServices.Engine.Flight,
            AppServices.Settings.Brs.Destination, bag.Bag.Class, new LicencePlate(bag.Plate), 1, bag.Bag.WeightKg,
            true, PassengerStatus.NotBoarded, "00A", 0, name[^1], name[0]);
    }
}
