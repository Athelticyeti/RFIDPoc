using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;

namespace KQ.Brs.Poc.ViewModels;

/// <summary>
/// Where a tag stands at one antenna on the Reader test page: read now, either most strongly of all antennas (the tag
/// is here) or more weakly (a cross-read from a neighbouring antenna); read earlier; or never.
/// </summary>
public enum Sighting { Never, Earlier, Weaker, Strongest }

/// <summary>One tag in the Reader test list: which antennas see it now, how strongly, and when it was last read.</summary>
public sealed partial class TagSightingViewModel(string epc) : ObservableObject
{
    public string Epc { get; } = epc;
    public string ShortEpc => Epc.Length > 6 ? Epc[^6..] : Epc;

    [ObservableProperty] public partial bool IsLive { get; set; }
    [ObservableProperty] public partial string Reads { get; set; } = "0";
    [ObservableProperty] public partial string Rssi { get; set; } = "-";
    [ObservableProperty] public partial string Ago { get; set; } = "";
    [ObservableProperty] public partial Sighting A0 { get; set; }
    [ObservableProperty] public partial Sighting A1 { get; set; }
    [ObservableProperty] public partial Sighting A2 { get; set; }
    [ObservableProperty] public partial Sighting A3 { get; set; }

    public void SetAntenna(int antenna, Sighting s)
    {
        switch (antenna)
        {
            case 0: A0 = s; break;
            case 1: A1 = s; break;
            case 2: A2 = s; break;
            case 3: A3 = s; break;
        }
    }

    public static Brush PipFill(Sighting s) => Ui.Resource(s switch
    {
        Sighting.Strongest => "StatusMatchedBrush",
        Sighting.Earlier => "ControlStrokeColorDefaultBrush",
        _ => "SubtleFillColorTransparentBrush",
    });

    public static Brush PipBorder(Sighting s) => Ui.Resource(s switch
    {
        Sighting.Strongest => "StatusMatchedBrush",
        Sighting.Weaker => "StatusInScanBrush",
        _ => "ControlStrokeColorDefaultBrush",
    });

    public static Brush PipText(Sighting s) => s switch
    {
        Sighting.Strongest => new SolidColorBrush(Microsoft.UI.Colors.White),
        Sighting.Weaker => Ui.Resource("StatusInScanBrush"),
        Sighting.Earlier => Ui.Resource("TextFillColorSecondaryBrush"),
        _ => Ui.Resource("TextFillColorDisabledBrush"),
    };

    public static double LiveOpacity(bool live) => live ? 1 : 0;
}
