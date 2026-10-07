using KQ.Brs.Core;
using KQ.Brs.Core.Events;
using KQ.Brs.Core.Persistence;
using KQ.Brs.Core.Reconciliation;
using KQ.Brs.Core.Scanning;
using KQ.Brs.Core.Simulation;
using KQ.Brs.Poc.ViewModels;
using Microsoft.UI.Dispatching;

namespace KQ.Brs.Poc.Services;

/// <summary>
/// Composition root: the in-process equivalent of the spec's BRS core, reader gateway and simulator (spec 4.1),
/// wired together for one desktop app.
/// </summary>
public static class AppServices
{
    public static AppSettings Settings { get; private set; } = SettingsStore.Load();
    public static BrsStore Store { get; private set; } = null!;
    public static PersistenceWriter Writer { get; private set; } = null!;
    public static BrsEventHub Hub { get; } = new();
    public static ReconciliationEngine Engine { get; private set; } = null!;
    public static ReadPipeline Pipeline { get; private set; } = null!;
    public static VirtualFlightSimulator Simulator { get; private set; } = null!;
    public static ReaderSupervisor Reader { get; private set; } = null!;
    public static AppState State { get; private set; } = null!;

    public static async Task InitialiseAsync(DispatcherQueue dispatcher)
    {
        State = new AppState(dispatcher);
        Hub.Subscribe(State.Post);

        Store = new BrsStore(Path.Combine(SettingsStore.DataFolder, "brs.db"));
        await Task.Run(Store.EnsureSchema);
        Writer = new PersistenceWriter(Store);
        Writer.Error += msg => dispatcher.TryEnqueue(() => State.ShowToast("Database", msg, isError: true));

        Engine = new ReconciliationEngine(Settings.Brs, Hub, Writer);
        Pipeline = new ReadPipeline(Engine, Writer, () => Settings.Brs);
        Pipeline.Error += msg => dispatcher.TryEnqueue(() => State.ShowToast("Read pipeline", msg, isError: true));

        Simulator = new VirtualFlightSimulator(Engine, Writer, () => Settings.Brs.Simulator);
        Simulator.Changed += () => dispatcher.TryEnqueue(() =>
        {
            State.SimRunning = Simulator.IsRunning;
            State.SimStatus = Simulator.IsRunning || Simulator.Pending > 0
                ? $"Virtual time {Simulator.VirtualTime:hh\\:mm\\:ss} · {Simulator.Pending} steps left"
                : "Simulator idle";
        });
        Simulator.Error += msg => dispatcher.TryEnqueue(() => State.ShowToast("Simulator", msg, isError: true));

        Reader = new ReaderSupervisor(Pipeline, Engine, () => Settings);
        Reader.HealthChanged += State.Post;
        Reader.Warning += msg => dispatcher.TryEnqueue(() => State.ShowToast("Reader", msg, isError: true));

        await LoadFlightAsync();
        if (Settings.ArmLoadingOnStart) await Engine.SetRampArmedAsync(true);
        if (Settings.AutoConnect)
            Reader.Connect(startStreaming: true);
    }

    private static async Task LoadFlightAsync()
    {
        var stored = await Task.Run(Store.Load);
        // An empty flight unless demo bags are switched on: passengers are then added one by one from the Dashboard.
        await Engine.InitialiseAsync(stored, () => Settings.GenerateDemoBags
            ? ManifestGenerator.Generate(Settings.Brs, Engine.Flight, Settings.Brs.Simulator.Seed)
            : []);
        await State.LoadAsync(Engine.SnapshotAsync);
    }

    /// <summary>
    /// Adds a passenger with licence plates that continue from the last one ever issued (kept in settings), so a
    /// tag written before the database was cleaned can never match a new passenger.
    /// </summary>
    public static async Task<IReadOnlyList<string>> AddPassengerAsync(string surname, string initial, KQ.Brs.Core.Domain.CabinClass cls,
        int bags, int weightKg, bool authorityToLoad)
    {
        var plates = await Engine.AddPassengerAsync(surname, initial, cls, bags, weightKg, authorityToLoad, Settings.LastPlateSerial + 1);
        var last = int.Parse(plates[^1][ReconciliationEngine.AddedPassengerPrefix.Length..], System.Globalization.CultureInfo.InvariantCulture);
        if (last > Settings.LastPlateSerial) ApplySettings(Settings with { LastPlateSerial = last });
        return plates;
    }

    /// <summary>
    /// Writes a bag's licence plate onto the single tag held at the check-in antenna (the POC's "print the bag tag"),
    /// then records it. Throws <see cref="KQ.Rfid.Alien.TagWriteException"/> with a user-facing message if it can't.
    /// </summary>
    public static async Task<KQ.Rfid.Alien.TagWriteResult> WriteTagAsync(string plate)
    {
        var reader = Reader.Reader
            ?? throw new KQ.Rfid.Alien.TagWriteException("The reader isn't connected. Connect it on the Live reads page first.");
        var desk = Settings.Brs.Antennas.FirstOrDefault(a => a.ScanPointId == KQ.Brs.Core.Domain.ScanPoint.Desk14 && a.Enabled)
            ?? throw new KQ.Rfid.Alien.TagWriteException("No antenna is set to Check-in · Desk 14 (Settings → Antenna → scan point).");

        var epc = LicencePlateCodec.Encode(new KQ.Brs.Core.Domain.LicencePlate(plate));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        // Never take a tag off another passenger's bag: the tag in the field must be blank/unknown, or this bag's own.
        var result = await reader.WriteSingleTagAsync(desk.Antenna, epc, timeout.Token, async found =>
        {
            var owner = await Engine.TagOwnerAsync(found);
            if (owner == null || owner.Plate == plate) return null;
            await Engine.RecordTagWriteRefusedAsync(owner.Plate, found, plate, reader.ReaderId);   // FR-12 audit
            return $"This tag already belongs to {owner.PassengerName}'s bag {owner.Plate}. Use a new tag for this bag.";
        });
        if (result.OldEpc != result.NewEpc)
        {
            await Engine.RecordTagWrittenAsync(plate, result.OldEpc, result.NewEpc);
            // The tag may still be held at the desk: make its next read count as a fresh check-in scan.
            Pipeline.ResetTag(result.NewEpc);
        }
        return result;
    }

    public static void ApplySettings(AppSettings settings)
    {
        Settings = settings;
        Engine.Options = settings.Brs;
        SettingsStore.Save(settings);
    }

    /// <summary>Wipes the demo (optionally keeping which physical tag belongs to which bag) and rebuilds the flight.</summary>
    public static async Task ResetDemoAsync(bool keepBindings)
    {
        Simulator.Stop();
        await Writer.FlushAsync();
        await Task.Run(() => Store.Reset(keepBindings));
        await LoadFlightAsync();
        // Tags already lying at an antenna are evaluated afresh against the new flight on their next read.
        foreach (var point in KQ.Brs.Core.Domain.ScanPoint.All) Pipeline.ResetScanPoint(point.Id);
    }

    public static async Task ShutdownAsync()
    {
        Simulator.Pause();
        await Reader.DisposeAsync();   // AutoMode/TagStreamMode OFF so the reader stops transmitting
        try { await Writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
    }
}
