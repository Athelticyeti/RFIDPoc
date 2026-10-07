using System.Diagnostics;
using CommunityToolkit.WinUI.Controls;
using KQ.Brs.Core;
using KQ.Brs.Core.Domain;
using KQ.Brs.Poc.Services;
using KQ.Brs.Poc.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace KQ.Brs.Poc.Views;

public sealed partial class SettingsPage : Page
{
    private readonly (ComboBox Point, ToggleSwitch Enabled, NumberBox MinRssi)[] _antennas = new (ComboBox, ToggleSwitch, NumberBox)[4];
    private bool _loading;
    private readonly SettingsCard[] _cards;

    public SettingsPage()
    {
        InitializeComponent();
        _cards = [Ant0Card, Ant1Card, Ant2Card, Ant3Card];
        var cards = _cards;
        for (var i = 0; i < 4; i++)
        {
            var point = new ComboBox { Header = "Scan point", MinWidth = 220, ItemsSource = ScanPoint.All.Select(p => p.Name).ToList() };
            var enabled = new ToggleSwitch { OnContent = "On", OffContent = "Off", MinWidth = 0, VerticalAlignment = VerticalAlignment.Bottom };
            var minRssi = new NumberBox { Header = "Minimum RSSI", PlaceholderText = "0 = accept all", Minimum = 0, Maximum = 100000, SmallChange = 500, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 140 };
            ToolTipService.SetToolTip(minRssi, "Minimum RSSI (0 = accept all)");
            cards[i].Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { point, minRssi, enabled } };
            _antennas[i] = (point, enabled, minRssi);
        }
    }

    protected override void OnNavigatedTo(NavigationEventArgs e) => Load(AppServices.Settings);

    private void Load(AppSettings s)
    {
        _loading = true;
        HostBox.Text = s.ReaderHost;
        PortBox.Value = s.ReaderPort;
        UserBox.Text = s.ReaderUser;
        PasswordBox.Password = s.ReaderPassword;
        StreamPortBox.Value = s.StreamPort;
        AutoConnectToggle.IsOn = s.AutoConnect;

        for (var i = 0; i < 4; i++)
        {
            var a = s.Brs.Antennas.FirstOrDefault(x => x.Antenna == i) ?? new AntennaAssignment(i, ScanPoint.Belt04, Enabled: false);
            _antennas[i].Point.SelectedIndex = ScanPoint.All.ToList().FindIndex(p => p.Id == a.ScanPointId);
            _antennas[i].Enabled.IsOn = a.Enabled;
            _antennas[i].MinRssi.Value = a.MinRssi;
        }
        for (var i = 0; i < 4; i++) _cards[i].Description = Ui.AntennaName(s.Brs, i);
        DemoBagsToggle.IsOn = s.GenerateDemoBags;
        UnlinkedAlertsToggle.IsOn = s.ShowUnlinkedTagAlerts;
        MisrouteToggle.IsOn = s.ShowMisrouteDemo;
        ArmLoadingToggle.IsOn = s.ArmLoadingOnStart;

        DedupeBox.Value = s.Brs.DedupeWindow.TotalSeconds;
        UnknownBeltToggle.IsOn = s.Brs.RaiseUnknownAtBelt;
        RuleAuthority.IsOn = s.Brs.LoadRules.RequireAuthorityToLoad;
        RuleNoShow.IsOn = s.Brs.LoadRules.RequirePassengerNotNoShow;
        RuleDesk.IsOn = s.Brs.LoadRules.RequireDeskCheckIn;
        RuleBelt.IsOn = s.Brs.LoadRules.RequireBeltScan;
        PinBox.Password = s.SupervisorPin;

        var sim = s.Brs.Simulator;
        PoolBox.Value = s.Brs.RealTagPoolSize;
        SimNoBoardBox.Value = sim.PaxNotBoarded;
        SimDelBox.Value = sim.DeletedBsms;
        SimMisrouteBox.Value = sim.Misroutes;
        SimMissedBox.Value = sim.MissedBeltReadRate * 100;
        SimStrayBox.Value = sim.StrayTags;
        SeedBox.Value = sim.Seed;
        ThemeBox.SelectedIndex = (int)s.Theme;
        SavedText.Text = "";
        _loading = false;
    }

    private static int Int(NumberBox box, int fallback) => double.IsNaN(box.Value) ? fallback : (int)box.Value;

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var old = AppServices.Settings;
        var antennas = Enumerable.Range(0, 4).Select(i => new AntennaAssignment(i,
            ScanPoint.All[Math.Max(0, _antennas[i].Point.SelectedIndex)].Id,
            _antennas[i].Enabled.IsOn,
            double.IsNaN(_antennas[i].MinRssi.Value) ? 0 : _antennas[i].MinRssi.Value)).ToList();

        var settings = old with
        {
            ReaderHost = HostBox.Text.Trim(),
            ReaderPort = Int(PortBox, 23),
            ReaderUser = UserBox.Text.Trim(),
            ReaderPassword = PasswordBox.Password,
            StreamPort = Int(StreamPortBox, 4000),
            AutoConnect = AutoConnectToggle.IsOn,
            SupervisorPin = PinBox.Password.Length > 0 ? PinBox.Password : old.SupervisorPin,
            Theme = (AppTheme)Math.Max(0, ThemeBox.SelectedIndex),
            GenerateDemoBags = DemoBagsToggle.IsOn,
            ShowUnlinkedTagAlerts = UnlinkedAlertsToggle.IsOn,
            ShowMisrouteDemo = MisrouteToggle.IsOn,
            ArmLoadingOnStart = ArmLoadingToggle.IsOn,
            Brs = old.Brs with
            {
                Antennas = antennas,
                DedupeWindow = TimeSpan.FromSeconds(double.IsNaN(DedupeBox.Value) ? 3 : DedupeBox.Value),
                RaiseUnknownAtBelt = UnknownBeltToggle.IsOn,
                RealTagPoolSize = Int(PoolBox, 50),
                LoadRules = new LoadRuleOptions
                {
                    RequireAuthorityToLoad = RuleAuthority.IsOn, RequirePassengerNotNoShow = RuleNoShow.IsOn,
                    RequireDeskCheckIn = RuleDesk.IsOn, RequireBeltScan = RuleBelt.IsOn,
                },
                Simulator = old.Brs.Simulator with
                {
                    PaxNotBoarded = Int(SimNoBoardBox, 4), DeletedBsms = Int(SimDelBox, 2), Misroutes = Int(SimMisrouteBox, 2),
                    MissedBeltReadRate = (double.IsNaN(SimMissedBox.Value) ? 1.5 : SimMissedBox.Value) / 100,
                    StrayTags = Int(SimStrayBox, 3), Seed = Int(SeedBox, 504),
                },
            },
        };

        var readerChanged = settings.ReaderHost != old.ReaderHost || settings.ReaderPort != old.ReaderPort ||
                            settings.ReaderUser != old.ReaderUser || settings.ReaderPassword != old.ReaderPassword ||
                            settings.StreamPort != old.StreamPort;
        AppServices.ApplySettings(settings);
        SavedText.Text = $"Saved {DateTime.Now:HH:mm:ss}";

        if (settings.ArmLoadingOnStart && !old.ArmLoadingOnStart)
        {
            // Arm it now as well, and re-check any tag already lying at the loading antenna.
            await AppServices.Engine.SetRampArmedAsync(true);
            AppServices.Pipeline.ResetScanPoint(ScanPoint.Ramp);
        }

        if (readerChanged && AppServices.State.Health.State != SupervisorState.Disconnected)
        {
            var streaming = AppServices.Reader.WantStreaming;
            await AppServices.Reader.DisconnectAsync();
            AppServices.Reader.Connect(streaming);
            SavedText.Text += " · reconnecting to the reader";
        }
    }

    /// <summary>Fills in the calibrated boardroom set-up (BrsOptions.PocDemoAntennas) and saves it.</summary>
    private void PocDefaults_Click(object sender, RoutedEventArgs e)
    {
        var points = ScanPoint.All.ToList();
        foreach (var a in BrsOptions.PocDemoAntennas)
        {
            _antennas[a.Antenna].Point.SelectedIndex = points.FindIndex(p => p.Id == a.ScanPointId);
            _antennas[a.Antenna].MinRssi.Value = a.MinRssi;
            _antennas[a.Antenna].Enabled.IsOn = a.Enabled;
        }
        DedupeBox.Value = 3;
        ArmLoadingToggle.IsOn = true;
        Save_Click(sender, e);
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeBox.SelectedIndex < 0) return;
        App.Window?.ApplyTheme((AppTheme)ThemeBox.SelectedIndex);
    }

    /// <summary>Deletes all demo data after an explicit "I understand" confirmation. Settings are kept.</summary>
    private async void CleanDatabase_Click(object sender, RoutedEventArgs e)
    {
        var totals = AppServices.State.Totals;
        var understood = new CheckBox { Content = "I understand this cannot be undone." };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Clean the database?",
            Content = new StackPanel
            {
                Spacing = 12, MaxWidth = 460,
                Children =
                {
                    new InfoBar
                    {
                        IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Error, Title = "Everything will be deleted",
                        Message = $"{totals.Expected + totals.Offloaded} bag(s), their passengers and tag links, every scan, exception and Type B message, and the whole audit trail.",
                    },
                    new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Text = (DemoBagsToggle.IsOn
                                   ? "The flight then starts again with the 700 generated demo bags."
                                   : "The flight then starts empty: add passengers from the Dashboard.")
                               + " Tags you have already written keep their licence plate; new passengers get new plate numbers, so old tags won't match them. Settings are kept.",
                    },
                    understood,
                },
            },
            PrimaryButtonText = "Delete everything",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false,
        };
        understood.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = true;
        understood.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled = false;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        if (DemoBagsToggle.IsOn != AppServices.Settings.GenerateDemoBags)
            AppServices.ApplySettings(AppServices.Settings with { GenerateDemoBags = DemoBagsToggle.IsOn });
        await AppServices.ResetDemoAsync(keepBindings: false);
        AppServices.State.ShowToast("Database cleaned", AppServices.Settings.GenerateDemoBags
            ? "Fresh KQ-504 flight with 700 demo bags."
            : "Empty KQ-504 flight. Add passengers from the Dashboard.");
    }

    private void DataFolder_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{SettingsStore.DataFolder}\"") { UseShellExecute = true });
}
