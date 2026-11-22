using System.Collections.Concurrent;
using System.Net.WebSockets;
using PcMaintenance.Remote.Core.Protocol;
using PcMaintenance.Remote.Core.Transport;

namespace PcMaintenance.Remote.Server;

/// <summary>
/// 轉送中樞：記住線上被控端、把控制端的連線要求路由給對應被控端、取得同意後把兩條 WebSocket
/// 的二進位框對轉。伺服器全程只看得到控制面的路由資訊（誰連誰），畫面與操作是端對端加密的、看不到。
/// 單一被控端一次只服務一條連線。
/// </summary>
public sealed class RelayHub(AuditLog audit)
{
    private readonly ConcurrentDictionary<string, AgentControl> _agents = new();
    private readonly ConcurrentDictionary<string, RelayPair> _relays = new();

    public IReadOnlyList<AgentSummary> OnlineAgents() =>
        _agents.Values.Select(a => new AgentSummary
        {
            AgentId = a.Register.AgentId,
            ComputerName = a.Register.ComputerName,
            Groups = a.Register.Groups,
        }).ToList();

    // ---- 被控端控制連線 ----

    public async Task HandleAgentAsync(WebSocket ws, CancellationToken ct)
    {
        using var control = new ControlChannel(ws);
        AgentControl? agent = null;
        try
        {
            if (await control.ReceiveAsync(ct).ConfigureAwait(false) is not RegisterMessage reg) return;
            agent = new AgentControl(reg, control);
            _agents[reg.AgentId] = agent;

            while (!ct.IsCancellationRequested)
            {
                var msg = await control.ReceiveAsync(ct).ConfigureAwait(false);
                if (msg is null) break;
                switch (msg)
                {
                    case SessionConsentMessage c: agent.Complete(c.RequestId, c); break;
                    case SessionRejectMessage r: agent.Complete(r.RequestId, r); break;
                    // HeartbeatMessage 等其餘維持連線即可
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
        finally
        {
            if (agent is not null) _agents.TryRemove(new KeyValuePair<string, AgentControl>(agent.Register.AgentId, agent));
        }
    }

    // ---- 控制端控制連線：送一個連線要求、拿回同意或拒絕 ----

    public async Task HandleControllerAsync(WebSocket ws, CancellationToken ct)
    {
        using var control = new ControlChannel(ws);
        try
        {
            if (await control.ReceiveAsync(ct).ConfigureAwait(false) is not SessionRequestMessage req) return;

            if (!_agents.TryGetValue(req.TargetAgentId, out var agent))
            {
                await control.SendAsync(new SessionRejectMessage { RequestId = req.RequestId, Reason = "被控端不在線上" }, ct).ConfigureAwait(false);
                audit.LogStart(req.RequestId, req.TargetAgentId, req.AdminName, req.Mode, req.RequestHidden, "agent-offline");
                return;
            }

            ControlMessage resp;
            try
            {
                var expect = agent.Expect(req.RequestId, TimeSpan.FromSeconds(30));
                await agent.Control.SendAsync(req, ct).ConfigureAwait(false);
                resp = await expect.ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await control.SendAsync(new SessionRejectMessage { RequestId = req.RequestId, Reason = "被控端未回應" }, ct).ConfigureAwait(false);
                audit.LogStart(req.RequestId, req.TargetAgentId, req.AdminName, req.Mode, req.RequestHidden, "timeout");
                return;
            }

            bool hidden = resp is SessionConsentMessage { Hidden: true };
            bool accepted = resp is SessionConsentMessage;
            audit.LogStart(req.RequestId, req.TargetAgentId, req.AdminName, req.Mode, hidden, accepted ? "accepted" : "rejected");
            await control.SendAsync(resp, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
    }

    // ---- 轉送：兩條 WebSocket 配對後對轉二進位 ----

    public async Task HandleRelayAsync(WebSocket ws, string requestId, string role, CancellationToken ct)
    {
        var pair = _relays.GetOrAdd(requestId, _ => new RelayPair());
        pair.Set(role, ws);

        if (!await pair.WaitBothAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false))
        {
            _relays.TryRemove(new KeyValuePair<string, RelayPair>(requestId, pair));
            return;
        }

        if (role == "controller") // 指定由控制端這條負責抽送，避免兩條都抽造成重複
        {
            try { await RelayPair.PumpAsync(pair.Agent!, pair.Controller!, ct).ConfigureAwait(false); }
            finally
            {
                pair.Done.TrySetResult();
                _relays.TryRemove(new KeyValuePair<string, RelayPair>(requestId, pair));
                audit.LogEnd(requestId);
            }
        }
        else
        {
            await pair.Done.Task.ConfigureAwait(false);
        }
    }

    private sealed class AgentControl(RegisterMessage register, ControlChannel control)
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<ControlMessage>> _pending = new();
        public RegisterMessage Register { get; } = register;
        public ControlChannel Control { get; } = control;

        public Task<ControlMessage> Expect(string requestId, TimeSpan timeout)
        {
            var tcs = new TaskCompletionSource<ControlMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[requestId] = tcs;
            return tcs.Task.WaitAsync(timeout);
        }

        public void Complete(string requestId, ControlMessage msg)
        {
            if (_pending.TryRemove(requestId, out var tcs)) tcs.TrySetResult(msg);
        }
    }

    private sealed class RelayPair
    {
        private readonly TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _lock = new();
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public WebSocket? Agent { get; private set; }
        public WebSocket? Controller { get; private set; }

        public void Set(string role, WebSocket ws)
        {
            lock (_lock)
            {
                if (role == "agent") Agent = ws; else Controller = ws;
                if (Agent is not null && Controller is not null) _both.TrySetResult();
            }
        }

        public async Task<bool> WaitBothAsync(TimeSpan timeout, CancellationToken ct)
        {
            try { await _both.Task.WaitAsync(timeout, ct).ConfigureAwait(false); return true; }
            catch { return false; }
        }

        public static async Task PumpAsync(WebSocket a, WebSocket c, CancellationToken ct)
        {
            var toController = PumpOneAsync(a, c, ct);
            var toAgent = PumpOneAsync(c, a, ct);
            await Task.WhenAny(toController, toAgent).ConfigureAwait(false);
        }

        private static async Task PumpOneAsync(WebSocket src, WebSocket dst, CancellationToken ct)
        {
            try
            {
                while (true)
                {
                    var m = await WebSocketIo.ReceiveMessageAsync(src, ct).ConfigureAwait(false);
                    if (m is null) break;
                    await WebSocketIo.SendMessageAsync(dst, m.Value.Data, m.Value.Type, ct).ConfigureAwait(false);
                }
            }
            catch (Exception) { /* 任一端斷線即結束這一向 */ }
        }
    }
}
