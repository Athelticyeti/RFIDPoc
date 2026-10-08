using System.Collections.ObjectModel;
using KQ.Brs.Poc.Services;
using KQ.Brs.Poc.ViewModels;
using KQ.Rfid.Alien;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Path = Microsoft.UI.Xaml.Shapes.Path;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace KQ.Brs.Poc.Views;

/// <summary>
/// Diagnostics only, outside the POC: the reader and its four antennas, every tag each antenna can see right now as a
/// dot, and a list of tag IDs. Uses the raw reads, before the POC's RSSI filter, scan points or reconciliation.
/// </summary>
public sealed partial class ReaderTestPage : Page
{
    private const double StationTop = 24, StationWidth = 360, StationHeight = 640, StationPitch = 400;
    private const double FieldTop = 190, FieldHeight = 430;   // inside a station
    private const double CellWidth = 82, CellHeight = 68, Dot = 30;
    private const int Cols = 4, Rows = 6;

    private static readonly TimeSpan LiveFor = TimeSpan.FromSeconds(1.5);   // "being read now"
    private static readonly TimeSpan HoldFor = TimeSpan.FromSeconds(10);    // dot stays (fading) this long after the last read

    // Where each antenna's cable plugs into the reader picture (its Ant 0..3 connectors).
    private static readonly double[] PortX = [716, 772, 829, 885];
    private const double PortY = 772;

    private sealed class AntennaSeen
    {
        public DateTimeOffset First, Last;
        public long Reads;
        public double? Rssi;
        public double? Smoothed;   // RSSI averaged over the last few reads, so the strongest antenna doesn't flicker
        public double MaxRssi;
    }

    private sealed class TagSeen(string epc)
    {
        public string Epc { get; } = epc;
        public DateTimeOffset First, Last;
        public long Reads;
        public double? Rssi;
        public AntennaSeen?[] Antennas { get; } = new AntennaSeen?[4];
    }

    private sealed record Station(Rectangle Card, Ellipse Ring1, Ellipse Ring2, TextBlock Count, TextBlock Rate, TextBlock Empty, Path LiveCable);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, TagSeen> _tags = new();   // guarded by _lock (written on the pipeline task)
    private readonly ObservableCollection<TagSightingViewModel> _rows = new();
    private readonly Dictionary<string, TagSightingViewModel> _rowByEpc = new();
    private readonly Station[] _stations = new Station[4];
    private readonly List<(Ellipse Halo, double Phase)> _halos = new();
    private readonly Dictionary<(int Antenna, string Epc), FrameworkElement> _dots = new();
    private readonly Dictionary<int, long> _lastReads = new();
    private readonly long[] _rates = new long[4];   // reads per second, per antenna
    private readonly DateTimeOffset?[] _antennaLast = new DateTimeOffset?[4];   // each antenna's latest read, as of the last refresh
    private readonly DispatcherTimer _frame = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly DispatcherTimer _data = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _powerApply = new() { Interval = TimeSpan.FromMilliseconds(400) };   // applies the slider once it settles
    private bool _syncingPower;
    private int? _appliedAttenuation;
    private string? _lastLayout;
    private string? _selected;
    private bool _built;
    private int _ticks;

