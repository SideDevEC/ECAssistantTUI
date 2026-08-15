using System.Text.Json.Serialization;

namespace ECAssistant.Tools;

/// <summary>
/// Tool policy manager — checks permissions before tool execution.
/// Maintains a registry of tool permissions and enforces them.
/// </summary>
public class ToolPolicy
{
    private readonly Dictionary<string, ToolPermission> _permissions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _defaultAllowed = new(StringComparer.OrdinalIgnoreCase);

    public ToolPolicy()
    {
        SetDefaultPermissions();
    }

    private void SetDefaultPermissions()
    {
        _permissions["EFileResearchTool"] = new ToolPermission { ToolName = "EFileResearchTool", Level = ToolPermissionLevel.Allowed, Reason = "Read-only research" };
        _permissions["EFileAnalyzer"] = new ToolPermission { ToolName = "EFileAnalyzer", Level = ToolPermissionLevel.Allowed, Reason = "Read-only analysis" };
        _permissions["EShellAgent"] = new ToolPermission { ToolName = "EShellAgent", Level = ToolPermissionLevel.Allowed, Reason = "Primary tool — command execution" };
    }

    public void SetPermission(string toolName, ToolPermissionLevel level, string? reason = null)
    {
        _permissions[toolName] = new ToolPermission
        {
            ToolName = toolName,
            Level = level,
            Reason = reason
        };
    }

    public ToolPermissionLevel GetPermissionLevel(string toolName)
    {
        if (_permissions.TryGetValue(toolName, out var perm))
            return perm.Level;
        return ToolPermissionLevel.Allowed;
    }

    public bool IsAllowed(string toolName) => GetPermissionLevel(toolName) == ToolPermissionLevel.Allowed;
    public bool RequiresApproval(string toolName) => GetPermissionLevel(toolName) == ToolPermissionLevel.ApprovalRequired;
    public bool IsBlocked(string toolName) => GetPermissionLevel(toolName) == ToolPermissionLevel.Blocked;
    public bool IsUsable(string toolName) => GetPermissionLevel(toolName) != ToolPermissionLevel.Blocked;

    public List<ToolPermission> GetAllPermissions() => _permissions.Values.ToList();

    public ToolPolicyDecision Check(string toolName, Dictionary<string, string?> args)
    {
        var level = GetPermissionLevel(toolName);

        return level switch
        {
            ToolPermissionLevel.Blocked => new ToolPolicyDecision { CanExecute = false, NeedsApproval = false, Message = $"Tool '{toolName}' is blocked." },
            ToolPermissionLevel.ApprovalRequired => new ToolPolicyDecision { CanExecute = false, NeedsApproval = true, Message = $"Tool '{toolName}' requires approval." },
            _ => new ToolPolicyDecision { CanExecute = true, NeedsApproval = false, Message = "Allowed" }
        };
    }

    public void LoadFromConfig(List<ToolPermissionConfigEntry>? entries)
    {
        if (entries == null) return;
        foreach (var entry in entries)
        {
            if (Enum.TryParse<ToolPermissionLevel>(entry.Level, true, out var level))
                SetPermission(entry.ToolName, level, entry.Reason);
        }
    }
}