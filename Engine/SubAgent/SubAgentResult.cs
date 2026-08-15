using System.Text;

namespace ECAssistant.Engine;

public class SubAgentResult
{
    public bool Succeeded { get; set; }
    public string FinalOutput { get; set; } = "";
    public int ToolCallsMade { get; set; }
    public TimeSpan Duration { get; set; }
    public SubAgentError? Error { get; set; }
    public List<string> FilesCreated { get; set; } = new();
    public List<string> FilesModified { get; set; } = new();
    public List<string> ToolCallLog { get; set; } = new();

    public string ErrorString => Error?.Message ?? (Succeeded ? "" : "Unknown error");

    public string ToContextString()
    {
        if (Succeeded)
        {
            return $"✅ Sub-agent succeeded ({ToolCallsMade} tool calls, {Duration.TotalSeconds:F1}s)\n" +
                   $"Output: {FinalOutput}";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"❌ Sub-agent failed ({Duration.TotalSeconds:F1}s)");
        if (Error != null)
            sb.Append(Error.ToStructuredString());
        if (FilesCreated.Count > 0)
            sb.AppendLine($"Files created (partial): {string.Join(", ", FilesCreated)}");
        if (FilesModified.Count > 0)
            sb.AppendLine($"Files modified (partial): {string.Join(", ", FilesModified)}");
        if (!string.IsNullOrEmpty(FinalOutput))
            sb.AppendLine($"Partial output: {FinalOutput}");
        return sb.ToString();
    }
}