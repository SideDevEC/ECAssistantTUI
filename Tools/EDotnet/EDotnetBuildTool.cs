using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ECAssistant.Config;
using ECAssistant.Interfaces;
using System.Text.Json;

namespace ECAssistant.Tools.Build;

/// <summary>
/// .NET build/test tool.
/// </summary>
public class EDotnetBuildTool : EToolBase
{
    private readonly IProcessRunner _processRunner;
    private readonly JsonElement? _toolConfig;

    public override string Name => "DotnetBuild";
    public override string Description => "Run dotnet build, test, or restore commands.";
    public override string UsageExample => "<toolcall>DotnetBuild<action>build</action></toolcall>";

    public override bool IsEnabled { get; protected set; } = true;

    public EDotnetBuildTool(IProcessRunner processRunner, EAgentConfig config)
    {
        _processRunner = processRunner;
        config.Tools.TryGetValue(Name, out var tc);
        _toolConfig = tc.ValueKind == JsonValueKind.Undefined ? null : tc;
        IsEnabled = ReadCfg(_toolConfig, "enabled", true);
    }

    public override object GetConfigSection() => new { enabled = true };

    public override async Task<EToolResult> ExecuteAsync(Dictionary<string, string?> arguments, CancellationToken cancellationToken = default)
    {
        var action = arguments.GetValueOrDefault("action")?.Trim().ToLower() ?? "build";
        var projectPath = arguments.GetValueOrDefault("projectPath")?.Trim()
                       ?? arguments.GetValueOrDefault("project")?.Trim()
                       ?? "";

        var command = $"dotnet {action}{(string.IsNullOrEmpty(projectPath) ? "" : $" {projectPath}")}";
        var result = await _processRunner.ExecuteAsync(command, null, cancellationToken);

        if (result.TimedOut)
            return EToolResult.Failure(Name, "Build exceeded time limit.");

        var allOutput = result.StdOut + "\n" + result.StdErr;
        var errors = ParseBuildErrors(allOutput);
        var warnings = ParseBuildWarnings(allOutput);

        if (result.ExitCode == 0 && errors.Count == 0)
            return EToolResult.Success(Name, $"[Build Success] {warnings.Count} warning(s).\n{result.StdOut}");
        else if (result.ExitCode != 0)
            return EToolResult.Failure(Name, $"[Build Failed (Exit {result.ExitCode})] {errors.Count} error(s), {warnings.Count} warning(s).\n{allOutput}");
        else
            return EToolResult.Success(Name, $"[Build Success with warnings] {warnings.Count} warning(s).\n{result.StdOut}");
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

    // Stateless utility — no mutable state
    private static List<BuildError> ParseBuildErrors(string output)
    {
        var errors = new List<BuildError>();
        var pattern = @"(.+?)\((\d+),(\d+)\):\s+(error|fatal error)\s+(\w+):\s+(.+)$";
        foreach (Match m in Regex.Matches(output, pattern, RegexOptions.Multiline))
            errors.Add(new BuildError(m.Groups[1].Value, int.Parse(m.Groups[2].Value), m.Groups[5].Value, m.Groups[6].Value));
        return errors;
    }

    private static List<BuildError> ParseBuildWarnings(string output)
    {
        var warnings = new List<BuildError>();
        var pattern = @"(.+?)\((\d+),(\d+)\):\s+warning\s+(\w+):\s+(.+)$";
        foreach (Match m in Regex.Matches(output, pattern, RegexOptions.Multiline))
            warnings.Add(new BuildError(m.Groups[1].Value, int.Parse(m.Groups[2].Value), m.Groups[4].Value, m.Groups[5].Value));
        return warnings;
    }
}

internal record BuildError(string File, int Line, string Code, string Message);