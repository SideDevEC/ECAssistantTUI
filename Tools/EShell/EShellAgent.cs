using static ECAssistant.EColor;

using System.Diagnostics;
using System.IO;
using System.Text;
using ECAssistant.Tools;
using ECAssistant;
using ECAssistant.UI;
using ECAssistant.Services;

namespace ECAssistant.Tools.Shell;

/// <summary>
/// Shell Agent Tool — the primary tool for all file and system operations.
/// Gives the LLM full control over filesystem, commands, code execution.
/// 
/// On Windows: uses powershell.exe with .ps1 temp scripts.
/// On macOS: uses /bin/zsh with .sh temp scripts.
/// 
/// Every LLM knows shell commands, so this one tool handles:
/// - Read/write/copy/move/delete files
/// - List and search files
/// - Search content
/// - Compile code, run scripts
/// </summary>
public class EShellAgent : EToolBase
{
    private readonly string _workingDirectory;

    // v10.16: OS detection — determined once at construction
    private static readonly bool IsWindows = OperatingSystem.IsWindows();
    private static readonly bool IsMacOS = OperatingSystem.IsMacOS();

    public EShellAgent(string workingDirectory)
    {
        _workingDirectory = Path.GetFullPath(workingDirectory);
    }

    public override string Name => "EShellAgent";

    public override string Description =>
        "Full filesystem and shell command execution. " +
        "Can read/write/copy/move/delete files and folders, run any shell command, " +
        "compile code, search files, manage projects. " +
        "Working directory is set automatically — use relative paths.";

    public override string UsageExample => IsWindows
        ? "EShellAgent(command=\"Get-Content Program.cs\")"
        : "EShellAgent(command=\"cat Program.cs\")";

    public override string GetToolRules() => IsWindows
        ? "RULE: Put the ENTIRE PowerShell command in one <command> tag. No other tags allowed.\n" +
          "Use relative paths — the working directory is already set.\n" +
          "You can chain commands with semicolons: Get-ChildItem; Write-Host 'done'"
        : "RULE: Put the ENTIRE zsh command in one <command> tag. No other tags allowed.\n" +
          "Use relative paths — the working directory is already set.\n" +
          "You can chain commands with semicolons: ls; echo 'done'";

    public override string GetToolExample() => IsWindows
        ? "<toolcall>EShellAgent<command>Get-Content Program.cs</command></toolcall>\n" +
          "<toolcall>EShellAgent<command>Copy-Item Program.cs Program_backup.cs</command></toolcall>\n" +
          "<toolcall>EShellAgent<command>Get-ChildItem -Filter *.cs</command></toolcall>\n" +
          "<toolcall>EShellAgent<command>Select-String -Pattern \"TODO\" -Path *.cs</command></toolcall>\n" +
          "<toolcall>EShellAgent<command>Set-Content -Path notes.txt -Value 'Hello World'</command></toolcall>"
        : "<toolcall>EShellAgent<command>cat Program.cs</command></toolcall>\n" +
          "<toolcall>EShellAgent<command>cp Program.cs Program_backup.cs</command></toolcall>\n" +
          "<toolcall>EShellAgent<command>ls *.cs</command></toolcall>\n" +
          "<toolcall>EShellAgent<command>grep \"TODO\" *.cs</command></toolcall>\n" +
          "<toolcall>EShellAgent<command>echo 'Hello World' > notes.txt</command></toolcall>";

