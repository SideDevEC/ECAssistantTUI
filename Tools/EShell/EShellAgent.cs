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
/// Every LLM knows shell commands, so this one tool handles:
/// - Read files: Get-Content
/// - Write files: Set-Content, Out-File
/// - Copy files: Copy-Item
/// - Move/rename: Move-Item
/// - Delete files: Remove-Item
/// - List files: Get-ChildItem
/// - Search files: Get-ChildItem -Recurse -Filter
/// - Search content: Select-String
/// - Compile code: dotnet build
/// - Run scripts: any PowerShell command
/// </summary>
public class EShellAgent : EToolBase
{
    private readonly string _workingDirectory;

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

    public override string UsageExample =>
        "EShellAgent(command=\"Get-Content Program.cs\")";

    public override string GetToolRules() =>
        "RULE: Put the ENTIRE shell command in one <command> tag. No other tags allowed.\n" +
        "Use relative paths — the working directory is already set.\n" +
        "You can chain commands with semicolons: Get-ChildItem; Write-Host 'done'";

    public override string GetToolExample() =>
        "<toolcall>EShellAgent<command>Get-Content Program.cs</command></toolcall>\n" +
        "<toolcall>EShellAgent<command>Copy-Item Program.cs Program_backup.cs</command></toolcall>\n" +
        "<toolcall>EShellAgent<command>Get-ChildItem -Filter *.cs</command></toolcall>\n" +
        "<toolcall>EShellAgent<command>Select-String -Pattern \"TODO\" -Path *.cs</command></toolcall>\n" +
        "<toolcall>EShellAgent<command>Set-Content -Path notes.txt -Value 'Hello World'</command></toolcall>";

    public override async Task<EToolResult> ExecuteAsync(Dictionary<string, string?> arguments, CancellationToken cancellationToken = default)
    {
        var psCommand = arguments.GetValueOrDefault("command");
        if (string.IsNullOrWhiteSpace(psCommand))
            return EToolResult.Failure(Name, "Missing 'command' argument.");

        var description = arguments.GetValueOrDefault("description") ?? "";

        try
        {
            var result = await RunShellAsync(psCommand!, _workingDirectory, cancellationToken);

            var metadata = new Dictionary<string, string>
            {
                ["exit_code"] = result.ExitCode.ToString(),
                ["chars_output"] = result.StandardOutput.Length.ToString()
            };

            // v10.11.2: PowerShell non-terminating errors (like New-Item with bad path)
            // write to stderr but may still exit with code 0 when run with -File.
            // Check both exit code AND stderr to detect failures.
            var hasStderrOutput = !string.IsNullOrWhiteSpace(result.StandardError);
            
            if (result.ExitCode == 0 && !hasStderrOutput)
            {
                // True success — no errors
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
                // v10.11.2: Exit code 0 but stderr has content — partial success or non-terminating error.
                // Report as success but include the error text so the LLM can self-correct.
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
                var safeCmd = EscapeXml(psCommand!);
                Logger.Error("Shell", $"Command failed (exit={result.ExitCode}): {psCommand?.Substring(0, Math.Min(psCommand.Length, 100))}");
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
    /// Uses a temp script file to avoid quoting issues.
    /// </summary>
    private static async Task<ShellProcessResult> RunShellAsync(string command, string workingDir, CancellationToken cancellationToken = default)
    {
        // Write command to a temp .ps1 file to avoid all quoting issues
        // v10.11.2: Prepend $ErrorActionPreference = "Stop" so non-terminating errors
        // (like New-Item with a non-existent path) become terminating errors that
        // set a non-zero exit code. Without this, PowerShell writes to stderr but
        // exits with code 0, causing the tool to report success.
        var tempScript = Path.Combine(Path.GetTempPath(), $"ecagent_{Guid.NewGuid():N}.ps1");
        // v10.12.10: Don't use $ErrorActionPreference = 'Stop' — it kills the script on the
        // first error, so remaining commands in a ; chain never execute. Instead, use 'Continue'
        // so all commands run, collect errors, and report them at the end.
        var scriptContent = "$ErrorActionPreference = 'Continue'\n" + command + "\n\nif ($error.Count -gt 0) {\n  Write-Error ($error -join '`n')\n  exit 1\n}";
        await File.WriteAllTextAsync(tempScript, scriptContent);

        try
        {
            var psi = new ProcessStartInfo()
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{tempScript}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workingDir,
            };

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start PowerShell process.");

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

            return new ShellProcessResult(stdout, stderr, proc.ExitCode);
        }
        finally
        {
            // Clean up temp script
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

/// <summary>Lightweight result structure from PowerShell execution.</summary>
internal record ShellProcessResult(string StandardOutput, string StandardError, int ExitCode);