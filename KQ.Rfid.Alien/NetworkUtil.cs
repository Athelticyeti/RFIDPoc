using System.Net;
using System.Net.Sockets;

namespace KQ.Rfid.Alien;

public static class NetworkUtil
{
    /// <summary>
    /// The local IP the reader can reach us on: the address of the adapter Windows routes to the reader through.
    /// Needed because this PC has two adapters on the reader's subnet. No packets are sent.
    /// </summary>
    public static IPAddress LocalAddressFacing(IPAddress remote)
    {
        using var probe = new Socket(remote.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        probe.Connect(remote, 23);
        return ((IPEndPoint)probe.LocalEndPoint!).Address;
    }
}
