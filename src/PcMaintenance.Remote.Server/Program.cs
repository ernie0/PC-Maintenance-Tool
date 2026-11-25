using PcMaintenance.Remote.Server;

// 轉送伺服器（放 NAS，之後經 Cloudflare Tunnel 以 WSS 對外，不開 port）。
// 稽核資料庫路徑可用環境變數 REMOTE_AUDIT_DB 指定；預設在內容根目錄。
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

string dbPath = Environment.GetEnvironmentVariable("REMOTE_AUDIT_DB")
    ?? Path.Combine(app.Environment.ContentRootPath, "remote-audit.db");

using var audit = new AuditLog(dbPath);
var hub = new RelayHub(audit);
app.MapRemoteServer(hub);

app.Run();
