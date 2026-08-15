using System.Text;
using System.Linq;
using ECAssistant;

namespace ECAssistant.Analysis;

/// <summary>
/// Cross-File Context Analyzer — scans the project directory, builds file relationships,
/// detects patterns, and produces a useful project summary.
/// 
/// v2: Improved with real analysis — line counts, file types, TODOs, circular deps,
/// dependency graph, and a human-readable summary. No more placeholder logic.
/// </summary>
public class EContextAnalyzer : IDisposable
{
    private readonly string _projectRoot;
    private readonly List<FileInfoData> _files = new();
    private List<ProjectRelationship> _relationships = new();
    private ProjectArchitecture? _architecture;

    public EContextAnalyzer(string projectRoot)
    {
        _projectRoot = Path.GetFullPath(projectRoot);
    }

    public async Task<ProjectArchitecture> AnalyzeProjectAsync(
        string? targetDirectory = null, HashSet<string>? extensionsToScan = null)
    {
        var scanDir = targetDirectory ?? _projectRoot;

        // Default extensions to scan
        var exts = extensionsToScan ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".md", ".json", ".txt", ".xml", ".ps1", ".sln",
            ".csproj", ".config", ".sql", ".html", ".css", ".js"
        };

        // ── Phase 1: Scan all files ──
        await ScanFiles(scanDir, exts);

        // ── Phase 2: Analyze content (line counts, TODOs, imports) ──
        foreach (var file in _files)
        {
            await AnalyzeFileContent(file);
        }

        // ── Phase 3: Map relationships (imports, references) ──
        _relationships = MapRelationships();

        // ── Phase 4: Build architecture summary ──
        _architecture = BuildArchitecture();

