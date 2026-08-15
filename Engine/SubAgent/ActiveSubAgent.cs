namespace ECAssistant.Engine;

public class ActiveSubAgent
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
    public string Description { get; set; } = "";
    public CancellationTokenSource Cts { get; set; } = new();
    public EAgentEngine? Engine { get; set; }
    public Task<SubAgentResult>? Task { get; set; }
    public SubAgentTask TaskDef { get; set; } = null!;
    public DateTime StartedAt { get; } = DateTime.UtcNow;
}