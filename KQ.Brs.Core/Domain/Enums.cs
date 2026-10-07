namespace KQ.Brs.Core.Domain;

/// <summary>Bag lifecycle (spec 5). Forward-only: a read at an earlier point never moves a bag backwards.</summary>
public enum BagStatus
{
    Expected = 0,
    CheckedIn = 1,
    Sorted = 2,
    Loaded = 3,
    Transferred = 4,
    Arrived = 5,
    Offloaded = 10,
}

public enum CabinClass { Economy, SkyPriority, Business }

public enum TagType { Disposable, Reusable }

/// <summary>Where a bag's scans come from in the POC.</summary>
public enum BagSource
{
    /// <summary>Moved through the scan points by the simulator.</summary>
    Virtual,

    /// <summary>Reserved for real tags, bound at the desk antenna.</summary>
    RealPool,

    /// <summary>Belongs to another flight (e.g. KQ-412 misroute demo).</summary>
    Foreign,
}

/// <summary>Passenger status from the BSM .S/ element: checked in, boarded, or no-show.</summary>
public enum PassengerStatus { CheckedIn, Boarded, NotBoarded }

public enum ScanPointKind { Desk, Tunnel, Ramp, Transfer, Arrival }

public enum ScanOutcome { Matched, Unknown, WrongFlight, Offloaded, Duplicate, NotAuthorised, Bound, Ignored }

public enum ExceptionType { WrongFlight, Unknown, MissingAtSorter, NotLoaded, PaxNotBoarded, NotAuthorised, Offloaded }

public enum ExceptionState { Open, Acknowledged, Resolved }

public enum Severity { Info, Warning, Critical }

public enum MessageType { BSM, BPM, BTM, BUM }

public enum MessageDirection { In, Out }