        return _architecture;
    }

    // ─── Phase 1: File Scanning ──────────────────────

    private async Task ScanFiles(string directory, HashSet<string> extensions)
    {
        _files.Clear();

        if (!Directory.Exists(directory)) return;

        // Skip common irrelevant directories
        var excludeDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", ".vs", ".git", "node_modules", "packages", ".idea" };

        var allFiles = Directory.GetFiles(directory, "*.*", SearchOption.AllDirectories)
            .Where(f => !excludeDirs.Any(ex => f.Contains(Path.DirectorySeparatorChar + ex + Path.DirectorySeparatorChar)))
            .Where(f => extensions.Contains(Path.GetExtension(f)))
            .ToList();

        foreach (var file in allFiles)
        {
            try
            {
                var info = new FileInfo(file);
                _files.Add(new FileInfoData
                {
                    FilePath = file,
                    RelativePath = Path.GetRelativePath(_projectRoot, file),
                    ContentType = GetContentType(file),
                    FileSize = (int)info.Length,
                    LastModified = info.LastWriteTime
                });
            }
            catch (Exception)
            {
            }
        }
    }

    // ─── Phase 2: Content Analysis ────────────────────

    private async Task AnalyzeFileContent(FileInfoData file)
    {
        try
        {
            var content = await File.ReadAllTextAsync(file.FilePath);
            file.LineCount = content.Split('\n', StringSplitOptions.None).Length;
            file.ImportList = ParseImports(content).ToList();

            // Find TODOs
            var todoMatches = System.Text.RegularExpressions.Regex.Matches(
                content, @"(?i)//\s*TODO|//\s*HACK|//\s*FIXME|<!--\s*TODO");
            file.TodoCount = todoMatches.Count;

            // Find class/method definitions (C# only)
            if (file.ContentType == "CSharpCode")
            {
                file.ClassCount = System.Text.RegularExpressions.Regex.Matches(
                    content, @"(?i)\b(class|interface|struct|enum|record)\s+\w+").Count;
                file.MethodCount = System.Text.RegularExpressions.Regex.Matches(
                    content, @"(?i)\b(public|private|protected|internal)\s+(static\s+)?(async\s+)?\w+\s+\w+\s*\(").Count;
            }
        }
        catch { /* Skip unreadable files */ }
    }

    // ─── Phase 3: Relationship Mapping ────────────────

    private List<ProjectRelationship> MapRelationships()
    {
        var rels = new List<ProjectRelationship>();

        foreach (var file in _files)
        {
            if (file.ImportList == null) continue;

            foreach (var import in file.ImportList)
            {
                var target = FindFileByNamespace(import);
                rels.Add(new ProjectRelationship
                {
                    SourceFile = file.RelativePath,
                    TargetFile = target ?? $"(external: {import})",
                    RelationshipType = "Import",
                    Importance = target != null ? 0.9 : 0.3
                });
            }
        }

        return rels;
    }

    // ─── Phase 4: Architecture Detection ──────────────

    private ProjectArchitecture BuildArchitecture()
    {
        var issues = new List<string>();

        // Detect circular dependencies
        var graph = BuildDependencyGraph();
        foreach (var node in graph.Keys)
        {
            var visited = new HashSet<string>();
            if (HasCycle(node, graph, visited, new HashSet<string>()))
            {
                issues.Add($"Circular dependency detected involving: {node}");
            }
        }

        // Detect orphaned files (not referenced by anything, not startup files)
        var referenced = _relationships.Select(r => r.TargetFile).ToHashSet();
        foreach (var file in _files)
        {
            if (!referenced.Contains(file.RelativePath) && !IsStartupFile(file) && file.ContentType != "Markdown")
            {
                issues.Add($"Potentially unused: {file.RelativePath}");
            }
        }

        // Detect files with many TODOs
        var todoHeavy = _files.Where(f => f.TodoCount > 3).ToList();
        foreach (var file in todoHeavy)
        {
            issues.Add($"High TODO count ({file.TodoCount}): {file.RelativePath}");
        }

        // Detect very large files
        var largeFiles = _files.Where(f => f.LineCount > 500).ToList();
        foreach (var file in largeFiles)
        {
            issues.Add($"Large file ({file.LineCount} lines): {file.RelativePath}");
        }

        return new ProjectArchitecture
        {
            ProjectRoot = _projectRoot,
            Files = _files.Count,
            Relationships = _relationships.Count,
            ProjectType = DetectProjectType(),
            DependencyGraph = graph,
            PotentialIssues = issues
        };
    }

    // ─── Helpers ───────────────────────────────────────

    private string GetContentType(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLower();
        return ext switch
        {
            ".cs" => "CSharpCode",
            ".csproj" => "CSharpProject",
            ".sln" => "Solution",
            ".md" => "Markdown",
            ".json" => "Json",
            ".xml" => "Xml",
            ".ps1" => "PowerShell",
            ".sql" => "Sql",
            ".html" => "Html",
            ".css" => "Css",
            ".js" => "JavaScript",
            ".txt" => "Text",
            ".config" => "Config",
            _ => "Unknown"
        };
    }

    private IEnumerable<string> ParseImports(string content)
    {
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("using ") && trimmed.EndsWith(";"))
            {
                yield return trimmed.Substring(6).TrimEnd(';');
            }
        }
    }

    private string? FindFileByNamespace(string ns)
    {
        // Try to match namespace to a file path
        var parts = ns.Split('.');
        if (parts.Length == 0) return null;

        var lastPart = parts[^1];
        foreach (var file in _files)
        {
            if (Path.GetFileNameWithoutExtension(file.RelativePath) == lastPart)
                return file.RelativePath;
        }
        return null;
    }

    private bool IsStartupFile(FileInfoData file)
    {
        var name = Path.GetFileNameWithoutExtension(file.FilePath);
        return new[] { "Program", "Startup", "Main", "App", "index" }
            .Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase));
    }

    private string DetectProjectType()
    {
        var hasCsproj = _files.Any(f => f.ContentType == "CSharpProject");
        var hasSln = _files.Any(f => f.ContentType == "Solution");
        var hasController = _files.Any(f => f.RelativePath.Contains("Controller"));
        var hasTest = _files.Any(f => f.RelativePath.Contains("Test"));
        var hasApi = _files.Any(f => f.RelativePath.Contains("Api"));

        if (hasController && hasApi) return "ASP.NET Web API";
        if (hasController) return "ASP.NET MVC";
        if (hasSln && hasCsproj) return ".NET Solution (multi-project)";
        if (hasCsproj && hasTest) return ".NET Project with Tests";
        if (hasCsproj) return ".NET Console Application";
        return "Mixed/Unknown";
    }

    private Dictionary<string, List<string>> BuildDependencyGraph()
    {
        var graph = new Dictionary<string, List<string>>();
        foreach (var rel in _relationships)
        {
            if (!graph.ContainsKey(rel.SourceFile))
                graph[rel.SourceFile] = new List<string>();
            if (rel.TargetFile != null)
                graph[rel.SourceFile].Add(rel.TargetFile);
        }
        return graph;
    }

    private bool HasCycle(string node, Dictionary<string, List<string>> graph, HashSet<string> visited, HashSet<string> stack)
    {
        visited.Add(node);
        stack.Add(node);

        if (graph.TryGetValue(node, out var deps))
        {
            foreach (var dep in deps)
            {
                if (!visited.Contains(dep) && HasCycle(dep, graph, visited, stack))
                    return true;
                if (stack.Contains(dep))
                    return true;
            }
        }

        stack.Remove(node);
        return false;
    }

    // ─── Public Summary ────────────────────────────────

    public string GetDebugContextSummary()
    {
        if (_architecture == null) return "No analysis run yet.";

        var sb = new StringBuilder();
        sb.AppendLine("=== Project Analysis ===\n");
        sb.AppendLine($"Project type: {_architecture.ProjectType}");
        sb.AppendLine($"Files scanned: {_architecture.Files}");
        sb.AppendLine($"Relationships: {_architecture.Relationships}");
        sb.AppendLine($"Issues found: {_architecture.PotentialIssues.Count}");
        sb.AppendLine();

        // File type breakdown
        var byType = _files.GroupBy(f => f.ContentType).OrderByDescending(g => g.Count());
        sb.AppendLine("File types:");
        foreach (var group in byType)
        {
            sb.AppendLine($"  {group.Key}: {group.Count()} files, {group.Sum(f => f.LineCount)} lines");
        }
        sb.AppendLine();

        // Code stats
        var codeFiles = _files.Where(f => f.ContentType == "CSharpCode").ToList();
        if (codeFiles.Count > 0)
        {
            sb.AppendLine($"C# stats:");
            sb.AppendLine($"  Total lines: {codeFiles.Sum(f => f.LineCount)}");
            sb.AppendLine($"  Total classes: {codeFiles.Sum(f => f.ClassCount)}");
            sb.AppendLine($"  Total methods: {codeFiles.Sum(f => f.MethodCount)}");
            sb.AppendLine($"  Total TODOs: {codeFiles.Sum(f => f.TodoCount)}");
            sb.AppendLine();
        }

        // Issues
        if (_architecture.PotentialIssues.Count > 0)
        {
            sb.AppendLine("Issues:");
            foreach (var issue in _architecture.PotentialIssues.Take(20))
            {
                sb.AppendLine($"  ⚠ {issue}");
            }
            if (_architecture.PotentialIssues.Count > 20)
                sb.AppendLine($"  ... and {_architecture.PotentialIssues.Count - 20} more");
        }

        return sb.ToString();
    }

    public void Dispose() { }
}

// ─── Data Structures ─────────────────────────────────
