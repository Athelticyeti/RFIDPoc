using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace KQ.Rfid.Alien;

/// <summary>
/// Replays a CSV of reads ("offsetMs,epc,antenna,rssi" per line) with the original timing, for tests and for
/// demos without hardware (spec 3.3).
/// </summary>
public sealed class SimulatedReader(string readerId, IReadOnlyList<string> csvLines) : IRfidReader
{
    private readonly Channel<TagRead> _reads = Channel.CreateUnbounded<TagRead>();
    private CancellationTokenSource? _replay;

    public string ReaderId { get; } = readerId;
    public ReaderStatus Status { get; private set; } = ReaderStatus.Offline;
    public event Action<ReaderStatus, string?>? StatusChanged;

    public Task ConnectAsync(CancellationToken ct)
    {
        SetStatus(ReaderStatus.Online);
        return Task.CompletedTask;
    }

    public Task ConfigureAsync(ReaderProfile profile, CancellationToken ct) => Task.CompletedTask;

    public Task StartStreamingAsync(CancellationToken ct)
    {
        _replay = new CancellationTokenSource();
        _ = ReplayAsync(_replay.Token);
        SetStatus(ReaderStatus.Streaming);
        return Task.CompletedTask;
    }

    public Task StopStreamingAsync(CancellationToken ct)
    {
        _replay?.Cancel();
        SetStatus(ReaderStatus.Online);
        return Task.CompletedTask;
    }

    public Task<string> SendCommandAsync(string command, CancellationToken ct) => Task.FromResult("OK");

    public async IAsyncEnumerable<TagRead> ReadsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var read in _reads.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return read;
    }

    private async Task ReplayAsync(CancellationToken ct)
    {
        var start = DateTimeOffset.UtcNow;
        try
        {
            foreach (var line in csvLines)
            {
                var parts = line.Split(',');
                if (parts.Length < 3 || !long.TryParse(parts[0], out var offsetMs)) continue;

                var due = start.AddMilliseconds(offsetMs) - DateTimeOffset.UtcNow;
                if (due > TimeSpan.Zero) await Task.Delay(due, ct).ConfigureAwait(false);

                var now = DateTimeOffset.UtcNow;
                double? rssi = parts.Length > 3 && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : null;
                _reads.Writer.TryWrite(new TagRead(ReaderId, TagRead.NormaliseEpc(parts[1]), int.Parse(parts[2], CultureInfo.InvariantCulture), rssi, 1, now, now));
            }
        }
        catch (OperationCanceledException) { }
    }

    private void SetStatus(ReaderStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(status, null);
    }

    public ValueTask DisposeAsync()
    {
        _replay?.Cancel();
        _reads.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
