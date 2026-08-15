using System.Text;
using System.Text.RegularExpressions;
using ECAssistant.Interfaces;

namespace ECAssistant.Tools.Research;

/// <summary>
/// EFileResearchTool — scan project files, read content for LLM analysis.
/// Use for: finding code patterns, checking file structure, reading source code,
/// researching project dependencies.
/// </summary>
public class EFileResearchTool : ITool
{
    private readonly IFileSystem _fileSystem;
    private readonly string _searchRoot;
    private readonly HashSet<string> _defaultExtensions;
    private readonly int _maxCharsPerFile;

    public EFileResearchTool(IFileSystem fileSystem, IConfigProvider configProvider)
    {
        _fileSystem = fileSystem;
        _searchRoot = Path.GetFullPath(configProvider.GetValue("searchRoot", Directory.GetCurrentDirectory()));
        _defaultExtensions = new HashSet<string>(
            configProvider.GetValue("researchExtensions", ".cs,.md,.json,.xml,.yml,.yaml,.txt,.sh,.ps1,.py,.js,.ts").Split(','),
            StringComparer.OrdinalIgnoreCase);
        _maxCharsPerFile = configProvider.GetInt("maxCharsPerFile", 10000);
    }

    public string Name => "EFileResearchTool";

    public string Description =>
        "Scan project files, read content for LLM analysis. Use for: finding code patterns, checking file structure, reading source code, researching project dependencies.";

    public Task<string> ExecuteAsync(string input, CancellationToken ct = default)
    {
        return ExecuteCore(ParseInput(input), ct);
    }

    public Interfaces.ToolPolicy GetPolicy() => Interfaces.ToolPolicy.Allowed(Name);

    private async Task<string> ExecuteCore(Dictionary<string, string?> arguments, CancellationToken ct)
    {
        try
        {
            var query = arguments.GetValueOrDefault("query") ?? "";
            var maxFiles = arguments.TryGetValue("max_files", out var mf) && int.TryParse(arguments["max_files"], out int n) ? n : 20;
            var extensionsList = arguments.TryGetValue("extensions", out var exStr)
                ? new HashSet<string>(exStr!.Split(',', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase)
                : _defaultExtensions;

            // Cancellation support
            if (ct.IsCancellationRequested)
                return "[CANCELLED] File research was cancelled by user.";

            // IFileSystem.ListFiles is not recursive, so we do a recursive scan manually
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
                    if (ct.IsCancellationRequested)
                    {
                        sb.AppendLine("FileResearch: Cancelled mid-scan.");
                        break;
                    }
                    var content = _fileSystem.ReadFile(filePath);
                    if (content.Length > _maxCharsPerFile)
                        content = content.Substring(0, _maxCharsPerFile) + "\n... [truncated]";
                    // Escape angle brackets to prevent XML tag confusion in LLM history
                    content = content.Replace("\u003c", "<").Replace("\u003e", ">");

                    sb.AppendLine($"## {relativePath} ({content.Length} chars)");
                    sb.AppendLine(content);
                    sb.AppendLine();
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"!! Error reading {filePath}: {ex.Message}\n");
                }
            }

            return $"Research results for: {query}\n{sb}\nFiles scanned: {filtered.Count}";
        }
        catch (UnauthorizedAccessException ex)
        {
            return $"Access denied: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Recursively list all files under a directory (IFileSystem.ListFiles is not recursive).
    /// </summary>
    private List<string> ListFilesRecursive(string directory)
    {
        var result = new List<string>();
        var files = _fileSystem.ListFiles(directory, "*");
        result.AddRange(files);

        var subDirs = Directory.GetDirectories(directory);
        foreach (var subDir in subDirs)
        {
            result.AddRange(ListFilesRecursive(subDir));
        }

        return result;
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