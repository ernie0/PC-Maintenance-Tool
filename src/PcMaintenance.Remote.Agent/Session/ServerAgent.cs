using System.Net.WebSockets;
using PcMaintenance.Remote.Core.Crypto;
using PcMaintenance.Remote.Core.Protocol;
using PcMaintenance.Remote.Core.Transport;

namespace PcMaintenance.Remote.Agent.Session;

/// <summary>被控端對一個連線要求的決定。</summary>
public sealed record ConsentDecision
{
    public required bool Accept { get; init; }
    public string? RejectReason { get; init; }
    public bool Hidden { get; init; }
    public bool PairingEstablished { get; init; }

    public static ConsentDecision Reject(string reason) => new() { Accept = false, RejectReason = reason };
    /// <summary>直接同意、正常顯示（P1-A 的預設；配對與隱藏模式在 P1-B 加）。</summary>
    public static readonly ConsentDecision AcceptVisible = new() { Accept = true };
}

/// <summary>被控端的身分與登記資料。</summary>
public sealed record AgentIdentity
{
    public required string AgentId { get; init; }
    public required string ComputerName { get; init; }
    public string[] Groups { get; init; } = [];
    public string AgentVersion { get; init; } = "0.1.0";
}

/// <summary>
/// 被控端「連伺服器」模式：連到轉送伺服器登記，等控制端的連線要求，依策略同意或拒絕，
/// 同意後開一條轉送 WebSocket 跑 <see cref="AgentSession"/>。一次服務一條連線。
/// 控制面（登記、要求、同意）是明文、伺服器看得到以便路由與稽核；畫面與操作走端對端加密的轉送連線。
/// </summary>
public sealed class ServerAgent(Uri serverBase, AgentIdentity identity, AgentSessionOptions? options = null)
{
    private readonly AgentSessionOptions _options = options ?? new AgentSessionOptions();

    /// <summary>決定是否接受一個連線要求；預設一律同意、正常顯示。P1-B 會換成查配對＋隱藏模式。</summary>
    public Func<SessionRequestMessage, ConsentDecision> ConsentPolicy { get; set; } = _ => ConsentDecision.AcceptVisible;

    /// <summary>每條實際連線交握後的驗證碼（給上層顯示核對）。</summary>
    public event Action<string>? VerificationCodeReady;

    public async Task RunAsync(CancellationToken ct)
    {
        using var controlWs = new ClientWebSocket();
        await controlWs.ConnectAsync(WsUri("/ws/agent"), ct).ConfigureAwait(false);
        using var control = new ControlChannel(controlWs);

        await control.SendAsync(new RegisterMessage
        {
            ComputerName = identity.ComputerName,
            Groups = identity.Groups,
            AgentVersion = identity.AgentVersion,
            AgentId = identity.AgentId,
        }, ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            var msg = await control.ReceiveAsync(ct).ConfigureAwait(false);
            if (msg is null) break;
            if (msg is not SessionRequestMessage req) continue;

            ConsentDecision decision;
            try { decision = ConsentPolicy(req); }
            catch { decision = ConsentDecision.Reject("被控端內部錯誤"); }

            if (!decision.Accept)
            {
                await control.SendAsync(new SessionRejectMessage { RequestId = req.RequestId, Reason = decision.RejectReason ?? "拒絕" }, ct).ConfigureAwait(false);
                continue;
            }

            using var kex = new KeyExchange();
            SessionKeys keys = kex.DeriveKeys(req.ControllerPublicKey, isInitiator: false);
            await control.SendAsync(new SessionConsentMessage
            {
                RequestId = req.RequestId,
                AgentPublicKey = kex.PublicKey,
                Hidden = decision.Hidden,
                PairingEstablished = decision.PairingEstablished,
            }, ct).ConfigureAwait(false);

            await RunRelaySessionAsync(req.RequestId, keys, ct).ConfigureAwait(false);
        }
    }

    private async Task RunRelaySessionAsync(string requestId, SessionKeys keys, CancellationToken ct)
    {
        try
        {
            using var relayWs = new ClientWebSocket();
            await relayWs.ConnectAsync(WsUri($"/ws/relay?session={Uri.EscapeDataString(requestId)}&role=agent"), ct).ConfigureAwait(false);
            var link = new WebSocketSecureLink(relayWs, keys);
            await using var session = new AgentSession(link, _options);
            session.VerificationCodeReady += code => VerificationCodeReady?.Invoke(code);
            await session.RunAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* 這條連線出錯就收掉，回去等下一個要求 */ }
    }

    private Uri WsUri(string pathAndQuery)
    {
        string scheme = serverBase.Scheme == "https" ? "wss" : "ws";
        return new Uri($"{scheme}://{serverBase.Authority}{pathAndQuery}");
    }
}
