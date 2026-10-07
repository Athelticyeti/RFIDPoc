namespace KQ.Brs.Core.Domain;

/// <summary>
/// A bag on a manifest, created from a BSM (spec 5). Static data comes from BSMs; Status/ULD/last scan are derived
/// from the append-only BagEvent log (replayed on startup). Owned by the reconciliation engine's single thread.
/// </summary>
public sealed class Bag
{
    public required LicencePlate Plate { get; init; }
    public required string Flight { get; set; }          // designator, e.g. KQ504
    public required string Destination { get; set; }     // e.g. EBB
    public required string Surname { get; set; }
    public required string Initial { get; set; }
    public CabinClass Class { get; set; }
    public TagType TagType { get; set; }
    public int WeightKg { get; set; }
    public BagSource Source { get; set; }

    /// <summary>Authority to load from the BSM .S/ element (false e.g. when the ticket isn't valid).</summary>
    public bool AuthorityToLoad { get; set; } = true;

    public PassengerStatus PassengerStatus { get; set; } = PassengerStatus.CheckedIn;

    /// <summary>True once a BSM DEL has been received.</summary>
    public bool Deleted { get; set; }

    /// <summary>For a KQ-412 demo bag: the KQ-504 bag the tag was taken from (to undo the flag).</summary>
    public LicencePlate? OriginalPlate { get; set; }

    public string? Epc { get; set; }
    public BagStatus Status { get; set; } = BagStatus.Expected;
    public string? Uld { get; set; }
    public string? LastScanPoint { get; set; }
    public DateTimeOffset? LastSeenUtc { get; set; }

    public string PassengerName => $"{Initial}. {Surname}";

    /// <summary>Copy for handing to another thread (persistence).</summary>
    public Bag Clone() => (Bag)MemberwiseClone();
}

/// <summary>Append-only audit record (spec 5, FR-12). Never updated or deleted.</summary>
public sealed record BagEvent
{
    public required string Id { get; init; }
    public required string Plate { get; init; }
    public required string Flight { get; init; }
    public required string Kind { get; init; }              // e.g. BSM, DeskBind, Sorted, Loaded, LoadRefused, Rescan, Override
    public required DateTimeOffset OccurredUtc { get; init; }
    public string? ScanPoint { get; init; }
    public MessageType? MessageType { get; init; }
    public string? ReaderId { get; init; }
    public int? Antenna { get; init; }
    public double? Rssi { get; init; }
    public ScanOutcome? Outcome { get; init; }
    public bool? LoadAuthorised { get; init; }
    public string? Uld { get; init; }
    public string? Handler { get; init; }
    public BagStatus? NewStatus { get; init; }
    public string? RawMessage { get; init; }
    public string? Note { get; init; }
    /// <summary>The <see cref="Scanning.BagScan.IdempotencyKey"/> of the scan this event records, if any.</summary>
    public string? ScanKey { get; init; }

    public static string NewId() => Guid.CreateVersion7().ToString("N");
}

/// <summary>An exception that drives alerts (spec 5). Owned by the engine thread.</summary>
public sealed class BagException
{
    public required string Id { get; init; }
    public required string Plate { get; init; }
    public string? Epc { get; init; }
    public required ExceptionType Type { get; init; }
    public required Severity Severity { get; init; }
    public required DateTimeOffset RaisedUtc { get; init; }
    public required string Message { get; set; }
    public BagSource Source { get; init; }
    public string? ScanPoint { get; init; }
    public ExceptionState State { get; set; } = ExceptionState.Open;
    public DateTimeOffset? AcknowledgedUtc { get; set; }
    public DateTimeOffset? ResolvedUtc { get; set; }
    public string? ResolvedBy { get; set; }
    public string? ResolutionNote { get; set; }
    public bool Overridden { get; set; }

    public bool IsOpen => State != ExceptionState.Resolved;

    public BagException Clone() => (BagException)MemberwiseClone();
}

/// <summary>A scan point (spec 5), configured once.</summary>
public sealed record ScanPoint(string Id, string Name, ScanPointKind Kind, MessageType Message)
{
    public const string Desk14 = "DESK14";
    public const string Belt04 = "BELT04";
    public const string Ramp = "RAMP";

    public static readonly IReadOnlyList<ScanPoint> All =
    [
        new(Desk14, "Check-in · Desk 14", ScanPointKind.Desk, MessageType.BSM),
        new(Belt04, "Sorting tunnel · Belt 04", ScanPointKind.Tunnel, MessageType.BPM),
        new(Ramp, "Loading · Hold 2", ScanPointKind.Ramp, MessageType.BPM),
    ];

    public static ScanPoint Get(string id) => All.First(p => p.Id == id);
}

/// <summary>A message in the Type B inbox/outbox (spec 6.1). SentUtc is null while queued (outbox).</summary>
public sealed record TypeBMessage(
    string Id, MessageDirection Direction, MessageType Type, string? Plate, string Raw, DateTimeOffset CreatedUtc, DateTimeOffset? SentUtc);
