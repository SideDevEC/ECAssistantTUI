namespace ECAssistant.Tools;

/// <summary>
/// Result of a tool policy check.
/// </summary>
public class ToolPolicyDecision
{
    public bool CanExecute { get; init; }
    public bool NeedsApproval { get; init; }
    public string Message { get; init; } = "";
}