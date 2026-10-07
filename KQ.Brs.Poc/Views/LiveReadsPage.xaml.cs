using System.Collections.ObjectModel;
using KQ.Brs.Core.Domain;
using KQ.Brs.Poc.Services;
using KQ.Brs.Poc.ViewModels;
using KQ.Rfid.Alien;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace KQ.Brs.Poc.Views;

public sealed partial class LiveReadsPage : Page
{
    private readonly ObservableCollection<AntennaCardViewModel> _antennas = new(Enumerable.Range(0, 4).Select(a => new AntennaCardViewModel(a)));
    private readonly ObservableCollection<string> _raw = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private int _ticks;
    private bool _subscribed;

    public LiveReadsPage()
    {
        InitializeComponent();
        AntennaCards.ItemsSource = _antennas;
        RawList.ItemsSource = _raw;
        _timer.Tick += (_, _) => Tick();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ScanList.ItemsSource = AppServices.State.ScanLog;
        if (!_subscribed)
        {
            _subscribed = true;
            AppServices.State.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(AppState.Health)) RenderReader(); };
        }
        RenderReader();
        _timer.Start();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => _timer.Stop();

    private void Tick()
    {
        // Raw reads: drain what the pipeline queued since the last tick (newest first, capped).
        var queue = AppServices.Pipeline.RecentReads;
        var batch = new List<string>();
        while (queue.TryDequeue(out var r))
            batch.Add($"{r.LastSeenUtc.ToLocalTime():HH:mm:ss.fff}  a{r.Antenna}  {r.Epc}  {r.Rssi:N0}");
        if (PauseRaw.IsOn) batch.Clear();
        foreach (var line in batch.TakeLast(60)) _raw.Insert(0, line);
        while (_raw.Count > 300) _raw.RemoveAt(_raw.Count - 1);

        if (++_ticks % 4 != 0) return;   // antenna figures once a second
        var settings = AppServices.Settings.Brs;
        foreach (var card in _antennas)
        {
            var assignment = settings.Antennas.FirstOrDefault(a => a.Antenna == card.Antenna);
            var name = Ui.AntennaName(settings, card.Antenna);
            card.Sample(AppServices.Pipeline.Antennas[card.Antenna], name, assignment?.Enabled ?? false);
        }
        RenderCounters();
    }

    private void RenderReader()
    {
        var h = AppServices.State.Health;
        var s = AppServices.Settings;
        ReaderTitle.Text = h.ReaderName ?? "Alien ALR-9900+";
        ReaderSub.Text = $"{s.ReaderHost}:{s.ReaderPort}";
        CommandText.Text = $"{s.ReaderHost}:{s.ReaderPort}";
        StreamText.Text = h.StreamEndPoint ?? $"this PC :{s.StreamPort}";

        (ReaderInfo.Severity, ReaderInfo.Title) = h.State switch
        {
            SupervisorState.Streaming => (InfoBarSeverity.Success, "Streaming"),
            SupervisorState.Online => (InfoBarSeverity.Informational, "Connected, not streaming"),
            SupervisorState.Connecting => (InfoBarSeverity.Informational, "Connecting"),
            SupervisorState.Retrying => (InfoBarSeverity.Error, "Reader offline"),
            _ => (InfoBarSeverity.Warning, "Disconnected"),
        };
        ReaderInfo.Message = h.Message;

        var connected = h.State is SupervisorState.Online or SupervisorState.Streaming;
        var trying = h.State is SupervisorState.Connecting or SupervisorState.Retrying;
        ConnectButton.Content = connected || trying ? "Disconnect" : "Connect";
        StreamButton.Content = h.State == SupervisorState.Streaming ? "Stop stream" : "Start stream";
        StreamButton.IsEnabled = connected;
        RenderCounters();
    }

    private void RenderCounters()
    {
        var reader = AppServices.Reader.Reader;
        CountersText.Text = $"{reader?.ReadCount ?? 0:N0} reads · {AppServices.State.Health.Reconnects} reconnects · {AppServices.Pipeline.FilteredReads:N0} filtered";
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        var state = AppServices.State.Health.State;
        if (state is SupervisorState.Disconnected) AppServices.Reader.Connect(startStreaming: true);
        else await AppServices.Reader.DisconnectAsync();
    }

    private async void Stream_Click(object sender, RoutedEventArgs e) =>
        await AppServices.Reader.SetStreamingAsync(AppServices.State.Health.State != SupervisorState.Streaming);

    private async void Discover_Click(object sender, RoutedEventArgs e)
    {
        DiscoverButton.IsEnabled = false;
        DiscoverText.Text = "Listening for reader heartbeats (UDP 3988)…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            var found = new List<ReaderHeartbeat>();
            await Task.Run(async () =>
            {
                await foreach (var hb in ReaderDiscovery.ListenAsync(cts.Token))
                {
                    if (found.Any(f => f.MacAddress == hb.MacAddress)) continue;
                    found.Add(hb);
                    DispatcherQueue.TryEnqueue(() => DiscoverText.Text = string.Join("\n",
                        found.Select(f => $"{f.Name} · {f.IPAddress}:{f.CommandPort} · {f.MacAddress}")));
                }
            });
            if (found.Count == 0) DiscoverText.Text = "No heartbeats heard. Is the reader on this network?";
        }
        catch (Exception ex)
        {
            DiscoverText.Text = $"Discovery failed: {ex.Message}";
        }
        finally
        {
            DiscoverButton.IsEnabled = true;
        }
    }
}
