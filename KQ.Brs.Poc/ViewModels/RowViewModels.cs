using CommunityToolkit.Mvvm.ComponentModel;
using KQ.Brs.Core.Domain;
using KQ.Brs.Core.Events;
using KQ.Brs.Core.Reconciliation;

namespace KQ.Brs.Poc.ViewModels;

/// <summary>Dashboard badge (spec 7.1): MATCHED, IN SCAN, EXCEPTION, MISSING, plus EXPECTED and OFFLOADED.</summary>
public enum BadgeKind { Expected, InScan, Matched, Exception, Missing, Offloaded, Held }

/// <summary>One bag row, updated in place so the virtualised list doesn't re-create items.</summary>
public sealed partial class BagRowViewModel : ObservableObject
{
    private static readonly TimeSpan InScanWindow = TimeSpan.FromSeconds(3);

    public BagRowViewModel(BagView bag) => Update(bag);

    public BagView Bag { get; private set; } = null!;

    [ObservableProperty] public partial BadgeKind Badge { get; private set; }
    [ObservableProperty] public partial string LastSeenText { get; private set; } = "";

    public string Plate => Bag.Plate;
    public string Passenger => Bag.PassengerName;
    public string ClassText => Bag.Class switch { CabinClass.Business => "J", CabinClass.SkyPriority => "SkyPriority", _ => "Y" };
    public bool IsReal => Bag.Source != BagSource.Virtual || (Bag.Epc != null && !SyntheticEpc.TryDecode(Bag.Epc, out _));
    public string SourceGlyph => IsReal ? "" : "";     // tag vs. virtual device
    public string SourceText => IsReal ? (Bag.Epc == null ? "Real tag pool (unbound)" : $"Real tag {Bag.Epc}") : "Virtual (simulator)";
    public string StatusText => Bag.Deleted && Bag.Status != BagStatus.Offloaded ? "Deleted" : Bag.Status.ToString();
    public string LastScanPointText => Bag.LastScanPoint switch
    {
        ScanPoint.Desk14 => "Check-in", ScanPoint.Belt04 => "Sorting", ScanPoint.Ramp => "Loading", null => "-", var other => other,
    };
    public string UldText => Bag.Uld ?? "-";
    public string FlightText => Bag.Flight == "KQ504" ? "" : Bag.Flight;
    public bool IsOtherFlight => Bag.Flight != "KQ504";
    public bool DeskDone => Bag.Status is >= BagStatus.CheckedIn and < BagStatus.Offloaded;
    public bool BeltDone => Bag.Status is >= BagStatus.Sorted and < BagStatus.Offloaded;
    public bool RampDone => Bag.Status is >= BagStatus.Loaded and < BagStatus.Offloaded;

    public string BadgeText => Badge switch
    {
        BadgeKind.InScan => "IN SCAN",
        BadgeKind.Matched => "MATCHED",
        BadgeKind.Exception => "EXCEPTION",
        BadgeKind.Missing => "MISSING",
        BadgeKind.Offloaded => "OFFLOADED",
        BadgeKind.Held => "HELD",
        _ => "EXPECTED",
    };

    public void Update(BagView bag)
    {
        Bag = bag;
        OnPropertyChanged(string.Empty);   // every derived property
        Refresh(DateTimeOffset.UtcNow);
    }

    /// <summary>Re-evaluates time-dependent display (IN SCAN highlight, "12 s ago").</summary>
    public void Refresh(DateTimeOffset now)
    {
        Badge = Bag switch
        {
            { OpenException: ExceptionType.NotLoaded or ExceptionType.MissingAtSorter } => BadgeKind.Missing,
            { OpenException: not null } => BadgeKind.Exception,
            { Status: BagStatus.Offloaded } or { Deleted: true } => BadgeKind.Offloaded,
            // Refused at the ramp and set aside: the passenger can't fly (ticket or no-show).
            { Status: BagStatus.CheckedIn or BagStatus.Sorted } when !Bag.AuthorityToLoad || Bag.PassengerStatus == PassengerStatus.NotBoarded => BadgeKind.Held,
            _ when Bag.LastSeenUtc is { } seen && now - seen < InScanWindow => BadgeKind.InScan,
            { Status: >= BagStatus.CheckedIn } => BadgeKind.Matched,
            _ => BadgeKind.Expected,
        };
        LastSeenText = Bag.LastSeenUtc is { } t ? Ui.Ago(now - t) : "";
        OnPropertyChanged(nameof(BadgeText));
    }

    public bool IsRecentlySeen(DateTimeOffset now) => Bag.LastSeenUtc is { } t && now - t < TimeSpan.FromMinutes(2);
}

public sealed partial class ExceptionRowViewModel : ObservableObject
{
    public ExceptionRowViewModel(ExceptionView e) => Update(e);

    public ExceptionView Exception { get; private set; } = null!;
    [ObservableProperty] public partial string AgeText { get; private set; } = "";

