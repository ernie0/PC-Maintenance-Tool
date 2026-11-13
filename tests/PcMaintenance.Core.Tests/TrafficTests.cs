using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using PcMaintenance.Core.Common;
using PcMaintenance.Core.Network;

namespace PcMaintenance.Core.Tests;

public class TrafficTests
{
    [Fact]
    public void Events_are_read_as_process_bytes_and_direction()
    {
        // TCPv4 send: PID, size, destination, source, destination port, source port.
        Assert.Equal(new TrafficPacket(1234, 1460, true), Parse(10, Payload(1234, 1460, IPAddress.Parse("203.0.113.7"), IPAddress.Parse("192.168.1.5"))));
        Assert.Equal(new TrafficPacket(1234, 512, false), Parse(43, Payload(1234, 512, IPAddress.Parse("192.168.1.5"), IPAddress.Parse("8.8.8.8"))));
        Assert.Equal(new TrafficPacket(77, 9000, false), Parse(27, Payload(77, 9000, IPAddress.Parse("2001:db8::5"), IPAddress.Parse("2001:4860::8888"))));
        Assert.Equal(new TrafficPacket(77, 100, true), Parse(58, Payload(77, 100, IPAddress.Parse("2001:4860::8888"), IPAddress.Parse("2001:db8::5"))));
    }

    [Fact]
    public void Loopback_other_events_and_short_payloads_are_not_counted()
    {
        Assert.Null(Parse(10, Payload(1, 100, IPAddress.Loopback, IPAddress.Loopback)));
        Assert.Null(Parse(26, Payload(1, 100, IPAddress.IPv6Loopback, IPAddress.IPv6Loopback)));
        Assert.Null(Parse(12, Payload(1, 100, IPAddress.Parse("203.0.113.7"), IPAddress.Parse("192.168.1.5"))));
        Assert.Null(Parse(26, Payload(1, 100, IPAddress.Parse("203.0.113.7"), IPAddress.Parse("192.168.1.5"))));
    }

    [Fact]
    public void Counting_needs_elevation_and_counts_this_process_when_it_has_it()
    {
        bool elevated;
        using (var identity = WindowsIdentity.GetCurrent()) elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        if (!elevated)
        {
            var refused = Assert.Throws<UnauthorizedAccessException>(TrafficMonitor.Start);
            Assert.Contains("系統管理員", refused.Message);
            return;
        }

        using var monitor = TrafficMonitor.Start();
        // TEST-NET-1 is never routed, but the datagram still leaves through the default route and is counted.
        using (var udp = new UdpClient()) udp.Send(new byte[1000], new IPEndPoint(IPAddress.Parse("192.0.2.1"), 9));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        long sent = 0;
        while (DateTime.UtcNow < deadline && sent < 1000)
        {
            Thread.Sleep(250);
            sent = monitor.Snapshot().GetValueOrDefault(Environment.ProcessId).Sent;
        }
        Assert.True(sent >= 1000, $"sent {sent}");
        Assert.True(monitor.IsRunning);
    }

    [Theory]
    [InlineData(800, "800 bps")]
    [InlineData(12_345, "12.3 Kbps")]
    [InlineData(12_345_678, "12.3 Mbps")]
    [InlineData(2_500_000_000, "2.5 Gbps")]
    public void Speeds_read_like_task_manager(double bitsPerSecond, string expected) =>
        Assert.Equal(expected, ByteSize.FormatBitsPerSecond(bitsPerSecond));

    private static TrafficPacket? Parse(ushort eventId, byte[] payload)
    {
        var buffer = Marshal.AllocHGlobal(payload.Length);
        try
        {
            Marshal.Copy(payload, 0, buffer, payload.Length);
            return TrafficMonitor.Parse(eventId, buffer, payload.Length);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static byte[] Payload(int pid, int size, IPAddress destination, IPAddress source)
    {
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes(pid));
        bytes.AddRange(BitConverter.GetBytes(size));
        bytes.AddRange(destination.GetAddressBytes());
        bytes.AddRange(source.GetAddressBytes());
        bytes.AddRange([0x01, 0xBB, 0xC8, 0x00]);
        return [.. bytes];
    }
}
