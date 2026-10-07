using KQ.Brs.Core.Domain;
using KQ.Brs.Core.Events;

namespace KQ.Brs.Core.Reconciliation;

/// <summary>What happened to a scan.</summary>
public sealed record ScanResult(ScanOutcome Outcome, string? Plate, string Detail, LoadDecision? Decision = null);

/// <summary>Everything the UI needs to draw from scratch.</summary>
public sealed record EngineSnapshot(
    FlightKey Flight, IReadOnlyList<BagView> Bags, IReadOnlyList<ExceptionView> Exceptions,
    IReadOnlyList<TypeBMessage> Messages, FlightTotals Totals, string SelectedUld, bool RampArmed, bool OutboxPaused);

/// <summary>FR-10 before-pushback check.</summary>
public sealed record PrePushbackReport(
    IReadOnlyList<BagView> CheckedInNotLoaded, int NeverCheckedIn, IReadOnlyList<BagView> OffloadRequired, int ExceptionsRaised);
