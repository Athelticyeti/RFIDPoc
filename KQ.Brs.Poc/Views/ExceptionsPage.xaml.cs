using System.Collections.ObjectModel;
using KQ.Brs.Core.Domain;
using KQ.Brs.Poc.Services;
using KQ.Brs.Poc.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace KQ.Brs.Poc.Views;

public sealed partial class ExceptionsPage : Page
{
    private readonly ObservableCollection<ExceptionRowViewModel> _filtered = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _dirty = true;
    private bool _subscribed;

    public ExceptionsPage()
    {
        InitializeComponent();
        List.ItemsSource = _filtered;
        _timer.Tick += (_, _) => { if (_dirty) Rebuild(); };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        // The real/virtual filter only matters when the simulator has virtual bags.
        RealOnly.Visibility = AppServices.Settings.GenerateDemoBags ? Visibility.Visible : Visibility.Collapsed;
        if (!AppServices.Settings.GenerateDemoBags) RealOnly.IsOn = false;
        if (!_subscribed)
        {
            _subscribed = true;
            AppServices.State.Exceptions.CollectionChanged += (_, _) => _dirty = true;
            // States change in place (Open → Resolved), which moves rows between tabs.
            AppServices.State.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(AppState.Totals)) _dirty = true; };
        }
        Rebuild();
        _timer.Start();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => _timer.Stop();

    private void StateBar_Changed(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => Rebuild();

    private void Filter_Changed(object sender, RoutedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        if (!_subscribed) return;
        _dirty = false;
        var tag = StateBar.SelectedItem?.Tag as string ?? "Open";
        var rows = AppServices.State.Exceptions.Where(x =>
            (tag == "All" || x.Exception.State.ToString() == tag) &&
            (!RealOnly.IsOn || x.Exception.Source != BagSource.Virtual)).ToList();

        // Replace only if different, so buttons don't flicker every second.
        if (!rows.SequenceEqual(_filtered))
        {
            _filtered.Clear();
            foreach (var r in rows) _filtered.Add(r);
        }
        Empty.Visibility = _filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ViewBag_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string plate) await BagDetailDialog.ShowForAsync(plate, XamlRoot);
    }

    private async void Ack_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string id) await AppServices.Engine.AcknowledgeAsync(id);
        _dirty = true;
    }

    private async void Resolve_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string id) return;
        var note = new TextBox { Header = "What was done?", PlaceholderText = "e.g. Bag found and loaded on rescan", AcceptsReturn = true, MinHeight = 80 };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Resolve exception", Content = note,
            PrimaryButtonText = "Resolve", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(note.Text)) return;
        await AppServices.Engine.ResolveAsync(id, "OPS", note.Text.Trim());
        _dirty = true;
    }

    private async void Override_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string id) return;
        var pin = new PasswordBox { Header = "Supervisor PIN", Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
        var reason = new TextBox { Header = "Reason (recorded in the audit trail)", AcceptsReturn = true, MinHeight = 80 };
        var error = new TextBlock { Foreground = Ui.Resource("SystemFillColorCriticalBrush") };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Supervisor override",
            Content = new StackPanel { Spacing = 12, Children = { pin, reason, error } },
            PrimaryButtonText = "Override", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (pin.Password != AppServices.Settings.SupervisorPin) { error.Text = "Wrong PIN."; args.Cancel = true; }
            else if (string.IsNullOrWhiteSpace(reason.Text)) { error.Text = "A reason is required."; args.Cancel = true; }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await AppServices.Engine.OverrideAsync(id, "SUPERVISOR", reason.Text.Trim());
        _dirty = true;
    }
}
