using System.Net;
using System.Net.Sockets;
using System.Text;

namespace KQ.Rfid.Alien;

/// <summary>
/// TCP listener for the reader's Tag Stream: the reader connects to us and sends one line per tag read as soon
/// as the read happens. The reader may drop and reconnect (TagStreamKeepAliveTime), so connections are accepted
/// in a loop.
/// </summary>
public sealed class TagStreamListener : IDisposable
{
    private readonly TcpListener _listener;
    private readonly string _readerId;
    private readonly CancellationTokenSource _cts = new();
    private int _connections;

    /// <summary>Raised on a background thread with the reads parsed from each received chunk.</summary>
    public event Action<TagRead[]>? ReadsReceived;

    /// <summary>Raised on a background thread for connection problems.</summary>
    public event Action<string>? Error;

    public TagStreamListener(IPAddress localAddress, int port, string readerId)
    {
        _listener = new TcpListener(localAddress, port);
        _readerId = readerId;
    }

    public IPEndPoint LocalEndPoint => (IPEndPoint)_listener.LocalEndpoint;

    /// <summary>Number of times the reader has connected to the stream.</summary>
    public int Connections => _connections;

    public void Start()
    {
        _listener.Start();
        _ = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                Interlocked.Increment(ref _connections);
                _ = ReadLoopAsync(client, token);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) when (token.IsCancellationRequested) { return; }
            catch (Exception ex) { Error?.Invoke($"Tag stream accept failed: {ex.Message}"); }
        }
    }

    private async Task ReadLoopAsync(TcpClient client, CancellationToken token)
    {
        using var _ = client;
        var buffer = new byte[8192];
        var pending = new StringBuilder();
        try
        {
            var stream = client.GetStream();
            int n;
            while ((n = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                var received = DateTimeOffset.UtcNow;
                pending.Append(Encoding.ASCII.GetString(buffer, 0, n));
                var reads = TakeCompleteLines(pending, received);
                if (reads.Length > 0)
                    ReadsReceived?.Invoke(reads);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            Error?.Invoke($"Tag stream connection dropped: {ex.Message}");
        }
    }

    /// <summary>Parses complete lines out of the buffer, leaving a partial last line for the next chunk.</summary>
    internal TagRead[] TakeCompleteLines(StringBuilder pending, DateTimeOffset receivedUtc)
    {
        var text = pending.ToString();
        var lastBreak = text.LastIndexOf('\n');
        if (lastBreak < 0) return [];

        pending.Clear().Append(text, lastBreak + 1, text.Length - lastBreak - 1);
        var reads = new List<TagRead>();
        foreach (var line in text.AsSpan(0, lastBreak).ToString().Split('\n'))
        {
            if (AlienTagStream.ParseLine(line, _readerId, receivedUtc) is { } read)
                reads.Add(read);
        }
        return [.. reads];
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
    }
}
