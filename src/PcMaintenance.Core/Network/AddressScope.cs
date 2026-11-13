using System.Net;
using System.Net.Sockets;

namespace PcMaintenance.Core.Network;

public enum AddressScope
{
    /// <summary>0.0.0.0 or ::, what a socket listening on every network card is bound to.</summary>
    Unspecified,
    Loopback,
    /// <summary>Private (10/8, 172.16/12, 192.168/16), link-local, and IPv6 unique-local addresses.</summary>
    LocalNetwork,
    /// <summary>Multicast and broadcast.</summary>
    Multicast,
    Internet,
}

public static class AddressClassifier
{
    public static AddressScope Classify(IPAddress address)
    {
        address = Normalize(address);
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return AddressScope.Unspecified;
        if (IPAddress.IsLoopback(address)) return AddressScope.Loopback;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            // 224/4 multicast, 240/4 reserved, 255.255.255.255 broadcast.
            if (b[0] >= 224) return AddressScope.Multicast;
            if (b[0] == 10 || b[0] == 172 && (b[1] & 0xF0) == 16 || b[0] == 192 && b[1] == 168 || b[0] == 169 && b[1] == 254)
                return AddressScope.LocalNetwork;
            return AddressScope.Internet;
        }
        if (address.IsIPv6Multicast) return AddressScope.Multicast;
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal) return AddressScope.LocalNetwork;
        return AddressScope.Internet;
    }

    /// <summary>An IPv4 address carried in IPv6 (::ffff:1.2.3.4, as dual-mode sockets report it) as plain IPv4.</summary>
    public static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
