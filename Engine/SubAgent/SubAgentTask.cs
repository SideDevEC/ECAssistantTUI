namespace ECAssistant.Engine;

public class SubAgentTask
{
    public string Description { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string WorkingDir { get; set; } = "";
    public List<string> AllowedTools { get; set; } = new();
    public uint ContextSize { get; set; } = 16384;
    public int MaxTurns { get; set; } = 5;
    public int TimeoutSeconds { get; set; } = 120;
    public int MaxToolCalls { get; set; } = 20;
    public long MaxDiskBytes { get; set; } = 50 * 1024 * 1024;
    public int MaxRetries { get; set; } = 1;
    public int RetryDelayMs { get; set; } = 1000;
}