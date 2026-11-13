using System.Net;
using System.Net.Sockets;
using PcMaintenance.Core.Common;

namespace PcMaintenance.Core.Network;

/// <summary>
/// The company's list of addresses to watch for (settings: suspiciousAddressList): one IP address or range
/// (CIDR, e.g. 203.0.113.0/24) per line; text after # is a comment, and anything after the first space or comma on
/// a line is ignored, so most published block lists can be used as they are.
/// </summary>
public sealed class SuspiciousAddressList
{
    private readonly HashSet<IPAddress> _addresses;
    private readonly List<IPNetwork> _ranges;

    private SuspiciousAddressList(HashSet<IPAddress> addresses, List<IPNetwork> ranges, int rejected)
    {
        _addresses = addresses;
        _ranges = ranges;
        RejectedLines = rejected;
    }

    public int Count => _addresses.Count + _ranges.Count;

    /// <summary>Lines that held something other than an address or a range.</summary>
    public int RejectedLines { get; }

    public static SuspiciousAddressList Parse(IEnumerable<string> lines)
    {
        var addresses = new HashSet<IPAddress>();
        var ranges = new List<IPNetwork>();
        var rejected = 0;
        foreach (var line in lines)
        {
            var text = line.Split('#')[0].Trim();
            if (text.Length == 0) continue;
            var token = text.Split([' ', '\t', ',', ';'], 2)[0];
            // IPAddress also accepts shorthand like "10" (0.0.0.10), which no list means.
            if (token.IndexOfAny(['.', ':']) >= 0 && !token.Contains('/') && IPAddress.TryParse(token, out var address)) addresses.Add(AddressClassifier.Normalize(address));
            else if (IPNetwork.TryParse(token, out var range)) ranges.Add(range);
            else rejected++;
        }
        return new SuspiciousAddressList(addresses, ranges, rejected);
    }

    /// <summary>The configured file, or null when none is set. A relative path is read from the company settings folder.</summary>
    public static SuspiciousListLoad Load(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.SuspiciousAddressList)) return new SuspiciousListLoad(null, null, null);
        var path = Environment.ExpandEnvironmentVariables(settings.SuspiciousAddressList.Trim());
        if (!Path.IsPathRooted(path)) path = Path.Combine(AppPaths.MachineDataDir, path);
        try
        {
            return new SuspiciousListLoad(Parse(File.ReadLines(path)), path, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new SuspiciousListLoad(null, path, $"無法讀取可疑 IP 清單（{UserMessage.For(ex)}）：{path}");
        }
    }

    public bool Contains(IPAddress address)
    {
        address = AddressClassifier.Normalize(address);
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0) address = new IPAddress(address.GetAddressBytes());
        return _addresses.Contains(address) || _ranges.Any(r => r.Contains(address));
    }
}

public sealed record SuspiciousListLoad(SuspiciousAddressList? List, string? Path, string? Error);
