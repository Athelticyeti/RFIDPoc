using KQ.Brs.Core.Domain;
using KQ.Brs.Core.Reconciliation;
using KQ.Brs.Poc.Services;
using KQ.Brs.Poc.ViewModels;
using KQ.Rfid.Alien;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace KQ.Brs.Poc.Views;

/// <summary>Bag details and its full event timeline (spec 7.1: "click a bag to see its full event timeline").</summary>
public sealed class BagDetailDialog : ContentDialog
{
    private BagDetailDialog() { }

    public static async Task ShowForAsync(string plate, XamlRoot root)
    {
        var row = AppServices.State.FindBag(plate);
        if (row == null) return;
        var bag = row.Bag;
        var events = await AppServices.Engine.TimelineAsync(plate);

        var dialog = new BagDetailDialog
        {
            XamlRoot = root,
            Title = $"Bag {bag.Plate}",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };

        // Real tags can be turned into a KQ-412 bag (and back) to demo the misroute workflow.
        var canFlag = AppServices.Settings.ShowMisrouteDemo && bag.Epc != null && row.IsReal && bag.Flight == "KQ504";
        var canUnflag = bag.Epc != null && bag.Flight != "KQ504" && row.IsReal;
        if (canFlag) dialog.PrimaryButtonText = "Flag tag as KQ-412 (misroute demo)";
        if (canUnflag) dialog.PrimaryButtonText = "Undo KQ-412 flag";

        // Real-tag bags on KQ-504 can have their licence plate written onto a physical tag (FR-02, spec 3.4.2).
        var canWrite = row.IsReal && bag.Flight == "KQ504" && !bag.Deleted;
        if (canWrite) dialog.SecondaryButtonText = bag.Epc != null && LicencePlateCodec.TryDecode(bag.Epc, out _) ? "Rewrite plate to tag" : "Write plate to tag";

        var panel = new StackPanel { Spacing = 14, MinWidth = 620 };
        panel.Children.Add(Facts(
            ("Passenger", bag.PassengerName),
            ("Flight", bag.Flight == "KQ504" ? "KQ-504 NBO → EBB" : $"{bag.Flight} (not KQ-504)"),
            ("Class / tag", $"{row.ClassText} · {bag.TagType} tag · {bag.WeightKg} kg"),
            ("Status", $"{row.StatusText}{(bag.Uld != null ? $" in {bag.Uld}" : "")}"),
            ("Authority to load", bag.AuthorityToLoad ? "Yes" : "No (ticket not valid)"),
            ("Passenger status", bag.PassengerStatus.ToString()),
            ("Tag", row.SourceText)));

        panel.Children.Add(new TextBlock { Text = "Event history (append-only audit trail)", FontWeight = FontWeights.SemiBold });
        var timeline = new StackPanel { Spacing = 0 };
        foreach (var e in events.Reverse())
            timeline.Children.Add(TimelineItem(e));
        panel.Children.Add(new ScrollViewer { Content = timeline, MaxHeight = 360 });
        dialog.Content = panel;

        var choice = await dialog.ShowAsync();
        if (choice == ContentDialogResult.Secondary)
        {
            var write = new WriteTagDialog(bag.Plate, bag.PassengerName, 1, 1) { XamlRoot = root };
            if (await write.ShowAsync() == ContentDialogResult.Primary && write.Result is { } written)
                AppServices.State.ShowToast("Bag tag written", $"{bag.PassengerName}: plate {bag.Plate} is now on the tag. Hold it at check-in to confirm.");
            return;
        }
        if (choice != ContentDialogResult.Primary || bag.Epc == null) return;
        try
        {
            if (canFlag)
            {
                var foreign = await AppServices.Engine.FlagForeignAsync(bag.Epc);
                AppServices.State.ShowToast("Misroute demo", $"Tag now belongs to KQ-412 bag {foreign}. Pass it through the Belt 04 antennas.");
            }
            else
            {
                await AppServices.Engine.UnflagForeignAsync(bag.Epc);
                AppServices.State.ShowToast("Misroute demo", "Tag returned to its KQ-504 bag.");
            }
        }
        catch (Exception ex)
        {
            AppServices.State.ShowToast("Could not change the tag", ex.Message, isError: true);
        }
    }

    private static Grid Facts(params (string Label, string Value)[] facts)
    {
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < facts.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = facts[i].Label, Foreground = Ui.Resource("TextFillColorSecondaryBrush") };
            var value = new TextBlock { Text = facts[i].Value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            Grid.SetRow(label, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            grid.Children.Add(label);
            grid.Children.Add(value);
        }
        return grid;
    }

