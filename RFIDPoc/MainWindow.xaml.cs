using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using nsAlienRFID2;

namespace RFIDPoc
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<TagRow> _tags = new();
        private readonly Dictionary<string, TagRow> _tagsById = new();

        private ReaderConnection? _connection;
        private clsReaderMonitor? _monitor;
        private CancellationTokenSource? _pollCts;
        private LiveReader? _live;

        private const int MaxLogLines = 500;

        // Live data arrives on background threads and is queued here; the UI timer drains it every 100 ms, so a
        // burst of hundreds of reads per second becomes ~10 cheap grid updates per second instead of a flood.
        private readonly ConcurrentQueue<(TagInfo[] Tags, bool PerRead)> _pending = new();
        private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
        private DateTime? _lastUpdate;
        private int _lastUpdateTagCount;
        private bool _lastUpdatePerRead;
        private int _staleDropped;
        private readonly Queue<(DateTime Time, int Reads)> _recentReads = new();

        public MainWindow()
        {
            InitializeComponent();
            TagGrid.ItemsSource = _tags;
            _uiTimer.Tick += UiTimer_Tick;
            _uiTimer.Start();
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_connection?.IsConnected == true)
            {
                await DisconnectAsync();
                return;
            }

            if (!int.TryParse(PortBox.Text, out var port))
            {
                Log("Invalid port.");
                return;
            }

            var ip = IpBox.Text.Trim();
            var user = UserBox.Text.Trim();
            var password = PasswordBox.Password;

            ConnectButton.IsEnabled = false;
            SetStatus($"Connecting to {ip}:{port}...");
            try
            {
                _connection = await Task.Run(() => ReaderConnection.Open(ip, port, user, password));
                var (name, type) = await _connection.RunAsync(r => (r.ReaderName, r.ReaderType));

                _connection.Reader.Disconnected += data => Dispatcher.BeginInvoke(() => OnReaderLost($"Disconnected: {data}"));
                _connection.Reader.AlienChannelDown += msg => Dispatcher.BeginInvoke(() => OnReaderLost($"Channel down: {msg}"));

                Log($"Connected to '{name}' ({type}) at {ip}:{port}");
                SetStatus($"Connected: {name}");
                SetConnectedUi(true);
            }
            catch (Exception ex)
            {
                Log($"Error: {ex.Message}");
                SetStatus("Disconnected");
                _connection = null;
            }
            finally
            {
                ConnectButton.IsEnabled = true;
            }
        }

        private async void ReadOnceButton_Click(object sender, RoutedEventArgs e)
        {
            ReadOnceButton.IsEnabled = false;
            try
            {
                var count = await ReadTagsAsync();
                Log($"Read once: {count} tag(s) in list.");
            }
            catch (Exception ex)
            {
                Log($"Read error: {ex.Message}");
            }
            finally
            {
                ReadOnceButton.IsEnabled = _connection?.IsConnected == true && _pollCts == null;
            }
        }

        private async void PollButton_Click(object sender, RoutedEventArgs e)
        {
            if (_pollCts != null)
            {
                _pollCts.Cancel();
                return;
            }

            if (!int.TryParse(IntervalBox.Text, out var interval) || interval < 50)
                interval = 500;

            _pollCts = new CancellationTokenSource();
            var token = _pollCts.Token;
            PollButton.Content = "Stop Polling";
            ReadOnceButton.IsEnabled = false;
            Log($"Polling every {interval} ms...");

            try
            {
                while (!token.IsCancellationRequested)
                {
                    await ReadTagsAsync();
                    await Task.Delay(interval, token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log($"Polling stopped: {ex.Message}");
            }
            finally
            {
                _pollCts.Dispose();
                _pollCts = null;
                PollButton.Content = "Start Polling";
                ReadOnceButton.IsEnabled = _connection?.IsConnected == true;
                Log("Polling stopped.");
            }
        }

        /// <summary>
        /// Fetches the reader's current tag list ("get TagList") and merges it into the grid.
        /// </summary>
        private async Task<int> ReadTagsAsync()
        {
            if (_connection?.IsConnected != true)
                throw new InvalidOperationException("Reader is not connected.");

            var tags = await _connection.RunAsync(reader =>
            {
                var raw = reader.TagList;
                if (string.IsNullOrWhiteSpace(raw) || raw.Contains("(No Tags)", StringComparison.OrdinalIgnoreCase))
                    return Array.Empty<TagInfo>();

                AlienUtils.ParseTagList(raw, out var parsed);
                if (parsed is { Length: > 0 })
                    return parsed;

                // Reader is in a custom TagListFormat the SDK can't parse (e.g. "%k" = bare EPC per line),
                // so take each line as a tag ID; antenna/count/RSSI aren't available in that format.
                return raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                          .Where(line => line.Length > 0 && !line.Contains('='))
                          .Select(line => new TagInfo { TagID = line, ReadCount = 1 })
                          .ToArray();
            });

            MergeTags(tags);
            return tags.Length;
        }

        /// <summary>
        /// Merges tags into the grid. Must run on the UI thread. perRead = each entry is one read (Tag Stream);
        /// otherwise the array is the reader's tag list and ReadCount is the reader's running count.
        /// </summary>
        private void MergeTags(TagInfo[] tags, bool perRead = false)
        {
            var now = DateTime.Now;
            _lastUpdate = now;
            _lastUpdateTagCount = tags.Length;
            _lastUpdatePerRead = perRead;
            if (perRead)
                _recentReads.Enqueue((now, tags.Length));
            var added = new List<string>();
            foreach (var tag in tags)
            {
                if (string.IsNullOrWhiteSpace(tag.TagID)) continue;
                var id = tag.TagID.Replace(" ", "").Trim();

                if (!_tagsById.TryGetValue(id, out var row))
                {
                    row = new TagRow { TagId = id, FirstSeen = now };
                    _tagsById[id] = row;
                    _tags.Add(row);
                    added.Add(id);
                }

                row.Antenna = tag.Antenna;
                // A tag list carries the reader's running count per tag; a stream carries single reads.
                row.ReadCount = perRead ? row.ReadCount + 1 : Math.Max(tag.ReadCount, 1);
                row.Rssi = tag.RSSI;
                row.LastSeen = now;
            }

            if (added.Count == 1)
                Log($"New tag: {added[0]}");
            else if (added.Count > 1)
                Log($"{added.Count} new tags (e.g. {added[0]})");
        }

        /// <summary>
        /// Applies queued live data, then refreshes the "last update" line so it's visible whether the reader
        /// is sending or the screen is lagging.
        /// </summary>
        private void UiTimer_Tick(object? sender, EventArgs e)
        {
            // Stream reads are merged as one batch; notifications are whole lists, so only the newest matters.
            var streamReads = new List<TagInfo>();
            TagInfo[]? latestList = null;
            while (_pending.TryDequeue(out var item))
            {
                if (item.PerRead) streamReads.AddRange(item.Tags);
                else latestList = item.Tags;
            }
            if (latestList != null) MergeTags(latestList);
            if (streamReads.Count > 0) MergeTags(streamReads.ToArray(), perRead: true);

            if (_lastUpdate is not { } last)
            {
                UpdateText.Text = "";
                return;
            }

            var now = DateTime.Now;
            while (_recentReads.TryPeek(out var r) && now - r.Time > TimeSpan.FromSeconds(1))
                _recentReads.Dequeue();

            var age = (now - last).TotalSeconds;
            var stale = _staleDropped > 0 ? $" · {_staleDropped} stale notification(s) dropped" : "";
            UpdateText.Text = _lastUpdatePerRead
                ? $"Last read {age:F1} s ago · {_recentReads.Sum(r => r.Reads)} reads/s · {_tags.Count} unique tags"
                : $"Last update {age:F1} s ago ({_lastUpdateTagCount} tags) · {_tags.Count} unique tags{stale}";
        }

        private async void LiveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_connection?.IsConnected != true) return;
            LiveButton.IsEnabled = false;
            try
            {
                if (_live != null)
                {
                    await StopLiveAsync();
                    return;
                }

                _pollCts?.Cancel();
                var mode = LiveModeBox.SelectedIndex == 1 ? LiveMode.Notify : LiveMode.Stream;
                var live = new LiveReader(_connection, mode);
                // Listeners and parsing run on background threads; data is queued for the UI timer to merge.
                var perRead = mode == LiveMode.Stream;
                live.TagsReceived += tags => _pending.Enqueue((tags, perRead));
                live.StaleDropped += count => Dispatcher.BeginInvoke(() => _staleDropped = count);
                live.Error += msg => Dispatcher.BeginInvoke(() => Log(msg));
                _staleDropped = 0;
                _pending.Clear();

                await live.StartAsync();
                _live = live;
                LiveButton.Content = "Stop Live";
                ReadOnceButton.IsEnabled = PollButton.IsEnabled = LiveModeBox.IsEnabled = false;
                SetStatus($"Live ({mode}) - reader pushing to {live.ListenAddress}");
                Log(mode == LiveMode.Stream
                    ? $"Live stream on: reader sends every read to {live.ListenAddress} as it happens."
                    : $"Live notify on: reader sends its tag list to {live.ListenAddress} when tags change.");
            }
            catch (Exception ex)
            {
                Log($"Could not start live mode: {ex.Message}");
                _live?.Dispose();
                _live = null;
            }
            finally
            {
                LiveButton.IsEnabled = _connection?.IsConnected == true;
            }
        }

        private async Task StopLiveAsync()
        {
            if (_live == null) return;
            var live = _live;
            _live = null;
            try
            {
                await live.StopAsync();
                Log("Live mode off: AutoMode=OFF.");
            }
            catch (Exception ex)
            {
                Log($"Error stopping live mode: {ex.Message}");
            }
            LiveButton.Content = "Start Live";
            var connected = _connection?.IsConnected == true;
            ReadOnceButton.IsEnabled = PollButton.IsEnabled = connected;
            LiveModeBox.IsEnabled = true;
            if (connected) SetStatus("Connected");
        }

        private void DiscoverButton_Click(object sender, RoutedEventArgs e)
        {
            if (_monitor != null)
            {
                StopDiscovery();
                return;
            }

            try
            {
                // Listens for the UDP heartbeats Alien readers broadcast (port 3988, every ~30s by default).
                _monitor = new clsReaderMonitor { ComPortsMonitoring = false, NetworkMonitoring = true };
                _monitor.ReaderAddedOnNetwork += info => Dispatcher.BeginInvoke(() => OnReaderDiscovered(info));
                _monitor.StartListening();
                DiscoverButton.Content = "Stop Discover";
                Log("Listening for reader heartbeats (can take up to ~30s)...");
            }
            catch (Exception ex)
            {
                Log($"Discovery error: {ex.Message}");
                _monitor = null;
            }
        }

        private void OnReaderDiscovered(IReaderInfo info)
        {
            Log($"Found reader: {info.Name} ({info.Type}) at {info.IPAddress}:{info.TelnetPort}, MAC {info.MACAddress}");
            if (_connection?.IsConnected != true)
            {
                IpBox.Text = info.IPAddress;
                PortBox.Text = info.TelnetPort.ToString();
            }
        }

        private void StopDiscovery()
        {
            try
            {
                _monitor?.StopListening();
                _monitor?.Dispose();
            }
            catch { }
            _monitor = null;
            DiscoverButton.Content = "Discover";
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            _tags.Clear();
            _tagsById.Clear();
            _lastUpdate = null;
        }

        private async Task DisconnectAsync()
        {
            _pollCts?.Cancel();
            await StopLiveAsync();
            try
            {
                if (_connection != null)
                    await _connection.RunAsync(reader => reader.Disconnect());
            }
            catch (Exception ex)
            {
                Log($"Disconnect error: {ex.Message}");
            }
            _connection = null;
            SetConnectedUi(false);
            SetStatus("Disconnected");
            Log("Disconnected.");
        }

        private void OnReaderLost(string message)
        {
            Log(message);
            _pollCts?.Cancel();
            if (_connection?.IsConnected != true)
            {
                _live?.Dispose();
                _live = null;
                SetConnectedUi(false);
                SetStatus("Disconnected");
            }
        }

        private void ConfigButton_Click(object sender, RoutedEventArgs e)
        {
            if (_connection?.IsConnected != true) return;
            new ConfigWindow(_connection) { Owner = this }.ShowDialog();
        }

        private void SetConnectedUi(bool connected)
        {
            ConnectButton.Content = connected ? "Disconnect" : "Connect";
            ReadOnceButton.IsEnabled = connected;
            PollButton.IsEnabled = connected;
            ConfigButton.IsEnabled = connected;
            LiveButton.IsEnabled = connected;
            if (!connected) LiveButton.Content = "Start Live";
            LiveModeBox.IsEnabled = true;
            IpBox.IsEnabled = PortBox.IsEnabled = UserBox.IsEnabled = PasswordBox.IsEnabled = !connected;
        }

        private void SetStatus(string text) => StatusText.Text = text;

        private void Log(string message)
        {
            LogBox.AppendText($"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
            // A TextBox gets slow as its text grows, so keep only the most recent lines.
            if (LogBox.LineCount > MaxLogLines + 100)
                LogBox.Text = LogBox.Text[LogBox.GetCharacterIndexFromLineIndex(LogBox.LineCount - MaxLogLines)..];
            LogBox.ScrollToEnd();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _pollCts?.Cancel();
            _live?.Dispose();
            StopDiscovery();
            try { _connection?.Reader.Disconnect(); } catch { }
        }
    }
}
