using System.Text;
using System.Text.RegularExpressions;
using ECAssistant.Interfaces;

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
public class EFileReaderTool : ITool
{
    private readonly IFileSystem _fileSystem;
    private readonly string _workingDir;

    public EFileReaderTool(IFileSystem fileSystem, IConfigProvider configProvider)
    {
        _fileSystem = fileSystem;
        _workingDir = configProvider.GetValue("workingDir", Directory.GetCurrentDirectory());
    }

    public string Name => "EFileReader";

    public string Description =>
        "Read a file's contents with line numbers, offset, and limit. " +
        "Prevents context blowups on large files by controlling how much is read. " +
        "Returns line-numbered content plus total line count so you know if there's more.";

    public Task<string> ExecuteAsync(string input, CancellationToken ct = default)
    {
        return ExecuteCore(ParseInput(input));
    }

    public Interfaces.ToolPolicy GetPolicy() => Interfaces.ToolPolicy.Allowed(Name);

    private async Task<string> ExecuteCore(Dictionary<string, string?> arguments)
    {
        var filePath = arguments.GetValueOrDefault("file")?.Trim();
        if (string.IsNullOrEmpty(filePath))
            return $"EFileReader: Missing required argument: file";

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
        if (!_fileSystem.FileExists(fullPath))
            return $"EFileReader: File not found: {filePath}";

        try
        {
            var content = _fileSystem.ReadFile(fullPath);
            var lines = content.Split('\n');
            var totalLines = lines.Length;

            // Apply offset (1-based to 0-based)
            var startIndex = Math.Max(0, offset - 1);
            if (startIndex >= totalLines)
                return $"File: {filePath}\nTotalLines: {totalLines}\n[Offset {offset} is beyond end of file]";

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

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"EFileReader: Error reading file: {ex.Message}";
        }
    }

    private string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path))
            return path;
        return Path.Combine(_workingDir, path);
    }

    private Dictionary<string, string?> ParseInput(string input)
    {
        var args = new Dictionary<string, string?>();
        var matches = Regex.Matches(input, @"<(\w+)>(.*?)</\1>");
        foreach (Match match in matches)
            args[match.Groups[1].Value] = match.Groups[2].Value;
        return args;
    }
}