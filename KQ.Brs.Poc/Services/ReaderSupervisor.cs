using KQ.Brs.Core.Reconciliation;
using KQ.Brs.Core.Scanning;
using KQ.Rfid.Alien;

namespace KQ.Brs.Poc.Services;

public enum SupervisorState { Disconnected, Connecting, Online, Streaming, Retrying }

/// <summary>Reader health as shown in the title bar and Live reads page.</summary>
public sealed record ReaderHealth(SupervisorState State, string Message, DateTimeOffset? RetryAt = null, string? ReaderName = null,
    string? StreamEndPoint = null, int Reconnects = 0);

/// <summary>
/// Keeps the Alien reader connected and streaming (spec 3.2): connects, configures the tag stream, runs the read
/// pipeline, and when the reader drops (heartbeat failure) reconnects with exponential backoff 1 s → 30 s.
/// Streaming is a desired state that survives reconnects.
/// </summary>
public sealed class ReaderSupervisor(ReadPipeline pipeline, ReconciliationEngine engine, Func<AppSettings> settings) : IAsyncDisposable
{
    private CancellationTokenSource? _run;
    private Task? _loop;
    private AlienAlr9900Reader? _reader;
    private volatile bool _wantStreaming;
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _reconnects;

    public ReaderHealth Health { get; private set; } = new(SupervisorState.Disconnected, "Not connected");

    /// <summary>Raised on a background thread.</summary>
    public event Action<ReaderHealth>? HealthChanged;

    /// <summary>Raised on a background thread for non-fatal problems (stream drops, pipeline errors).</summary>
    public event Action<string>? Warning;

    public AlienAlr9900Reader? Reader => _reader;
    public bool WantStreaming => _wantStreaming;

    public void Connect(bool startStreaming)
    {
        _wantStreaming = startStreaming || _wantStreaming;
        if (_loop is { IsCompleted: false }) { Wake(); return; }
        _run = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_run.Token));
    }

    public async Task DisconnectAsync()
    {
        _wantStreaming = false;
        if (_run == null) return;
        _run.Cancel();
        if (_loop != null) await _loop.ConfigureAwait(false);
        _run = null;
        SetHealth(new(SupervisorState.Disconnected, "Not connected"));
    }

    public async Task SetStreamingAsync(bool on)
    {
        _wantStreaming = on;
        var reader = _reader;
        if (reader == null) { if (on) Connect(true); return; }
        try
        {
            if (on) await reader.StartStreamingAsync(CancellationToken.None).ConfigureAwait(false);
            else await reader.StopStreamingAsync(CancellationToken.None).ConfigureAwait(false);
            SetHealth(Health with { State = on ? SupervisorState.Streaming : SupervisorState.Online, Message = on ? "Streaming tag reads" : "Connected (idle)" });
        }
        catch (Exception ex)
        {
            Warning?.Invoke(ex.Message);
            if (on) _wantStreaming = false;
        }
    }

    private void Wake() => _wake.TrySetResult();

    private async Task LoopAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            var s = settings();
            var reader = new AlienAlr9900Reader(new AlienReaderOptions
            {
                Host = s.ReaderHost, CommandPort = s.ReaderPort, Username = s.ReaderUser, Password = s.ReaderPassword, StreamPort = s.StreamPort,
            });
            var lost = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            reader.StatusChanged += (status, reason) => { if (status == ReaderStatus.Offline) lost.TrySetResult(reason); };
            reader.Warning += msg => Warning?.Invoke(msg);

            using var pipelineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task? pipelineTask = null;
            try
            {
                SetHealth(new(SupervisorState.Connecting, $"Connecting to {s.ReaderHost}…", Reconnects: _reconnects));
                await reader.ConnectAsync(ct).ConfigureAwait(false);
                await reader.ConfigureAsync(new ReaderProfile(), ct).ConfigureAwait(false);
                _reader = reader;
                engine.LogReaderStatus("Online", reader.ReaderName);
                pipelineTask = pipeline.RunAsync(reader, pipelineCts.Token);

                if (_wantStreaming)
                {
                    await reader.StartStreamingAsync(ct).ConfigureAwait(false);
                    engine.LogReaderStatus("Streaming", null);
                }
                SetHealth(new(_wantStreaming ? SupervisorState.Streaming : SupervisorState.Online,
                    _wantStreaming ? "Streaming tag reads" : "Connected (idle)", null, reader.ReaderName,
                    reader.StreamEndPoint?.ToString(), _reconnects));
                delay = TimeSpan.FromSeconds(1);

                // Run until the heartbeat says the reader is gone, or we're asked to stop.
                var reason = await lost.Task.WaitAsync(ct).ConfigureAwait(false);
                engine.LogReaderStatus("Offline", reason);
                Warning?.Invoke($"Reader offline: {reason}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                engine.LogReaderStatus("Offline", ex.Message);
                SetHealth(new(SupervisorState.Retrying, ex.Message, Reconnects: _reconnects));
            }
            finally
            {
                _reader = null;
                pipelineCts.Cancel();
                if (pipelineTask != null) { try { await pipelineTask.ConfigureAwait(false); } catch { } }
                await reader.DisposeAsync().ConfigureAwait(false);   // AutoMode/TagStreamMode OFF, lock released
            }

            if (ct.IsCancellationRequested) break;

            // Back off 1, 2, 4 … 30 s (±10 % jitter), unless woken by a Connect click.
            var wait = delay * (0.9 + Random.Shared.NextDouble() * 0.2);
            _reconnects++;
            SetHealth(new(SupervisorState.Retrying, $"Reader offline - retrying in {wait.TotalSeconds:F0} s", DateTimeOffset.UtcNow + wait, Reconnects: _reconnects));
            _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try { await Task.WhenAny(Task.Delay(wait, ct), _wake.Task).ConfigureAwait(false); } catch { }
            delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
        }
    }

    private void SetHealth(ReaderHealth health)
    {
        Health = health;
        HealthChanged?.Invoke(health);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
        }
        catch { }
    }
}
