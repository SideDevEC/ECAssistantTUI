namespace ECAssistant.Engine;

/// <summary>
/// A group of toolcalls that can execute in parallel.
/// Groups are ordered — group N depends on results from groups 0..N-1.
/// </summary>
public class DependencyGroup
{
    public List<ToolCallRequest> ToolCalls { get; set; } = new();
    public int GroupIndex { get; set; }

    public bool IsParallel => ToolCalls.Count > 1;

    public override string ToString()
    {
        var names = string.Join(", ", ToolCalls.Select(tc => $"{tc.ToolName}#{tc.Index}"));
        return $"Group {GroupIndex} ({ToolCalls.Count} call{(ToolCalls.Count > 1 ? "s" : "")}): {names}";
    }
}