using KQ.Brs.Core.Domain;
using KQ.Brs.Core.TypeB;

namespace KQ.Brs.Tests;

public class TypeBTests
{
    private static readonly FlightKey Kq504 = new("KQ", 504, new DateOnly(2026, 10, 2));

    // Normalised to \n: with core.autocrlf the file (and so this literal) has \r\n, and the tests edit it by line.
    private static readonly string Bsm = """
        BSM
        .V/1LNBO
        .F/KQ504/02OCT/EBB/Y
        .N/0706100001002
        .W/K/2/38
        .S/Y/23A/C/012
        .P/1MWANGI/J
        ENDBSM
        """.ReplaceLineEndings("\n");

    [Fact]
    public void Parses_a_bsm_with_two_plates()
    {
        var bsm = Assert.IsType<Bsm>(TypeBParser.Parse(Bsm));

        Assert.Equal(BsmAction.Original, bsm.Action);
        Assert.Equal("NBO", bsm.Station);
        Assert.Equal("KQ504", bsm.Flight);
        Assert.Equal("EBB", bsm.Destination);
        Assert.Equal(["0706100001", "0706100002"], bsm.Plates.Select(p => p.Value));
        Assert.Equal(38, bsm.WeightKg);
        Assert.True(bsm.AuthorityToLoad);
        Assert.Equal(PassengerStatus.CheckedIn, bsm.PassengerStatus);
        Assert.Equal("MWANGI", bsm.Surname);
        Assert.Equal("J", bsm.Initial);
    }

    [Fact]
    public void Parses_change_and_delete_variants()
    {
        var chg = Assert.IsType<Bsm>(TypeBParser.Parse(Bsm.Replace("BSM\n", "BSM\nCHG\n").Replace(".S/Y/23A/C/", ".S/N/23A/N/")));
        Assert.Equal(BsmAction.Change, chg.Action);
        Assert.False(chg.AuthorityToLoad);
        Assert.Equal(PassengerStatus.NotBoarded, chg.PassengerStatus);

        var del = Assert.IsType<Bsm>(TypeBParser.Parse(Bsm.Replace("BSM\n", "BSM\nDEL\n")));
        Assert.Equal(BsmAction.Delete, del.Action);
    }

    [Fact]
    public void Builder_and_parser_round_trip_a_bsm()
    {
        var raw = TypeBBuilder.Bsm(BsmAction.Original, "NBO", Kq504, "EBB", CabinClass.Business, new LicencePlate("0706100011"), 2, 40,
            false, PassengerStatus.Boarded, "2A", 5, "Otieno", "r");
        var bsm = Assert.IsType<Bsm>(TypeBParser.Parse(raw));

        Assert.Equal(CabinClass.Business, bsm.Class);
        Assert.Equal(2, bsm.Plates.Count);
        Assert.False(bsm.AuthorityToLoad);
        Assert.Equal(PassengerStatus.Boarded, bsm.PassengerStatus);
        Assert.Equal("OTIENO", bsm.Surname);
    }

    [Fact]
    public void Builder_and_parser_round_trip_a_bpm_with_uld()
    {
        var at = new DateTimeOffset(2026, 10, 2, 10, 45, 12, TimeSpan.Zero);
        var raw = TypeBBuilder.Bpm("NBO", "RAMP", at, Kq504, "EBB", CabinClass.Economy, new LicencePlate("0706100001"), "AK8");
        var bpm = Assert.IsType<Bpm>(TypeBParser.Parse(raw));

        Assert.Contains(".J/S/BRS/RAMP/02OCT/104512", raw);
        Assert.Equal("RAMP", bpm.ScanPoint);
        Assert.Equal("AK8", bpm.Uld);
        Assert.Equal("0706100001", bpm.Plate.Value);
        Assert.Equal(at, bpm.ProcessedUtc);
    }

    [Fact]
    public void Builder_and_parser_round_trip_a_bum()
    {
        var raw = TypeBBuilder.Bum("NBO", Kq504, "EBB", new LicencePlate("0706100001"), "PAX NOT BOARDED");
        var bum = Assert.IsType<Bum>(TypeBParser.Parse(raw));
        Assert.Equal("PAX NOT BOARDED", bum.Reason);
    }

    [Fact]
    public void Rejects_a_bsm_without_a_plate() =>
        Assert.Throws<TypeBParseException>(() => TypeBParser.Parse("BSM\n.F/KQ504/02OCT/EBB/Y\nENDBSM"));
}
