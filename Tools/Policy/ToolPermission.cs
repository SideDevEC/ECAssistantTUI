using System.Text.Json.Serialization;

namespace ECAssistant.Tools;

/// <summary>
/// Permission rule for a single tool.
/// </summary>
public class ToolPermission
{
    public string ToolName { get; set; } = "";
    public ToolPermissionLevel Level { get; set; } = ToolPermissionLevel.Allowed;
    public List<string>? ApprovalPatterns { get; set; }
    public string? Reason { get; set; }
}