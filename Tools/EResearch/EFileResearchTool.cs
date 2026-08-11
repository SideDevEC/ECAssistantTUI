using static ECAssistant.EColor;
using System.Text;
using ECAssistant.Tools;
using ECAssistant;

namespace ECAssistant.Tools.Research;

public class EFileResearchTool : EToolBase
{
    private readonly string _searchRoot;
    private readonly HashSet<string> _defaultExtensions = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maxCharsPerFile;

    public EFileResearchTool(string searchRoot, HashSet<string>? defaultExtensions = null, int maxCharsPerFile = 10000)
    {
        _searchRoot = Path.GetFullPath(searchRoot);
        _defaultExtensions = defaultExtensions ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _maxCharsPerFile = maxCharsPerFile;
    }

    public override string Name => "EFileResearchTool";

    public override string Description =>
        "Scan project files, read content for LLM analysis. Use for: finding code patterns, checking file structure, reading source code, researching project dependencies.";

    public override string UsageExample => "EFileResearchTool.Research(query=\"find all async methods\", extensions=[\".cs\",\".md\"])";

    public override async Task<EToolResult> ExecuteAsync(Dictionary<string, string?> arguments)
    {
        try
        {
            var query = arguments.GetValueOrDefault("query") ?? "";
            var maxFiles = arguments.TryGetValue("max_files", out var mf) && int.TryParse(mf, out int n) ? n : 20;
            var extensionsList = arguments.TryGetValue("extensions", out var exStr)
                ? new HashSet<string>(exStr!.Split(',', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase)
                : _defaultExtensions;

            var allFiles = Directory.GetFiles(_searchRoot, "*.*", SearchOption.AllDirectories);
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
                    var content = await File.ReadAllTextAsync(filePath);
                    if (content.Length > _maxCharsPerFile)
                        content = content.Substring(0, _maxCharsPerFile) + "\n... [truncated]";

                    sb.AppendLine($"## {relativePath} ({content.Length} chars)");
                    sb.AppendLine(content);
                    sb.AppendLine();
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"!! Error reading {filePath}: {ex.Message}\n");
                }
            }

            return EToolResult.Success(Name,
                $"Research results for: {query}\n{sb}\nFiles scanned: {filtered.Count}");
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

    public override string GetToolExample()
        => "<toolcall>EFileResearchTool<files>.cs .md</files></toolcall>";
}
