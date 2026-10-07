namespace KQ.Rfid.Alien;

/// <summary>
/// One tag read from a reader (spec 3.3). Times are PC time: the reader's own timestamps depend on its
/// TimeZone setting, so they aren't trusted. RSSI is the reader's raw value (e.g. 11969.3), hence double.
/// </summary>
public sealed record TagRead(
    string ReaderId,
    string Epc,
    int Antenna,
    double? Rssi,
    int ReadCount,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc,
    double? Speed = null,
    double? FrequencyMHz = null)
{
    /// <summary>EPCs are compared without spaces, upper case.</summary>
    public static string NormaliseEpc(string epc) => epc.Replace(" ", "", StringComparison.Ordinal).Trim().ToUpperInvariant();
}
