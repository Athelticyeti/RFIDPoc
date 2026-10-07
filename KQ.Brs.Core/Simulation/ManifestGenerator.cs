using System.Globalization;
using KQ.Brs.Core.Domain;
using KQ.Brs.Core.TypeB;

namespace KQ.Brs.Core.Simulation;

/// <summary>
/// Generates the KQ-504 manifest as the DCS would send it: one BSM per passenger listing their 2 bags
/// (.N/ with a count), so the flight is created through the real Type B parse path (FR-01).
/// Plates run 0706100001..0706100700. Deterministic for a given seed.
/// </summary>
public static class ManifestGenerator
{
    public const int Passengers = 350;
    public const int BagsPerPassenger = 2;
    public const long FirstPlate = 706100001;

    private static readonly string[] Surnames =
    [
        "MWANGI", "OTIENO", "KAMAU", "WANJIKU", "ODHIAMBO", "NJOROGE", "ACHIENG", "KIPCHOGE", "MUTUA", "WAMBUI",
        "OMONDI", "CHEBET", "KARIUKI", "NYAMBURA", "MUSOKE", "NAKATO", "SSEMPALA", "NAMUBIRU", "OKELLO", "AUMA",
        "KIBET", "JEPKOSGEI", "MAINA", "NJERI", "OCHIENG", "AKINYI", "KIPROP", "WAFULA", "NABWIRE", "MUGISHA",
        "TUMUSIIME", "BYARUGABA", "ATIENO", "KOECH", "RUTO", "MURIUKI", "GITAU", "WEKESA", "MAKENA", "LUKWAGO",
        "SMITH", "PATEL", "OKAFOR", "MENSAH", "DUBOIS", "NAKAYIZA", "KIRABO", "OMOLLO", "SAITOTI", "LEMAYIAN",
    ];

    public static IEnumerable<string> Generate(BrsOptions options, FlightKey flight, int seed)
    {
        var random = new Random(seed);
        // About 1 % of passengers have a ticket problem (authority to load = N). They're kept out of the real-tag
        // pool (the last bags) so physical tags always belong to valid passengers.
        var virtualPax = Passengers - (options.RealTagPoolSize + 1) / BagsPerPassenger;
        var invalid = Enumerable.Range(0, virtualPax).OrderBy(_ => random.Next()).Take(Passengers / 100).ToHashSet();

        for (var pax = 0; pax < Passengers; pax++)
        {
            var cls = pax < 12 ? CabinClass.Business : pax < 32 ? CabinClass.SkyPriority : CabinClass.Economy;
            var plate = new LicencePlate((FirstPlate + pax * BagsPerPassenger).ToString("D10", CultureInfo.InvariantCulture));
            var surname = Surnames[random.Next(Surnames.Length)];
            var initial = ((char)('A' + random.Next(26))).ToString();
            var weight = random.Next(30, 47);
            var seat = $"{(cls == CabinClass.Business ? 1 + pax / 4 : 5 + pax / 6)}{"ABCDEF"[pax % 6]}";

            yield return TypeBBuilder.Bsm(BsmAction.Original, options.Station, flight, options.Destination, cls, plate,
                BagsPerPassenger, weight, authorityToLoad: !invalid.Contains(pax), PassengerStatus.CheckedIn, seat, pax + 1, surname, initial);
        }
    }
}
