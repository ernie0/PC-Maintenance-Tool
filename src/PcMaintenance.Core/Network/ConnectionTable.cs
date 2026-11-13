using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using PcMaintenance.Core.Common;

namespace PcMaintenance.Core.Network;

public enum TransportProtocol
{
    Tcp,
    Udp,
}

/// <summary>
/// One socket from Windows' connection tables. <see cref="Remote"/> is null for a listening TCP socket and for UDP,
/// which has no peer; <see cref="State"/> is null for UDP. Process id 0 owns TCP connections that their program has
/// already closed and Windows keeps for a while (TIME_WAIT).
/// </summary>
public sealed record NetworkConnection(TransportProtocol Protocol, IPEndPoint Local, IPEndPoint? Remote, TcpState? State, int ProcessId)
{
    public bool IsListening => Protocol == TransportProtocol.Udp || State == TcpState.Listen;
}

/// <summary>Every TCP and UDP socket with its owning process, read from the IP Helper tables (what netstat -ano shows); works without elevation.</summary>
public static class ConnectionTable
{
    // MIB_TCPROW_OWNER_PID: state, local addr, local port, remote addr, remote port, pid.
    private const int Tcp4RowSize = 24;
    // MIB_TCP6ROW_OWNER_PID: local addr[16], scope, port, remote addr[16], scope, port, state, pid.
    private const int Tcp6RowSize = 56;
    // MIB_UDPROW_OWNER_PID: local addr, local port, pid.
    private const int Udp4RowSize = 12;
    // MIB_UDP6ROW_OWNER_PID: local addr[16], scope, port, pid.
    private const int Udp6RowSize = 28;

    public static IReadOnlyList<NetworkConnection> Read()
    {
        var connections = new List<NetworkConnection>();
        IpHelperTable.Read((IntPtr buffer, ref int size) => NativeMethods.GetExtendedTcpTable(buffer, ref size, false, NativeMethods.AF_INET, NativeMethods.TCP_TABLE_OWNER_PID_ALL, 0),
            Tcp4RowSize, row => connections.Add(Tcp(
                IPv4(row, 4), Port(row, 8), IPv4(row, 12), Port(row, 16), Marshal.ReadInt32(row, 0), Marshal.ReadInt32(row, 20))));
        IpHelperTable.Read((IntPtr buffer, ref int size) => NativeMethods.GetExtendedTcpTable(buffer, ref size, false, NativeMethods.AF_INET6, NativeMethods.TCP_TABLE_OWNER_PID_ALL, 0),
            Tcp6RowSize, row => connections.Add(Tcp(
                IPv6(row, 0, 16), Port(row, 20), IPv6(row, 24, 40), Port(row, 44), Marshal.ReadInt32(row, 48), Marshal.ReadInt32(row, 52))));
        IpHelperTable.Read((IntPtr buffer, ref int size) => NativeMethods.GetExtendedUdpTable(buffer, ref size, false, NativeMethods.AF_INET, NativeMethods.UDP_TABLE_OWNER_PID, 0),
            Udp4RowSize, row => connections.Add(new NetworkConnection(
                TransportProtocol.Udp, new IPEndPoint(IPv4(row, 0), Port(row, 4)), null, null, Marshal.ReadInt32(row, 8))));
        IpHelperTable.Read((IntPtr buffer, ref int size) => NativeMethods.GetExtendedUdpTable(buffer, ref size, false, NativeMethods.AF_INET6, NativeMethods.UDP_TABLE_OWNER_PID, 0),
            Udp6RowSize, row => connections.Add(new NetworkConnection(
                TransportProtocol.Udp, new IPEndPoint(IPv6(row, 0, 16), Port(row, 20)), null, null, Marshal.ReadInt32(row, 24))));
        return connections;
    }

    private static NetworkConnection Tcp(IPAddress local, int localPort, IPAddress remote, int remotePort, int state, int processId)
    {
        var tcpState = (TcpState)state;
        // A listening socket reports 0.0.0.0:0 as its peer.
        var peer = tcpState == TcpState.Listen ? null : new IPEndPoint(remote, remotePort);
        return new NetworkConnection(TransportProtocol.Tcp, new IPEndPoint(local, localPort), peer, tcpState, processId);
    }

    // Ports sit in the low 16 bits of the DWORD in network byte order.
    private static int Port(IntPtr row, int offset)
    {
        var raw = Marshal.ReadInt32(row, offset);
        return (raw & 0xFF) << 8 | (raw >> 8) & 0xFF;
    }

    // Addresses are stored in network byte order, which is the byte order IPAddress takes.
    internal static IPAddress IPv4(IntPtr row, int offset)
    {
        var bytes = new byte[4];
        Marshal.Copy(row + offset, bytes, 0, 4);
        return new IPAddress(bytes);
    }

    private static IPAddress IPv6(IntPtr row, int offset, int scopeOffset)
    {
        var bytes = new byte[16];
        Marshal.Copy(row + offset, bytes, 0, 16);
        return new IPAddress(bytes, (uint)Marshal.ReadInt32(row, scopeOffset));
    }
}

/// <summary>Reads an IP Helper table: a DWORD row count followed by fixed-size rows. The table can grow between the size query and the read, so that is retried.</summary>
internal static class IpHelperTable
{
    internal delegate uint Query(IntPtr buffer, ref int size);

    internal static void Read(Query query, int rowSize, Action<IntPtr> readRow)
    {
        var size = 0;
        var result = query(IntPtr.Zero, ref size);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (result == NativeMethods.ERROR_NO_DATA) return;
            if (result != NativeMethods.ERROR_INSUFFICIENT_BUFFER && result != 0) throw new Win32Exception((int)result);
            var buffer = Marshal.AllocHGlobal(Math.Max(size, 4));
            try
            {
                result = query(buffer, ref size);
                if (result != 0) continue;
                var count = Marshal.ReadInt32(buffer);
                for (var i = 0; i < count; i++) readRow(buffer + 4 + i * rowSize);
                return;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        throw new Win32Exception((int)result);
    }
}
