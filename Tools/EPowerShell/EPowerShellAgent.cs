using static ECAssistant.EColor;

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text;
using System.Runtime.InteropServices;
using ECAssistant.Tools;
using ECAssistant;
using ECAssistant.UI;

namespace ECAssistant.Tools.PowerShell;

/// <summary>
/// PowerShell Agent Tool - gives the LLM full control over filesystem, 
/// PowerShell commands, file read/write/modify/delete, folder operations.
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
                "Full filesystem and PowerShell command execution. Can read/write/modify/delete files and folders, run PowerShell commands, search files, compile code, debug projects.";

    public override string UsageExample => 
              "EPowerShellAgent.Execute(command=\"$PSCommand\", description=\"What this does\");";

      /// <summary>
      /// Examples for the LLM: command always contains the COMPLETE PowerShell statement.
      /// Simple commands, file operations, variable assignments, piping — everything in one tag.
      /// </summary>
    public override string GetToolExample()
                   => "<toolcall>EPowerShellAgent<command>Get-Date</command></toolcall>" + System.Environment.NewLine +
                      "<toolcall>EPowerShellAgent<command>Get-Content C:\\myfile.txt</command></toolcall>" + System.Environment.NewLine +
                      "<toolcall>EPowerShellAgent<command>$files = Get-ChildItem; foreach($f in $files) { Write-Host $f.Name }</command></toolcall>";

      /// <summary>
      /// Strict policy rules for EPowerShellAgent — always enforce these.
      /// </summary>
    public override string GetToolRules()
                   => "RULE: <command> MUST contain the ENTIRE PowerShell statement. No other tags allowed." + System.Environment.NewLine +
                      "Always use exactly one <command> tag. Examples:" + System.Environment.NewLine +
                      "<toolcall>EPowerShellAgent<command>Get-Date</command></toolcall>" + System.Environment.NewLine +
                      "<toolcall>EPowerShellAgent<command>Get-Content C:\\myfile.txt</command></toolcall>" + System.Environment.NewLine +
                      "<toolcall>EPowerShellAgent<command>$files = Get-ChildItem; foreach($f in $files) { Write-Host $f.Name }</command></toolcall>" + System.Environment.NewLine +
                      "NEVER use <arg1> or any other tags. Put the full command in <command> only.";

    public override async Task<EToolResult> ExecuteAsync(Dictionary<string, string?> arguments)
               {
                 // 'command' holds the COMPLETE PowerShell command — everything goes here.
           var psCommand = arguments.GetValueOrDefault("command");
           if (string.IsNullOrWhiteSpace(psCommand))
                return EToolResult.Failure(Name, "Missing 'command' argument.");

          var description = arguments.GetValueOrDefault("description") ?? "(no description)";

          try
                   {
               var result = await RunPowerShellAsync(psCommand!);

             var metadata = new Dictionary<string, string> 
                      { ["exit_code"] = result.ExitCode.ToString(), ["chars_output"] = result.StandardOutput.Length.ToString() };

              return result.ExitCode == 0
                       ? EToolResult.Success(Name, $"[PS Success]\n{result.StandardOutput}\nCommand: {psCommand}\nDescription: {description}", metadata)
                        : EToolResult.Failure(Name, $"[PS Error (Exit {result.ExitCode})]\nSTDERR: {result.StandardError}\nCommand: {psCommand}", metadata);
                   }
          catch (Exception ex)
                   {
               return EToolResult.Failure(Name, $"Execution failed: {ex.GetType().Name}: {ex.Message}");
                   }
              }

           /// <summary>Run a command via cmd.exe to avoid stream conflicts.</summary>
    private static async Task<PSProcessResult> RunPowerShellAsync(string command)
               {
                  // Use cmd.exe as wrapper to avoid "stream in use" from RedirectStandardInput
                  // The actual execution goes through PowerShell via /c powershell ...
              var psi = new ProcessStartInfo() 
                    {
                  FileName = "cmd.exe",
                 Arguments = $"/c powershell -NoProfile -NonInteractive -Command \"{command}\"",
                  UseShellExecute = false,
                 RedirectStandardOutput = true,
                RedirectStandardError = true,
                  CreateNoWindow = true,
                   };

              using var proc = Process.Start(psi) 
                        ?? throw new InvalidOperationException("Failed to start process.");

                 // stdoutTask + stderrTask
            await proc.WaitForExitAsync()!;
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();

            return new PSProcessResult(stdout, stderr, proc.ExitCode);
              }

           /// <summary>
           /// Ask user to type a file path, read the file and return its content as the prompt.
           /// Simple, no external dependencies, works everywhere.
           /// </summary>
    public static async Task<string?> FilePickerPromptAsync(string? workDir = null)
              {
             var gui = new EGuiConsole();
            var root = Path.GetFullPath(workDir ?? Directory.GetCurrentDirectory());

            gui.WriteLineColored($"Enter file path (relative to {root}):");
            var filePath = gui.PromptRaw("      > ")?.Trim();
           if (string.IsNullOrEmpty(filePath)) return null;

                // If relative, resolve against working directory
            if (!Path.IsPathRooted(filePath))
                filePath = Path.Combine(root, filePath);

            try { return await File.ReadAllTextAsync(filePath); }
            catch (Exception ex) { Console.Error.WriteLine($"[Picker] Error: {ex.Message}"); return null; }
            }
}

/// <summary>Lightweight result structure from PowerShell execution.</summary>
internal record PSProcessResult(string StandardOutput, string StandardError, int ExitCode);
