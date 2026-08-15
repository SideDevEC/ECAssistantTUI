using ECAssistant.Tools;
using System.IO;
using System.Text.Json;
using System.Text;
using ECAssistant;

namespace ECAssistant.Tools.Example;

/// <summary>
/// EXAMPLE TOOL — Extends EToolBase to show how to add a new tool.
/// </summary>
public class EFileAnalyzer : EToolBase
{
    private readonly string _workingDir;

    public EFileAnalyzer(string workingDir) => _workingDir = workingDir;

    public override string Name => "EFileAnalyzer";

    public override string Description =>
        @"Analyze any text file in the working directory. Reads content, counts lines/words, " +
        @"identifies patterns, and reports findings to the agent for further analysis.";

    public override string UsageExample =>
        @"EFileAnalyzer.Analyze(filePath=""config.json"");";

    public override async Task<EToolResult> ExecuteAsync(Dictionary<string, string?> arguments, CancellationToken cancellationToken = default)
    {
        var filePath = arguments.GetValueOrDefault("filePath");
        if (string.IsNullOrWhiteSpace(filePath))
            return new EToolResult { ToolName = Name, Succeeded = false, Error = "Missing 'filePath' argument." };

        try
        {
            var fullPath = Path.Combine(_workingDir, filePath);
            if (!File.Exists(fullPath))
                return new EToolResult { ToolName = Name, Succeeded = false, Error = $"File not found: {fullPath}" };

            var content = await File.ReadAllTextAsync(fullPath);
            var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var words = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            var metadata = new Dictionary<string, string>
            {
                ["size_bytes"] = new FileInfo(fullPath).Length.ToString(),
                ["line_count"] = lines.Length.ToString(),
                ["word_count"] = words.Length.ToString(),
            };

            var output = $@"File: {filePath}
Size: {metadata["size_bytes"]} bytes
Lines: {metadata["line_count"]}
Words: {metadata["word_count"]}
Content preview (first 500 chars):
---
{(content.Length > 500 ? content.Substring(0, 500) : content)}
---";

            return new EToolResult { ToolName = Name, Succeeded = true, Output = output, Metadata = metadata };
        }
        catch (Exception ex)
        {
            return new EToolResult { ToolName = Name, Succeeded = false, Error = $"Error reading file: {ex.Message}" };
        }
    }

    public override string GetExtendedSystemPrompt()
    {
        return @"The EFileAnalyzer tool lets you read and analyze any text file. Use it before editing code files to understand their structure.";
    }

    public override string GetToolExample()
        => "<toolcall>EFileAnalyzer<path>Program.cs</path></toolcall>";
}