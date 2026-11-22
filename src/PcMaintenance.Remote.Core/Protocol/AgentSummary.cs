namespace PcMaintenance.Remote.Core.Protocol;

/// <summary>伺服器回給控制端的一台線上被控端摘要（GET /api/agents）。</summary>
public sealed record AgentSummary
{
    public required string AgentId { get; init; }
    public required string ComputerName { get; init; }
    public required string[] Groups { get; init; }
}