    public ReaderTestPage()
    {
        InitializeComponent();
        TagList.ItemsSource = _rows;
        _frame.Tick += (_, _) => Animate();
        _data.Tick += (_, _) => Refresh();
        _powerApply.Tick += async (_, _) => { _powerApply.Stop(); await ApplyPowerAsync(); };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (!_built)
        {
            _built = true;
            DrawGrid();
            BuildStations();
            BuildLegend();
            AppServices.State.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(AppState.Health)) return;
                RenderReader();
                _ = LoadPowerAsync();
            };
        }
        AppServices.Pipeline.RawRead += OnRawRead;
        AppServices.Pipeline.ProcessScans = false;   // walking tags past the antennas mustn't check bags in, sort or load them
        RenderReader();
        _ = LoadPowerAsync();
        Refresh();
        _frame.Start();
        _data.Start();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        AppServices.Pipeline.RawRead -= OnRawRead;
        AppServices.Pipeline.ProcessScans = true;
        _frame.Stop();
        _data.Stop();
    }

    /// <summary>On the pipeline task: just record the sighting.</summary>
    private void OnRawRead(TagRead read)
    {
        if (read.Antenna is < 0 or > 3) return;
        var now = DateTimeOffset.UtcNow;   // receipt time, so it compares with the PC clock (the reader's may differ)
        var count = Math.Max(1, read.ReadCount);
        lock (_lock)
        {
            if (!_tags.TryGetValue(read.Epc, out var tag))
                _tags[read.Epc] = tag = new TagSeen(read.Epc) { First = now };
            tag.Last = now;
            tag.Reads += count;
            tag.Rssi = read.Rssi;

            var a = tag.Antennas[read.Antenna] ??= new AntennaSeen { First = now };
            a.Last = now;
            a.Reads += count;
            a.Rssi = read.Rssi;
            if (read.Rssi is { } r)
            {
                if (r > a.MaxRssi) a.MaxRssi = r;
                a.Smoothed = a.Smoothed is { } m ? m * 0.7 + r * 0.3 : r;
            }
        }
    }

    // ------------------------------------------------------------------ static drawing

    private void DrawGrid()
    {
        var brush = Ui.Resource("MapGrid");
        for (var x = 0; x <= 1600; x += 40)
            GridLayer.Children.Add(new Line { X1 = x, Y1 = 0, X2 = x, Y2 = 1140, Stroke = brush, StrokeThickness = 1 });
        for (var y = 0; y <= 1140; y += 40)
            GridLayer.Children.Add(new Line { X1 = 0, Y1 = y, X2 = 1600, Y2 = y, Stroke = brush, StrokeThickness = 1 });
    }

    private static double StationLeft(int antenna) => 20 + antenna * StationPitch;

    private void BuildStations()
    {
        var antennaImage = new BitmapImage(new Uri("ms-appx:///Assets/antenna.png"));
        for (var a = 0; a < 4; a++)
        {
            var left = StationLeft(a);
            var cx = left + StationWidth / 2;

            // Cable from the station to its port on the reader: a dark lead, and a flowing accent line while reading.
            var bottom = StationTop + StationHeight;
            Geometry Cable() => new PathGeometry
            {
                Figures =
                {
                    new PathFigure
                    {
                        StartPoint = new Point(cx, bottom),
                        Segments = { new BezierSegment { Point1 = new Point(cx, bottom + 70), Point2 = new Point(PortX[a], PortY - 60), Point3 = new Point(PortX[a], PortY) } },
                    },
                },
            };
            CableLayer.Children.Add(new Path
            {
                Data = Cable(), Stroke = Ui.Resource("MapBelt"), StrokeThickness = 8,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            });
            var liveCable = new Path
            {
                Data = Cable(), Stroke = Ui.Resource("StatusMatchedBrush"), StrokeThickness = 4,
                StrokeDashArray = [1.5, 1.5], StrokeDashCap = PenLineCap.Round, Opacity = 0,
            };
            CableLayer.Children.Add(liveCable);

            var card = new Rectangle
            {
                Width = StationWidth, Height = StationHeight, RadiusX = 16, RadiusY = 16,
                Fill = Ui.Resource("MapZone"), Stroke = Ui.Resource("MapZoneStroke"), StrokeThickness = 1.5,
            };
            Place(card, left, StationTop);
            StationLayer.Children.Add(card);

            // The antenna, with rings that pulse while it reads.
            Ellipse Ring() => new()
            {
                Width = 190, Height = 190, StrokeThickness = 3, Stroke = Ui.Resource("StatusMatchedBrush"), Opacity = 0,
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(),
            };
            var ring1 = Ring();
            var ring2 = Ring();
            Place(ring1, left + 95 - 95, StationTop + 90 - 95);
            Place(ring2, left + 95 - 95, StationTop + 90 - 95);
            StationLayer.Children.Add(ring1);
            StationLayer.Children.Add(ring2);
            var picture = new Image { Source = antennaImage, Width = 150, Height = 140, Stretch = Stretch.Uniform };
            Place(picture, left + 20, StationTop + 20);
            StationLayer.Children.Add(picture);

            var eyebrow = new TextBlock
            {
                Text = $"ANTENNA {a}", FontSize = 16, FontWeight = FontWeights.Bold, CharacterSpacing = 120, Foreground = Ui.Resource("MapLabel"),
            };
            Place(eyebrow, left + 190, StationTop + 30);
            var count = new TextBlock { FontSize = 40, FontWeight = FontWeights.SemiBold, Foreground = Ui.Resource("MapTitle") };
            Place(count, left + 190, StationTop + 52);
            var rate = new TextBlock { FontSize = 18, Foreground = Ui.Resource("MapLabel"), Width = 160, TextTrimming = TextTrimming.CharacterEllipsis };
            Place(rate, left + 190, StationTop + 112);
            StationLayer.Children.Add(eyebrow);
            StationLayer.Children.Add(count);
            StationLayer.Children.Add(rate);

            // The field: one dot per tag this antenna sees.
            var field = new Rectangle
            {
                Width = 328, Height = FieldHeight, RadiusX = 10, RadiusY = 10,
                Fill = Ui.Resource("MapRegion"), Stroke = Ui.Resource("MapRegionStroke"), StrokeDashArray = [3, 3],
            };
            Place(field, left + 16, StationTop + FieldTop);
            StationLayer.Children.Add(field);
            var empty = new TextBlock
            {
                Text = "No tag in range", FontSize = 18, Foreground = Ui.Resource("MapLabel"), Opacity = 0.7,
                Width = 328, TextAlignment = TextAlignment.Center,
            };
            Place(empty, left + 16, StationTop + FieldTop + FieldHeight / 2 - 12);
            StationLayer.Children.Add(empty);

            _stations[a] = new Station(card, ring1, ring2, count, rate, empty, liveCable);
        }
    }

    private void BuildLegend()
    {
        void Item(string text, Brush? fill, Brush stroke, bool halo = false)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var g = new Grid { Width = 22, Height = 22 };
            if (halo) g.Children.Add(new Ellipse { Width = 22, Height = 22, Stroke = stroke, StrokeThickness = 2 });
            g.Children.Add(new Ellipse { Width = 12, Height = 12, Fill = fill, Stroke = halo ? null : stroke, StrokeThickness = 2 });
            panel.Children.Add(g);
            panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] });
            Legend.Children.Add(panel);
        }
        Item("Strongest antenna: the tag is here", Ui.Resource("StatusMatchedBrush"), Ui.Resource("TextFillColorSecondaryBrush"), halo: true);
        Item("Weaker cross-read", Ui.Resource("StatusInScanBrush"), Ui.Resource("StatusInScanBrush"));
        Item($"Seen in the last {HoldFor.TotalSeconds:F0} s", null, Ui.Resource("StatusExpectedBrush"));
        Item("Selected tag", null, Ui.Resource("AccentFillColorDefaultBrush"), halo: true);
    }

    // ------------------------------------------------------------------ live state

    private void Refresh()
    {
        var now = DateTimeOffset.UtcNow;
        List<TagSeen> tags;
        lock (_lock)
            tags = _tags.Values.Select(Copy).ToList();

        if (++_ticks % 5 == 0) UpdateRates();
        RenderList(tags, now);
        RenderStations(tags, now);
        RenderDetail(tags, now);
    }

    private static TagSeen Copy(TagSeen t)
    {
        var c = new TagSeen(t.Epc) { First = t.First, Last = t.Last, Reads = t.Reads, Rssi = t.Rssi };
        for (var a = 0; a < 4; a++)
            if (t.Antennas[a] is { } s)
                c.Antennas[a] = new AntennaSeen { First = s.First, Last = s.Last, Reads = s.Reads, Rssi = s.Rssi, Smoothed = s.Smoothed, MaxRssi = s.MaxRssi };
        return c;
    }

    /// <summary>The antenna reading this tag most strongly right now (smoothed RSSI): where the tag is. Null if none reads it now.</summary>
    private static int? Strongest(TagSeen t, DateTimeOffset now)
    {
        int? best = null;
        var bestRssi = double.MinValue;
        for (var a = 0; a < 4; a++)
            if (t.Antennas[a] is { } s && now - s.Last < LiveFor && (s.Smoothed ?? 0) > bestRssi)
                (best, bestRssi) = (a, s.Smoothed ?? 0);
        return best;
    }

    private static Sighting SightingAt(TagSeen t, int antenna, int? strongest, DateTimeOffset now) =>
        t.Antennas[antenna] is not { } s ? Sighting.Never
        : now - s.Last >= LiveFor ? Sighting.Earlier
        : antenna == strongest ? Sighting.Strongest : Sighting.Weaker;

    private void RenderList(List<TagSeen> tags, DateTimeOffset now)
    {
        // Newest tags on top; existing rows keep their place and only their figures change.
        foreach (var t in tags.OrderBy(t => t.First))
        {
            if (!_rowByEpc.TryGetValue(t.Epc, out var row))
            {
                row = new TagSightingViewModel(t.Epc);
                _rowByEpc[t.Epc] = row;
                _rows.Insert(0, row);
            }
            row.IsLive = now - t.Last < LiveFor;
            row.Reads = t.Reads.ToString("N0");
            row.Rssi = t.Rssi is { } r ? r.ToString("N0") : "-";
            row.Ago = Ui.Ago(now - t.Last);
            var strongest = Strongest(t, now);
            for (var a = 0; a < 4; a++)
                row.SetAntenna(a, SightingAt(t, a, strongest, now));
        }
        TagsTitle.Text = $"Tags seen · {tags.Count} · {tags.Count(t => now - t.Last < LiveFor)} in range now";
        EmptyText.Visibility = tags.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderStations(List<TagSeen> tags, DateTimeOffset now)
    {
        var strongest = tags.ToDictionary(t => t.Epc, t => Strongest(t, now));

        // Per antenna, the tags seen within the hold time, in the order they arrived (so dots don't jump about).
        var perAntenna = Enumerable.Range(0, 4).Select(a => tags
                .Where(t => t.Antennas[a] is { } s && now - s.Last < HoldFor)
                .OrderBy(t => t.Antennas[a]!.First).ThenBy(t => t.Epc)
                .Select(t => new FieldItem(t, t.Antennas[a]!, SightingAt(t, a, strongest[t.Epc], now), strongest[t.Epc])).ToList())
            .ToArray();

        // Rebuild the dots only when what they show changes: replacing the element under the mouse closes its tooltip.
        var layout = string.Join("|", perAntenna.SelectMany((list, a) => list.Select(x => $"{a}:{x.Tag.Epc}:{x.State}:{x.StrongestAntenna}"))) + "#" + _selected;
        if (layout != _lastLayout)
        {
            _lastLayout = layout;
            DotLayer.Children.Clear();
            _halos.Clear();
            _dots.Clear();
            for (var a = 0; a < 4; a++)
                LayoutField(a, perAntenna[a]);
        }

        // Dots that are no longer being read fade out over the hold time.
        for (var a = 0; a < 4; a++)
            foreach (var item in perAntenna[a])
                if (_dots.TryGetValue((a, item.Tag.Epc), out var dot))
                    dot.Opacity = item.State != Sighting.Earlier ? 1 : 1 - 0.7 * Math.Clamp((now - item.Seen.Last - LiveFor) / (HoldFor - LiveFor), 0, 1);

        for (var a = 0; a < 4; a++)
        {
            _antennaLast[a] = perAntenna[a].Select(x => (DateTimeOffset?)x.Seen.Last).Max();
            var station = _stations[a];
            var here = perAntenna[a].Count(x => x.State == Sighting.Strongest);
            var weaker = perAntenna[a].Count(x => x.State == Sighting.Weaker);

            // The big count is the tags that are here (read most strongly by this antenna); cross-reads are listed apart.
            station.Count.Text = here switch { 0 => "No tag", 1 => "1 tag", var n => $"{n} tags" };
            station.Count.Foreground = Ui.Resource(here > 0 ? "StatusMatchedBrush" : "MapTitle");
            station.Rate.Text = (_rates[a] > 0 ? $"{_rates[a]:N0} reads/s" : "Idle") + (weaker > 0 ? $" · {weaker} weaker" : "");
            station.Empty.Visibility = perAntenna[a].Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            station.Card.Stroke = Ui.Resource(here > 0 ? "StatusMatchedBrush" : weaker > 0 ? "StatusInScanBrush" : "MapZoneStroke");
            station.Card.StrokeThickness = here > 0 ? 3 : weaker > 0 ? 2 : 1.5;
        }
    }

    private sealed record FieldItem(TagSeen Tag, AntennaSeen Seen, Sighting State, int? StrongestAntenna);

    private void LayoutField(int antenna, List<FieldItem> items)
    {
        var originX = StationLeft(antenna) + 16 + (328 - Cols * CellWidth) / 2;
        var originY = StationTop + FieldTop + 14;
        var capacity = Cols * Rows;
        var shown = items.Count > capacity ? capacity - 1 : items.Count;

        for (var i = 0; i < shown; i++)
        {
            var (tag, seen, state, strongestAntenna) = items[i];
            var x = originX + i % Cols * CellWidth;
            var y = originY + i / Cols * CellHeight;
            var selected = tag.Epc == _selected;

            // Here (strongest): big green dot with a halo. Cross-read (weaker): smaller amber dot. Read earlier: hollow ring.
            var cell = new Grid { Width = CellWidth, Height = CellHeight, Background = new SolidColorBrush(Colors.Transparent) };
            var mark = new Grid { Width = 44, Height = 44, VerticalAlignment = VerticalAlignment.Top };
            if (selected)
                mark.Children.Add(new Ellipse { Width = 44, Height = 44, Stroke = Ui.Resource("AccentFillColorDefaultBrush"), StrokeThickness = 4 });
            mark.Children.Add(state switch
            {
                Sighting.Strongest => new Ellipse { Width = Dot, Height = Dot, Fill = Ui.Resource("StatusMatchedBrush"), Stroke = new SolidColorBrush(Colors.White), StrokeThickness = 1.5 },
                Sighting.Weaker => new Ellipse { Width = 20, Height = 20, Fill = Ui.Resource("StatusInScanBrush"), Stroke = new SolidColorBrush(Colors.White), StrokeThickness = 1.5 },
                _ => new Ellipse { Width = Dot, Height = Dot, Stroke = Ui.Resource("StatusExpectedBrush"), StrokeThickness = 2.5 },
            });
            cell.Children.Add(mark);
            cell.Children.Add(new TextBlock
            {
                Text = "…" + (tag.Epc.Length > 6 ? tag.Epc[^6..] : tag.Epc), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 14,
                Foreground = Ui.Resource(selected ? "AccentTextFillColorPrimaryBrush" : "MapLabel"), FontWeight = selected ? FontWeights.Bold : FontWeights.Normal,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            });
            var where = state switch
            {
                Sighting.Strongest => "Strongest here: the tag is at this antenna",
                Sighting.Weaker => $"Weaker cross-read: the tag is at antenna {strongestAntenna}",
                _ => "Not read in the last few seconds",
            };
            ToolTipService.SetToolTip(cell, $"{tag.Epc}\n{where}\nAntenna {antenna} · {seen.Reads:N0} reads · RSSI {seen.Smoothed:N0} (max {seen.MaxRssi:N0})\nClick to select");
            cell.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(cell).Properties.IsLeftButtonPressed) return;
                e.Handled = true;
                Select(tag.Epc);
            };
            Place(cell, x, y);
            DotLayer.Children.Add(cell);
            _dots[(antenna, tag.Epc)] = cell;

            if (state == Sighting.Strongest)
            {
                var halo = new Ellipse
                {
                    Width = 42, Height = 42, Stroke = Ui.Resource("StatusMatchedBrush"), StrokeThickness = 2.5, IsHitTestVisible = false,
                    RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(),
                };
                Place(halo, x + CellWidth / 2 - 21, y + 1);
                DotLayer.Children.Add(halo);
                _halos.Add((halo, i * 0.13));
            }
        }

        if (items.Count > shown)
        {
            var more = new TextBlock { Text = $"+{items.Count - shown}", FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Ui.Resource("MapLabel") };
            Place(more, originX + shown % Cols * CellWidth + 22, originY + shown / Cols * CellHeight + 8);
            DotLayer.Children.Add(more);
        }
    }

    private void RenderDetail(List<TagSeen> tags, DateTimeOffset now)
    {
        DetailAntennas.Children.Clear();
        var tag = tags.FirstOrDefault(t => t.Epc == _selected);
        CopyButton.IsEnabled = tag != null;
        if (tag == null)
        {
            DetailEpc.Text = "Pick a tag below";
            return;
        }
        if (DetailEpc.Text != tag.Epc) DetailEpc.Text = tag.Epc;

        var strongest = Strongest(tag, now);
        for (var a = 0; a < 4; a++)
        {
            var s = tag.Antennas[a];
            var state = SightingAt(tag, a, strongest, now);
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            line.Children.Add(new Border
            {
                Style = (Style)Resources["Pip"], Background = TagSightingViewModel.PipFill(state),
                BorderBrush = TagSightingViewModel.PipBorder(state), BorderThickness = new Thickness(2),
                Child = new TextBlock { Text = a.ToString(), Style = (Style)Resources["PipText"], Foreground = TagSightingViewModel.PipText(state) },
            });
            var text = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = Ui.Resource(s == null ? "TextFillColorTertiaryBrush" : "TextFillColorPrimaryBrush"),
                Text = s == null ? "Not seen" : $"{s.Reads:N0} reads · RSSI {s.Smoothed:N0} (max {s.MaxRssi:N0}) · {Ui.Ago(now - s.Last)}",
            };
            if (state == Sighting.Strongest)
                text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = " · strongest", FontWeight = FontWeights.SemiBold, Foreground = Ui.Resource("StatusMatchedBrush") });
            line.Children.Add(text);
            DetailAntennas.Children.Add(line);
        }
    }

    private void UpdateRates()
    {
        for (var a = 0; a < 4; a++)
        {
            var reads = AppServices.Pipeline.Antennas[a].Reads;
            _rates[a] = _lastReads.TryGetValue(a, out var last) ? reads - last : 0;
            _lastReads[a] = reads;
        }
    }

    private void RenderReader()
    {
        var h = AppServices.State.Health;
        Subtitle.Text = "Raw reads straight from the reader · POC scanning paused while this page is open · " + h.State switch
        {
            SupervisorState.Streaming => "reader streaming",
            SupervisorState.Online => "reader connected, not streaming",
            SupervisorState.Connecting => "connecting to the reader…",
            SupervisorState.Retrying => "reader offline, retrying",
            _ => "reader not connected",
        };
        ReaderButton.Content = h.State == SupervisorState.Disconnected ? "Connect" : "Start stream";
        ReaderButton.Visibility = h.State is SupervisorState.Disconnected or SupervisorState.Online ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ animation (30 fps, only while visible)

    private void Animate()
    {
        var ms = Environment.TickCount64;
        var now = DateTimeOffset.UtcNow;
        for (var a = 0; a < 4; a++)
        {
            var station = _stations[a];
            var active = _antennaLast[a] is { } t && now - t < LiveFor;
            Pulse(station.Ring1, active, (ms % 1400) / 1400.0);
            Pulse(station.Ring2, active, ((ms + 700) % 1400) / 1400.0);
            station.LiveCable.Opacity = active ? 0.95 : 0;
            station.LiveCable.StrokeDashOffset = -(ms % 600) / 200.0;   // flows from the antenna to the reader
        }

        foreach (var (halo, phase) in _halos)
        {
            var p = (ms / 1000.0 + phase) % 1.0;
            var s = (ScaleTransform)halo.RenderTransform;
            s.ScaleX = s.ScaleY = 0.8 + p * 0.7;
            halo.Opacity = 0.9 * (1 - p);
        }
    }

    private static void Pulse(Ellipse ring, bool active, double phase)
    {
        if (!active) { ring.Opacity = 0; return; }
        var s = (ScaleTransform)ring.RenderTransform;
        s.ScaleX = s.ScaleY = 0.6 + phase * 0.9;
        ring.Opacity = 0.85 * (1 - phase);
    }

    // ------------------------------------------------------------------ transmit power (all antennas together)

    private static string PowerLabel(int attenuation) => attenuation == 0 ? "Full power" : $"−{attenuation / 10.0:0.#} dB";

    /// <summary>Shows the reader's current RFAttenuation on the slider (right = full power).</summary>
    private async Task LoadPowerAsync()
    {
        var reader = AppServices.Reader.Reader;
        var connected = AppServices.State.Health.State is SupervisorState.Online or SupervisorState.Streaming;
        if (reader == null || !connected)
        {
            PowerSlider.IsEnabled = false;
            PowerText.Text = "-";
            _appliedAttenuation = null;
            return;
        }
        if (_appliedAttenuation != null || _powerApply.IsEnabled) { PowerSlider.IsEnabled = true; return; }   // already showing it
        try
        {
            var attenuation = await reader.GetRfAttenuationAsync(CancellationToken.None);
            _appliedAttenuation = attenuation;
            _syncingPower = true;
            PowerSlider.Value = AlienAlr9900Reader.MaxRfAttenuation - attenuation;
            _syncingPower = false;
            PowerText.Text = PowerLabel(attenuation);
            PowerSlider.IsEnabled = true;
        }
        catch (Exception ex)
        {
            PowerText.Text = "Unknown";
            ToolTipService.SetToolTip(PowerText, $"Could not read the reader's power: {ex.Message}");
        }
    }

    private void PowerSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_syncingPower) return;
        PowerText.Text = PowerLabel(AlienAlr9900Reader.MaxRfAttenuation - (int)e.NewValue);
        _powerApply.Stop();
        _powerApply.Start();
    }

    private async Task ApplyPowerAsync()
    {
        var attenuation = AlienAlr9900Reader.MaxRfAttenuation - (int)PowerSlider.Value;
        if (attenuation == _appliedAttenuation || AppServices.Reader.Reader is not { } reader) return;
        try
        {
            await reader.SetRfAttenuationAsync(attenuation, CancellationToken.None);
            _appliedAttenuation = attenuation;
            ToolTipService.SetToolTip(PowerText, null);
        }
        catch (Exception ex)
        {
            PowerText.Text = "Not applied";
            ToolTipService.SetToolTip(PowerText, $"The reader refused the power change: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ actions

    private void Select(string? epc)
    {
        _selected = epc;
        if (epc != null && _rowByEpc.TryGetValue(epc, out var row) && TagList.SelectedItem != row)
        {
            TagList.SelectedItem = row;
            TagList.ScrollIntoView(row);
        }
        Refresh();
    }

    private void TagList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var epc = (TagList.SelectedItem as TagSightingViewModel)?.Epc;
        if (epc != _selected) Select(epc);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        var package = new DataPackage();
        package.SetText(_selected);
        Clipboard.SetContent(package);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        lock (_lock) _tags.Clear();
        _rows.Clear();
        _rowByEpc.Clear();
        _selected = null;
        _lastLayout = null;
        Refresh();
    }

    private async void ReaderButton_Click(object sender, RoutedEventArgs e)
    {
        var state = AppServices.State.Health.State;
        if (state == SupervisorState.Disconnected) AppServices.Reader.Connect(startStreaming: true);
        else if (state == SupervisorState.Online) await AppServices.Reader.SetStreamingAsync(true);
    }

    private static void Place(UIElement e, double x, double y)
    {
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
    }
}
