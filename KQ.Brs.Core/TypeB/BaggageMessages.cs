using KQ.Brs.Core.Domain;

namespace KQ.Brs.Core.TypeB;

public enum BsmAction { Original, Change, Delete }

/// <summary>Base for parsed IATA Type B baggage messages (spec 6.1).</summary>
public abstract record BaggageMessage(MessageType Type, string Raw);

/// <summary>
/// Baggage Source Message. One BSM can list several plates (.N/ with a count); they share passenger and flight data.
/// </summary>
public sealed record Bsm(
    string Raw,
    BsmAction Action,
    string Station,
    string Flight,
    string FlightDate,
    string Destination,
    CabinClass Class,
    IReadOnlyList<LicencePlate> Plates,
    int? WeightKg,
    bool AuthorityToLoad,
    PassengerStatus PassengerStatus,
    string? Surname,
    string? Initial) : BaggageMessage(MessageType.BSM, Raw);

/// <summary>Baggage Processed Message: a scan at a processing point (.J/), optionally into a ULD (.U/).</summary>
public sealed record Bpm(
    string Raw, string Station, string ScanPoint, DateTimeOffset ProcessedUtc, string Flight, string FlightDate,
    string Destination, LicencePlate Plate, string? Uld) : BaggageMessage(MessageType.BPM, Raw);

/// <summary>Baggage Unload Message: an off-load (or arrival).</summary>
public sealed record Bum(
    string Raw, string Station, string Flight, string FlightDate, string Destination, LicencePlate Plate, string? Reason)
    : BaggageMessage(MessageType.BUM, Raw);
