using KQ.Brs.Core.Domain;

namespace KQ.Brs.Core.Events;

/// <summary>Immutable bag snapshot for the UI (the engine's Bag is mutable and engine-thread-only).</summary>
public sealed record BagView(
    string Plate, string Flight, string PassengerName, CabinClass Class, BagSource Source, BagStatus Status,
    string? Uld, string? LastScanPoint, DateTimeOffset? LastSeenUtc, string? Epc, int WeightKg, TagType TagType,
    ExceptionType? OpenException, bool AuthorityToLoad, PassengerStatus PassengerStatus, bool Deleted)
{
    public static BagView From(Bag bag, ExceptionType? openException) => new(
        bag.Plate.Value, bag.Flight, bag.PassengerName, bag.Class, bag.Source, bag.Status, bag.Uld, bag.LastScanPoint,
        bag.LastSeenUtc, bag.Epc, bag.WeightKg, bag.TagType, openException, bag.AuthorityToLoad, bag.PassengerStatus, bag.Deleted);
}

public sealed record ExceptionView(
    string Id, string Plate, string? Epc, ExceptionType Type, Severity Severity, ExceptionState State, string Message,
    BagSource Source, string? ScanPoint, DateTimeOffset RaisedUtc, DateTimeOffset? ResolvedUtc, string? ResolvedBy,
    string? ResolutionNote, bool Overridden)
{
    public static ExceptionView From(BagException e) => new(e.Id, e.Plate, e.Epc, e.Type, e.Severity, e.State, e.Message,
        e.Source, e.ScanPoint, e.RaisedUtc, e.ResolvedUtc, e.ResolvedBy, e.ResolutionNote, e.Overridden);
}

/// <summary>Dashboard totals (spec 7.1).</summary>
public sealed record FlightTotals(
    int Expected, int CheckInVerified, int SorterVerified, int Loaded, int Offloaded, int OpenExceptions,
    int LoadedAk7, int LoadedAk8, int StrayReads, int RealBound, int RealPool);

/// <summary>Result of a load request at the ramp (spec 4.2), with what the handheld needs to show.</summary>
public sealed record LoadDecision(
    bool Authorised, string Reason, string Epc, string? Plate, string Uld, string? PassengerName,
    string? BelongsTo, bool IsReal, ExceptionView? Exception, DateTimeOffset Utc);

public abstract record BrsEvent;

public sealed record BagUpdated(BagView Bag) : BrsEvent;

public sealed record ExceptionUpdated(ExceptionView Exception) : BrsEvent;

public sealed record TotalsUpdated(FlightTotals Totals) : BrsEvent;

public sealed record MessageLogged(TypeBMessage Message) : BrsEvent;

public sealed record LoadDecided(LoadDecision Decision) : BrsEvent;

/// <summary>A scan was processed (for live logs and toasts).</summary>
public sealed record ScanProcessed(
    string Epc, string? Plate, string ScanPointId, ScanOutcome Outcome, string Detail, bool IsReal, DateTimeOffset Utc) : BrsEvent;

/// <summary>A real tag was bound to a bag at the desk (as if the agent printed the bag tag).</summary>
public sealed record TagBound(string Epc, string Plate, string PassengerName) : BrsEvent;

/// <summary>An exception that locks the ramp handheld was cleared (by rescan or override).</summary>
public sealed record AlarmCleared(string ExceptionId, string How) : BrsEvent;

/// <summary>The whole state was reloaded (reset or startup); UIs should re-read everything.</summary>
public sealed record StateReloaded : BrsEvent;

/// <summary>
/// In-process broadcast from the engine to the UI and services. Publish runs subscribers on the publisher's thread,
/// so subscribers must only enqueue (never block or touch UI directly).
/// </summary>
public sealed class BrsEventHub
{
    private volatile Action<BrsEvent>[] _subscribers = [];
    private readonly Lock _lock = new();

    public IDisposable Subscribe(Action<BrsEvent> handler)
    {
        lock (_lock) _subscribers = [.. _subscribers, handler];
        return new Unsubscriber(() => { lock (_lock) _subscribers = _subscribers.Where(s => s != handler).ToArray(); });
    }

    public void Publish(BrsEvent e)
    {
        foreach (var s in _subscribers)
        {
            try { s(e); } catch { /* a faulty subscriber must not break the engine */ }
        }
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
