using static ECAssistant.EColor;

using System.Text;
using System.Linq;
using System.Xml.Linq;
using ECAssistant;

namespace ECAssistant.Analysis;

/// <summary>
/// Cross-File Context Analyzer — reads all files, builds relationship maps, detects patterns and issues.
/// </summary>
public class EContextAnalyzer : IDisposable
{
    private readonly string _projectRoot;
    private readonly Dictionary<string, FileInfoData> _fileRegistry = new(StringComparer.OrdinalIgnoreCase);
    private List<ProjectRelationship> _relationships = new();
    private ProjectArchitecture? _architecture;

     public EContextAnalyzer(string projectRoot)
         {
            _projectRoot = Path.GetFullPath(projectRoot);
            EColor.TagBold(Cyan, "Analyzer", $"Initialized for: {_projectRoot}");
         }

    public async Task<ProjectArchitecture> AnalyzeProjectAsync(
        string? targetDirectory = null, HashSet<string>? extensionsToScan = null)
       {
          var scanResult = await ScanDirectory(targetDirectory ?? _projectRoot);
            EColor.Tag(Success(), "Scan", $"Scanned {scanResult.Files.Count} files.");

         RegisterFiles(scanResult.Files);
            EColor.Tag(Success(), "Registry", $"Registered {_fileRegistry.Count} files.");

              _relationships = MapFileRelationships();
            EColor.Tag(Info(), "Map", $"Found {_relationships.Count} file relationships.");

              _architecture = BuildArchitectureMap(_relationships);
            EColor.TagBold(Info(), "Arch", $"Architecture: {_architecture.ProjectType}");

         return _architecture;
       }

    private async Task<DirectoryScanResult> ScanDirectory(string directory)
           {
          var result = new DirectoryScanResult();
             if (!Directory.Exists(directory)) return result;

            var allFiles = Directory.GetFiles(directory, "*.*", SearchOption.AllDirectories);
               foreach (var file in allFiles)
                   {
                  try
                      {
                   var relativePath = Path.GetRelativePath(_projectRoot, file);
                       var content = await File.ReadAllTextAsync(file);
                     result.Files.Add(new FileInfoData 
                         {
                            FilePath = file,
                          RelativePath = relativePath,
                       ContentType = GetContentType(file),
                           FileSize = content.Length,
                          LastModified = File.GetLastWriteTime(file)
                         });
                       }
                   catch (Exception ex)
                      {
                       EColor.Tag(Info(), "Skip", $"Skipped {file}: {ex.Message}");
                       }
                   }

            return result;
        }

    private void RegisterFiles(List<FileInfoData> files)
           {
          foreach (var file in files)
              {
                 if (file.FileSize > 102400 || IsBinaryFile(file.ContentType)) continue;
                      _fileRegistry[file.RelativePath] = file;

                   var content = File.ReadAllText(file.FilePath);
                    foreach (var l in content.Split('\n'))
                        {
                       if (l.Trim().StartsWith("using "))
                              {
                           var ns = l.Trim().Substring(6).TrimEnd(';');
                               _relationships.Add(new ProjectRelationship
                                  {
                                   SourceFile = file.RelativePath,
                                  TargetFile = FindMatchingFile(ns),
                                  RelationshipType = "Import",
                                  Importance = 0.9
                                  });
                              }
                        }
                   }
        }

    private List<ProjectRelationship> MapFileRelationships()
         {
          var relationships = new List<ProjectRelationship>();
             foreach (var kvp in _fileRegistry)
                 {
                  var file = kvp.Value;
                    var content = File.ReadAllText(file.FilePath);
                   foreach (var import in ParseImportStatements(content))
                       {
                        relationships.Add(new ProjectRelationship 
                           {
                             SourceFile = file.RelativePath,
                             TargetFile = FindMatchingFile(import),
                            RelationshipType = "Import",
                            Importance = CalculateImportance(file, import)
                           });
                        }
                     }

            return relationships;
         }

    private ProjectArchitecture BuildArchitectureMap(List<ProjectRelationship> relationships)
          {
          var arch = new ProjectArchitecture 
             {
             ProjectRoot = _projectRoot,
              Files = _fileRegistry.Count,
             Relationships = relationships.Count,
              ProjectType = DetectProjectType(relationships),
             DependencyGraph = BuildDependencyGraph(relationships),
             PotentialIssues = IdentifyPotentialIssues(relationships)
               };

            return arch;
          }

    private string DetectProjectType(List<ProjectRelationship> relationships)
         {
          var hasWeb = relationships.Any(r => r.TargetFile?.Contains("Controller") == true || r.TargetFile?.Contains("View") == true);
            var hasDb = relationships.Any(r => r.TargetFile?.Contains("Repository") == true || r.TargetFile?.Contains("DbContext") == true);
              var hasApi = relationships.Any(r => r.TargetFile?.Contains("ApiController") == true);
               var hasConsole = _fileRegistry.Any(f => f.Value.FilePath.EndsWith(".csproj") && f.Value.ContentType == "CSharpProject");
                var hasUnitTest = _fileRegistry.Any(f => f.Value.FilePath.Contains("Test"));

            if (hasWeb && hasDb) return "ASP.NET MVC";
              else if (hasApi && hasWeb) return "REST API with Frontend";
               else if (hasConsole) return "Console Application";
              else if (hasUnitTest) return "Test-Driven Project";
                else return "Mixed/Unknown Architecture";
         }