    private static Grid TimelineItem(BagEvent e)
    {
        var problem = e.Kind is "LoadRefused" or "BSM-DEL" or "TagWriteRefused" or "TagUnbound" || e.Outcome is ScanOutcome.WrongFlight or ScanOutcome.Offloaded;
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 12, Height = 12, Margin = new Thickness(0, 4, 0, 0), VerticalAlignment = VerticalAlignment.Top,
            Fill = Ui.Resource(problem ? "StatusExceptionBrush" : e.Kind is "Loaded" or "Sorted" or "DeskBind" or "DeskCheck" ? "StatusMatchedBrush" : "StatusExpectedBrush"),
        };
        grid.Children.Add(dot);

        var text = new StackPanel { Spacing = 2 };
        var where = e.ScanPoint != null ? $" · {e.ScanPoint}{(e.Antenna is { } a ? $" ant {a}" : "")}{(e.Rssi is { } rssi ? $" · RSSI {rssi:N0}" : "")}{(e.Uld != null ? $" → {e.Uld}" : "")}" : "";
        text.Children.Add(new TextBlock { Text = $"{e.OccurredUtc.ToLocalTime():HH:mm:ss}  {e.Kind}{where}", FontWeight = FontWeights.SemiBold });
        if (!string.IsNullOrEmpty(e.Note))
            text.Children.Add(new TextBlock { Text = e.Note, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Resource("TextFillColorSecondaryBrush") });
        if (e.Handler != null || e.ReaderId != null)
            text.Children.Add(new TextBlock
            {
                Text = string.Join(" · ", new[] { e.Handler is { } h ? $"by {h}" : null, e.ReaderId is { } r ? $"reader {r}" : null }.Where(s => s != null)),
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Foreground = Ui.Resource("TextFillColorTertiaryBrush"),
            });
        if (e.RawMessage != null)
            text.Children.Add(new Expander
            {
                Header = "Type B message",
                Content = new TextBlock { Text = e.RawMessage, FontFamily = new FontFamily("Cascadia Mono, Consolas"), IsTextSelectionEnabled = true },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 4, 0, 0),
            });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }
}

/// <summary>
/// Adds a passenger to KQ-504 during the demo (e.g. a guest from the audience). The app sends their BSM and keeps their
/// bags for real tags: the next tag held at Desk 14 is linked to them.
/// </summary>
public sealed class AddPassengerDialog : ContentDialog
{
    private readonly TextBox _surname = new() { Header = "Surname", PlaceholderText = "e.g. Otieno", MaxLength = 30 };
    private readonly TextBox _initial = new() { Header = "Initial", PlaceholderText = "A", MaxLength = 1, Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ComboBox _class = new() { Header = "Class", ItemsSource = new[] { "Economy (Y)", "SkyPriority (W)", "Business (J)" }, SelectedIndex = 0, MinWidth = 200 };
    private readonly NumberBox _bags = new() { Header = "Bags", Value = 1, Minimum = 1, Maximum = 9, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Width = 140, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly NumberBox _weight = new() { Header = "Total weight (kg)", Value = 23, Minimum = 1, Maximum = 200, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ToggleSwitch _ticket = new() { Header = "Ticket valid (authority to load)", IsOn = true };
    private readonly TextBlock _error = new() { Foreground = Ui.Resource("SystemFillColorCriticalBrush"), TextWrapping = TextWrapping.Wrap };

    public AddPassengerDialog()
    {
        Title = "Add a passenger to KQ-504";
        PrimaryButtonText = "Add passenger";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;

        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _surname, _initial } };
        _surname.Width = 260;
        var bagRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _bags, _weight } };
        Content = new StackPanel
        {
            Spacing = 14, MinWidth = 460,
            Children =
            {
                new TextBlock
                {
                    Text = "The app sends this passenger's BSM, as the check-in system would. Their bags are kept for real tags: the next tag held at Desk 14 (antenna 0) is linked to them, one tag per bag.",
                    TextWrapping = TextWrapping.Wrap, Foreground = Ui.Resource("TextFillColorSecondaryBrush"),
                },
                nameRow, _class, bagRow, _ticket, _error,
            },
        };

        PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                var cls = _class.SelectedIndex switch { 2 => CabinClass.Business, 1 => CabinClass.SkyPriority, _ => CabinClass.Economy };
                Plates = await AppServices.AddPassengerAsync(_surname.Text, _initial.Text, cls,
                    double.IsNaN(_bags.Value) ? 1 : (int)_bags.Value, double.IsNaN(_weight.Value) ? 23 : (int)_weight.Value, _ticket.IsOn);
                PassengerName = $"{_initial.Text.Trim().ToUpperInvariant()}. {_surname.Text.Trim().ToUpperInvariant()}";
            }
            catch (ArgumentException ex)
            {
                _error.Text = ex.Message;
                args.Cancel = true;
            }
            finally
            {
                deferral.Complete();
            }
        };
    }

    public IReadOnlyList<string> Plates { get; private set; } = [];
    public string PassengerName { get; private set; } = "";
}

