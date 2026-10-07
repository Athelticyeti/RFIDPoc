using System.Globalization;
using System.Text;
using KQ.Brs.Core.Domain;

namespace KQ.Brs.Core.TypeB;

/// <summary>Builds RP 1745-style baggage messages (POC subset; the inverse of <see cref="TypeBParser"/>).</summary>
public static class TypeBBuilder
{
    public static string Bsm(BsmAction action, string station, FlightKey flight, string destination, CabinClass cls,
        LicencePlate firstPlate, int count, int weightKg, bool authorityToLoad, PassengerStatus status,
        string seat, int sequence, string surname, string initial)
    {
        var sb = new StringBuilder("BSM\n");
        if (action == BsmAction.Change) sb.Append("CHG\n");
        if (action == BsmAction.Delete) sb.Append("DEL\n");
        sb.Append(CultureInfo.InvariantCulture, $".V/1L{station}\n");
        sb.Append(CultureInfo.InvariantCulture, $".F/{flight.Designator}/{flight.TypeBDate}/{destination}/{ClassCode(cls)}\n");
        sb.Append(CultureInfo.InvariantCulture, $".N/{firstPlate.Value}{count:D3}\n");
        sb.Append(CultureInfo.InvariantCulture, $".W/K/{count}/{weightKg}\n");
        sb.Append(CultureInfo.InvariantCulture, $".S/{(authorityToLoad ? 'Y' : 'N')}/{seat}/{StatusCode(status)}/{sequence:D3}\n");
        sb.Append(CultureInfo.InvariantCulture, $".P/1{surname.ToUpperInvariant()}/{initial.ToUpperInvariant()}\n");
        sb.Append("ENDBSM");
        return sb.ToString();
    }

    /// <summary>BPM for a scan at a processing point; ULD only for loads.</summary>
    public static string Bpm(string station, string scanPoint, DateTimeOffset processedUtc, FlightKey flight,
        string destination, CabinClass cls, LicencePlate plate, string? uld)
    {
        var t = processedUtc.UtcDateTime;
        var sb = new StringBuilder("BPM\n");
        sb.Append(CultureInfo.InvariantCulture, $".V/1L{station}\n");
        sb.Append(CultureInfo.InvariantCulture, $".J/S/BRS/{scanPoint}/{t.ToString("ddMMM", CultureInfo.InvariantCulture).ToUpperInvariant()}/{t:HHmmss}\n");
        sb.Append(CultureInfo.InvariantCulture, $".F/{flight.Designator}/{flight.TypeBDate}/{destination}/{ClassCode(cls)}\n");
        sb.Append(CultureInfo.InvariantCulture, $".N/{plate.Value}\n");
        if (uld != null) sb.Append(CultureInfo.InvariantCulture, $".U/{uld}\n");
        sb.Append("ENDBPM");
        return sb.ToString();
    }

    public static string Bum(string station, FlightKey flight, string destination, LicencePlate plate, string reason)
    {
        var sb = new StringBuilder("BUM\n");
        sb.Append(CultureInfo.InvariantCulture, $".V/1L{station}\n");
        sb.Append(CultureInfo.InvariantCulture, $".F/{flight.Designator}/{flight.TypeBDate}/{destination}\n");
        sb.Append(CultureInfo.InvariantCulture, $".N/{plate.Value}\n");
        sb.Append(CultureInfo.InvariantCulture, $".X/{reason}\n");
        sb.Append("ENDBUM");
        return sb.ToString();
    }

    private static char ClassCode(CabinClass cls) => cls switch
    {
        CabinClass.Business => 'J',
        CabinClass.SkyPriority => 'W',
        _ => 'Y',
    };

    private static char StatusCode(PassengerStatus status) => status switch
    {
        PassengerStatus.Boarded => 'B',
        PassengerStatus.NotBoarded => 'N',
        _ => 'C',
    };
}
