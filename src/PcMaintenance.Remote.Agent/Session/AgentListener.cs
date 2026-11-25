using System.Net;
using System.Net.Sockets;
using PcMaintenance.Remote.Core.Transport;

namespace PcMaintenance.Remote.Agent.Session;

/// <summary>
/// 被控端的 TCP 監聽器（P1 區網直連用；之後會改成往 NAS 伺服器註冊、由伺服器轉送）。
/// 一次只服務一條連線——同時多個控制端操作同一台會打架，所以接到新連線時前一條若還在就拒絕。
/// </summary>
public sealed class AgentListener
{
    private readonly IPEndPoint _endpoint;
    private readonly AgentSessionOptions _options;

    public AgentListener(IPEndPoint endpoint, AgentSessionOptions? options = null)
    {
        _endpoint = endpoint;
        _options = options ?? new AgentSessionOptions();
    }

    /// <summary>交握完成、驗證碼可顯示時觸發（每條連線一次）。</summary>
    public event Action<string>? VerificationCodeReady;

    /// <summary>實際綁定的本機位址（傳入 port 0 時可用來得知系統配的 port）。</summary>
    public IPEndPoint? BoundEndpoint { get; private set; }

    public async Task ListenAsync(CancellationToken ct)
    {
        var listener = new TcpListener(_endpoint);
        listener.Start();
        BoundEndpoint = (IPEndPoint)listener.LocalEndpoint;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using TcpClient client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                client.NoDelay = true;
                try
                {
                    var link = await SecureSession.HandshakeAsync(client.GetStream(), isInitiator: false, ct).ConfigureAwait(false);
                    await using var session = new AgentSession(link, _options);
                    session.VerificationCodeReady += code => VerificationCodeReady?.Invoke(code);
                    await session.RunAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception) { /* 這條連線出錯就收掉，繼續等下一條 */ }
            }
        }
        finally { listener.Stop(); }
    }
}
