using static ECAssistant.EColor;

using System.Diagnostics;
using System.IO;
using System.Text;
using ECAssistant.Tools;
using ECAssistant;
using ECAssistant.UI;
using ECAssistant.Services;

namespace ECAssistant.Tools.PowerShell;

/// <summary>
/// PowerShell Agent Tool — the primary tool for all file and system operations.
/// Gives the LLM full control over filesystem, commands, code execution.
/// 
/// Every LLM knows PowerShell, so this one tool handles:
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
public class EPowerShellAgent : EToolBase
{
    private readonly string _workingDirectory;

    public EPowerShellAgent(string workingDirectory)
    {
        _workingDirectory = Path.GetFullPath(workingDirectory);
    }

    public override string Name => "EPowerShellAgent";

    public override string Description =>
        "Full filesystem and PowerShell command execution. " +
        "Can read/write/copy/move/delete files and folders, run any PowerShell command, " +
        "compile code, search files, manage projects. " +
        "Working directory is set automatically — use relative paths.";

    public override string UsageExample =>
        "EPowerShellAgent(command=\"Get-Content Program.cs\")";

    public override string GetToolRules() =>
        "RULE: Put the ENTIRE PowerShell command in one <command> tag. No other tags allowed.\n" +
        "Use relative paths — the working directory is already set.\n" +
        "You can chain commands with semicolons: Get-ChildItem; Write-Host 'done'";

    public override string GetToolExample() =>
        "<toolcall>EPowerShellAgent<command>Get-Content Program.cs</command></toolcall>\n" +
        "<toolcall>EPowerShellAgent<command>Copy-Item Program.cs Program_backup.cs</command></toolcall>\n" +
        "<toolcall>EPowerShellAgent<command>Get-ChildItem -Filter *.cs</command></toolcall>\n" +
        "<toolcall>EPowerShellAgent<command>Select-String -Pattern \"TODO\" -Path *.cs</command></toolcall>\n" +
        "<toolcall>EPowerShellAgent<command>Set-Content -Path notes.txt -Value 'Hello World'</command></toolcall>";

    public override async Task<EToolResult> ExecuteAsync(Dictionary<string, string?> arguments)
    {
        var psCommand = arguments.GetValueOrDefault("command");
        if (string.IsNullOrWhiteSpace(psCommand))
            return EToolResult.Failure(Name, "Missing 'command' argument.");

        var description = arguments.GetValueOrDefault("description") ?? "";

        try
        {
            var result = await RunPowerShellAsync(psCommand!, _workingDirectory);

            var metadata = new Dictionary<string, string>
            {
                ["exit_code"] = result.ExitCode.ToString(),
                ["chars_output"] = result.StandardOutput.Length.ToString()
            };

            if (result.ExitCode == 0)
            {
                // Escape angle brackets in output to prevent XML tag confusion
                var safeOutput = EscapeXml(result.StandardOutput);
                var safeCmd = EscapeXml(psCommand!);

                var output = string.IsNullOrEmpty(result.StandardOutput)
                    ? $"[PS Success] Command completed (no output)."
                    : $"[PS Success]\n{safeOutput}";

                if (!string.IsNullOrEmpty(description))
                    output += $"\nDescription: {description}";

                return EToolResult.Success(Name, output, metadata);
            }
            else
            {
                var safeErr = EscapeXml(result.StandardError);
                var safeCmd = EscapeXml(psCommand!);
                Logger.Error("PowerShell", $"Command failed (exit={result.ExitCode}): {psCommand?.Substring(0, Math.Min(psCommand.Length, 100))}");
                return EToolResult.Failure(Name,
                    $"[PS Error (Exit {result.ExitCode})]\nSTDERR: {safeErr}\nCommand: {safeCmd}",
                    metadata);
            }
        }
        catch (Exception ex)
        {
            return EToolResult.Failure(Name, $"Execution failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Run a PowerShell command with proper working directory.
    /// Uses a temp script file to avoid quoting issues with cmd.exe.
    /// </summary>
    private static async Task<PSProcessResult> RunPowerShellAsync(string command, string workingDir)
    {
        // Write command to a temp .ps1 file to avoid all quoting issues
        var tempScript = Path.Combine(Path.GetTempPath(), $"ecagent_{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(tempScript, command);

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

            await proc.WaitForExitAsync();
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();

            return new PSProcessResult(stdout, stderr, proc.ExitCode);
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
internal record PSProcessResult(string StandardOutput, string StandardError, int ExitCode);