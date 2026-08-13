using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ECAssistant.Services;

namespace ECAssistant.Tools.Git;

/// <summary>
/// Git Integration Tool — wraps common git operations with structured output.
/// Returns clean, parsed results instead of raw git output.
/// 
/// Usage:
///   <toolcall>EGitTool<action>status</action></toolcall>
///   <toolcall>EGitTool<action>diff</action></toolcall>
///   <toolcall>EGitTool<action>commit</action><message>fix: update config</message></toolcall>
///   <toolcall>EGitTool<action>log</action><max_entries>5</max_entries></toolcall>
/// </summary>
public class EGitTool : EToolBase
{
    private readonly string _workingDir;

    public EGitTool(string workingDir) => _workingDir = workingDir;

    public override string Name => "EGitTool";

    public override string Description =>
        "Git operations with structured output. Actions: init, status, diff, commit, push, pull, log, " +
        "add, branch, checkout. Better than raw shell for git — parses output into clean format.";

    public override string UsageExample =>
        "EGitTool(action=\"status\")";

    public override string GetToolRules() =>
        "<action>=init|status|diff|commit|push|pull|log|add|branch|checkout. " +
        "init: no args. commit: +<message>. add: +<files>('all'=-A). log: +<max_entries>. checkout: +<branch>.";


    public override string GetToolExample() =>
        "<toolcall>EGitTool<action>init</action></toolcall>\n" +
        "<toolcall>EGitTool<action>status</action></toolcall>\n" +
        "<toolcall>EGitTool<action>commit</action><message>fix: update</message></toolcall>";

    public override async Task<EToolResult> ExecuteAsync(Dictionary<string, string?> arguments, CancellationToken cancellationToken = default)
    {
        var action = arguments.GetValueOrDefault("action")?.ToLower().Trim();
        if (string.IsNullOrEmpty(action))
            return EToolResult.Failure(Name, "Missing 'action' argument.");

        var (cmd, needsApproval) = BuildGitCommand(action, arguments);

        if (string.IsNullOrEmpty(cmd))
            return EToolResult.Failure(Name, $"Unknown git action: {action}");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = cmd,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = _workingDir,
            };

            var process = Process.Start(psi);
            if (process == null)
                return EToolResult.Failure(Name, "Failed to start git process.");

            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            // v10.9.3: Cancellation support
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
            try
            {
                await process.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                if (cancellationToken.IsCancellationRequested)
                    return EToolResult.Failure(Name, "[CANCELLED] Git operation was cancelled by user.");
                return EToolResult.Failure(Name, "[TIMEOUT] Git operation exceeded 60 second limit.");
            }

            if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
            {
                return EToolResult.Failure(Name, $"git {action} failed (exit {process.ExitCode}):\n{stderr.Trim()}");
            }

            var result = ParseGitOutput(action, stdout);
            Logger.Info("GitTool", $"git {action}: exit={process.ExitCode}");

            return EToolResult.Success(Name, result, new Dictionary<string, string>
            {
                ["action"] = action,
                ["exit_code"] = process.ExitCode.ToString()
            });
        }
        catch (Exception ex)
        {
            return EToolResult.Failure(Name, $"git error: {ex.Message}");
        }
    }

    private (string cmd, bool needsApproval) BuildGitCommand(string action, Dictionary<string, string?> args)
    {
        switch (action)
        {
            case "init":
                return ("init", false);
            case "status":
                return ("status --porcelain", false);
            case "diff":
                return ("diff", false);
            case "diff-staged":
                return ("diff --cached", false);
            case "add":
            {
                var files = args.GetValueOrDefault("files") ?? ".";
                if (files == "all") files = "-A";
                return ($"add {files}", false);
            }
            case "commit":
            {
                var msg = args.GetValueOrDefault("message") ?? "";
                if (string.IsNullOrEmpty(msg)) return ("", false);
                return ($"commit -m \"{msg.Replace("\"", "\\\"")}\"", true);
            }
            case "push":
                return ("push", true);
            case "pull":
                return ("pull", false);
            case "log":
            {
                var max = args.GetValueOrDefault("max_entries") ?? "10";
                return ($"log --oneline -{max}", false);
            }
            case "branch":
                return ("branch -a", false);
            case "checkout":
            {
                var branch = args.GetValueOrDefault("branch") ?? "";
                if (string.IsNullOrEmpty(branch)) return ("", false);
                return ($"checkout {branch}", false);
            }
            case "current-branch":
                return ("rev-parse --abbrev-ref HEAD", false);
            default:
                return ("", false);
        }
    }

    private static string ParseGitOutput(string action, string rawOutput)
    {
        var sb = new StringBuilder();
        var output = rawOutput.Trim();

        switch (action)
        {
            case "init":
                if (output.Contains("Reinitialized") || output.Contains("Initialized"))
                    sb.AppendLine("✅ Git repository initialized.");
                else
                    sb.AppendLine($"Git init output: {output}");
                break;
            case "status":
                if (string.IsNullOrEmpty(output))
                {
                    sb.AppendLine("✅ Working tree clean — no changes.");
                }
                else
                {
                    var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    sb.AppendLine($"📋 Git Status ({lines.Length} changes):");
                    foreach (var line in lines)
                    {
                        var status = line.Length >= 2 ? line.Substring(0, 2).Trim() : "??";
                        var file = line.Length > 3 ? line.Substring(3).Trim() : line;
                        var icon = status switch
                        {
                            "M" => "📝",
                            "A" => "➕",
                            "D" => "➖",
                            "R" => "📦",
                            "??" => "❓",
                            _ => "📝"
                        };
                        sb.AppendLine($"  {icon} [{status}] {file}");
                    }
                }
                break;

            case "log":
                if (string.IsNullOrEmpty(output))
                {
                    sb.AppendLine("(No commits yet.)");
                }
                else
                {
                    var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    sb.AppendLine($"📜 Recent commits ({lines.Length}):");
                    foreach (var line in lines)
                        sb.AppendLine($"  {line.Trim()}");
                }
                break;

            case "branch":
                if (string.IsNullOrEmpty(output))
                {
                    sb.AppendLine("(No branches.)");
                }
                else
                {
                    var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    sb.AppendLine($"🌿 Branches ({lines.Length}):");
                    foreach (var line in lines)
                    {
                        var isCurrent = line.StartsWith("*");
                        var name = line.Trim().Replace("* ", "");
                        sb.AppendLine($"  {(isCurrent ? "→ " : "  ")}{name}");
                    }
                }
                break;

            case "diff":
            case "diff-staged":
                if (string.IsNullOrEmpty(output))
                {
                    sb.AppendLine("No differences.");
                }
                else
                {
                    sb.AppendLine($"📝 Changes ({output.Length} chars):");
                    sb.AppendLine(output);
                }
                break;

            default:
                if (string.IsNullOrEmpty(output))
                    sb.AppendLine($"git {action} completed (no output).");
                else
                    sb.AppendLine(output);
                break;
        }

        return sb.ToString();
    }
}