    public string Id => Exception.Id;
    public string Plate => Exception.Plate;
    public string TypeText => Exception.Type switch
    {
        ExceptionType.WrongFlight => "Wrong flight",
        ExceptionType.PaxNotBoarded => "Passenger not boarded",
        ExceptionType.NotAuthorised => "Not authorised",
        ExceptionType.MissingAtSorter => "Missing at sorter",
        ExceptionType.NotLoaded => "Not loaded",
        ExceptionType.Unknown => "Unknown tag",
        var t => t.ToString(),
    };
    public string Glyph => Exception.Type switch
    {
        ExceptionType.WrongFlight => "",        // warning
        ExceptionType.Unknown => "",            // unknown
        ExceptionType.MissingAtSorter => "",    // missing
        ExceptionType.NotLoaded => "",
        ExceptionType.Offloaded or ExceptionType.PaxNotBoarded => "",
        _ => "",
    };
    public bool IsCritical => Exception.Severity == Severity.Critical;
    public bool IsOpen => Exception.State != ExceptionState.Resolved;
    public bool CanAcknowledge => Exception.State == ExceptionState.Open;
    public bool CanResolve => IsOpen && !IsCritical;
    public bool CanOverride => IsOpen && IsCritical;
    public string StateText => Exception.State.ToString();
    public string SourceText => Exception.Source == BagSource.Virtual ? "Virtual" : "Real tag";
    public string RaisedText => Exception.RaisedUtc.ToLocalTime().ToString("HH:mm:ss");
    public string ResolutionText => Exception.ResolvedUtc is { } r
        ? $"{(Exception.Overridden ? "Overridden" : "Resolved")} by {Exception.ResolvedBy} after {Ui.Duration(r - Exception.RaisedUtc)}: {Exception.ResolutionNote}"
        : "";

    public void Update(ExceptionView e)
    {
        Exception = e;
        OnPropertyChanged(string.Empty);
        Refresh(DateTimeOffset.UtcNow);
    }

    public void Refresh(DateTimeOffset now) =>
        AgeText = IsOpen ? $"open {Ui.Duration(now - Exception.RaisedUtc)}" : "";
}

public sealed class MessageRowViewModel(TypeBMessage message)
{
    public TypeBMessage Message { get; set; } = message;
    public string Id => Message.Id;
    public string TypeText => Message.Type.ToString();
    public string Plate => Message.Plate ?? "";
    public string CreatedText => Message.CreatedUtc.ToLocalTime().ToString("HH:mm:ss");
    public string StateText => Message.Direction == MessageDirection.In ? "Received" : Message.SentUtc is { } s ? $"Sent {s.ToLocalTime():HH:mm:ss}" : "Queued (MQ down)";
    public bool IsQueued => Message.Direction == MessageDirection.Out && Message.SentUtc == null;
    public string FirstLine => Message.Raw.Split('\n').Skip(1).FirstOrDefault(l => l.StartsWith(".F/") || l is "CHG" or "DEL") ?? "";
    public string Summary
    {
        get
        {
            var lines = Message.Raw.Split('\n');
            var action = lines.Length > 1 && lines[1] is "CHG" or "DEL" ? lines[1] + " " : "";
            var j = lines.FirstOrDefault(l => l.StartsWith(".J/"))?.Split('/');
            var u = lines.FirstOrDefault(l => l.StartsWith(".U/"))?[3..];
            return $"{action}{(j is { Length: > 3 } ? j[3] : "")}{(u != null ? " → " + u : "")}".Trim();
        }
    }
}

public sealed class DecisionViewModel(LoadDecision decision)
{
    public LoadDecision Decision { get; } = decision;
    public bool Authorised => Decision.Authorised;
    public string Title => Decision.Authorised ? $"LOAD → {Decision.Uld}" : "DO NOT LOAD";
    public string Plate => Decision.Plate ?? Decision.Epc;
    public string Passenger => Decision.PassengerName ?? "Unknown tag";
    public string Reason => Decision.BelongsTo is { } f ? $"{Decision.Reason}. Belongs to {f}." : Decision.Reason;
    public string Time => Decision.Utc.ToLocalTime().ToString("HH:mm:ss");
    public string SourceText => Decision.IsReal ? "Real tag" : "Virtual";
    public string Glyph => Decision.Authorised ? "" : "";
}

public sealed class ScanLogViewModel(ScanProcessed scan, string? passenger)
{
    public DateTimeOffset Utc { get; } = scan.Utc;
    public string Time { get; } = scan.Utc.ToLocalTime().ToString("HH:mm:ss.f");
    public string ScanPoint { get; } = scan.ScanPointId;
    public string Epc { get; } = scan.Epc;
    public string Plate { get; } = scan.Plate ?? "-";
    public string Passenger { get; } = passenger ?? "";
    public string Outcome { get; } = scan.Outcome.ToString();
    public string Detail { get; } = scan.Detail;
    public bool IsProblem { get; } = scan.Outcome is ScanOutcome.WrongFlight or ScanOutcome.NotAuthorised or ScanOutcome.Offloaded or ScanOutcome.Unknown;
}
