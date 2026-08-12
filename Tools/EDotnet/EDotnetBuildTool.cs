using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ECAssistant.Services;

namespace ECAssistant.Tools.Build;

/// <summary>
/// Dotnet Build Tool — runs dotnet build, parses errors/warnings, and returns
/// structured output that's easy for the LLM to understand and fix.
/// 
/// Instead of raw build output, the LLM gets:
/// - Build status (succeeded/failed)
/// - Error count, warning count
/// - Each error with file, line, column, error code, and message
/// - Each warning with file, line, and message
/// 
/// Usage:
///   <toolcall>EDotnetBuild<project>MyProject.csproj</project></toolcall>
///   <toolcall>EDotnetBuild<action>build</action><project>MyProject.csproj</project></toolcall>
///   <toolcall>EDotnetBuild<action>test</action></toolcall>
/// </summary>
public class EDotnetBuildTool : EToolBase
{
    private readonly string _workingDir;

    public EDotnetBuildTool(string workingDir)
    {
        _workingDir = workingDir;
    }

    public override string Name => "EDotnetBuild";

    public override string Description =>
        "Run dotnet build or test, parse errors and warnings, return structured results. " +
        "Use for: building projects, running tests, checking for compile errors. " +
        "Much better than raw PowerShell output for build results.";

    public override string UsageExample =>
        "EDotnetBuild(project=\"MyProject.csproj\")";

    public override string GetToolRules() =>
        "<action>=build|test|test-filter|restore|clean|format (default:build). " +
        "+<project>? +<configuration>? +<filter>? Returns structured errors (file,line,code).";


    public override string GetToolExample() =>
        "<toolcall>EDotnetBuild<project>ECAssistant.csproj</project></toolcall>\n" +
        "<toolcall>EDotnetBuild<action>test</action></toolcall>\n" +
        "<toolcall>EDotnetBuild<action>test-filter</action><filter>TestClass.TestMethod</filter></toolcall>\n" +
        "<toolcall>EDotnetBuild<action>format</action></toolcall>\n" +
        "<toolcall>EDotnetBuild<action>build</action><configuration>Release</configuration></toolcall>";

    public override async Task<EToolResult> ExecuteAsync(Dictionary<string, string?> arguments)
    {
        var action = arguments.GetValueOrDefault("action")?.ToLower().Trim() ?? "build";
        var project = arguments.GetValueOrDefault("project") ?? "";
        var configuration = arguments.GetValueOrDefault("configuration") ?? "Debug";

        string cmd;
        switch (action)
        {
            case "build": cmd = "build"; break;
            case "test": cmd = "test"; break;
            case "test-filter":
            {
                var filter = arguments.GetValueOrDefault("filter") ?? "";
                cmd = string.IsNullOrEmpty(filter) ? "test" : "test --filter \"" + filter + "\"";
                break;
            }
            case "restore": cmd = "restore"; break;
            case "clean": cmd = "clean"; break;
            case "format": cmd = "format"; break;
            case "format-check": cmd = "format --verify-no-changes"; break;
            default: cmd = "build"; break;
        }

        if (!string.IsNullOrEmpty(project))
            cmd += $" \"{project}\"";

        if (action == "build" || action == "clean")
            cmd += $" -c {configuration}";

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = cmd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _workingDir,
        };

        var process = Process.Start(psi);
        if (process == null)
            return EToolResult.Failure(Name, "Failed to start dotnet process.");

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var exitCode = process.ExitCode;
        var allOutput = stdout + "\n" + stderr;

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

        Logger.Info("DotnetBuild", $"dotnet {action}: exit={exitCode}, errors={errors.Count}, warnings={warnings.Count}");

        var success = exitCode == 0;
        return EToolResult.Success(Name, sb.ToString(), new Dictionary<string, string>
        {
            ["exit_code"] = exitCode.ToString(),
            ["error_count"] = errors.Count.ToString(),
            ["warning_count"] = warnings.Count.ToString(),
            ["succeeded"] = success.ToString()
        });
    }

    private static List<BuildError> ParseBuildErrors(string output)
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

    private static List<BuildError> ParseBuildWarnings(string output)
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
}

internal class BuildError
{
    public string File { get; set; } = "";
    public int Line { get; set; }
    public int Column { get; set; }
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
}