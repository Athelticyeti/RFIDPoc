using System.Net.Sockets;
using System.Text;

namespace KQ.Rfid.Alien;

public sealed class AlienCommandException(string message) : Exception(message);

/// <summary>
/// Alien Reader Protocol over the Telnet command port (spec 3.2), without the legacy SDK.
/// Login answers the "Username>" / "Password>" prompts; after that every command is sent as "\x01" + command + "\r\n"
/// (machine mode: no echo, no prompt) and the reply ends with '\0'. Only ordinal string operations are used
/// (the legacy SDK's culture-sensitive IndexOf("\0") breaks on .NET 5+).
/// </summary>
public sealed class AlienCommandClient : IAsyncDisposable
{
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly StringBuilder _buffer = new();
    private readonly byte[] _readBuffer = new byte[8192];
    private TcpClient? _client;
    private NetworkStream? _stream;

    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public bool IsConnected => _client?.Connected == true;

    public async Task ConnectAsync(string host, int port, string username, string password, CancellationToken ct)
    {
        await DisposeConnectionAsync().ConfigureAwait(false);
        _buffer.Clear();

        var client = new TcpClient { NoDelay = true };
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(LoginTimeout);
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
        }
        _client = client;
        _stream = client.GetStream();

        await ReadUntilAsync("Username>", LoginTimeout, ct).ConfigureAwait(false);
        await WriteAsync(username + "\r\n", ct).ConfigureAwait(false);
        await ReadUntilAsync("Password>", LoginTimeout, ct).ConfigureAwait(false);
        await WriteAsync(password + "\r\n", ct).ConfigureAwait(false);

        // Success ends at the "Alien>" prompt; a bad login says "Invalid" and asks for the username again.
        var reply = await ReadUntilAnyAsync(["Alien>", "Username>", "Invalid", "Error"], LoginTimeout, ct).ConfigureAwait(false);
        if (!reply.Contains("Alien>", StringComparison.Ordinal))
        {
            await DisposeConnectionAsync().ConfigureAwait(false);
            throw new AlienCommandException("Login failed (check username/password).");
        }

        // Drop anything after the prompt (e.g. its '\0') so the first command's reply starts clean.
        await Task.Delay(100, ct).ConfigureAwait(false);
        DrainAvailable();
        _buffer.Clear();
    }

    /// <summary>Sends a command and returns the reply without its terminator. Throws if the reader reports an error.</summary>
    public async Task<string> SendCommandAsync(string command, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_stream == null) throw new AlienCommandException("Not connected.");
            _buffer.Clear();
            await WriteAsync("\x01" + command + "\r\n", ct).ConfigureAwait(false);
            var reply = await ReadUntilTerminatorAsync(CommandTimeout, ct).ConfigureAwait(false);
            if (reply.Contains("Error", StringComparison.Ordinal))
                throw new AlienCommandException($"{command}: {reply}");
            return reply;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>"get Name" → the value after " = ".</summary>
    public async Task<string> GetAsync(string name, CancellationToken ct)
    {
        var reply = await SendCommandAsync("get " + name, ct).ConfigureAwait(false);
        var eq = reply.IndexOf('=');
        return eq >= 0 ? reply[(eq + 1)..].Trim() : reply;
    }

    public Task<string> SetAsync(string name, string value, CancellationToken ct) =>
        SendCommandAsync($"set {name} = {value}", ct);

    private async Task WriteAsync(string text, CancellationToken ct)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        await _stream!.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    private async Task<string> ReadUntilTerminatorAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            while (true)
            {
                var text = _buffer.ToString();
                var end = text.IndexOf('\0');
                if (end >= 0)
                {
                    _buffer.Remove(0, end + 1);
                    return text[..end].Trim();
                }
                await ReadChunkAsync(cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AlienCommandException("Reader did not reply in time.");
        }
    }

    private Task<string> ReadUntilAsync(string marker, TimeSpan timeout, CancellationToken ct) =>
        ReadUntilAnyAsync([marker], timeout, ct);

    private async Task<string> ReadUntilAnyAsync(string[] markers, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            while (true)
            {
                var text = _buffer.ToString();
                foreach (var marker in markers)
                {
                    var at = text.IndexOf(marker, StringComparison.Ordinal);
                    if (at >= 0)
                    {
                        _buffer.Remove(0, at + marker.Length);
                        return text[..(at + marker.Length)];
                    }
                }
                await ReadChunkAsync(cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AlienCommandException($"Timed out waiting for '{string.Join("' or '", markers)}' from the reader.");
        }
    }

    private async Task ReadChunkAsync(CancellationToken ct)
    {
        var n = await _stream!.ReadAsync(_readBuffer, ct).ConfigureAwait(false);
        if (n == 0)
            throw new AlienCommandException("Reader closed the connection.");
        _buffer.Append(Encoding.ASCII.GetString(_readBuffer, 0, n));
    }

    private void DrainAvailable()
    {
        while (_client?.Available > 0)
        {
            if (_stream!.Read(_readBuffer, 0, Math.Min(_readBuffer.Length, _client.Available)) == 0)
                break;
        }
    }

    private async Task DisposeConnectionAsync()
    {
        if (_stream != null)
            await _stream.DisposeAsync().ConfigureAwait(false);
        _client?.Dispose();
        _stream = null;
        _client = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_stream != null)
        {
            // Polite logout; ignore failures (the connection may already be gone).
            try { await WriteAsync("\x01quit\r\n", CancellationToken.None).ConfigureAwait(false); } catch { }
        }
        await DisposeConnectionAsync().ConfigureAwait(false);
    }
}
