using System.Text;
using ECAssistant.Orchestration;

namespace ECAssistant.Engine;

public class SubAgentError
{
    public SubAgentErrorKind Kind { get; set; }
    public string Message { get; set; } = "";
    public string AttemptedAction { get; set; } = "";
    public List<string> SuccessfulActions { get; set; } = new();
    public List<string> FailedActions { get; set; } = new();
    public List<string> FilesModified { get; set; } = new();
    public string PartialOutput { get; set; } = "";
    public OrchestratorStatus Status { get; set; }
    public int RetryAttempt { get; set; }

    public string ToStructuredString()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Error Kind: {Kind}");
        sb.AppendLine($"Message: {Message}");
        if (!string.IsNullOrEmpty(AttemptedAction))
            sb.AppendLine($"Attempted: {AttemptedAction}");
        if (SuccessfulActions.Count > 0)
            sb.AppendLine($"Succeeded before failure: {string.Join(", ", SuccessfulActions)}");
        if (FailedActions.Count > 0)
            sb.AppendLine($"Failed actions: {string.Join(", ", FailedActions)}");
        if (FilesModified.Count > 0)
            sb.AppendLine($"Files modified: {string.Join(", ", FilesModified)}");
        if (!string.IsNullOrEmpty(PartialOutput))
            sb.AppendLine($"Partial output: {PartialOutput}");
        sb.AppendLine($"Status: {Status}");
        if (RetryAttempt > 0)
            sb.AppendLine($"Retry attempt: {RetryAttempt}");
        return sb.ToString();
    }
}