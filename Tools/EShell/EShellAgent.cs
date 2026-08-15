using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using ECAssistant.Interfaces;

namespace ECAssistant.Tools.Shell;

/// <summary>
/// Shell Agent Tool — the primary tool for all file and system operations.
/// </summary>
public class EShellAgent : ITool
{
    private readonly IProcessRunner _processRunner;
    private readonly IConfigProvider _configProvider;
    private readonly string _workingDirectory;
    private readonly bool _isWindows = OperatingSystem.IsWindows();

    public EShellAgent(IProcessRunner processRunner, IConfigProvider configProvider, string workingDirectory)
    {
        _processRunner = processRunner;
        _configProvider = configProvider;
        _workingDirectory = Path.GetFullPath(workingDirectory);
    }

    public string Name => "EShellAgent";

    public string Description =>
        "Full filesystem and shell command execution. " +
        "Can read/write/copy/move/delete files and folders, run any shell command, " +
        "compile code, search files, manage projects. " +
        "Working directory is set automatically — use relative paths.";

    public async Task<string> ExecuteAsync(string input, CancellationToken ct = default)
    {
        var command = ExtractCommand(input);
        if (string.IsNullOrWhiteSpace(command))
            return $"[{Name}] Missing command argument.";

        try
        {
            var result = await RunShellAsync(command, _workingDirectory, ct);

            var hasStderrOutput = !string.IsNullOrWhiteSpace(result.StandardError);

            if (result.ExitCode == 0 && !hasStderrOutput)
            {
                var safeOutput = EscapeXml(result.StandardOutput);
                return string.IsNullOrEmpty(result.StandardOutput)
                    ? $"[Shell Success] Command completed (no output)."
                    : $"[Shell Success]\n{safeOutput}";
            }
            else if (result.ExitCode == 0 && hasStderrOutput)
            {
                var safeOutput = EscapeXml(result.StandardOutput);
                var safeErr = EscapeXml(result.StandardError);
                return string.IsNullOrEmpty(result.StandardOutput)
                    ? $"[Shell Warning] Command completed but produced error output:\nSTDERR: {safeErr}"
                    : $"[Shell Warning]\n{safeOutput}\n\nSTDERR: {safeErr}";
            }
            else
            {
                var safeErr = EscapeXml(result.StandardError);
                var safeCmd = EscapeXml(command);
                return $"[Shell Error (Exit {result.ExitCode})]\nSTDERR: {safeErr}\nCommand: {safeCmd}";
            }
        }
        catch (Exception ex)
        {
            return $"[{Name}] Execution failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    public ECAssistant.Interfaces.ToolPolicy GetPolicy() => ECAssistant.Interfaces.ToolPolicy.Approved(Name);

    private async Task<ShellProcessResult> RunShellAsync(string command, string workingDir, CancellationToken cancellationToken = default)
    {
        string shellCommand;

        if (_isWindows)
            shellCommand = $"powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command}\"";
        else
            shellCommand = $"/bin/zsh -c \"{command}\"";

        var result = await _processRunner.ExecuteAsync(shellCommand, workingDir, cancellationToken);
        return new ShellProcessResult(result.StdOut, result.StdErr, result.ExitCode);
    }

    /// <summary>
    /// Extract the command from the input string.
    /// Handles XML tag format from ToolAdapter: <command>date</command>
    /// Also handles raw command text and key=value format.
    /// </summary>
    private string ExtractCommand(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        input = input.Trim();

        // XML tag format from ToolAdapter
        var xmlMatch = Regex.Match(input, @"<command>(.*?)</command>", RegexOptions.IgnoreCase);
        if (xmlMatch.Success)
            return xmlMatch.Groups[1].Value.Trim();

        // key="value" format (fallback)
        var kvMatch = Regex.Match(input, @"command\s*=\s*""([^""]*)""");
        if (kvMatch.Success)
            return kvMatch.Groups[1].Value;

        // key=value without quotes
        if (input.StartsWith("command=", StringComparison.OrdinalIgnoreCase))
            return input.Substring("command=".Length).Trim().Trim('"');

        // Raw input — just return as-is
        return input;
    }

    private string EscapeXml(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return text.Replace("<", "&lt;").Replace(">", "&gt;");
    }
}

internal record ShellProcessResult(string StandardOutput, string StandardError, int ExitCode);