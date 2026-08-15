using System.Text;
using System.Text.Json;
using ECAssistant.Config;
using ECAssistant.Interfaces;

namespace ECAssistant.Tools.Reader;

/// <summary>
/// EFileReader — read file contents with offset/limit/token-budget control.
/// </summary>
public class EFileReaderTool : EToolBase
{
    private readonly IFileSystem _fileSystem;
    private readonly JsonElement? _toolConfig;
    private readonly string _workingDir;

    public override string Name => "EFileReader";

    public override string Description =>
        "Read a file's contents with line numbers, offset, and limit. " +
        "Prevents context blowups on large files by controlling how much is read. " +
        "Returns line-numbered content plus total line count so you know if there's more.";

    public override string UsageExample =>
        "<toolcall>EFileReader<file>Program.cs</file><offset>1</offset><limit>50</limit></toolcall>";

    public override bool IsEnabled { get; protected set; } = true;

    public EFileReaderTool(IFileSystem fileSystem, EAgentConfig config)
    {
        _fileSystem = fileSystem;
        config.Tools.TryGetValue(Name, out var tc);
        _toolConfig = tc.ValueKind == JsonValueKind.Undefined ? null : tc;
        IsEnabled = ReadCfg(_toolConfig, "enabled", true);
        _workingDir = config.AgentSettings.WorkingDirectory;
    }

    public override object GetConfigSection() => new { enabled = true };

    public override Task<EToolResult> ExecuteAsync(Dictionary<string, string?> arguments, CancellationToken cancellationToken = default)
    {
        var filePath = arguments.GetValueOrDefault("file")?.Trim();
        if (string.IsNullOrEmpty(filePath))
            return Task.FromResult(EToolResult.Failure(Name, "Missing required argument: file"));

        var offset = 1;
        if (arguments.TryGetValue("offset", out var offsetStr) && int.TryParse(offsetStr, out var o))
            offset = Math.Max(1, o);

        var limit = 100;
        if (arguments.TryGetValue("limit", out var limitStr) && int.TryParse(limitStr, out var l))
            limit = Math.Max(1, Math.Min(500, l));

        var maxChars = 4000;
        if (arguments.TryGetValue("maxchars", out var mcStr) && int.TryParse(mcStr, out var mc))
            maxChars = Math.Max(100, Math.Min(20000, mc));

        var fullPath = ResolvePath(filePath);
        if (!_fileSystem.FileExists(fullPath))
            return Task.FromResult(EToolResult.Failure(Name, $"File not found: {filePath}"));

        try
        {
            var content = _fileSystem.ReadFile(fullPath);
            var lines = content.Split('\n');
            var totalLines = lines.Length;

            var startIndex = Math.Max(0, offset - 1);
            if (startIndex >= totalLines)
                return Task.FromResult(EToolResult.Failure(Name, $"Offset {offset} is beyond end of file (total: {totalLines} lines)"));

            var available = totalLines - startIndex;
            var take = Math.Min(limit, available);

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

            return Task.FromResult(EToolResult.Success(Name, sb.ToString()));
        }
        catch (Exception ex)
        {
            return Task.FromResult(EToolResult.Failure(Name, $"Error reading file: {ex.Message}"));
        }
    }

    private static T ReadCfg<T>(JsonElement? section, string key, T defaultValue)
    {
        if (section.HasValue && section.Value.ValueKind == JsonValueKind.Object)
        {
            if (section.Value.TryGetProperty(key, out var prop))
            {
                try { return prop.Deserialize<T>() ?? defaultValue; } catch { return defaultValue; }
            }
        }
        return defaultValue;
    }

    private string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path)) return path;
        return Path.Combine(_workingDir, path);
    }
}