using System.Net;
using System.Net.Sockets;
using System.Text;
using KQ.Rfid.Alien;

namespace KQ.Brs.Tests;

public class ReaderLibraryTests
{
    private const string SampleLine = "id:E2003009281101450600D744 t:1790993096903 TIME2:19:04:56.903 a:2 vel:0.001 sig:11969.3 freq:865.700";

    [Fact]
    public void Parses_a_tag_stream_line()
    {
        var read = AlienTagStream.ParseLine(SampleLine, "R1", DateTimeOffset.UnixEpoch);

        Assert.NotNull(read);
        Assert.Equal("E2003009281101450600D744", read.Epc);
        Assert.Equal(2, read.Antenna);
        Assert.Equal(11969.3, read.Rssi);
        Assert.Equal(865.7, read.FrequencyMHz!.Value, 3);
        Assert.Equal(DateTimeOffset.UnixEpoch, read.LastSeenUtc); // PC time, not the reader's t: field
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage without fields")]
    [InlineData("t:123 a:1")]
    public void Ignores_lines_without_an_id(string line) =>
        Assert.Null(AlienTagStream.ParseLine(line, "R1", DateTimeOffset.UtcNow));

    [Fact]
    public void Keeps_a_partial_line_for_the_next_chunk()
    {
        using var listener = new TagStreamListener(IPAddress.Loopback, 0, "R1");
        var pending = new StringBuilder(SampleLine + "\r\n" + SampleLine[..20]);

        var first = listener.TakeCompleteLines(pending, DateTimeOffset.UtcNow);
        Assert.Single(first);
        Assert.Equal(SampleLine[..20], pending.ToString());

        pending.Append(SampleLine[20..] + "\r\n");
        Assert.Single(listener.TakeCompleteLines(pending, DateTimeOffset.UtcNow));
        Assert.Equal("", pending.ToString());
    }

    [Fact]
    public async Task Listener_accepts_the_reader_reconnecting()
    {
        using var listener = new TagStreamListener(IPAddress.Loopback, 0, "R1");
        var received = new List<TagRead>();
        var got = new SemaphoreSlim(0);
        listener.ReadsReceived += reads => { lock (received) received.AddRange(reads); got.Release(); };
        listener.Start();

        for (var i = 0; i < 2; i++)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, listener.LocalEndPoint.Port);
            await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(SampleLine + "\r\n"));
            Assert.True(await got.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        Assert.Equal(2, received.Count);
        Assert.Equal(2, listener.Connections);
    }

    [Fact]
    public async Task Command_client_logs_in_and_parses_replies()
    {
        await using var server = await FakeAlienServer.StartAsync();
        await using var client = new AlienCommandClient();

        await client.ConnectAsync("127.0.0.1", server.Port, "alien", "password", CancellationToken.None);

        Assert.Equal("Time-E RFID Reader 1", await client.GetAsync("ReaderName", CancellationToken.None));
        var error = await Assert.ThrowsAsync<AlienCommandException>(() => client.GetAsync("Nonsense", CancellationToken.None));
        Assert.Contains("Error 1", error.Message);
        await client.SetAsync("AutoMode", "ON", CancellationToken.None);
        Assert.Contains("\x01set AutoMode = ON", server.Received);
    }

    [Fact]
    public async Task Command_client_rejects_a_bad_login()
    {
        await using var server = await FakeAlienServer.StartAsync();
        await using var client = new AlienCommandClient();

        await Assert.ThrowsAsync<AlienCommandException>(() =>
            client.ConnectAsync("127.0.0.1", server.Port, "alien", "wrong", CancellationToken.None));
    }

    [Fact]
    public void Parses_a_reader_heartbeat()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <Alien-RFID-Reader-Heartbeat>
              <ReaderName>Time-E RFID Reader 1</ReaderName>
              <ReaderType>Alien RFID Tag Reader, Model: ALR-9900+EMA</ReaderType>
              <IPAddress>192.168.0.161</IPAddress>
              <CommandPort>23</CommandPort>
              <MACAddress>00:1B:5F:00:4F:EB</MACAddress>
              <ReaderVersion>13.09.13.00</ReaderVersion>
            </Alien-RFID-Reader-Heartbeat>
            """;
        Assert.True(ReaderDiscovery.TryParse(xml, out var hb));
        Assert.Equal("192.168.0.161", hb.IPAddress);
        Assert.Equal(23, hb.CommandPort);
    }

    [Fact]
    public void Only_one_app_can_hold_the_reader_lock()
    {
        var host = "test-" + Guid.NewGuid().ToString("N");
        using var first = ReaderLock.TryAcquire(host);
        Assert.NotNull(first);
        Assert.Null(ReaderLock.TryAcquire(host));
        first!.Dispose();
        using var again = ReaderLock.TryAcquire(host);
        Assert.NotNull(again);
    }

    /// <summary>Minimal Alien reader: banner, login prompts, '\0'-terminated replies, "Error" for unknown commands.</summary>
    private sealed class FakeAlienServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly StringBuilder _received = new();

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public string Received { get { lock (_received) return _received.ToString(); } }

        public static Task<FakeAlienServer> StartAsync()
        {
            var server = new FakeAlienServer();
            server._listener.Start();
            _ = server.ServeAsync();
            return Task.FromResult(server);
        }

        private async Task ServeAsync()
        {
            using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII);
            async Task Send(string s) => await stream.WriteAsync(Encoding.ASCII.GetBytes(s), _cts.Token);

            await Send("*****\r\n* Alien Technology : RFID Reader\r\n*****\r\n\r\nUsername>\0");
            var user = await reader.ReadLineAsync(_cts.Token);
            await Send($"{user}\r\nPassword>\0");
            var pass = await reader.ReadLineAsync(_cts.Token);
            if (pass != "password")
            {
                await Send("********\r\nError: Invalid username and/or password.\r\nUsername>\0");
                return;
            }
            await Send("********\r\n\r\nAlien>\0");

            while (await reader.ReadLineAsync(_cts.Token) is { } line)
            {
                lock (_received) _received.Append(line).Append('\n');
                var cmd = line.TrimStart('\x01');
                var reply = cmd switch
                {
                    "get ReaderName" => "ReaderName = Time-E RFID Reader 1",
                    _ when cmd.StartsWith("set ", StringComparison.Ordinal) => cmd[4..],
                    _ => "Error 1: Command not understood.",
                };
                await Send(reply + "\r\n\0");
            }
        }

        public ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            return ValueTask.CompletedTask;
        }
    }
}
