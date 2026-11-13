using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace PcMaintenance.Core.Network;

/// <summary>
/// Reverse DNS for addresses the user asks about. This sends queries to the PC's DNS server, so it only runs when
/// the user presses the button, never on its own and never from the command line. Answers, including "no name",
/// are kept for the rest of the session.
/// </summary>
public sealed class HostNameResolver
{
    private const int Parallel = 16;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    private readonly ConcurrentDictionary<IPAddress, string> _names = new();

    /// <summary>The name found, "" when the lookup found none, or null when it was never looked up.</summary>
    public string? Get(IPAddress address) => _names.TryGetValue(Key(address), out var name) ? name : null;

    /// <summary>
    /// Looks up the addresses not asked about before and returns how many that was; <paramref name="progress"/> gets
    /// the number done after each answer, so names can be shown as they come in.
    /// </summary>
    public async Task<int> ResolveAsync(IEnumerable<IPAddress> addresses, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        var pending = addresses.Select(Key).Where(IsWorthAsking).Distinct().Where(a => !_names.ContainsKey(a)).ToList();
        var done = 0;
        using var gate = new SemaphoreSlim(Parallel);
        await Task.WhenAll(pending.Select(async address =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                _names[address] = await LookupAsync(address, cancellationToken) ?? "";
            }
            finally
            {
                gate.Release();
            }
            progress?.Report(Interlocked.Increment(ref done));
        }));
        return pending.Count;
    }

    /// <summary>How many of these addresses a lookup would ask about (not yet asked, and not loopback or multicast).</summary>
    public int CountPending(IEnumerable<IPAddress> addresses) =>
        addresses.Select(Key).Where(IsWorthAsking).Distinct().Count(a => !_names.ContainsKey(a));

    private static async Task<string?> LookupAsync(IPAddress address, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            var entry = await Dns.GetHostEntryAsync(address.ToString(), timeout.Token);
            // Without a PTR record Windows can answer with the address itself.
            return entry.HostName.Length == 0 || IPAddress.TryParse(entry.HostName, out _) ? null : entry.HostName;
        }
        catch (SocketException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static IPAddress Key(IPAddress address)
    {
        address = AddressClassifier.Normalize(address);
        return address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0 ? new IPAddress(address.GetAddressBytes()) : address;
    }

    private static bool IsWorthAsking(IPAddress address) =>
        AddressClassifier.Classify(address) is AddressScope.Internet or AddressScope.LocalNetwork;
}
