using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ECAssistant.Interfaces;

namespace ECAssistant.Tools.Build;

/// <summary>
/// .NET build/test tool.
/// </summary>
public class EDotnetBuildTool : ITool
{
    private readonly IProcessRunner _processRunner;
    private readonly IConfigProvider _configProvider;
    private readonly IColorFormatter _colorFormatter;

    public string Name => "DotnetBuild";
    public string Description => "Run dotnet build, test, or restore commands.";

    public EDotnetBuildTool(IProcessRunner processRunner, IConfigProvider configProvider, IColorFormatter colorFormatter)
    {
        _processRunner = processRunner;
        _configProvider = configProvider;
        _colorFormatter = colorFormatter;
    }

    public async Task<string> ExecuteAsync(string input, CancellationToken ct = default)
    {
        var action = "build";
        var projectPath = "";

        if (!string.IsNullOrWhiteSpace(input))
        {
            var args = ParseInput(input);
            action = args.GetValueOrDefault("action")?.Trim().ToLower() ?? "build";
            projectPath = args.GetValueOrDefault("projectPath")?.Trim() ?? args.GetValueOrDefault("project")?.Trim() ?? "";

            // Fallback: raw input without key=value or XML format (pipe-separated)
            if (args.Count == 0)
            {
                var parts = input.Split('|');
                if (parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]))
                    action = parts[0].Trim().ToLower();
                if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]))
                    projectPath = parts[1].Trim();
            }
        }

        var command = $"dotnet {action}{(string.IsNullOrEmpty(projectPath) ? "" : $" {projectPath}")}";
        var result = await _processRunner.ExecuteAsync(command, null, ct);

        if (result.TimedOut)
            return $"[{Name}] [TIMEOUT] Build exceeded time limit.";

        var allOutput = result.StdOut + "\n" + result.StdErr;
        var errors = ParseBuildErrors(allOutput);
        var warnings = ParseBuildWarnings(allOutput);

        if (result.ExitCode == 0 && errors.Count == 0)
            return $"[Build Success] {warnings.Count} warning(s).\n{result.StdOut}";
        else if (result.ExitCode != 0)
            return $"[Build Failed (Exit {result.ExitCode})] {errors.Count} error(s), {warnings.Count} warning(s).\n{allOutput}";
        else
            return $"[Build Success with warnings] {warnings.Count} warning(s).\n{result.StdOut}";
    }

    public ECAssistant.Interfaces.ToolPolicy GetPolicy() => ECAssistant.Interfaces.ToolPolicy.Allowed(Name);

    // Stateless utility — no mutable state
    private static Dictionary<string, string> ParseInput(string input)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(input)) return result;

        // XML tag format from ToolAdapter: <action>build</action><project>MyProj.csproj</project>
        var xmlMatches = Regex.Matches(input, @"<(\w+)>(.*?)</\1>");
        foreach (Match m in xmlMatches)
            result[m.Groups[1].Value] = m.Groups[2].Value;

        if (result.Count > 0) return result;

        // key="value" format (fallback)
        var kvMatches = Regex.Matches(input, @"(\w+)\s*=\s*""([^""]*)""");
        foreach (Match m in kvMatches)
            result[m.Groups[1].Value] = m.Groups[2].Value;

        return result;
    }

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