    private Dictionary<string, List<string>> BuildDependencyGraph(List<ProjectRelationship> relationships)
         {
          var graph = new Dictionary<string, List<string>>();
             foreach (var rel in relationships)
                 {
                  if (!graph.ContainsKey(rel.SourceFile)) graph[rel.SourceFile] = new List<string>();
                      if (rel.TargetFile != null) graph[rel.SourceFile].Add(rel.TargetFile);
                     }

            return graph;
         }

    private List<string> IdentifyPotentialIssues(List<ProjectRelationship> relationships)
         {
          var issues = new List<string>();
             foreach (var file in _fileRegistry.Values)
                 {
                  var isReferenced = relationships.Any(r => r.TargetFile == file.RelativePath);
                     if (!isReferenced && !IsStartupFile(file))
                        issues.Add($"Potentially unused: {file.RelativePath}");
                     }

            foreach (var rel in relationships)
              {
               var hasCycle = relationships.Any(r => r.TargetFile == rel.SourceFile && r.SourceFile == rel.TargetFile);
                  if (hasCycle) issues.Add($"Circular dependency: {rel.SourceFile} <-> {rel.TargetFile}");
                 }

            return issues;
         }

    private List<string> ParseImportStatements(string content)
          {
          var imports = new List<string>();
             foreach (var line in content.Split('\n'))
                 {
                  if (line.Trim().StartsWith("using "))
                      {
                       var ns = line.Trim().Substring(6).TrimEnd(';');
                          imports.Add(ns);
                            }
                           }

            return imports;
         }

    private string GetContentType(string filePath)
          {
          var ext = Path.GetExtension(filePath).ToLower();
          return ext switch
              {
               ".cs" => "CSharpCode",
                ".csproj" => "CSharpProject",
                  _ => "Unknown"
                 };
           }

    private bool IsBinaryFile(string contentType) => contentType == "Unknown";

    private string? FindMatchingFile(string nsName)
          {
          foreach (var kvp in _fileRegistry)
               {
              if (kvp.Key.Contains(nsName)) return kvp.Key;
                  }

            return null;
         }

     public async Task<FileAnalysisResult> AnalyzeFileAsync(string filePath)
           {
          var file = new FileInfo(filePath);
             if (!file.Exists) return new FileAnalysisResult { FilePath = filePath, Status = "NotFound" };

              var content = await File.ReadAllTextAsync(file.FullName);
                 var imports = ParseImportStatements(content);
                   return new FileAnalysisResult 
                        {
                          FilePath = file.FullName,
                            ContentLength = content.Length,
                       ImportsFound = imports.Count,
                   ImportList = imports.ToList(),
                        Status = "Analyzed"
                             };
                           }

     public string GetDebugContextSummary()
          {
          var sb = new StringBuilder();
             sb.AppendLine("=== Context Analyzer Summary ===");
                sb.AppendLine($"Files analyzed: {_fileRegistry.Count}");
            sb.AppendLine($"Relationships found: {_relationships.Count}");
                sb.AppendLine($"Architecture type: {_architecture?.ProjectType ?? "Unknown"}");

            return sb.ToString();
         }

     public void Dispose() { EColor.TagBold(Info(), "Analyzer", "Disposed."); }
        private double CalculateImportance(FileInfoData source, string targetNs) => 0.6;
        private bool IsStartupFile(FileInfoData fileInfo)
           { var fn = Path.GetFileNameWithoutExtension(fileInfo.FilePath);
             return new[] { "Program", "Startup", "Main", "App" }.Any(n => fn.Contains(n)); }
}

public class FileInfoData
{
    public string FilePath { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string ContentType { get; set; } = "";
    public int FileSize { get; set; }
    public DateTime LastModified { get; set; }
}

public class DirectoryScanResult
{
    public List<FileInfoData> Files { get; set; } = new();
}

public class ProjectRelationship
{
    public string SourceFile { get; set; } = "";
    public string? TargetFile { get; set; }
    public string RelationshipType { get; set; } = "";
    public double Importance { get; set; }
}

public class ProjectArchitecture
{
    public string ProjectRoot { get; set; } = "";
    public int Files { get; set; }
    public int Relationships { get; set; }
    public string ProjectType { get; set; } = "";
    public Dictionary<string, List<string>> DependencyGraph { get; set; } = new();
    public List<string> PotentialIssues { get; set; } = new();
}

public class FileAnalysisResult
{
    public string FilePath { get; set; } = "";
    public int ContentLength { get; set; }
    public int ImportsFound { get; set; }
    public List<string>? ImportList { get; set; }
    public string Status { get; set; } = "";
}
