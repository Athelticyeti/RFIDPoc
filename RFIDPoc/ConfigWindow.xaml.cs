using System.Windows;
using System.Windows.Data;

namespace RFIDPoc
{
    /// <summary>
    /// Reads and edits reader settings through the SDK's CLI properties. Settings persist on the reader.
    /// </summary>
    public partial class ConfigWindow : Window
    {
        private readonly ReaderConnection _connection;
        private readonly List<ReaderSetting> _settings = ReaderSetting.CreateAll();

        public ConfigWindow(ReaderConnection connection)
        {
            InitializeComponent();
            _connection = connection;

            var view = new ListCollectionView(_settings);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ReaderSetting.Group)));
            SettingsGrid.ItemsSource = view;

            Loaded += async (_, _) => await RefreshAsync();
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

        private async Task RefreshAsync()
        {
            SetBusy(true, "Reading settings from reader...");
            try
            {
                // Read everything in one trip through the gate; a failure on one setting shouldn't hide the rest.
                var results = await _connection.RunAsync(reader => _settings.Select(s =>
                {
                    try { return (Value: s.ReadFrom(reader), Error: (string?)null); }
                    catch (Exception ex) { return (Value: "", Error: ex.InnerException?.Message ?? ex.Message); }
                }).ToList());

                for (int i = 0; i < _settings.Count; i++)
                {
                    _settings[i].Loaded(results[i].Value);
                    _settings[i].Status = results[i].Error is { } err ? $"Read error: {Clean(err)}" : "";
                }
                SetBusy(false, $"Read {results.Count(r => r.Error == null)} of {_settings.Count} settings.");
            }
            catch (Exception ex)
            {
                SetBusy(false, $"Refresh failed: {ex.Message}");
            }
        }

        private async void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            SettingsGrid.CommitEdit();
            var changes = _settings.Where(s => s.IsDirty).ToList();
            if (changes.Count == 0)
            {
                StatusText.Text = "No changes to apply.";
                return;
            }

            var summary = string.Join(Environment.NewLine, changes.Select(s => $"  {s.Name}:  \"{s.Current}\"  →  \"{s.NewValue.Trim()}\""));
            var answer = MessageBox.Show(this,
                $"Write these settings to the reader? They persist on the device.{Environment.NewLine}{Environment.NewLine}{summary}",
                "Apply reader settings", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;

            SetBusy(true, "Applying...");
            int ok = 0;
            foreach (var setting in changes)
            {
                var value = setting.NewValue.Trim();
                try
                {
                    var readBack = await _connection.RunAsync(reader =>
                    {
                        setting.WriteTo(reader, value);
                        return setting.ReadFrom(reader);
                    });
                    setting.Loaded(readBack);
                    setting.Status = "✓ Applied";
                    ok++;
                }
                catch (Exception ex)
                {
                    setting.Status = $"✗ {Clean(ex.InnerException?.Message ?? ex.Message)}";
                }
            }
            SetBusy(false, $"Applied {ok} of {changes.Count} change(s).");
        }

        private void DiscardButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var s in _settings)
                s.NewValue = s.Current;
            StatusText.Text = "Edits discarded.";
        }

        private async void SyncClockButton_Click(object sender, RoutedEventArgs e)
        {
            var dateTime = _settings.First(s => s.Name == "DateTime");
            var now = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss");
            SetBusy(true, "Setting reader clock...");
            try
            {
                var readBack = await _connection.RunAsync(reader =>
                {
                    reader.DateTime = now;
                    return dateTime.ReadFrom(reader);
                });
                dateTime.Loaded(readBack);
                dateTime.Status = "✓ Synced";
                SetBusy(false, $"Reader clock set to {now}.");
            }
            catch (Exception ex)
            {
                SetBusy(false, $"Clock sync failed: {Clean(ex.InnerException?.Message ?? ex.Message)}");
            }
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            var command = CommandBox.Text.Trim();
            if (command.Length == 0) return;

            SendButton.IsEnabled = false;
            try
            {
                var response = await _connection.RunAsync(reader => reader.SendReceive(command, false));
                CommandOutput.AppendText($"> {command}{Environment.NewLine}{Clean(response)}{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                CommandOutput.AppendText($"> {command}{Environment.NewLine}! {Clean(ex.Message)}{Environment.NewLine}");
            }
            finally
            {
                CommandOutput.ScrollToEnd();
                SendButton.IsEnabled = true;
            }
        }

        private void SetBusy(bool busy, string message)
        {
            RefreshButton.IsEnabled = ApplyButton.IsEnabled = DiscardButton.IsEnabled = SyncClockButton.IsEnabled = !busy;
            StatusText.Text = message;
        }

        private static string Clean(string text) => text.Replace("\0", "").Trim();
    }
}
