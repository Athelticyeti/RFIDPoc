using KQ.Brs.Poc.Services;
using KQ.Brs.Poc.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KQ.Brs.Poc.Controls;

/// <summary>Reader health (spec 6.3 ReaderStatusChanged): green streaming, amber connecting/retrying, grey offline.</summary>
public sealed partial class ReaderStatusPill : UserControl
{
    private AppState? _state;
    private readonly DispatcherTimer _countdown = new() { Interval = TimeSpan.FromSeconds(1) };

    public ReaderStatusPill()
    {
        InitializeComponent();
        _countdown.Tick += (_, _) => Render();
    }

    public void Attach(AppState state)
    {
        _state = state;
        state.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AppState.Health)) Render(); };
        _countdown.Start();
        Render();
    }

    public void Stop() => _countdown.Stop();

    private void Render()
    {
        if (_state == null) return;
        var h = _state.Health;
        var (title, brush) = h.State switch
        {
            SupervisorState.Streaming => ("Reader streaming", "StatusMatchedBrush"),
            SupervisorState.Online => ("Reader connected", "StatusMatchedBrush"),
            SupervisorState.Connecting => ("Connecting…", "StatusInScanBrush"),
            SupervisorState.Retrying => ("Reader offline", "StatusExceptionBrush"),
            _ => ("Reader disconnected", "StatusExpectedBrush"),
        };
        Title.Text = title;
        Dot.Fill = Ui.Resource(brush);
        Detail.Text = h.State == SupervisorState.Retrying && h.RetryAt is { } at
            ? $"Retrying in {Math.Max(0, (at - DateTimeOffset.UtcNow).TotalSeconds):F0} s"
            : h.ReaderName ?? AppServices.Settings.ReaderHost;
        ToolTipService.SetToolTip(this, h.Message);
    }
}
