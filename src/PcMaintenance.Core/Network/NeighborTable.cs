using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using PcMaintenance.Core.Common;

namespace PcMaintenance.Core.Network;

/// <summary>A device on the local network that this PC has talked to recently, from the ARP table.</summary>
public sealed record NeighborDevice(IPAddress Address, string Mac, string AdapterName, bool IsStatic, bool IsGateway);

/// <summary>
/// Reads the IPv4 ARP table: only devices this PC exchanged packets with recently, nothing is scanned or sent.
/// Broadcast, multicast and unresolved entries are left out.
/// </summary>
public static class NeighborTable
{
    // MIB_IPNETROW: interface index, MAC length, MAC[8], address, type.
    private const int RowSize = 24;
    private const int TypeInvalid = 2;
    private const int TypeStatic = 4;

    public static IReadOnlyList<NeighborDevice> Read()
    {
        var adapters = Adapters();
        var devices = new List<NeighborDevice>();
        IpHelperTable.Read((IntPtr buffer, ref int size) => NativeMethods.GetIpNetTable(buffer, ref size, false), RowSize, row =>
        {
            var type = Marshal.ReadInt32(row, 20);
            var macLength = Math.Min(Marshal.ReadInt32(row, 4), 8);
            var mac = new byte[macLength];
            Marshal.Copy(row + 8, mac, 0, macLength);
            var address = ConnectionTable.IPv4(row, 16);
            if (type == TypeInvalid || !IsDevice(address, mac)) return;
            var adapter = adapters.GetValueOrDefault(Marshal.ReadInt32(row, 0));
            devices.Add(new NeighborDevice(address, FormatMac(mac), adapter.Name ?? "", type == TypeStatic, adapter.Gateways?.Contains(address) == true));
        });
        return devices;
    }

    /// <summary>A real device has a unicast address and a resolved unicast MAC (not all zeros, broadcast, or multicast).</summary>
    internal static bool IsDevice(IPAddress address, byte[] mac) =>
        AddressClassifier.Classify(address) is not (AddressScope.Multicast or AddressScope.Unspecified or AddressScope.Loopback) &&
        mac.Length == 6 && mac.Any(b => b != 0) && (mac[0] & 1) == 0;

    /// <summary>The form Windows shows: AA-BB-CC-DD-EE-FF.</summary>
    public static string FormatMac(byte[] mac) => string.Join('-', mac.Select(b => b.ToString("X2")));

    private static Dictionary<int, (string? Name, HashSet<IPAddress>? Gateways)> Adapters()
    {
        var adapters = new Dictionary<int, (string?, HashSet<IPAddress>?)>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                var properties = adapter.GetIPProperties();
                var gateways = properties.GatewayAddresses.Select(g => g.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToHashSet();
                adapters[properties.GetIPv4Properties().Index] = (adapter.Name, gateways);
            }
            catch (Exception ex) when (ex is NetworkInformationException or NotSupportedException or InvalidOperationException)
            {
                // An adapter without IPv4 has no ARP entries.
            }
        }
        return adapters;
    }
}
