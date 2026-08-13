using System.Text;
using System.Text.RegularExpressions;
using ECAssistant.Session;

namespace ECAssistant.Tools.Reader;

/// <summary>
/// EFileReader — read file contents with offset/limit/token-budget control.
///
/// Prevents context blowups from large files. The LLM specifies how much it needs:
/// - offset: line number to start reading from (1-based, default 1)
/// - limit: max lines to read (default 100)
/// - maxchars: max total chars to read (default 4000)
///
/// Returns the file content with line numbers, plus metadata about total lines
/// so the LLM knows if there's more to read.
/// </summary>
public class EFileReaderTool : EToolBase
{
    private readonly string _workingDir;

    public EFileReaderTool(string workingDir)
    {
        _workingDir = workingDir;
    }

    public override string Name => "EFileReader";

    public override string Description =>
        "Read a file's contents with line numbers, offset, and limit. " +
        "Prevents context blowups on large files by controlling how much is read. " +
        "Returns line-numbered content plus total line count so you know if there's more.";

    public override string UsageExample =>
        "<toolcall>EFileReader<file>src/Program.cs</file></toolcall>\n" +
        "<toolcall>EFileReader<file>src/Program.cs</file><offset>50</offset><limit>30</limit></toolcall>\n" +
        "<toolcall>EFileReader<file>large.log</file><offset>1</offset><limit>100</limit><maxchars>4000</maxchars></toolcall>";

    public override string GetToolRules() =>
        "Rules:\n" +
        "1. file (required): path to the file to read (relative to working dir or absolute)\n" +
        "2. offset (optional): line number to start from (1-based, default 1)\n" +
        "3. limit (optional): max lines to read (default 100)\n" +
        "4. maxchars (optional): max total chars to read (default 4000)\n" +
        "5. Always check TotalLines in the result — if offset+limit < TotalLines, there's more to read\n" +
        "6. Prefer EFileReader over EShellAgent `cat` for reading files — controlled, no context blowup";

    public override string GetToolExample() =>
        "Examples:\n" +
        "Read first 100 lines: <toolcall>EFileReader<file>src/Program.cs</file></toolcall>\n" +
        "Read lines 50-80: <toolcall>EFileReader<file>src/Program.cs</file><offset>50</offset><limit>30</limit></toolcall>\n" +
        "Read with char budget: <toolcall>EFileReader<file>big.log</file><maxchars>2000</maxchars></toolcall>";

    public override async Task<EToolResult> ExecuteAsync(Dictionary<string, string?> arguments, CancellationToken cancellationToken = default)
    {
        var filePath = arguments.GetValueOrDefault("file")?.Trim();
        if (string.IsNullOrEmpty(filePath))
            return EToolResult.Failure(Name, "Missing required argument: file");

        // Parse optional args
        var offset = 1;
        if (arguments.TryGetValue("offset", out var offsetStr) && int.TryParse(offsetStr, out var o))
            offset = Math.Max(1, o);

        var limit = 100;
        if (arguments.TryGetValue("limit", out var limitStr) && int.TryParse(limitStr, out var l))
            limit = Math.Max(1, Math.Min(500, l));

        var maxChars = 4000;
        if (arguments.TryGetValue("maxchars", out var mcStr) && int.TryParse(mcStr, out var mc))
            maxChars = Math.Max(100, Math.Min(20000, mc));

        // Resolve path
        var fullPath = ResolvePath(filePath);
        if (!File.Exists(fullPath))
            return EToolResult.Failure(Name, $"File not found: {filePath}");

        try
        {
            var lines = await File.ReadAllLinesAsync(fullPath, cancellationToken);
            var totalLines = lines.Length;

            // Apply offset (1-based to 0-based)
            var startIndex = Math.Max(0, offset - 1);
            if (startIndex >= totalLines)
                return EToolResult.Success(Name,
                    $"File: {filePath}\nTotalLines: {totalLines}\n[Offset {offset} is beyond end of file]");

            // Take lines within limit
            var available = totalLines - startIndex;
            var take = Math.Min(limit, available);

            // Build output with line numbers, respecting maxChars
            var sb = new StringBuilder();
            sb.AppendLine($"File: {filePath}");
            sb.AppendLine($"TotalLines: {totalLines}");
            sb.AppendLine($"Showing: lines {offset}–{offset + take - 1} of {totalLines}");
            sb.AppendLine();

            var charCount = 0;
            var linesShown = 0;
            for (int i = startIndex; i < startIndex + take && i < totalLines; i++)
            {
                var lineText = $"{i + 1,5}: {lines[i]}";
                if (charCount + lineText.Length + 1 > maxChars && linesShown > 0)
                {
                    sb.AppendLine($"[Truncated at {maxChars} chars. Use offset={offset + linesShown} to continue.]");
                    break;
                }
                sb.AppendLine(lineText);
                charCount += lineText.Length + 1;
                linesShown++;
            }

            if (offset + linesShown < totalLines)
                sb.AppendLine($"\n[More available: {totalLines - offset - linesShown + 1} lines remaining. Use offset={offset + linesShown} to read more.]");

            var metadata = new Dictionary<string, string>
            {
                { "totalLines", totalLines.ToString() },
                { "linesShown", linesShown.ToString() },
                { "offset", offset.ToString() },
                { "file", filePath }
            };

            return EToolResult.Success(Name, sb.ToString(), metadata);
        }
        catch (Exception ex)
        {
            return EToolResult.Failure(Name, $"Error reading file: {ex.Message}");
        }
    }

    private string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path))
            return path;
        return Path.Combine(_workingDir, path);
    }
}