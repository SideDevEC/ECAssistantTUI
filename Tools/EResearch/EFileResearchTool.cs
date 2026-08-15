using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ECAssistant.Config;
using ECAssistant.Interfaces;

namespace ECAssistant.Tools.Research;

/// <summary>
/// EFileResearchTool — scan project files, read content for LLM analysis.
/// </summary>
public class EFileResearchTool : EToolBase
{
    private readonly IFileSystem _fileSystem;
    private readonly JsonElement? _toolConfig;
    private readonly string _searchRoot;
    private readonly HashSet<string> _defaultExtensions;
    private readonly int _maxCharsPerFile;

    public override string Name => "EFileResearchTool";

    public override string Description =>
        "Scan project files, read content for LLM analysis. Use for: finding code patterns, " +
        "checking file structure, reading source code, researching project dependencies.";

    public override string UsageExample =>
        "<toolcall>EFileResearchTool<query>find all controllers</query><max_files>20</max_files></toolcall>";

    public override bool IsEnabled { get; protected set; } = true;

    public EFileResearchTool(IFileSystem fileSystem, EAgentConfig config)
    {
        _fileSystem = fileSystem;
        _searchRoot = Path.GetFullPath(config.AgentSettings.WorkingDirectory);
        config.Tools.TryGetValue(Name, out var tc);
        _toolConfig = tc.ValueKind == JsonValueKind.Undefined ? null : tc;
        IsEnabled = ReadCfg(_toolConfig, "enabled", true);
        var exts = ReadCfg(_toolConfig, "default_extensions", new[] { ".cs", ".md", ".json", ".txt", ".xml", ".sql", ".html", ".css", ".js" });
        _defaultExtensions = new HashSet<string>(exts, StringComparer.OrdinalIgnoreCase);
        _maxCharsPerFile = ReadCfg(_toolConfig, "max_chars_per_file", 15000);
    }

    public override object GetConfigSection() => new
    {
        enabled = true,
        default_extensions = new[] { ".cs", ".md", ".json", ".txt", ".xml", ".sql", ".html", ".css", ".js" },
        max_chars_per_file = 15000,
        max_files_to_scan = 50,
        query_limit = 20
    };

    public override async Task<EToolResult> ExecuteAsync(Dictionary<string, string?> arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            var query = arguments.GetValueOrDefault("query") ?? "";
            var maxFiles = arguments.TryGetValue("max_files", out var mf) && int.TryParse(arguments["max_files"], out int n) ? n : 20;
            var extensionsList = arguments.TryGetValue("extensions", out var exStr)
                ? new HashSet<string>(exStr!.Split(',', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase)
                : _defaultExtensions;

            if (cancellationToken.IsCancellationRequested)
                return EToolResult.Failure(Name, "File research was cancelled by user.");

            var allFiles = ListFilesRecursive(_searchRoot);
            var filtered = allFiles.Where(f =>
                extensionsList.Any(ext => Path.GetExtension(f).Equals(ext, StringComparison.OrdinalIgnoreCase)))
                .Take(maxFiles).ToList();

            var sb = new StringBuilder();
            sb.AppendLine($"Found {filtered.Count} files matching query: {query}\n");

            foreach (var filePath in filtered)
            {
                try
                {
                    var relativePath = Path.GetRelativePath(_searchRoot, filePath);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        sb.AppendLine("FileResearch: Cancelled mid-scan.");
                        break;
                    }
                    var content = _fileSystem.ReadFile(filePath);
                    if (content.Length > _maxCharsPerFile)
                        content = content.Substring(0, _maxCharsPerFile) + "\n... [truncated]";
                    content = content.Replace("<", "<").Replace(">", ">");

                    sb.AppendLine($"## {relativePath} ({content.Length} chars)");
                    sb.AppendLine(content);
                    sb.AppendLine();
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"!! Error reading {filePath}: {ex.Message}\n");
                }
            }

            return EToolResult.Success(Name, $"Research results for: {query}\n{sb}\nFiles scanned: {filtered.Count}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return EToolResult.Failure(Name, $"Access denied: {ex.Message}");
        }
        catch (Exception ex)
        {
            return EToolResult.Failure(Name, $"Error: {ex.Message}");
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

    private List<string> ListFilesRecursive(string directory)
    {
        var result = new List<string>();
        var files = _fileSystem.ListFiles(directory, "*");
        result.AddRange(files);

        var subDirs = Directory.GetDirectories(directory);
        foreach (var subDir in subDirs)
            result.AddRange(ListFilesRecursive(subDir));

        return result;
    }
}