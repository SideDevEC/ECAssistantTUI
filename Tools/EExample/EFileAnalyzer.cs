using static ECAssistant.EColor;

using ECAssistant.Tools;
using System.IO;
using System.Text.Json;
using System.Text;
using ECAssistant;

namespace ECAssistant.Tools.Example;

/// <summary>
/// EXAMPLE TOOL — Extends EToolBase to show how to add a new tool.
/// To create your own tool:
///     1. Create a new class in Tools/<Category>/MyTool.cs
///     2. Extend EToolBase and override Name, Description, UsageExample, ExecuteAsync()
///     3. Register it in Program.cs: agent.RegisterTool(new MyTool());
/// 
/// This example tool demonstrates reading file content for analysis.
/// More tools follow the exact same pattern — drop a new class in Tools/ and register it.
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

     public override async Task<EToolResult> ExecuteAsync(Dictionary<string, string?> arguments)
            {
            var filePath = arguments.GetValueOrDefault("filePath");
               if (string.IsNullOrWhiteSpace(filePath))
                    return EToolResult.Failure(Name, "Missing 'filePath' argument.");

                 try
                       {
                     var fullPath = Path.Combine(_workingDir, filePath);
                          if (!File.Exists(fullPath))
                              return EToolResult.Failure(Name, $"File not found: {fullPath}");

                             var content = await File.ReadAllTextAsync(fullPath);
                                 var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                                     var words = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                                         var metadata = new Dictionary<string, string>
                                               {
                                                  ["size_bytes"] = new System.IO.FileInfo(fullPath).Length.ToString(),
                                                      ["line_count"] = lines.Length.ToString(),
                                                          ["word_count"] = words.Length.ToString(),
                                                               };

                                                                  return EToolResult.Success(
                                                                       Name,
                                                                              $@"File: {filePath}
                                                                                     Size: {metadata["size_bytes"]} bytes
                                                                                         Lines: {metadata["line_count"]}
                                                                                             Words: {metadata["word_count"]}
                                                                                                   Content preview (first 500 chars):
                                                                                                   ---
                                                                                                       {content.Take(500).Aggregate("", (a, b) => a + b)}
                                                                                                     ---",
                                                                                                       metadata);
                                                                                                  }
                                                                                            catch (Exception ex)
                                                                                                   {
                                                                                                     return EToolResult.Failure(Name, $"Error reading file: {ex.Message}");
                                                                                                           }
                                                                                                               }

    public override string GetExtendedSystemPrompt()
                    {
                    return @"The EFileAnalyzer tool lets you read and analyze any text file. Use it before editing code files to understand their structure.";
                 }

    public override string GetToolExample()
              => "<toolcall>EFileAnalyzer<path>Program.cs</path></toolcall>";
}
