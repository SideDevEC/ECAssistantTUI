using System.Threading;
using System.Threading.Tasks;
using ECAssistant.Interfaces;

namespace ECAssistant.Tools.Shell;

/// <summary>
/// Shell Agent Tool — the primary tool for all file and system operations.
/// Gives the LLM full control over filesystem, commands, code execution.
/// 
/// On Windows: uses powershell.exe.
/// On macOS: uses /bin/zsh.
/// 
/// Every LLM knows shell commands, so this one tool handles:
/// - Read/write/copy/move/delete files
/// - List and search files
/// - Search content
/// - Compile code, run scripts
/// </summary>
public class EShellAgent : ITool
{
    private readonly IProcessRunner _processRunner;
    private readonly IConfigProvider _configProvider;
    private readonly IColorFormatter _colorFormatter;
    private readonly string _workingDirectory;

    // v10.16: OS detection — determined once at construction (instance-based)
    private readonly bool _isWindows = OperatingSystem.IsWindows();
    private readonly bool _isMacOS = OperatingSystem.IsMacOS();

    public EShellAgent(IProcessRunner processRunner, IConfigProvider configProvider, IColorFormatter colorFormatter, string workingDirectory)
    {
        _processRunner = processRunner;
        _configProvider = configProvider;
        _colorFormatter = colorFormatter;
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
        var command = input?.Trim();
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

    /// <summary>
    /// Run a shell command with proper working directory.
    /// v10.16: OS-aware — Windows uses powershell.exe, macOS uses /bin/zsh.
    /// </summary>
    private async Task<ShellProcessResult> RunShellAsync(string command, string workingDir, CancellationToken cancellationToken = default)
    {
        string shellCommand;

        if (_isWindows)
        {
            // Windows: PowerShell with error collection
            shellCommand = $"powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command}\"";
        }
        else
        {
            // macOS/Linux: zsh
            shellCommand = $"/bin/zsh -c \"{command}\"";
        }

        var result = await _processRunner.ExecuteAsync(shellCommand, workingDir, cancellationToken);

        return new ShellProcessResult(result.StdOut, result.StdErr, result.ExitCode);
    }

    /// <summary>Escape angle brackets to prevent XML tag confusion in LLM history.</summary>
    private string EscapeXml(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return text.Replace("<", "<").Replace(">", ">");
    }
}

/// <summary>Lightweight result structure from shell execution.</summary>
internal record ShellProcessResult(string StandardOutput, string StandardError, int ExitCode);