using KQ.Brs.Poc.Services;
using KQ.Brs.Poc.ViewModels;
using KQ.Brs.Poc.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KQ.Brs.Poc;

public sealed partial class MainWindow : Window
{
    private static readonly Dictionary<string, Type> Pages = new()
    {
        ["Dashboard"] = typeof(DashboardPage),
        ["Map"] = typeof(MapPage),
        ["LiveReads"] = typeof(LiveReadsPage),
        ["Ramp"] = typeof(RampPage),
        ["Exceptions"] = typeof(ExceptionsPage),
        ["Messages"] = typeof(MessagesPage),
        ["Reports"] = typeof(ReportsPage),
        ["Settings"] = typeof(SettingsPage),
    };

    private bool _closing;
    private bool _started;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "KQ.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1600, 1000));
        if (AppWindow.Presenter is OverlappedPresenter p && DisplayArea.Primary.WorkArea.Height < 1050) p.Maximize();

        ApplyTheme(AppServices.Settings.Theme);
        AppWindow.Closing += OnClosing;
        Root.Loaded += async (_, _) => await StartAsync();
    }

    private async Task StartAsync()
    {
        try
        {
            await AppServices.InitialiseAsync(DispatcherQueue);
        }
        catch (Exception ex)
        {
            LoadingText.Text = $"Could not start: {ex.Message}";
            return;
        }

        var state = AppServices.State;
        ReaderPill.Attach(state);
        FlightText.Text = state.FlightTitle;
        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppState.Totals)) UpdateExceptionBadge(state.Totals.OpenExceptions);
            if (e.PropertyName == nameof(AppState.FlightTitle)) FlightText.Text = state.FlightTitle;
        };
        UpdateExceptionBadge(state.Totals.OpenExceptions);
        state.Toast += ShowToast;
        state.AlarmRaised += row => Alarm.Raise(row);
        state.AlarmCleared += id => Alarm.Clear(id);

        _started = true;
        Nav.SelectedItem = DashboardItem;   // navigates via SelectionChanged
        Loading.Visibility = Visibility.Collapsed;
    }

    public void ApplyTheme(AppTheme theme) =>
        Root.RequestedTheme = theme switch { AppTheme.Light => ElementTheme.Light, AppTheme.Dark => ElementTheme.Dark, _ => ElementTheme.Default };

    /// <summary>Opens a page by tag (used by "view bag", "go to exceptions" links).</summary>
    public void Navigate(string tag, object? parameter = null)
    {
        if (!Pages.TryGetValue(tag, out var page)) return;
        ContentFrame.Navigate(page, parameter);
        Nav.SelectedItem = tag == "Settings"
            ? Nav.SettingsItem
            : Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == tag);
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (!_started) return;
        var tag = args.IsSettingsSelected ? "Settings" : (args.SelectedItem as NavigationViewItem)?.Tag as string;
        if (tag != null && Pages.TryGetValue(tag, out var page) && ContentFrame.CurrentSourcePageType != page)
            ContentFrame.Navigate(page);
    }

    private void UpdateExceptionBadge(int open)
    {
        ExceptionsBadge.Value = open;
        ExceptionsBadge.Visibility = open > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowToast(string title, string message, bool isError)
    {
        var bar = new InfoBar
        {
            Title = title, Message = message, IsOpen = true,
            Severity = isError ? InfoBarSeverity.Error : InfoBarSeverity.Success,
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AcrylicInAppFillColorDefaultBrush"],
        };
        bar.Closed += (_, _) => Toasts.Children.Remove(bar);
        Toasts.Children.Add(bar);
        while (Toasts.Children.Count > 4) Toasts.Children.RemoveAt(0);

        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(isError ? 8 : 4);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => { if (!_closing) Toasts.Children.Remove(bar); };
        timer.Start();
    }

    /// <summary>Stop the reader cleanly (AutoMode OFF) before the window goes away.</summary>
    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closing) return;
        args.Cancel = true;
        _closing = true;
        // Stop every UI update first: timers that tick after the window is gone would touch destroyed controls.
        AppServices.State?.Stop();
        ReaderPill.Stop();
        Toasts.Children.Clear();
        ContentFrame.Navigate(typeof(Page));   // leaves the current page, which stops its own refresh timer
        LoadingText.Text = "Stopping reader…";
        Loading.Visibility = Visibility.Visible;
        try { await AppServices.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(6)); } catch { }
        Close();
    }
}
