using ECAssistant;

namespace ECAssistant.Engine;

/// <summary>
/// A single parsed tool call request from the LLM response.
/// </summary>
public class ToolCallRequest
{
    public string? ToolName { get; set; }
    public Dictionary<string, string?> Args { get; set; } = new();
    public int Index { get; set; }

    public override string ToString() => $"[{Index}] {ToolName}({string.Join(", ", Args.Select(kvp => $"{kvp.Key}={StringUtil.Truncate(kvp.Value ?? "", 40)}"))})";
}