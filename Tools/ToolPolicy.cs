using System.Text.Json.Serialization;

namespace ECAssistant.Tools;

/// <summary>
/// Tool permission policy — defines which tools are allowed, which require approval,
/// and which are blocked. Per-agent configurable.
/// 
/// Levels:
/// - Allowed: tool runs freely, no approval needed
/// - ApprovalRequired: tool runs only after user approves via channel
/// - Blocked: tool cannot be used at all
/// </summary>
public enum ToolPermissionLevel
{
    /// <summary>Tool runs without asking — read-only/safe operations</summary>
    Allowed,

    /// <summary>Tool requires user approval before executing — write/modify operations</summary>
    ApprovalRequired,

    /// <summary>Tool is blocked and cannot be used</summary>
    Blocked
}

/// <summary>
/// Permission rule for a single tool.
/// </summary>
public class ToolPermission
{
    /// <summary>Tool name this permission applies to</summary>
    public string ToolName { get; set; } = "";

    /// <summary>Permission level</summary>
    public ToolPermissionLevel Level { get; set; } = ToolPermissionLevel.Allowed;

    /// <summary>Optional: specific argument patterns that require approval (e.g., delete commands)</summary>
    public List<string>? ApprovalPatterns { get; set; }

    /// <summary>Optional: description of why this permission is set</summary>
    public string? Reason { get; set; }
}

/// <summary>
/// Tool policy manager — checks permissions before tool execution.
/// Maintains a registry of tool permissions and enforces them.
/// </summary>
public class ToolPolicy
{
    private readonly Dictionary<string, ToolPermission> _permissions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _defaultAllowed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Create a tool policy with default permissions.
    /// By default, read-only tools are allowed, write tools require approval,
    /// and dangerous tools are blocked.
    /// </summary>
    public ToolPolicy()
    {
        // Default policy — safe-by-default
        SetDefaultPermissions();
    }

    private void SetDefaultPermissions()
    {
        // Read-only tools — always allowed
        _permissions["EFileRead"] = new ToolPermission { ToolName = "EFileRead", Level = ToolPermissionLevel.Allowed, Reason = "Read-only operation" };
        _permissions["EDirList"] = new ToolPermission { ToolName = "EDirList", Level = ToolPermissionLevel.Allowed, Reason = "Read-only operation" };
        _permissions["EFileSearch"] = new ToolPermission { ToolName = "EFileSearch", Level = ToolPermissionLevel.Allowed, Reason = "Read-only operation" };
        _permissions["EFileResearchTool"] = new ToolPermission { ToolName = "EFileResearchTool", Level = ToolPermissionLevel.Allowed, Reason = "Read-only research" };
        _permissions["EFileAnalyzer"] = new ToolPermission { ToolName = "EFileAnalyzer", Level = ToolPermissionLevel.Allowed, Reason = "Read-only analysis" };

        // Write tools — allowed but logged (agent should be able to create/edit files)
        _permissions["EFileWrite"] = new ToolPermission { ToolName = "EFileWrite", Level = ToolPermissionLevel.Allowed, Reason = "File creation — sandboxed to working dir" };
        _permissions["EFileEdit"] = new ToolPermission { ToolName = "EFileEdit", Level = ToolPermissionLevel.Allowed, Reason = "File editing — sandboxed to working dir" };

        // PowerShell — allowed by default (the agent needs to be able to run commands)
        // Future: could be split into read-only PowerShell (allowed) vs write PowerShell (approval)
        _permissions["EPowerShellAgent"] = new ToolPermission { ToolName = "EPowerShellAgent", Level = ToolPermissionLevel.Allowed, Reason = "Command execution — needed for code, build, debugging" };
    }

    /// <summary>Set permission for a specific tool.</summary>
    public void SetPermission(string toolName, ToolPermissionLevel level, string? reason = null)
    {
        _permissions[toolName] = new ToolPermission
        {
            ToolName = toolName,
            Level = level,
            Reason = reason
        };
    }

    /// <summary>Get permission level for a tool. Default: Allowed if unknown.</summary>
    public ToolPermissionLevel GetPermissionLevel(string toolName)
    {
        if (_permissions.TryGetValue(toolName, out var perm))
            return perm.Level;
        return ToolPermissionLevel.Allowed; // Default: allow unknown tools
    }

    /// <summary>Check if a tool is allowed to run without approval.</summary>
    public bool IsAllowed(string toolName)
        => GetPermissionLevel(toolName) == ToolPermissionLevel.Allowed;

    /// <summary>Check if a tool requires approval before running.</summary>
    public bool RequiresApproval(string toolName)
        => GetPermissionLevel(toolName) == ToolPermissionLevel.ApprovalRequired;

    /// <summary>Check if a tool is blocked.</summary>
    public bool IsBlocked(string toolName)
        => GetPermissionLevel(toolName) == ToolPermissionLevel.Blocked;

    /// <summary>Check if a tool can be used at all (allowed or approval required, but not blocked).</summary>
    public bool IsUsable(string toolName)
        => GetPermissionLevel(toolName) != ToolPermissionLevel.Blocked;

    /// <summary>Get all permissions as a list (for display/config).</summary>
    public List<ToolPermission> GetAllPermissions()
        => _permissions.Values.ToList();

    /// <summary>Check permission for a tool call and return a policy decision.</summary>
    public ToolPolicyDecision Check(string toolName, Dictionary<string, string?> args)
    {
        var level = GetPermissionLevel(toolName);

        switch (level)
        {
            case ToolPermissionLevel.Blocked:
                return ToolPolicyDecision.Deny($"Tool '{toolName}' is blocked. Reason: {_permissions.GetValueOrDefault(toolName)?.Reason ?? "Unknown"}");

            case ToolPermissionLevel.ApprovalRequired:
                return ToolPolicyDecision.RequiresApproval($"Tool '{toolName}' requires user approval before executing.");

            case ToolPermissionLevel.Allowed:
            default:
                return ToolPolicyDecision.Allow();
        }
    }

    /// <summary>Load permissions from a config section (for future JSON config support).</summary>
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

/// <summary>Result of a tool policy check.</summary>
public class ToolPolicyDecision
{
    public bool CanExecute { get; init; }
    public bool NeedsApproval { get; init; }
    public string Message { get; init; } = "";

    public static ToolPolicyDecision Allow() => new() { CanExecute = true, NeedsApproval = false, Message = "Allowed" };
    public static ToolPolicyDecision RequiresApproval(string msg) => new() { CanExecute = false, NeedsApproval = true, Message = msg };
    public static ToolPolicyDecision Deny(string msg) => new() { CanExecute = false, NeedsApproval = false, Message = msg };
}

/// <summary>Config entry for tool permissions in appsettings.json.</summary>
public class ToolPermissionConfigEntry
{
    [JsonPropertyName("tool")]
    public string ToolName { get; set; } = "";

    [JsonPropertyName("level")]
    public string Level { get; set; } = "Allowed";

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}