    public override async Task<EToolResult> ExecuteAsync(Dictionary<string, string?> arguments, CancellationToken cancellationToken = default)
    {
        var command = arguments.GetValueOrDefault("command");
        if (string.IsNullOrWhiteSpace(command))
            return EToolResult.Failure(Name, "Missing 'command' argument.");

        var description = arguments.GetValueOrDefault("description") ?? "";

        try
        {
            var result = await RunShellAsync(command!, _workingDirectory, cancellationToken);

            var metadata = new Dictionary<string, string>
            {
                ["exit_code"] = result.ExitCode.ToString(),
                ["chars_output"] = result.StandardOutput.Length.ToString()
            };

            var hasStderrOutput = !string.IsNullOrWhiteSpace(result.StandardError);
            
            if (result.ExitCode == 0 && !hasStderrOutput)
            {
                var safeOutput = EscapeXml(result.StandardOutput);
                var output = string.IsNullOrEmpty(result.StandardOutput)
                    ? $"[Shell Success] Command completed (no output)."
                    : $"[Shell Success]\n{safeOutput}";

                if (!string.IsNullOrEmpty(description))
                    output += $"\nDescription: {description}";

                return EToolResult.Success(Name, output, metadata);
            }
            else if (result.ExitCode == 0 && hasStderrOutput)
            {
                var safeOutput = EscapeXml(result.StandardOutput);
                var safeErr = EscapeXml(result.StandardError);
                Logger.Warn("Shell", $"Command had stderr output (exit=0): {result.StandardError.Substring(0, Math.Min(result.StandardError.Length, 200))}");
                
                var output = string.IsNullOrEmpty(result.StandardOutput)
                    ? $"[Shell Warning] Command completed but produced error output:\nSTDERR: {safeErr}"
                    : $"[Shell Warning]\n{safeOutput}\n\nSTDERR: {safeErr}";

                if (!string.IsNullOrEmpty(description))
                    output += $"\nDescription: {description}";

                return EToolResult.Success(Name, output, metadata);
            }
            else
            {
                var safeErr = EscapeXml(result.StandardError);
                var safeCmd = EscapeXml(command!);
                Logger.Error("Shell", $"Command failed (exit={result.ExitCode}): {command?.Substring(0, Math.Min(command.Length, 100))}");
                return EToolResult.Failure(Name,
                    $"[Shell Error (Exit {result.ExitCode})]\nSTDERR: {safeErr}\nCommand: {safeCmd}",
                    metadata);
            }
        }
        catch (Exception ex)
        {
            return EToolResult.Failure(Name, $"Execution failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Run a shell command with proper working directory.
    /// v10.16: OS-aware — Windows uses powershell.exe, macOS uses /bin/zsh.
    /// Uses a temp script file to avoid quoting issues.
    /// </summary>
    private static async Task<ShellProcessResult> RunShellAsync(string command, string workingDir, CancellationToken cancellationToken = default)
    {
        var tempScript = Path.Combine(Path.GetTempPath(),
            $"ecagent_{Guid.NewGuid():N}{(IsWindows ? ".ps1" : ".sh")}");

        string scriptContent;
        string fileName;
        string arguments;

        if (IsWindows)
        {
            // Windows: PowerShell with error collection (existing behavior)
            // v10.12.10: Use 'Continue' so all commands in a ; chain execute,
            // then check $error at the end.
            scriptContent = "$ErrorActionPreference = 'Continue'\n" + command +
                "\n\nif ($error.Count -gt 0) {\n  Write-Error ($error -join '`n')\n  exit 1\n}";
            fileName = "powershell.exe";
            arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{tempScript}\"";
        }
        else
        {
            // macOS/Linux: zsh with set -e for error detection
            // set -e makes the script exit on first error (equivalent to $ErrorActionPreference='Stop')
            // But we want all commands to run like PowerShell 'Continue', so we use:
            // - Run all commands, capture exit codes, fail if any non-zero
            scriptContent = "#!/bin/zsh\n" + command + "\n";
            fileName = "/bin/zsh";
            arguments = $"\"{tempScript}\"";
        }

        await File.WriteAllTextAsync(tempScript, scriptContent);

        try
        {
            var psi = new ProcessStartInfo()
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workingDir,
            };

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException($"Failed to start {fileName} process.");

            // v9.9: Tool timeout — 60s default, prevent hanging commands
            // v10.9.3: Also linked to external cancellation token (ESC/stop)
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
            try
            {
                await proc.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                if (cancellationToken.IsCancellationRequested)
                    return new ShellProcessResult("", "[CANCELLED] Command was cancelled by user.", -1);
                return new ShellProcessResult("", "[TIMEOUT] Command exceeded 60 second limit and was killed.", -1);
            }
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();

            // v10.16: On macOS, make the temp script executable (needed for .sh)
            // (Process.Start with /bin/zsh script.sh works without +x, but just in case)
            return new ShellProcessResult(stdout, stderr, proc.ExitCode);
        }
        finally
        {
            try { File.Delete(tempScript); } catch { }
        }
    }

    /// <summary>Escape angle brackets to prevent XML tag confusion in LLM history.</summary>
    private static string EscapeXml(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return text.Replace("<", "&lt;").Replace(">", "&gt;");
    }

    /// <summary>
    /// Ask user to type a file path, read the file and return its content as the prompt.
    /// </summary>
    public static async Task<string?> FilePickerPromptAsync(string? workDir = null)
    {
        var gui = new EGuiConsole();
        var root = Path.GetFullPath(workDir ?? Directory.GetCurrentDirectory());

        gui.WriteLineColored($"Enter file path (relative to {root}):");
        var filePath = gui.PromptRaw("      > ")?.Trim();
        if (string.IsNullOrEmpty(filePath)) return null;

        if (!Path.IsPathRooted(filePath))
            filePath = Path.Combine(root, filePath);

        try { return await File.ReadAllTextAsync(filePath); }
        catch (Exception ex) { Console.Error.WriteLine($"[Picker] Error: {ex.Message}"); return null; }
    }
}

/// <summary>Lightweight result structure from shell execution.</summary>
internal record ShellProcessResult(string StandardOutput, string StandardError, int ExitCode);