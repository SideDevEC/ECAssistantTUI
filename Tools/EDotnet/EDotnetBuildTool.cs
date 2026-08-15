using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ECAssistant.Interfaces;

namespace ECAssistant.Tools.Build;

/// <summary>
/// Dotnet Build Tool — runs dotnet build/restore/pack/publish commands.
/// 
/// Parses output for:
/// - Build status (succeeded/failed)
/// - Error count, warning count
/// - Each error with file, line, column, error code, and message
/// - Each warning with file, line, and message
/// </summary>
public class EDotnetBuildTool : ITool
{
    private readonly IProcessRunner _processRunner;
    private readonly IConfigProvider _configProvider;
    private readonly IColorFormatter _colorFormatter;

    private const int TimeoutSeconds = 300; // 5 minutes

    public EDotnetBuildTool(IProcessRunner processRunner, IConfigProvider configProvider, IColorFormatter colorFormatter)
    {
        _processRunner = processRunner;
        _configProvider = configProvider;
        _colorFormatter = colorFormatter;
    }

    public string Name => "DotnetBuild";

    public string Description =>
        "Builds .NET projects using dotnet CLI. " +
        "Supports build, restore, pack, and publish actions. " +
        "Parses build output for errors and warnings with file locations.";

    public async Task<string> ExecuteAsync(string input, CancellationToken ct = default)
    {
        var action = "build";
        var projectPath = "";

        if (!string.IsNullOrWhiteSpace(input))
        {
            var parts = input.Split('|');
            if (parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]))
                action = parts[0].Trim().ToLower();
            if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]))
                projectPath = parts[1].Trim();
        }

        var command = $"dotnet {action}{(string.IsNullOrEmpty(projectPath) ? "" : $" {projectPath}")}";

        var result = await _processRunner.ExecuteAsync(command, null, ct);

        if (result.TimedOut)
            return $"[{Name}] [TIMEOUT] Build exceeded {TimeoutSeconds / 60} minute limit.";

        var stdout = result.StdOut;
        var stderr = result.StdErr;
        var allOutput = stdout + "\n" + stderr;
        var exitCode = result.ExitCode;

        // Parse errors and warnings
        var errors = ParseBuildErrors(allOutput);
        var warnings = ParseBuildWarnings(allOutput);

        var sb = new StringBuilder();
        sb.AppendLine($"=== dotnet {action} {(exitCode == 0 ? "SUCCEEDED" : "FAILED")} (exit: {exitCode}) ===");
        sb.AppendLine();

        if (errors.Count > 0)
        {
            sb.AppendLine($"❌ ERRORS ({errors.Count}):");
            foreach (var err in errors)
            {
                sb.AppendLine($"  [{err.Code}] {err.File}({err.Line},{err.Column}): {err.Message}");
            }
            sb.AppendLine();
        }

        if (warnings.Count > 0)
        {
            sb.AppendLine($"⚠️ WARNINGS ({warnings.Count}):");
            foreach (var warn in warnings.Take(10)) // limit warnings shown
            {
                sb.AppendLine($"  [{warn.Code}] {warn.File}({warn.Line}): {warn.Message}");
            }
            if (warnings.Count > 10)
                sb.AppendLine($"  ... and {warnings.Count - 10} more warnings");
            sb.AppendLine();
        }

        if (errors.Count == 0 && warnings.Count == 0)
        {
            sb.AppendLine("✅ Build succeeded with no errors or warnings.");
            // Include last few lines of output for confirmation
            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length > 0)
            {
                sb.AppendLine("\nOutput (last 5 lines):");
                foreach (var line in lines.TakeLast(5))
                    sb.AppendLine($"  {line.Trim()}");
            }
        }

        var success = exitCode == 0;
        return sb.ToString();
    }

    public ECAssistant.Interfaces.ToolPolicy GetPolicy() => ECAssistant.Interfaces.ToolPolicy.Allowed(Name);

    private List<BuildError> ParseBuildErrors(string output)
    {
        var errors = new List<BuildError>();
        // Pattern: file.cs(line,col): error CSXXXX: message
        var pattern = @"^(.+?)\((\d+),(\d+)\):\s+error\s+(\w+):\s+(.+)$";
        var matches = Regex.Matches(output, pattern, RegexOptions.Multiline);

        foreach (Match m in matches)
        {
            errors.Add(new BuildError
            {
                File = m.Groups[1].Value.Trim(),
                Line = int.Parse(m.Groups[2].Value),
                Column = int.Parse(m.Groups[3].Value),
                Code = m.Groups[4].Value,
                Message = m.Groups[5].Value.Trim()
            });
        }
        return errors;
    }

    private List<BuildError> ParseBuildWarnings(string output)
    {
        var warnings = new List<BuildError>();
        var pattern = @"^(.+?)\((\d+),(\d+)\):\s+warning\s+(\w+):\s+(.+)$";
        var matches = Regex.Matches(output, pattern, RegexOptions.Multiline);

        foreach (Match m in matches)
        {
            warnings.Add(new BuildError
            {
                File = m.Groups[1].Value.Trim(),
                Line = int.Parse(m.Groups[2].Value),
                Column = int.Parse(m.Groups[3].Value),
                Code = m.Groups[4].Value,
                Message = m.Groups[5].Value.Trim()
            });
        }
        return warnings;
    }

    /// <summary>Represents a build error or warning parsed from dotnet output.</summary>
    private class BuildError
    {
        public string File { get; set; } = "";
        public int Line { get; set; }
        public int Column { get; set; }
        public string Code { get; set; } = "";
        public string Message { get; set; } = "";
    }
}