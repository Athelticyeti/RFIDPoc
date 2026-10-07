using KQ.Brs.Core.Domain;

namespace KQ.Brs.Core;

/// <summary>One antenna's role in the POC: which scan point it simulates.</summary>
public sealed record AntennaAssignment(int Antenna, string ScanPointId, bool Enabled = true, double MinRssi = 0);

/// <summary>FR-05 load rules. Each can be switched off for the demo.</summary>
public sealed record LoadRuleOptions
{
    public bool RequireAuthorityToLoad { get; init; } = true;
    public bool RequirePassengerNotNoShow { get; init; } = true;
    public bool RequireDeskCheckIn { get; init; } = true;

    /// <summary>When off (default), a bag with no belt scan still loads but raises a MissingAtSorter warning.</summary>
    public bool RequireBeltScan { get; init; }
}

/// <summary>Simulator settings for the virtual bags.</summary>
public sealed record SimulatorOptions
{
    public double Speed { get; init; } = 30;
    public int Seed { get; init; } = 504;
    public int PaxNotBoarded { get; init; } = 4;
    public int DeletedBsms { get; init; } = 2;
    public int Misroutes { get; init; } = 2;
    public double MissedBeltReadRate { get; init; } = 0.015;
    public double NoShowAtRampRate { get; init; } = 0.005;
    public int StrayTags { get; init; } = 3;
}

/// <summary>Settings for the BRS core. Immutable; replace the whole record to change settings.</summary>
public sealed record BrsOptions
{
    public string Station { get; init; } = "NBO";
    public string FlightCarrier { get; init; } = "KQ";
    public int FlightNumber { get; init; } = 504;
    public string Destination { get; init; } = "EBB";

    /// <summary>The last N bags of the manifest are kept for real tags (bound at the desk).</summary>
    public int RealTagPoolSize { get; init; } = 50;

    public TimeSpan DedupeWindow { get; init; } = TimeSpan.FromSeconds(3);

    public IReadOnlyList<AntennaAssignment> Antennas { get; init; } =
    [
        new(0, ScanPoint.Desk14),
        new(1, ScanPoint.Belt04),
        new(2, ScanPoint.Belt04),
        new(3, ScanPoint.Ramp),
    ];

    /// <summary>
    /// Antenna set-up for the boardroom POC demo, calibrated on the walk test of 2026-10-06: real reads were
    /// 56,884 (check-in), 5,742 and 22,684 (tunnel) and 2,662 (loading); cross-reads were up to 2,712 at check-in and
    /// 457 in the tunnel. The minimums sit between the two.
    /// </summary>
    public static IReadOnlyList<AntennaAssignment> PocDemoAntennas { get; } =
    [
        new(0, ScanPoint.Desk14, Enabled: true, MinRssi: 10_000),
        new(1, ScanPoint.Belt04, Enabled: true, MinRssi: 1_500),
        new(2, ScanPoint.Belt04, Enabled: true, MinRssi: 1_500),
        new(3, ScanPoint.Ramp, Enabled: true, MinRssi: 800),
    ];

    public LoadRuleOptions LoadRules { get; init; } = new();
    public SimulatorOptions Simulator { get; init; } = new();

    /// <summary>Unknown (unbound) tags at the belt: raise an Unknown exception, or only count them as strays.</summary>
    public bool RaiseUnknownAtBelt { get; init; }

    public FlightKey FlightKey(DateOnly date) => new(FlightCarrier, FlightNumber, date);
}
