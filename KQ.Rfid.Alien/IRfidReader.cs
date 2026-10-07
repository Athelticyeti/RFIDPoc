namespace KQ.Rfid.Alien;

public enum ReaderStatus
{
    Offline,
    Connecting,
    Online,
    Streaming,
}

/// <summary>
/// Reader settings applied by <see cref="IRfidReader.ConfigureAsync"/>. Null values leave the reader's current
/// (tuned) setting alone.
/// </summary>
public sealed record ReaderProfile
{
    public string? AntennaSequence { get; init; }
    public string? AcqG2Session { get; init; }
    public string? PersistTime { get; init; }
}

/// <summary>A reader that streams tag reads (spec 3.3, plus start/stop and status for the POC).</summary>
public interface IRfidReader : IAsyncDisposable
{
    string ReaderId { get; }
    ReaderStatus Status { get; }

    /// <summary>Raised on a background thread when <see cref="Status"/> changes; the string explains why.</summary>
    event Action<ReaderStatus, string?>? StatusChanged;

    Task ConnectAsync(CancellationToken ct);
    Task ConfigureAsync(ReaderProfile profile, CancellationToken ct);
    Task StartStreamingAsync(CancellationToken ct);
    Task StopStreamingAsync(CancellationToken ct);
    Task<string> SendCommandAsync(string command, CancellationToken ct);

    /// <summary>All reads, in arrival order, until the reader is disposed.</summary>
    IAsyncEnumerable<TagRead> ReadsAsync(CancellationToken ct);
}

/// <summary>A tag rewritten by <see cref="AlienAlr9900Reader.WriteSingleTagAsync"/>: its EPC before and after.</summary>
public sealed record TagWriteResult(string OldEpc, string NewEpc);

/// <summary>A tag write that was refused or failed; the message says what to do.</summary>
public sealed class TagWriteException(string message) : Exception(message);
