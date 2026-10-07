using System.Globalization;

namespace KQ.Brs.Core.Domain;

/// <summary>10-digit IATA licence plate (e.g. 0706100001: 0 + KQ numeric code 706 + serial).</summary>
public readonly record struct LicencePlate
{
    public LicencePlate(string value)
    {
        if (value is not { Length: 10 } || !value.All(char.IsAsciiDigit))
            throw new ArgumentException($"Licence plate must be 10 digits: '{value}'.", nameof(value));
        Value = value;
    }

    public string Value { get; }

    public static bool TryParse(string? text, out LicencePlate plate)
    {
        plate = default;
        var trimmed = text?.Trim();
        if (trimmed is not { Length: 10 } || !trimmed.All(char.IsAsciiDigit)) return false;
        plate = new LicencePlate(trimmed);
        return true;
    }

    public override string ToString() => Value;
}

/// <summary>Carrier + flight number + date (spec 5).</summary>
public sealed record FlightKey(string Carrier, int Number, DateOnly Date)
{
    public string Designator => $"{Carrier}{Number}";

    /// <summary>Type B date, e.g. 02OCT.</summary>
    public string TypeBDate => Date.ToString("ddMMM", CultureInfo.InvariantCulture).ToUpperInvariant();

    public override string ToString() => $"{Carrier}-{Number}";
}
