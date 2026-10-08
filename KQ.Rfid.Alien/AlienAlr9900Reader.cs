using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace KQ.Rfid.Alien;

public sealed record AlienReaderOptions
{
    public required string Host { get; init; }
    public int CommandPort { get; init; } = 23;
    public string Username { get; init; } = "alien";
    public string Password { get; init; } = "password";
    public int StreamPort { get; init; } = 4000;

    /// <summary>"get ReaderName" interval; must be well under the reader's NetworkTimeout (90 s).</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Alien ALR-9900+ (spec 3.3): a command channel on the Telnet port plus a Tag Stream listener. The reader runs in
/// autonomous mode and pushes every read to us over TCP; we never poll.
/// </summary>
public sealed class AlienAlr9900Reader : IRfidReader
{
    private readonly AlienReaderOptions _options;
    private readonly AlienCommandClient _commands = new();
    private readonly Channel<TagRead> _reads = Channel.CreateBounded<TagRead>(
        new BoundedChannelOptions(20_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly CancellationTokenSource _lifetime = new();
    private TagStreamListener? _listener;
    private ReaderLock? _lock;
    private Task? _heartbeat;
    private long _readCount;

    public AlienAlr9900Reader(AlienReaderOptions options)
    {
        _options = options;
        ReaderId = $"ALR9900-{options.Host}";
    }

    public string ReaderId { get; }
    public ReaderStatus Status { get; private set; } = ReaderStatus.Offline;
    public event Action<ReaderStatus, string?>? StatusChanged;

    /// <summary>Where the reader sends its stream (this PC's reader-facing IP), once connected.</summary>
    public IPEndPoint? StreamEndPoint { get; private set; }

    public string? ReaderName { get; private set; }
    public long ReadCount => Interlocked.Read(ref _readCount);
    public int StreamConnections => _listener?.Connections ?? 0;

    /// <summary>Raised on a background thread for stream/heartbeat problems that don't change status.</summary>
    public event Action<string>? Warning;

    public async Task ConnectAsync(CancellationToken ct)
    {
        SetStatus(ReaderStatus.Connecting, null);
        try
        {
            await _commands.ConnectAsync(_options.Host, _options.CommandPort, _options.Username, _options.Password, ct).ConfigureAwait(false);
            ReaderName = await _commands.GetAsync("ReaderName", ct).ConfigureAwait(false);

            // If a previous session crashed while streaming, the reader is still in autonomous mode.
            await _commands.SetAsync("AutoMode", "OFF", ct).ConfigureAwait(false);

            var local = NetworkUtil.LocalAddressFacing(IPAddress.Parse(_options.Host));
            StreamEndPoint = new IPEndPoint(local, _options.StreamPort);
            _heartbeat ??= HeartbeatLoopAsync(_lifetime.Token);
            SetStatus(ReaderStatus.Online, null);
        }
        catch (Exception ex)
        {
            SetStatus(ReaderStatus.Offline, ex.Message);
            throw;
        }
    }

    public async Task ConfigureAsync(ReaderProfile profile, CancellationToken ct)
    {
        if (StreamEndPoint == null) throw new InvalidOperationException("Connect first.");

        await _commands.SetAsync("NotifyMode", "OFF", ct).ConfigureAwait(false);
        await _commands.SetAsync("TagStreamFormat", "Custom", ct).ConfigureAwait(false);
        await _commands.SetAsync("TagStreamCustomFormat", AlienTagStream.CustomFormat, ct).ConfigureAwait(false);
        await _commands.SetAsync("TagStreamAddress", StreamEndPoint.ToString(), ct).ConfigureAwait(false);
        if (profile.AntennaSequence is { } seq) await _commands.SetAsync("AntennaSequence", seq, ct).ConfigureAwait(false);
        if (profile.AcqG2Session is { } session) await _commands.SetAsync("AcqG2Session", session, ct).ConfigureAwait(false);
        if (profile.PersistTime is { } persist) await _commands.SetAsync("PersistTime", persist, ct).ConfigureAwait(false);
    }

    public async Task StartStreamingAsync(CancellationToken ct)
    {
        if (StreamEndPoint == null) throw new InvalidOperationException("Connect first.");

        _lock ??= ReaderLock.TryAcquire(_options.Host)
                  ?? throw new InvalidOperationException("Another app is already streaming from this reader. Stop it there first.");

        if (_listener == null)
        {
            var listener = new TagStreamListener(StreamEndPoint.Address, StreamEndPoint.Port, ReaderId);
            listener.ReadsReceived += OnReads;
            listener.Error += msg => Warning?.Invoke(msg);
            listener.Start();
            _listener = listener;
        }

        await _commands.SetAsync("TagStreamMode", "ON", ct).ConfigureAwait(false);
        await _commands.SetAsync("AutoMode", "ON", ct).ConfigureAwait(false);
        SetStatus(ReaderStatus.Streaming, null);
    }

    public async Task StopStreamingAsync(CancellationToken ct)
    {
        try
        {
            if (_commands.IsConnected)
            {
                await _commands.SetAsync("AutoMode", "OFF", ct).ConfigureAwait(false);
                await _commands.SetAsync("TagStreamMode", "OFF", ct).ConfigureAwait(false);
            }
        }
        finally
        {
            StopListener();
            if (Status == ReaderStatus.Streaming)
                SetStatus(ReaderStatus.Online, null);
        }
    }

    public Task<string> SendCommandAsync(string command, CancellationToken ct) => _commands.SendCommandAsync(command, ct);

    /// <summary>The ALR-9900's largest RFAttenuation (15 dB below full power).</summary>
    public const int MaxRfAttenuation = 150;

    /// <summary>Transmit power reduction for all antennas together, in tenths of a dB (0 = full power).</summary>
    public async Task<int> GetRfAttenuationAsync(CancellationToken ct) =>
        int.Parse(await _commands.GetAsync("RFAttenuation", ct).ConfigureAwait(false), CultureInfo.InvariantCulture);

    public Task SetRfAttenuationAsync(int tenthsDb, CancellationToken ct) =>
        _commands.SetAsync("RFAttenuation", Math.Clamp(tenthsDb, 0, MaxRfAttenuation).ToString(CultureInfo.InvariantCulture), ct);

    /// <summary>
    /// Writes a new 96-bit EPC (24 hex characters) to the single tag in front of <paramref name="antenna"/>.
    /// Streaming is paused while it runs. It refuses unless exactly one tag is in that antenna's field, because the
    /// reader would otherwise write whichever tag it singles out. The write is verified by reading the tag back.
    /// </summary>
    /// <param name="vet">Called with the tag found in the field, before anything is written. Return a message to refuse
    /// (e.g. the tag already belongs to another bag), or null to go ahead.</param>
    public async Task<TagWriteResult> WriteSingleTagAsync(int antenna, string newEpc, CancellationToken ct,
        Func<string, Task<string?>>? vet = null)
    {
        if (newEpc.Length != 24 || !newEpc.All(Uri.IsHexDigit))
            throw new ArgumentException("The new EPC must be 24 hex characters (96 bits).", nameof(newEpc));
        newEpc = newEpc.ToUpperInvariant();

        var wasStreaming = Status == ReaderStatus.Streaming;
        var sequence = await _commands.GetAsync("AntennaSequence", ct).ConfigureAwait(false);
        var progAntenna = await _commands.GetAsync("ProgAntenna", ct).ConfigureAwait(false);
        var session = await _commands.GetAsync("AcqG2Session", ct).ConfigureAwait(false);
        try
        {
            if (wasStreaming) await _commands.SetAsync("AutoMode", "OFF", ct).ConfigureAwait(false);
            await _commands.SetAsync("AntennaSequence", antenna.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
            // Tags the stream has just read stay quiet for a moment in session 1; session 0 makes every tag answer now.
            await _commands.SetAsync("AcqG2Session", "0", ct).ConfigureAwait(false);
            // Right after autonomous mode stops, the reader needs a moment before an on-demand inventory sees tags.
            if (wasStreaming) await Task.Delay(600, ct).ConfigureAwait(false);

            var inField = await ReadFieldAsync(rounds: 5, ct).ConfigureAwait(false);
            if (inField.Count != 1)
                throw new TagWriteException(inField.Count == 0
                    ? $"No tag at antenna {antenna}. Hold the bag's tag there and try again."
                    : $"{inField.Count} tags at antenna {antenna}. Hold only this bag's tag there (put the others away) and try again.");

            var oldEpc = inField.First();
            if (vet != null && await vet(oldEpc).ConfigureAwait(false) is { } refusal)
                throw new TagWriteException(refusal);
            if (oldEpc != newEpc)
            {
                await _commands.SetAsync("ProgAntenna", antenna.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
                try
                {
                    await _commands.SendCommandAsync("Program Tag = " + SpacedBytes(newEpc), ct).ConfigureAwait(false);
                }
                catch (AlienCommandException ex)
                {
                    throw new TagWriteException($"The reader could not write the tag: {ex.Message}");
                }
                var after = await ReadFieldAsync(rounds: 3, ct).ConfigureAwait(false);
                if (!after.Contains(newEpc))
                    throw new TagWriteException("The tag did not read back with the new number. Keep it still at the antenna and try again.");
            }
            return new TagWriteResult(oldEpc, newEpc);
        }
        finally
        {
            // Put the reader back exactly as it was, even if the write failed.
            try { await _commands.SetAsync("AntennaSequence", sequence, CancellationToken.None).ConfigureAwait(false); } catch { }
            try { await _commands.SetAsync("ProgAntenna", progAntenna, CancellationToken.None).ConfigureAwait(false); } catch { }
            try { await _commands.SetAsync("AcqG2Session", session, CancellationToken.None).ConfigureAwait(false); } catch { }
            if (wasStreaming)
            {
                try { await _commands.SetAsync("AutoMode", "ON", CancellationToken.None).ConfigureAwait(false); } catch { }
            }
        }
    }

    /// <summary>On-demand inventory of the current antenna sequence (AutoMode must be off): the distinct EPCs seen.</summary>
    private async Task<HashSet<string>> ReadFieldAsync(int rounds, CancellationToken ct)
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < rounds; i++)
        {
            await _commands.SendCommandAsync("clear TagList", ct).ConfigureAwait(false);
            var list = await _commands.SendCommandAsync("get TagList", ct).ConfigureAwait(false);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(list, "<TagID>([^<]+)</TagID>"))
                seen.Add(TagRead.NormaliseEpc(m.Groups[1].Value));
        }
        return seen;
    }

    private static string SpacedBytes(string hex) =>
        string.Join(" ", Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)));

    public async IAsyncEnumerable<TagRead> ReadsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var read in _reads.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return read;
    }

    private void OnReads(TagRead[] reads)
    {
        foreach (var read in reads)
            _reads.Writer.TryWrite(read);
        Interlocked.Add(ref _readCount, reads.Length);
    }

    /// <summary>Keeps the command session alive and detects a lost reader (spec 3.2: get ReaderName every 10 s).</summary>
    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (Status is ReaderStatus.Offline or ReaderStatus.Connecting) continue;
                try
                {
                    await _commands.GetAsync("ReaderName", ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    StopListener();
                    SetStatus(ReaderStatus.Offline, $"Heartbeat failed: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void StopListener()
    {
        if (_listener != null)
        {
            _listener.ReadsReceived -= OnReads;
            _listener.Dispose();
            _listener = null;
        }
        _lock?.Dispose();
        _lock = null;
    }

    private void SetStatus(ReaderStatus status, string? reason)
    {
        if (Status == status && reason == null) return;
        Status = status;
        StatusChanged?.Invoke(status, reason);
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await StopStreamingAsync(timeout.Token).ConfigureAwait(false);
        }
        catch { }
        await _commands.DisposeAsync().ConfigureAwait(false);
        _reads.Writer.TryComplete();
    }
}
