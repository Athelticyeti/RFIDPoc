using KQ.Brs.Core.Domain;
using KQ.Brs.Core.Events;
using KQ.Brs.Poc.Services;
using KQ.Brs.Poc.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Path = Microsoft.UI.Xaml.Shapes.Path;
using Windows.Foundation;

namespace KQ.Brs.Poc.Views;

/// <summary>
/// Live floor plan: every bag as a dot in the place it really is (check-in, sorting, ULD, refused, offloaded),
/// coloured by whether that place is right; the antennas as beacons that pulse while reading; and a tracker that
/// follows one bag along its route.
/// </summary>
public sealed partial class MapPage : Page
{
    private enum Region { Queue, CheckedIn, HallProblems, Sorted, Ak7, Ak8, Held, Offloaded }
    private enum DotKind { Expected, Good, Attention, Wrong, Offloaded }

    // Where each region's dots go (design units of the 1600 × 840 plan).
    private static readonly Dictionary<Region, Rect> Areas = new()
    {
        [Region.Queue] = new(72, 178, 346, 148),
        [Region.CheckedIn] = new(72, 508, 346, 268),
        [Region.HallProblems] = new(532, 178, 486, 142),
        [Region.Sorted] = new(532, 508, 486, 268),
        [Region.Ak7] = new(1132, 172, 166, 104),
        [Region.Ak8] = new(1372, 172, 166, 104),
        [Region.Held] = new(1132, 474, 400, 36),
        [Region.Offloaded] = new(1128, 574, 76, 204),
    };

    // Antenna beacons: position on the plan and caption.
    // Antenna beacons: position on the plan, caption, and where the caption goes (clear of the other labels).
    private static readonly (int Antenna, Point At, string Caption, Point LabelAt)[] Beacons =
    [
        (0, new Point(170, 400), "Check-in", new Point(100, 436)),
        (1, new Point(800, 344), "Tunnel left", new Point(828, 316)),
        (2, new Point(800, 456), "Tunnel right", new Point(828, 452)),
        (3, new Point(1250, 400), "Loading point", new Point(1276, 346)),
    ];

    private static readonly ExceptionType[] WrongTypes =
        [ExceptionType.WrongFlight, ExceptionType.Unknown, ExceptionType.NotAuthorised, ExceptionType.PaxNotBoarded, ExceptionType.Offloaded];

    private const double Dot = 18, Pitch = 24;

    private readonly DispatcherTimer _frame = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<(Ellipse Ring, double Phase)> _halos = new();
    private readonly Dictionary<int, (Ellipse Ring1, Ellipse Ring2, TextBlock Rate)> _beacons = new();
    private readonly Dictionary<int, long> _lastReads = new();
    private string? _tracked;
    private bool _dirty = true;
    private string? _lastLayout;   // what the dots last showed; they are only rebuilt when it changes
    private bool _built;

    // A bag that changes area slides there along the belt instead of jumping, so the audience sees it move.
    private const double MoveMs = 1400;
    private readonly Dictionary<string, (Region Region, Point At)> _settled = new();   // where each bag's dot last landed
    private readonly Dictionary<string, Move> _moves = new();

    private sealed class Move(Point[] path)
    {
        public Point[] Path { get; } = path;
        public long Start { get; } = Environment.TickCount64;
        public List<(UIElement Part, double Dx, double Dy)> Parts { get; } = new();
    }

