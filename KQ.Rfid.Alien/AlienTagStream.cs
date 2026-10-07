using System.Globalization;

namespace KQ.Rfid.Alien;

/// <summary>
/// Format and parser for the reader's Tag Stream (TagStreamFormat = Custom), one read per line.
/// </summary>
public static class AlienTagStream
{
    /// <summary>
    /// Key:value fields. %k = EPC without spaces, %a = antenna, RSSI = raw signal, MSEC2 = epoch ms (depends on the
    /// reader's TimeZone, so unused), SPEED = m/s, FREQ = MHz.
    /// </summary>
    public const string CustomFormat = "id:%k t:${MSEC2} TIME2:${TIME2} a:%a vel:${SPEED} sig:${RSSI} freq:${FREQ}";

    /// <summary>
    /// Parses "id:E200... t:1790993096903 TIME2:19:04:56.903 a:0 vel:0.001 sig:11969.3 freq:865.700".
    /// Returns null for lines without an id (blank lines, keep-alives, garbage).
    /// </summary>
    public static TagRead? ParseLine(string line, string readerId, DateTimeOffset receivedUtc)
    {
        string? id = null;
        int antenna = 0;
        double? rssi = null, speed = null, freq = null;

        foreach (var part in line.Trim('\0', '\r', '\n', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = part.IndexOf(':');
            if (colon <= 0) continue;
            var key = part[..colon];
            var value = part[(colon + 1)..];

            switch (key)
            {
                case "id": id = value; break;
                case "a": int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out antenna); break;
                case "sig": rssi = ParseDouble(value); break;
                case "vel": speed = ParseDouble(value); break;
                case "freq": freq = ParseDouble(value); break;
            }
        }

        if (string.IsNullOrEmpty(id))
            return null;

        return new TagRead(readerId, TagRead.NormaliseEpc(id), antenna, rssi, 1, receivedUtc, receivedUtc, speed, freq);
    }

    private static double? ParseDouble(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
}
