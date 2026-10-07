using KQ.Brs.Core.Domain;

namespace KQ.Brs.Core.Reconciliation;

/// <summary>
/// POC licence-plate encoding for a 96-bit EPC (spec 3.4.2, ILicencePlateCodec): "BA60" + 10-digit plate + zero
/// padding, 24 hex characters, readable in a raw read (BA60 0706 2000 0100 0000 0000). Written onto real tags at
/// check-in, and used by the simulator for virtual bags. Replace Encode/TryDecode with IATA RP 1740c once the tag
/// supplier confirms the layout (spec 9.4); nothing else depends on the format.
/// </summary>
public static class LicencePlateCodec
{
    private const string Prefix = "BA60";

    public static string Encode(LicencePlate plate) => Prefix + plate.Value + "0000000000";

    public static bool TryDecode(string epc, out LicencePlate plate)
    {
        plate = default;
        return epc.Length == 24 && epc.StartsWith(Prefix, StringComparison.Ordinal) && LicencePlate.TryParse(epc[4..14], out plate);
    }
}

/// <summary>EPCs for virtual bags: the same encoding as real tags written at check-in.</summary>
public static class SyntheticEpc
{
    public static string For(LicencePlate plate) => LicencePlateCodec.Encode(plate);

    public static bool TryDecode(string epc, out LicencePlate plate) => LicencePlateCodec.TryDecode(epc, out plate);
}
