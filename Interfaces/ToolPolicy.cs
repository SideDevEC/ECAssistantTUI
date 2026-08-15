namespace ECAssistant.Interfaces;

/// <summary>
/// Tool permission policy record.
/// </summary>
public record ToolPolicy(
    string ToolName,
    string Level,
    string? Reason = null
)
{
   // Stateless factory — immutable record
    public static ToolPolicy Allowed(string name) => new(name, "Allowed");
   // Stateless factory — immutable record
    public static ToolPolicy Approved(string name) => new(name, "Approved");
   // Stateless factory — immutable record
    public static ToolPolicy Blocked(string name, string reason) => new(name, "Blocked", reason);
}