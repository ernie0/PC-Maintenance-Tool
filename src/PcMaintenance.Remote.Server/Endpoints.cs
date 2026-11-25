using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace PcMaintenance.Remote.Server;

/// <summary>轉送伺服器的 HTTP／WebSocket 端點。抽成擴充方法，讓正式程式與端對端測試共用同一套。</summary>
public static class RemoteServerEndpoints
{
    public static void MapRemoteServer(this WebApplication app, RelayHub hub)
    {
        app.UseWebSockets();

        // 控制端取得目前線上被控端清單
        app.MapGet("/api/agents", () => hub.OnlineAgents());

        // 被控端控制連線：註冊、收連線要求、回同意／拒絕
        app.MapGet("/ws/agent", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await hub.HandleAgentAsync(ws, ctx.RequestAborted);
        });

        // 控制端控制連線：送一個連線要求、拿回同意／拒絕
        app.MapGet("/ws/control", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await hub.HandleControllerAsync(ws, ctx.RequestAborted);
        });

        // 轉送連線：兩端各開一條帶同一 session 的 WebSocket，伺服器配對後對轉二進位框
        app.MapGet("/ws/relay", async (HttpContext ctx) =>
        {
            string? session = ctx.Request.Query["session"];
            string? role = ctx.Request.Query["role"];
            if (!ctx.WebSockets.IsWebSocketRequest || string.IsNullOrEmpty(session) || role is not ("agent" or "controller"))
            {
                ctx.Response.StatusCode = 400;
                return;
            }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await hub.HandleRelayAsync(ws, session, role, ctx.RequestAborted);
        });
    }
}
