using System.Globalization;
using KQ.Brs.Core.Domain;

namespace KQ.Brs.Core.TypeB;

public sealed class TypeBParseException(string message) : Exception(message);

/// <summary>
/// Parses RP 1745-style baggage messages (POC subset). A message starts with its type (BSM/BPM/BUM), optionally
/// followed by CHG or DEL, then one element per line (".F/KQ504/02OCT/EBB/Y"), and ends with END + type.
/// </summary>
public static class TypeBParser
{
    public static BaggageMessage Parse(string raw)
    {
        var lines = raw.Replace("\r", "", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0) throw new TypeBParseException("Empty message.");

        var type = lines[0];
        var action = BsmAction.Original;
        var elements = new List<(string Key, string[] Fields)>();

        foreach (var line in lines.Skip(1))
        {
            if (line.StartsWith("END", StringComparison.Ordinal)) break;
            if (line == "CHG") { action = BsmAction.Change; continue; }
            if (line == "DEL") { action = BsmAction.Delete; continue; }
            if (line.Length < 3 || line[0] != '.' || line[2] != '/') continue;
            elements.Add((line[1].ToString(), line[3..].Split('/')));
        }

        return type switch
        {
            "BSM" => ParseBsm(raw, action, elements),
            "BPM" => ParseBpm(raw, elements),
            "BUM" => ParseBum(raw, elements),
            _ => throw new TypeBParseException($"Unsupported message type '{type}'."),
        };
    }

    private static Bsm ParseBsm(string raw, BsmAction action, List<(string Key, string[] Fields)> elements)
    {
        var station = Field(elements, "V", 0) is { Length: > 2 } v ? v[2..] : "";
        var (flight, date, dest, cls) = ParseFlight(elements, required: true);

        // .N/0706100001003 = first plate + count of consecutive plates.
        var plates = new List<LicencePlate>();
        foreach (var (key, fields) in elements.Where(e => e.Key == "N"))
        {
            var n = fields[0];
            if (n.Length < 10 || !LicencePlate.TryParse(n[..10], out var first))
                throw new TypeBParseException($"Bad licence plate element '.N/{n}'.");
            var count = n.Length >= 13 && int.TryParse(n[10..13], out var c) && c > 0 ? c : 1;
            var start = long.Parse(first.Value, CultureInfo.InvariantCulture);
            for (var i = 0; i < count; i++)
                plates.Add(new LicencePlate((start + i).ToString("D10", CultureInfo.InvariantCulture)));
        }
        if (plates.Count == 0) throw new TypeBParseException("BSM has no .N/ licence plate.");

        // .W/K/{pieces}/{weight}
        int? weight = Field(elements, "W", 2) is { } w && int.TryParse(w, out var kg) ? kg : null;

        // .S/{authority Y|N}/{seat}/{status C|B|N}/{sequence}
        var authority = Field(elements, "S", 0) != "N";
        var paxStatus = Field(elements, "S", 2) switch
        {
            "B" => PassengerStatus.Boarded,
            "N" => PassengerStatus.NotBoarded,
            _ => PassengerStatus.CheckedIn,
        };

        // .P/1MWANGI/J
        var surname = Field(elements, "P", 0) is { Length: > 1 } p ? p.TrimStart('0', '1', '2', '3', '4', '5', '6', '7', '8', '9') : null;
        var initial = Field(elements, "P", 1);

        return new Bsm(raw, action, station, flight, date, dest, cls, plates, weight, authority, paxStatus, surname, initial);
    }

    private static Bpm ParseBpm(string raw, List<(string Key, string[] Fields)> elements)
    {
        var station = Field(elements, "V", 0) is { Length: > 2 } v ? v[2..] : "";
        // .J/S/{agent}/{scan point}/{ddMMM}/{HHmmss}
        var scanPoint = Field(elements, "J", 2) ?? "";
        var processed = DateTimeOffset.UtcNow;
        if (Field(elements, "J", 3) is { } d && Field(elements, "J", 4) is { } t &&
            DateTime.TryParseExact(d + t, "ddMMMHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when))
            processed = new DateTimeOffset(when, TimeSpan.Zero);
        var (flight, date, dest, _) = ParseFlight(elements, required: true);
        var plate = ParsePlate(elements);
        return new Bpm(raw, station, scanPoint, processed, flight, date, dest, plate, Field(elements, "U", 0));
    }

    private static Bum ParseBum(string raw, List<(string Key, string[] Fields)> elements)
    {
        var station = Field(elements, "V", 0) is { Length: > 2 } v ? v[2..] : "";
        var (flight, date, dest, _) = ParseFlight(elements, required: true);
        return new Bum(raw, station, flight, date, dest, ParsePlate(elements), Field(elements, "X", 0));
    }

    private static (string Flight, string Date, string Destination, CabinClass Class) ParseFlight(List<(string Key, string[] Fields)> elements, bool required)
    {
        var flight = Field(elements, "F", 0);
        if (flight == null && required) throw new TypeBParseException("Missing .F/ outbound flight.");
        var cls = Field(elements, "F", 3) switch
        {
            "J" or "C" => CabinClass.Business,
            "W" or "P" => CabinClass.SkyPriority,
            _ => CabinClass.Economy,
        };
        return (flight ?? "", Field(elements, "F", 1) ?? "", Field(elements, "F", 2) ?? "", cls);
    }

    private static LicencePlate ParsePlate(List<(string Key, string[] Fields)> elements) =>
        Field(elements, "N", 0) is { Length: >= 10 } n && LicencePlate.TryParse(n[..10], out var lp)
            ? lp
            : throw new TypeBParseException("Missing or bad .N/ licence plate.");

    private static string? Field(List<(string Key, string[] Fields)> elements, string key, int index)
    {
        foreach (var (k, fields) in elements)
        {
            if (k == key)
                return index < fields.Length && fields[index].Length > 0 ? fields[index] : null;
        }
        return null;
    }
}
