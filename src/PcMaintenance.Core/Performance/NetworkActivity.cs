using System.Diagnostics;
using System.Net.NetworkInformation;

namespace PcMaintenance.Core.Performance;

public sealed record NetworkActivity(string Id, string Name, string Description, double SentBitsPerSecond, double ReceivedBitsPerSecond);

/// <summary>
/// Throughput of the connected Ethernet and Wi-Fi adapters. Virtual adapters and the filter layers Windows stacks on
/// a real adapter ("…-QoS Packet Scheduler-0000") would only count the same traffic again.
/// </summary>
public sealed class NetworkActivitySampler
{
    private static readonly string[] VirtualHints =
    [
        "Virtual", "Hyper-V", "VMware", "VirtualBox", "TAP-", "Loopback", "Bluetooth", "WAN Miniport", "Npcap", "Tunnel", "Docker", "VPN",
        "Filter", "Scheduler",
    ];

    private Dictionary<string, (long Sent, long Received)> _previous = [];
    private long _previousTimestamp;

    public IReadOnlyList<NetworkActivity> Sample(long timestamp)
    {
        var seconds = _previousTimestamp == 0 ? 0 : (double)(timestamp - _previousTimestamp) / Stopwatch.Frequency;
        var current = new Dictionary<string, (long, long)>();
        var result = new List<NetworkActivity>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!IsShown(adapter.NetworkInterfaceType, adapter.Description, adapter.OperationalStatus)) continue;
            IPInterfaceStatistics statistics;
            try
            {
                statistics = adapter.GetIPStatistics();
            }
            catch (NetworkInformationException)
            {
                continue;
            }
            current[adapter.Id] = (statistics.BytesSent, statistics.BytesReceived);
            double sent = 0, received = 0;
            if (seconds > 0 && _previous.TryGetValue(adapter.Id, out var before))
            {
                sent = Math.Max(0, statistics.BytesSent - before.Sent) * 8 / seconds;
                received = Math.Max(0, statistics.BytesReceived - before.Received) * 8 / seconds;
            }
            result.Add(new NetworkActivity(adapter.Id, adapter.Name, adapter.Description, sent, received));
        }
        _previous = current;
        _previousTimestamp = timestamp;
        return result;
    }

    internal static bool IsShown(NetworkInterfaceType type, string description, OperationalStatus status) =>
        status == OperationalStatus.Up &&
        type is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet &&
        !VirtualHints.Any(hint => description.Contains(hint, StringComparison.OrdinalIgnoreCase));
}
