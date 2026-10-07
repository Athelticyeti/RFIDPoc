using System.Runtime.InteropServices;
using KQ.Brs.Core.Domain;
using KQ.Brs.Poc.Services;
using KQ.Brs.Poc.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KQ.Brs.Poc.Controls;

/// <summary>
/// The handheld alarm (FR-06, spec 7.2): sounds, covers the whole app and stays until the bag is rescanned or a
/// supervisor overrides with a reason. Alarms raised while one is showing are queued.
/// </summary>
public sealed partial class AlarmOverlay : UserControl
{
    private readonly List<ExceptionRowViewModel> _queue = new();
    private ExceptionRowViewModel? _current;
    private static string? _soundFile;

    public AlarmOverlay() => InitializeComponent();

    public void Raise(ExceptionRowViewModel alarm)
    {
        if (_current?.Id == alarm.Id || _queue.Any(a => a.Id == alarm.Id)) return;
        if (_current != null) { _queue.Add(alarm); UpdateQueueText(); return; }
        Show(alarm);
    }

    public void Clear(string exceptionId)
    {
        _queue.RemoveAll(a => a.Id == exceptionId);
        if (_current?.Id != exceptionId) { UpdateQueueText(); return; }

        _current = null;
        if (_queue.Count > 0)
        {
            var next = _queue[0];
            _queue.RemoveAt(0);
            Show(next);
            return;
        }
        Pulse.Stop();
        StopSound();
        Visibility = Visibility.Collapsed;
        AppServices.State.ShowToast("Alarm cleared", "Bag rescanned / exception closed. Handheld unlocked.");
    }

    private void Show(ExceptionRowViewModel alarm)
    {
        _current = alarm;
        var e = alarm.Exception;
        var bag = AppServices.State.FindBag(e.Plate);

        TypeText.Text = alarm.TypeText;
        PlateText.Text = e.Plate == "UNKNOWN" ? $"Unknown tag {e.Epc}" : e.Plate;
        PaxText.Text = bag?.Passenger ?? "Not on any manifest";
        BelongsText.Text = bag == null ? "No BSM received for this tag"
            : bag.Bag.Flight != "KQ504" ? $"{bag.Bag.Flight} to {DestinationName(bag)} (not KQ-504)"
            : $"KQ-504 to Entebbe{(bag.Bag.Uld != null ? $", currently in {bag.Bag.Uld}" : "")}";
        SeenText.Text = e.ScanPoint switch { ScanPoint.Belt04 => "Sorting tunnel · Belt 04", ScanPoint.Ramp => "Loading · Hold 2", ScanPoint.Desk14 => "Check-in · Desk 14", _ => "Message from DCS" };
        MessageText.Text = e.Message;

        RescanHint.Visibility = Visibility.Collapsed;
        OverridePanel.Visibility = Visibility.Collapsed;
        OverrideError.Text = "";
        PinBox.Password = "";
        ReasonBox.Text = "";
        UpdateQueueText();

        Visibility = Visibility.Visible;
        Pulse.Begin();
        if (MuteButton.IsChecked != true) StartSound();
        RescanButton.Focus(FocusState.Programmatic);
    }

    private static string DestinationName(BagRowViewModel bag) => bag.Bag.Flight switch
    {
        "KQ412" => "Dar es Salaam",
        _ => "another destination",
    };

    private void UpdateQueueText() => QueueText.Text = _queue.Count > 0 ? $"+{_queue.Count} more alarm(s) waiting" : "";

    private void Rescan_Click(object sender, RoutedEventArgs e)
    {
        if (_current?.Exception.Epc is { } epc) AppServices.Pipeline.ResetTag(epc);
        RescanHint.Visibility = Visibility.Visible;
    }

    private void ShowOverride_Click(object sender, RoutedEventArgs e)
    {
        OverridePanel.Visibility = OverridePanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (OverridePanel.Visibility == Visibility.Visible) PinBox.Focus(FocusState.Programmatic);
    }

    private async void Override_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        if (PinBox.Password != AppServices.Settings.SupervisorPin) { OverrideError.Text = "Wrong PIN."; return; }
        if (string.IsNullOrWhiteSpace(ReasonBox.Text)) { OverrideError.Text = "A reason is required."; return; }
        try
        {
            await AppServices.Engine.OverrideAsync(_current.Id, "SUPERVISOR", ReasonBox.Text.Trim());
        }
        catch (Exception ex)
        {
            OverrideError.Text = ex.Message;
        }
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (MuteButton.IsChecked == true) StopSound(); else StartSound();
    }

    // ----- sound: a generated two-tone WAV looped through winmm (works unpackaged, no WinRT media needed) -----

    private const uint SndAsync = 0x0001, SndLoop = 0x0008, SndFilename = 0x00020000, SndNoDefault = 0x0002;

    [LibraryImport("winmm.dll", EntryPoint = "PlaySoundW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PlaySound(string? sound, IntPtr module, uint flags);

    private static void StartSound()
    {
        _soundFile ??= WriteAlarmWav();
        PlaySound(_soundFile, IntPtr.Zero, SndAsync | SndLoop | SndFilename | SndNoDefault);
    }

    private static void StopSound() => PlaySound(null, IntPtr.Zero, 0);

    private static string WriteAlarmWav()
    {
        const int rate = 22050;
        var samples = new List<short>();
        foreach (var (freq, ms) in new[] { (988.0, 220), (0.0, 40), (740.0, 220), (0.0, 320) })
        {
            var n = rate * ms / 1000;
            for (var i = 0; i < n; i++)
            {
                var envelope = Math.Min(1, Math.Min(i, n - i) / 200.0);
                samples.Add((short)(freq == 0 ? 0 : Math.Sin(2 * Math.PI * freq * i / rate) * 9000 * envelope));
            }
        }
        var path = Path.Combine(Path.GetTempPath(), "kq-brs-alarm.wav");
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + samples.Count * 2); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(samples.Count * 2);
        foreach (var s in samples) w.Write(s);
        return path;
    }
}
