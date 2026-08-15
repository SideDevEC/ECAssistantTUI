using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using ECAssistant.Config;
using ECAssistant.Interfaces;

namespace ECAssistant.Tools.Shell;

/// <summary>
/// Shell Agent Tool — the primary tool for all file and system operations.
/// </summary>
public class EShellAgent : EToolBase
{
    private readonly IProcessRunner _processRunner;
    private readonly string _workingDirectory;
    private readonly bool _isWindows = OperatingSystem.IsWindows();
    private readonly JsonElement? _toolConfig;
    private readonly bool _usePwshCore;
    private readonly bool _fallbackToPwshExe;
    private readonly int _maxOutputChars;

    public override string Name => "EShellAgent";

    public override string Description =>
        "Full filesystem and shell command execution. " +
        "Can read/write/copy/move/delete files and folders, run any shell command, " +
        "compile code, search files, manage projects. " +
        "Working directory is set automatically — use relative paths.";

    public override string UsageExample =>
        "<toolcall>EShellAgent<command>Get-ChildItem</command></toolcall>";

    public override bool IsEnabled { get; protected set; } = true;

    public EShellAgent(IProcessRunner processRunner, EAgentConfig config, string workingDirectory)
    {
        _processRunner = processRunner;
        _workingDirectory = Path.GetFullPath(workingDirectory);
        config.Tools.TryGetValue(Name, out var tc);
        _toolConfig = tc.ValueKind == JsonValueKind.Undefined ? null : tc;
        IsEnabled = ReadCfg(_toolConfig, "enabled", true);
        _usePwshCore = ReadCfg(_toolConfig, "use_pwsh_core", true);
        _fallbackToPwshExe = ReadCfg(_toolConfig, "fallback_to_powershell_exe", true);
        _maxOutputChars = ReadCfg(_toolConfig, "max_output_chars", 50000);
    }

    public override object GetConfigSection() => new
    {
        enabled = true,
        use_pwsh_core = true,
        fallback_to_powershell_exe = true,
        max_output_chars = 50000
    };

    public override async Task<EToolResult> ExecuteAsync(Dictionary<string, string?> arguments, CancellationToken cancellationToken = default)
    {
        arguments ??= new Dictionary<string, string?>();
        var command = arguments.GetValueOrDefault("command")?.Trim();
        if (string.IsNullOrWhiteSpace(command))
            return EToolResult.Failure(Name, "Missing command argument.");

        try
        {
            var result = await RunShellAsync(command, _workingDirectory, cancellationToken);

            var hasStderrOutput = !string.IsNullOrWhiteSpace(result.StandardError);

            if (result.ExitCode == 0 && !hasStderrOutput)
            {
                var output = string.IsNullOrEmpty(result.StandardOutput)
                    ? "Command completed (no output)."
                    : result.StandardOutput;
                if (output.Length > _maxOutputChars)
                    output = output.Substring(0, _maxOutputChars) + "\n... [truncated]";
                return EToolResult.Success(Name, output);
            }
            else if (result.ExitCode == 0 && hasStderrOutput)
            {
                var output = string.IsNullOrEmpty(result.StandardOutput)
                    ? $"Command completed but produced error output:\nSTDERR: {result.StandardError}"
                    : $"{result.StandardOutput}\n\nSTDERR: {result.StandardError}";
                if (output.Length > _maxOutputChars)
                    output = output.Substring(0, _maxOutputChars) + "\n... [truncated]";
                return EToolResult.Success(Name, output);
            }
            else
            {
                return EToolResult.Failure(Name, $"Shell Error (Exit {result.ExitCode})\nSTDERR: {result.StandardError}\nCommand: {command}");
            }
        }
        catch (Exception ex)
        {
            return EToolResult.Failure(Name, $"Execution failed: {ex.GetType().Name}: {ex.Message}");
        }
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

    private async Task<ShellProcessResult> RunShellAsync(string command, string workingDir, CancellationToken cancellationToken = default)
    {
        string shellCommand;

        if (_isWindows)
        {
            var shell = _usePwshCore ? "pwsh" : "powershell.exe";
            shellCommand = $"{shell} -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command}\"";
        }
        else
            shellCommand = $"/bin/zsh -c \"{command}\"";

        var result = await _processRunner.ExecuteAsync(shellCommand, workingDir, cancellationToken);
        return new ShellProcessResult(result.StdOut, result.StdErr, result.ExitCode);
    }

    private string EscapeXml(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return text.Replace("<", "&lt;").Replace(">", "&gt;");
    }
}

internal record ShellProcessResult(string StandardOutput, string StandardError, int ExitCode);