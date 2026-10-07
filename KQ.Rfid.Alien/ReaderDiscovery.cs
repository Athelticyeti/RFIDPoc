using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace KQ.Rfid.Alien;

public sealed record ReaderHeartbeat(string Name, string Type, string IPAddress, int CommandPort, string MacAddress, string Version);

/// <summary>
/// Finds Alien readers by listening for the XML heartbeat they broadcast on UDP 3988 (every HeartbeatTime, 30 s
/// by default), so the reader's IP doesn't need to be known in advance.
/// </summary>
public static class ReaderDiscovery
{
    public const int HeartbeatPort = 3988;

    public static async IAsyncEnumerable<ReaderHeartbeat> ListenAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var udp = new UdpClient();
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, HeartbeatPort));

        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try { result = await udp.ReceiveAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { yield break; }

            if (TryParse(Encoding.ASCII.GetString(result.Buffer), out var heartbeat))
                yield return heartbeat;
        }
    }

    public static bool TryParse(string xml, out ReaderHeartbeat heartbeat)
    {
        heartbeat = null!;
        try
        {
            var root = XDocument.Parse(xml.Trim('\0', ' ', '\r', '\n')).Root;
            if (root?.Name.LocalName != "Alien-RFID-Reader-Heartbeat") return false;
            string Get(string name) => root.Element(name)?.Value.Trim() ?? "";
            heartbeat = new ReaderHeartbeat(Get("ReaderName"), Get("ReaderType"), Get("IPAddress"),
                int.TryParse(Get("CommandPort"), out var port) ? port : 23, Get("MACAddress"), Get("ReaderVersion"));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
