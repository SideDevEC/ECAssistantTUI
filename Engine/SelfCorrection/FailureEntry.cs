namespace ECAssistant.Engine;

public class FailureEntry
{
    public string ToolName { get; set; } = "";
    public string ErrorMessage { get; set; } = "";
    public string Command { get; set; } = "";
    public DateTime Timestamp { get; set; }
}