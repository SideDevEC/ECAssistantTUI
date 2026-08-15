using System.Text.Json.Serialization;

namespace ECAssistant.Tools;

/// <summary>
/// Permission level for a tool.
/// </summary>
public enum ToolPermissionLevel
{
    Allowed,
    ApprovalRequired,
    Blocked
}