/// <summary>
/// Writes a bag's licence plate onto the tag held at the check-in antenna: the POC's "print the bag tag". Skip leaves
/// the bag waiting, so the next unencoded tag at check-in is linked to it instead.
/// </summary>
public sealed class WriteTagDialog : ContentDialog
{
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressRing _busy = new() { IsActive = false, Width = 20, Height = 20 };

    public WriteTagDialog(string plate, string passenger, int index, int count)
    {
        Title = count > 1 ? $"Write bag tag {index} of {count}" : "Write bag tag";
        PrimaryButtonText = "Write tag";
        CloseButtonText = count > 1 && index < count ? "Skip this bag" : "Skip";
        DefaultButton = ContentDialogButton.Primary;

        var encoded = LicencePlateCodec.Encode(new KQ.Brs.Core.Domain.LicencePlate(plate));
        Content = new StackPanel
        {
            Spacing = 12, MinWidth = 460,
            Children =
            {
                new TextBlock { Text = $"{passenger} · licence plate {plate}", FontWeight = FontWeights.SemiBold },
                new TextBlock
                {
                    Text = "Hold ONE tag still at the Check-in · Desk 14 antenna (put any other tags away), then click Write tag. The plate is written into the tag and checked by reading it back.",
                    TextWrapping = TextWrapping.Wrap, Foreground = Ui.Resource("TextFillColorSecondaryBrush"),
                },
                new TextBlock { Text = $"Tag will read: {Spaced(encoded)}", FontFamily = new FontFamily("Cascadia Mono, Consolas"), IsTextSelectionEnabled = true },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _busy, _status } },
            },
        };

        PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            IsPrimaryButtonEnabled = false;
            _busy.IsActive = true;
            _status.Foreground = Ui.Resource("TextFillColorPrimaryBrush");
            _status.Text = "Writing…";
            try
            {
                Result = await AppServices.WriteTagAsync(plate);
            }
            catch (Exception ex) when (ex is TagWriteException or AlienCommandException)
            {
                _status.Foreground = Ui.Resource("SystemFillColorCriticalBrush");
                _status.Text = ex.Message;
                args.Cancel = true;   // keep the dialog open so they can fix it and retry
            }
            finally
            {
                _busy.IsActive = false;
                IsPrimaryButtonEnabled = true;
                deferral.Complete();
            }
        };
    }

    public TagWriteResult? Result { get; private set; }

    private static string Spaced(string hex) => string.Join(" ", Enumerable.Range(0, hex.Length / 4).Select(i => hex.Substring(i * 4, 4)));
}

/// <summary>FR-10: before pushback, bags checked in but not loaded, and loaded bags that must come off.</summary>
public sealed class PrePushbackDialog : ContentDialog
{
    public PrePushbackDialog(PrePushbackReport report)
    {
        Title = "Pre-pushback check · KQ-504";
        CloseButtonText = "Close";
        if (report.CheckedInNotLoaded.Count > 0) PrimaryButtonText = "Raise NotLoaded exceptions";

        var panel = new StackPanel { Spacing = 12, MinWidth = 560 };
        var ok = report.CheckedInNotLoaded.Count == 0 && report.OffloadRequired.Count == 0;
        panel.Children.Add(new InfoBar
        {
            IsOpen = true, IsClosable = false,
            Severity = ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
            Title = ok ? "Clear for pushback" : "Not clear for pushback",
            Message = $"{report.Loaded} of {report.Total} bag(s) loaded · {report.CheckedInNotLoaded.Count} checked in but not loaded · {report.OffloadRequired.Count} to offload · {report.NeverCheckedIn} never checked in",
        });
        panel.Children.Add(Section("Checked in, not loaded", report.CheckedInNotLoaded));
        panel.Children.Add(Section("Offload required (passenger not flying)", report.OffloadRequired));
        Content = new ScrollViewer { Content = panel, MaxHeight = 520 };
    }

    private static StackPanel Section(string title, IReadOnlyList<KQ.Brs.Core.Events.BagView> bags)
    {
        var p = new StackPanel { Spacing = 4 };
        p.Children.Add(new TextBlock { Text = $"{title} ({bags.Count})", FontWeight = FontWeights.SemiBold });
        foreach (var b in bags.Take(60))
            p.Children.Add(new TextBlock { Text = $"{b.Plate}   {b.PassengerName,-24}  {b.Status}{(b.Uld != null ? " in " + b.Uld : "")}", FontFamily = new FontFamily("Cascadia Mono, Consolas") });
        if (bags.Count > 60) p.Children.Add(new TextBlock { Text = $"… and {bags.Count - 60} more" });
        if (bags.Count == 0) p.Children.Add(new TextBlock { Text = "None", Foreground = Ui.Resource("TextFillColorSecondaryBrush") });
        return p;
    }
}
