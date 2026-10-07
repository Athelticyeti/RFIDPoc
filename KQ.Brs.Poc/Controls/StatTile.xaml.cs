using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace KQ.Brs.Poc.Controls;

/// <summary>A dashboard totals tile (spec 7.1): caption, big number, share of total, progress bar.</summary>
public sealed partial class StatTile : UserControl
{
    public StatTile() => InitializeComponent();

    public string Glyph { get => Icon.Glyph; set => Icon.Glyph = value; }
    public string Title { get => Caption.Text; set => Caption.Text = value; }

    /// <summary>Hides the progress bar (e.g. for the exceptions tile).</summary>
    public bool ShowBar { get => Bar.Visibility == Visibility.Visible; set => Bar.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }

    public void Set(int value, int total, string? sub = null, Brush? accent = null)
    {
        Value.Text = value.ToString("N0");
        Sub.Text = sub ?? (total > 0 ? $"of {total:N0}" : "");
        Bar.Value = total == 0 ? 0 : 100.0 * value / total;
        if (accent != null)
        {
            Value.Foreground = accent;
            Bar.Foreground = accent;
        }
        else
        {
            Value.ClearValue(TextBlock.ForegroundProperty);
            Bar.ClearValue(ProgressBar.ForegroundProperty);
        }
    }
}