    public MapPage()
    {
        InitializeComponent();
        _frame.Tick += (_, _) => Animate();
        _refresh.Tick += (_, _) => { UpdateRates(); Render(); RenderTunnelDecision(); };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (!_built)
        {
            _built = true;
            DrawGrid();
            BuildBeacons();
            BuildLegend();
            AppServices.State.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(AppState.Health)) RenderSubtitle(); };
        }
        if (e.Parameter is string plate) Track(plate);
        _dirty = true;
        Render();
        _frame.Start();
        _refresh.Start();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _frame.Stop();
        _refresh.Stop();
    }

    // ------------------------------------------------------------------ static drawing

    private void DrawGrid()
    {
        var brush = Ui.Resource("MapGrid");
        for (var x = 0; x <= 1600; x += 40)
            GridLayer.Children.Add(new Line { X1 = x, Y1 = 0, X2 = x, Y2 = 840, Stroke = brush, StrokeThickness = 1 });
        for (var y = 0; y <= 840; y += 40)
            GridLayer.Children.Add(new Line { X1 = 0, Y1 = y, X2 = 1600, Y2 = y, Stroke = brush, StrokeThickness = 1 });
    }

    private void BuildBeacons()
    {
        foreach (var (antenna, at, caption, labelAt) in Beacons)
        {
            Ellipse Ring() => new()
            {
                Width = 70, Height = 70, StrokeThickness = 3, Stroke = Ui.Resource("AccentFillColorDefaultBrush"), Opacity = 0,
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(),
            };
            var ring1 = Ring();
            var ring2 = Ring();
            var core = new Grid { Width = 36, Height = 36 };
            core.Children.Add(new Ellipse { Fill = Ui.Resource("AccentFillColorDefaultBrush"), Stroke = new SolidColorBrush(Colors.White), StrokeThickness = 2.5 });
            core.Children.Add(new TextBlock
            {
                Text = antenna.ToString(), Foreground = new SolidColorBrush(Colors.White), FontWeight = FontWeights.Bold, FontSize = 18,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            });
            Place(ring1, at.X - 35, at.Y - 35);
            Place(ring2, at.X - 35, at.Y - 35);
            Place(core, at.X - 18, at.Y - 18);
            BeaconLayer.Children.Add(ring1);
            BeaconLayer.Children.Add(ring2);
            BeaconLayer.Children.Add(core);

            var label = new StackPanel { Width = 140 };
            label.Children.Add(new TextBlock
            {
                Text = caption.ToUpperInvariant(), FontSize = 12, FontWeight = FontWeights.Bold, CharacterSpacing = 100,
                Foreground = Ui.Resource("MapTitle"),
            });
            var rate = new TextBlock { FontSize = 12, Foreground = Ui.Resource("MapLabel") };
            label.Children.Add(rate);
            Place(label, labelAt.X, labelAt.Y);
            BeaconLayer.Children.Add(label);
            ToolTipService.SetToolTip(core, $"Antenna {antenna} · {caption}");
            _beacons[antenna] = (ring1, ring2, rate);
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
        Item("Right place", Ui.Resource("StatusMatchedBrush"), Ui.Resource("StatusMatchedBrush"));
        Item("Needs attention", Ui.Resource("StatusInScanBrush"), Ui.Resource("StatusInScanBrush"));
        Item("Wrong place / refused", Ui.Resource("StatusExceptionBrush"), Ui.Resource("StatusExceptionBrush"));
        Item("Expected", null, Ui.Resource("StatusExpectedBrush"));
        Item("Offloaded", Ui.Resource("StatusOffloadedBrush"), Ui.Resource("StatusOffloadedBrush"));
        Item("Being read now", Ui.Resource("StatusMatchedBrush"), Ui.Resource("TextFillColorSecondaryBrush"), halo: true);
    }

    // ------------------------------------------------------------------ live state

    private static (Region Region, DotKind Kind) Classify(BagView b)
    {
        var wrong = b.OpenException is { } x && WrongTypes.Contains(x);
        var attention = b.OpenException is ExceptionType.MissingAtSorter or ExceptionType.NotLoaded
                        || (b.Status is BagStatus.CheckedIn or BagStatus.Sorted && (!b.AuthorityToLoad || b.PassengerStatus == PassengerStatus.NotBoarded));
        var foreign = b.Flight != "KQ504";

        if (b.Status == BagStatus.Offloaded || b.Deleted) return (Region.Offloaded, DotKind.Offloaded);
        if (b.Status == BagStatus.Loaded)
            return (b.Uld == "AK7" ? Region.Ak7 : Region.Ak8, wrong ? DotKind.Wrong : attention ? DotKind.Attention : DotKind.Good);
        if (wrong || foreign)
        {
            var region = b.LastScanPoint switch
            {
                ScanPoint.Ramp => Region.Held,
                ScanPoint.Belt04 => Region.HallProblems,
                ScanPoint.Desk14 => Region.CheckedIn,
                _ => Region.Queue,
            };
            return (region, DotKind.Wrong);
        }
        return b.Status switch
        {
            BagStatus.Sorted => (Region.Sorted, attention ? DotKind.Attention : DotKind.Good),
            BagStatus.CheckedIn => (Region.CheckedIn, attention ? DotKind.Attention : DotKind.Good),
            _ => (Region.Queue, DotKind.Expected),
        };
    }

    private Brush KindBrush(DotKind kind) => Ui.Resource(kind switch
    {
        DotKind.Good => "StatusMatchedBrush",
        DotKind.Attention => "StatusInScanBrush",
        DotKind.Wrong => "StatusExceptionBrush",
        DotKind.Offloaded => "StatusOffloadedBrush",
        _ => "StatusExpectedBrush",
    });

    private void Render()
    {
        var now = DateTimeOffset.UtcNow;
        var bags = AppServices.State.Bags.ToList();

        // Tags that belong to no bag, scanned in the last 10 s or still being read: shown as "?" in the problem area of
        // that scan point. Judged on each tag's latest scan (the log is newest first), so a tag that has just been
        // bound to a bag stops showing as unknown.
        var unknown = AppServices.State.ScanLog
            .GroupBy(s => s.Epc).Select(g => g.First())
            .Where(s => s.Plate == "-" && s.Outcome == nameof(ScanOutcome.Unknown)
                        && (now - s.Utc < TimeSpan.FromSeconds(10) || AppServices.Pipeline.IsBeingRead(s.Epc, ReadNow)))
            .ToList();

        // Rebuilding the dots replaces the element under the mouse, which swallows clicks and closes tooltips. So only
        // rebuild when something visible has changed (on the bench, tags lying at the antennas are read all the time).
        var layout = LayoutKey(bags, unknown, now);
        if (!_dirty && layout == _lastLayout) { RenderSubtitle(); return; }
        _dirty = false;
        _lastLayout = layout;
        DotLayer.Children.Clear();
        _halos.Clear();
        foreach (var move in _moves.Values) move.Parts.Clear();   // re-attached to the new elements as they are placed

        var groups = bags.GroupBy(r => Classify(r.Bag).Region).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var region in Enum.GetValues<Region>())
        {
            var rows = groups.GetValueOrDefault(region) ?? [];
            var strangers = unknown.Where(u => RegionForUnknown(u.ScanPoint) == region).ToList();
            LayoutRegion(region, rows, strangers, now);
        }

        QueueLabel.Text = $"Awaiting check-in · {Count(groups, Region.Queue)}";
        CheckedInLabel.Text = $"Checked in · {Count(groups, Region.CheckedIn)}";
        HallProblemLabel.Text = $"Problems in the hall · {Count(groups, Region.HallProblems)}";
        SortedLabel.Text = $"Sorted · KQ-504 make-up lane · {Count(groups, Region.Sorted)}";
        Ak7Label.Text = $"ULD AK7 · {Count(groups, Region.Ak7)}";
        Ak8Label.Text = $"ULD AK8 · {Count(groups, Region.Ak8)}";
        HeldLabel.Text = $"Refused at loading · {Count(groups, Region.Held)}";
        OffloadLabel.Text = $"Offloaded · {Count(groups, Region.Offloaded)}";

        RenderSubtitle();
        RenderRoute(bags.FirstOrDefault(r => r.Plate == _tracked)?.Bag);
        if (_tracked != null) _ = RenderJourneyAsync();
    }

    private static int Count(Dictionary<Region, List<BagRowViewModel>> groups, Region r) => groups.GetValueOrDefault(r)?.Count ?? 0;

    private string LayoutKey(List<BagRowViewModel> bags, List<ScanLogViewModel> unknown, DateTimeOffset now)
    {
        var sb = new System.Text.StringBuilder(_tracked).Append('#');
        foreach (var r in bags)
        {
            var b = r.Bag;
            var recent = BeingRead(b, now);
            sb.Append(r.Plate).Append(Classify(b)).Append(recent ? '*' : '-').Append(r.StatusText).Append(b.Uld)
              .Append(b.OpenException).Append(r.Passenger).Append(b.LastSeenUtc?.Ticks).Append('|');
        }
        foreach (var u in unknown) sb.Append(u.Epc).Append(u.ScanPoint).Append(u.Detail).Append('|');
        return sb.ToString();
    }

    // A tag lying at an antenna only starts a new scan every 15 s, so "being read" comes from the raw reads, not the
    // scans: it holds while the reader keeps reading the tag and ends this long after it stops.
    private static readonly TimeSpan ReadNow = TimeSpan.FromSeconds(3);

    private static bool BeingRead(BagView b, DateTimeOffset now) =>
        b.LastSeenUtc is { } seen && now - seen < ReadNow || AppServices.Pipeline.IsBeingRead(b.Epc, ReadNow);

    private static Region RegionForUnknown(string scanPoint) => scanPoint switch
    {
        ScanPoint.Ramp => Region.Held,
        ScanPoint.Belt04 => Region.HallProblems,
        _ => Region.Queue,
    };

    private void LayoutRegion(Region region, List<BagRowViewModel> rows, List<ScanLogViewModel> strangers, DateTimeOffset now)
    {
        var area = Areas[region];
        var cols = Math.Max(1, (int)(area.Width / Pitch));
        var capacity = cols * Math.Max(1, (int)(area.Height / Pitch));

        // The tracked bag always gets a place; most recently seen first.
        var ordered = rows.OrderByDescending(r => r.Plate == _tracked).ThenByDescending(r => r.Bag.LastSeenUtc).ThenBy(r => r.Plate).ToList();
        var i = 0;

        // The tracked bag gets the first row to itself, so its name tag doesn't cover the other bags.
        if (ordered.Count > 0 && ordered[0].Plate == _tracked && capacity >= 2 * cols)
        {
            AddBagDot(area, cols, 0, ordered[0], now);
            ordered.RemoveAt(0);
            i = cols;
            capacity -= cols;
        }

        var items = ordered.Count + strangers.Count;
        var shown = items > capacity ? Math.Max(0, capacity - 1) : items;
        var first = i;

        foreach (var s in strangers.Take(shown))
            AddUnknownDot(area, cols, i++, s);
        foreach (var row in ordered.Take(Math.Max(0, shown - strangers.Count)))
            AddBagDot(area, cols, i++, row, now);
        shown = i - first;

        if (items > shown)
        {
            var (x, y) = Slot(area, cols, i);
            var more = new TextBlock { Text = $"+{items - shown}", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Ui.Resource("MapLabel") };
            Place(more, x - 2, y + 1);
            DotLayer.Children.Add(more);
        }
    }

    private static (double X, double Y) Slot(Rect area, int cols, int index) =>
        (area.X + index % cols * Pitch, area.Y + index / cols * Pitch);

    private void AddBagDot(Rect area, int cols, int index, BagRowViewModel row, DateTimeOffset now)
    {
        var (x, y) = Slot(area, cols, index);
        var (region, kind) = Classify(row.Bag);
        var brush = KindBrush(kind);
        var firstPart = DotLayer.Children.Count;
        var tracked = row.Plate == _tracked;
        var dimmed = _tracked != null && !tracked;

        var dot = new Ellipse
        {
            Width = Dot, Height = Dot, IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Fill = kind == DotKind.Expected ? null : brush,
            Stroke = kind == DotKind.Expected ? brush : new SolidColorBrush(Colors.White),
            StrokeThickness = kind == DotKind.Expected ? 2 : 1.5,
            Opacity = dimmed ? 0.25 : 1,
        };
        // The click target is the whole grid cell, not just the dot: a hollow "awaiting check-in" ring has no fill,
        // so on its own only its 2 px outline could be clicked.
        var target = new Grid { Width = Pitch, Height = Pitch, Background = new SolidColorBrush(Colors.Transparent), Children = { dot } };
        ToolTipService.SetToolTip(target, $"{row.Passenger} · {row.Plate}\n{row.StatusText}{(row.Bag.Uld != null ? " in " + row.Bag.Uld : "")}{(row.Bag.OpenException is { } ex ? " · " + ex : "")}\nClick to track");
        target.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(target).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            Track(row.Plate);
        };
        Place(target, x - (Pitch - Dot) / 2, y - (Pitch - Dot) / 2);
        DotLayer.Children.Add(target);

        // Being read right now: a pulsing halo.
        if (BeingRead(row.Bag, now) && !dimmed)
        {
            var halo = new Ellipse
            {
                Width = 30, Height = 30, Stroke = Ui.Resource("TextFillColorSecondaryBrush"), StrokeThickness = 2,
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(), IsHitTestVisible = false,
            };
            Place(halo, x - 6, y - 6);
            DotLayer.Children.Add(halo);
            _halos.Add((halo, index * 0.13));
        }

        if (tracked)
        {
            var ring = new Ellipse { Width = 40, Height = 40, Stroke = Ui.Resource("AccentFillColorDefaultBrush"), StrokeThickness = 4, IsHitTestVisible = false };
            Place(ring, x - 11, y - 11);
            DotLayer.Children.Add(ring);
            var tag = new Border
            {
                Background = Ui.Resource("AccentFillColorDefaultBrush"), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 3, 8, 3),
                Child = new TextBlock { Text = row.Passenger, Foreground = new SolidColorBrush(Colors.White), FontWeight = FontWeights.SemiBold, FontSize = 14 },
                IsHitTestVisible = false,
            };
            Place(tag, x + 26, y - 4);   // beside the dot, clear of the region label above
            DotLayer.Children.Add(tag);
        }

        TrackMove(row.Plate, region, new Point(x, y), firstPart);
    }

    /// <summary>
    /// Starts a slide when the bag has changed area since its dot last landed, and attaches everything just drawn for
    /// it (dot, halo, tracking ring, name tag) to a slide in progress.
    /// </summary>
    private void TrackMove(string plate, Region region, Point to, int firstPart)
    {
        if (_settled.TryGetValue(plate, out var was) && was.Region != region)
            _moves[plate] = new Move(BeltRoute(was.At, to));
        _settled[plate] = (region, to);
        if (!_moves.TryGetValue(plate, out var move)) return;

        move.Path[^1] = to;   // its slot may have shifted while it was moving
        for (var i = firstPart; i < DotLayer.Children.Count; i++)
        {
            var part = DotLayer.Children[i];
            move.Parts.Add((part, Canvas.GetLeft(part) - to.X, Canvas.GetTop(part) - to.Y));
        }
        PlaceMove(move, Environment.TickCount64);
    }

    /// <summary>Down (or up) to the belt, along it, and out to the new place.</summary>
    private static Point[] BeltRoute(Point from, Point to)
    {
        const double belt = 400 - Dot / 2;
        return [from, new Point(from.X, belt), new Point(to.X, belt), to];
    }

    private static void PlaceMove(Move move, long now)
    {
        var t = Math.Clamp((now - move.Start) / MoveMs, 0, 1);
        t = t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;   // ease in and out

        var lengths = move.Path.Zip(move.Path.Skip(1), (a, b) => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y))).ToArray();
        var along = t * lengths.Sum();
        var at = move.Path[^1];
        for (var i = 0; i < lengths.Length; i++)
        {
            if (along <= lengths[i] && lengths[i] > 0)
            {
                var (a, b, f) = (move.Path[i], move.Path[i + 1], along / lengths[i]);
                at = new Point(a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f);
                break;
            }
            along -= lengths[i];
        }
        foreach (var (part, dx, dy) in move.Parts) Place(part, at.X + dx, at.Y + dy);
    }

    /// <summary>For a few seconds after the tunnel reads a tag: what it decided, beside the tunnel.</summary>
    private void RenderTunnelDecision()
    {
        var now = DateTimeOffset.UtcNow;
        var s = AppServices.State.ScanLog.FirstOrDefault(x => x.ScanPoint == ScanPoint.Belt04 && x.Outcome != nameof(ScanOutcome.Duplicate));
        if (s == null || now - s.Utc > TimeSpan.FromSeconds(5))
        {
            TunnelDecision.Visibility = Visibility.Collapsed;
            return;
        }

        var who = s.Passenger.Length > 0 ? s.Passenger : s.Plate != "-" ? s.Plate : "…" + (s.Epc.Length > 6 ? s.Epc[^6..] : s.Epc);
        var (text, brush) = s.Outcome switch
        {
            nameof(ScanOutcome.Matched) => ($"✓ Sorted · {who}", "StatusMatchedBrush"),
            nameof(ScanOutcome.WrongFlight) => ($"✗ {s.Detail} · {who}", "StatusExceptionBrush"),
            nameof(ScanOutcome.Offloaded) => ($"✗ Offloaded bag on the belt · {who}", "StatusExceptionBrush"),
            nameof(ScanOutcome.Unknown) => ($"? Unknown tag {who}", "StatusExceptionBrush"),
            _ => ($"{s.Detail} · {who}", "StatusInScanBrush"),
        };
        TunnelDecisionText.Text = text;
        TunnelDecision.Background = Ui.Resource(brush);
        TunnelDecision.Visibility = Visibility.Visible;
        TunnelDecision.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(TunnelDecision, 690 - TunnelDecision.DesiredSize.Width);   // right edge just left of the tunnel
    }

    private void AddUnknownDot(Rect area, int cols, int index, ScanLogViewModel s)
    {
        var (x, y) = Slot(area, cols, index);
        var g = new Grid { Width = Dot, Height = Dot, Opacity = _tracked != null ? 0.25 : 1 };
        g.Children.Add(new Ellipse { Fill = Ui.Resource("StatusExceptionBrush"), Stroke = new SolidColorBrush(Colors.White), StrokeThickness = 1.5, StrokeDashArray = [2, 1] });
        g.Children.Add(new TextBlock
        {
            Text = "?", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Colors.White),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });
        ToolTipService.SetToolTip(g, $"Unknown tag {s.Epc}\n{s.Detail}");
        Place(g, x, y);
        DotLayer.Children.Add(g);
    }

    private void RenderSubtitle()
    {
        var t = AppServices.State.Totals;
        var health = AppServices.State.Health.State;
        Subtitle.Text = $"KQ-504 · Nairobi → Entebbe · {t.Expected} bag(s) · {t.Loaded} loaded · " +
                        (health == SupervisorState.Streaming ? "reader streaming" : health == SupervisorState.Online ? "reader connected, not streaming" : "reader not connected");
    }

    private void UpdateRates()
    {
        foreach (var (antenna, (_, _, rate)) in _beacons)
        {
            var reads = AppServices.Pipeline.Antennas[antenna].Reads;
            var delta = _lastReads.TryGetValue(antenna, out var last) ? reads - last : 0;
            _lastReads[antenna] = reads;
            rate.Text = delta > 0 ? $"{delta} reads/s" : "idle";
        }
    }

    // ------------------------------------------------------------------ animation (30 fps, only while visible)

    private void Animate()
    {
        var ms = Environment.TickCount64;
        BeltStripes.StrokeDashOffset = -(ms % 1080) / 1000.0;   // the belt moves left to right

        var now = DateTimeOffset.UtcNow;
        foreach (var (antenna, (r1, r2, _)) in _beacons)
        {
            var last = AppServices.Pipeline.Antennas[antenna].LastReadUtc;
            var active = last is { } t && now - t < TimeSpan.FromSeconds(1.5);
            Pulse(r1, active, (ms % 1400) / 1400.0);
            Pulse(r2, active, ((ms + 700) % 1400) / 1400.0);
        }

        var tick = Environment.TickCount64;
        foreach (var (plate, move) in _moves.ToList())
        {
            PlaceMove(move, tick);
            if (tick - move.Start >= MoveMs) _moves.Remove(plate);
        }

        foreach (var (ring, phase) in _halos)
        {
            var p = (ms / 1000.0 + phase) % 1.0;
            var s = (ScaleTransform)ring.RenderTransform;
            s.ScaleX = s.ScaleY = 0.8 + p * 0.6;
            ring.Opacity = 0.9 * (1 - p);
        }
    }

    private static void Pulse(Ellipse ring, bool active, double phase)
    {
        if (!active) { ring.Opacity = 0; return; }
        var s = (ScaleTransform)ring.RenderTransform;
        s.ScaleX = s.ScaleY = 0.5 + phase * 1.1;
        ring.Opacity = 0.85 * (1 - phase);
    }

    // ------------------------------------------------------------------ tracking one bag

    private void Track(string plate)
    {
        _tracked = plate;
        StopTrackButton.Visibility = Visibility.Visible;
        JourneyCard.Visibility = Visibility.Visible;
        _dirty = true;
        Render();
        _ = RenderJourneyAsync();
    }

    private void StopTrack_Click(object sender, RoutedEventArgs e)
    {
        _tracked = null;
        TrackBox.Text = "";
        StopTrackButton.Visibility = Visibility.Collapsed;
        JourneyCard.Visibility = Visibility.Collapsed;
        _dirty = true;
        Render();
    }

    private void RenderRoute(BagView? bag)
    {
        Path[] segments = [RouteDeskToTunnel, RouteTunnelToLoading, RouteLoadingToUld];
        if (bag == null)
        {
            foreach (var s in segments) s.Visibility = Visibility.Collapsed;
            return;
        }

        var done = Ui.Resource("StatusMatchedBrush");
        var current = Ui.Resource("AccentFillColorDefaultBrush");
        var wrong = bag.OpenException is { } x && WrongTypes.Contains(x);
        void Show(Path p, bool passed, bool next)
        {
            p.Visibility = passed || next ? Visibility.Visible : Visibility.Collapsed;
            p.Stroke = wrong && next ? Ui.Resource("StatusExceptionBrush") : passed ? done : current;
            p.Opacity = passed ? 0.95 : 0.7;
        }

        var stage = bag.Status is BagStatus.Offloaded ? -1 : (int)bag.Status;   // 0 expected, 1 checked in, 2 sorted, 3 loaded
        Show(RouteDeskToTunnel, stage >= 2, stage == 1);
        Show(RouteTunnelToLoading, stage >= 3, stage == 2);

        var uldX = bag.Uld == "AK7" ? 1210 : 1450;
        RouteLoadingToUld.Data = new PathGeometry
        {
            Figures =
            {
                new PathFigure
                {
                    StartPoint = new Point(1250, 386),
                    Segments = { new LineSegment { Point = new Point(1250, 330) }, new LineSegment { Point = new Point(uldX, 330) }, new LineSegment { Point = new Point(uldX, 292) } },
                },
            },
        };
        Show(RouteLoadingToUld, stage >= 3, false);
    }

    private async Task RenderJourneyAsync()
    {
        if (_tracked is not { } plate) return;
        var row = AppServices.State.FindBag(plate);
        if (row == null) return;
        var events = await AppServices.Engine.TimelineAsync(plate);
        if (_tracked != plate) return;

        JourneyName.Text = row.Passenger;
        JourneyPlate.Text = $"{plate} · {row.ClassText} · {(row.Bag.Flight == "KQ504" ? "KQ-504 to Entebbe" : row.Bag.Flight + " (not KQ-504)")}";

        DateTimeOffset? When(params string[] kinds) => events.FirstOrDefault(e => kinds.Contains(e.Kind))?.OccurredUtc;
        var offloaded = When("Offloaded", "BSM-DEL");
        var steps = new List<(string Title, string Place, DateTimeOffset? At, bool Bad)>
        {
            ("BSM received", "Check-in system", When("BSM"), false),
            ("Checked in", "Desk 14", When("DeskBind", "DeskCheck", "TagBound"), false),
            ("Sorted", "Belt 04 tunnel", When("Sorted"), false),
            offloaded != null
                ? ("Offloaded", "Taken off the flight", offloaded, true)
                : ("Loaded", row.Bag.Uld != null ? $"ULD {row.Bag.Uld} · Hold 2" : "ULD · Hold 2", When("Loaded"), false),
        };

        JourneySteps.Children.Clear();
        JourneySteps.ColumnDefinitions.Clear();
        var currentFound = false;
        for (var i = 0; i < steps.Count; i++)
        {
            JourneySteps.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var (title, place, at, bad) = steps[i];
            var isDone = at != null;
            var isCurrent = !isDone && !currentFound;
            if (isCurrent) currentFound = true;

            var brush = bad ? Ui.Resource("StatusOffloadedBrush") : isDone ? Ui.Resource("StatusMatchedBrush")
                : isCurrent ? Ui.Resource("AccentFillColorDefaultBrush") : Ui.Resource("StatusExpectedBrush");

            var cell = new Grid { RowSpacing = 4 };
            cell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            cell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Connector to the next step.
            if (i < steps.Count - 1)
                cell.Children.Add(new Rectangle
                {
                    Height = 3, Margin = new Thickness(0, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Stretch, Fill = isDone && steps[i + 1].At != null ? Ui.Resource("StatusMatchedBrush") : Ui.Resource("ControlStrokeColorDefaultBrush"),
                    RenderTransform = new TranslateTransform { X = 0 },
                });
            var circle = new Grid { Width = 30, Height = 30, HorizontalAlignment = HorizontalAlignment.Left };
            circle.Children.Add(new Ellipse { Fill = isDone ? brush : Ui.Resource("CardBackgroundFillColorDefaultBrush"), Stroke = brush, StrokeThickness = 3 });
            circle.Children.Add(new FontIcon
            {
                Glyph = bad ? "" : isDone ? "" : isCurrent ? "" : "", FontSize = 13,
                Foreground = isDone ? new SolidColorBrush(Colors.White) : brush,
            });
            cell.Children.Add(circle);

            var text = new StackPanel { Spacing = 1 };
            text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Foreground = isDone || isCurrent ? Ui.Resource("TextFillColorPrimaryBrush") : Ui.Resource("TextFillColorTertiaryBrush") });
            text.Children.Add(new TextBlock { Text = place, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Foreground = Ui.Resource("TextFillColorSecondaryBrush") });
            text.Children.Add(new TextBlock
            {
                Text = at is { } t ? t.ToLocalTime().ToString("HH:mm:ss") : isCurrent ? "next" : "",
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Foreground = isCurrent ? Ui.Resource("AccentTextFillColorPrimaryBrush") : Ui.Resource("TextFillColorTertiaryBrush"),
            });
            Grid.SetRow(text, 1);
            cell.Children.Add(text);

            Grid.SetColumn(cell, i);
            JourneySteps.Children.Add(cell);
        }

        if (row.Bag.OpenException is { } ex)
        {
            JourneyPlate.Text += $" · open exception: {ex}";
        }
    }

    private async void JourneyDetails_Click(object sender, RoutedEventArgs e)
    {
        if (_tracked != null) await BagDetailDialog.ShowForAsync(_tracked, XamlRoot);
    }

    // ------------------------------------------------------------------ track box

    private void TrackBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        var q = sender.Text.Trim();
        sender.ItemsSource = q.Length == 0 ? null : AppServices.State.Bags
            .Where(b => b.Plate.Contains(q, StringComparison.OrdinalIgnoreCase) || b.Passenger.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Take(10).Select(b => $"{b.Passenger} · {b.Plate}").ToList();
    }

    private void TrackBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is string s) Track(s.Split(" · ")[^1]);
    }

    private void TrackBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var q = (args.ChosenSuggestion as string ?? sender.Text).Split(" · ")[^1].Trim();
        var row = AppServices.State.Bags.FirstOrDefault(b => b.Plate == q)
                  ?? AppServices.State.Bags.FirstOrDefault(b => b.Passenger.Contains(q, StringComparison.OrdinalIgnoreCase) || b.Plate.Contains(q));
        if (row != null) Track(row.Plate);
    }

    private static void Place(UIElement e, double x, double y)
    {
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
    }
}
