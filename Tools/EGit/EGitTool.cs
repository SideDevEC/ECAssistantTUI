using System.Text;
using System.Text.Json;
using ECAssistant.Config;
using ECAssistant.Interfaces;

namespace ECAssistant.Tools.Git;

/// <summary>
/// Git Integration Tool — wraps common git operations with structured output.
/// </summary>
public class EGitTool : EToolBase
{
    private readonly IProcessRunner _processRunner;
    private readonly IFileSystem _fileSystem;
    private readonly JsonElement? _toolConfig;
    private readonly string _workingDir;

    public override string Name => "EGitTool";

    public override string Description =>
        "Git operations with structured output. Actions: init, status, diff, commit, push, pull, log, " +
        "add, branch, checkout. Better than raw shell for git — parses output into clean format.";

    public override string UsageExample =>
        "<toolcall>EGitTool<action>status</action></toolcall>";

    public override bool IsEnabled { get; protected set; } = true;

    public EGitTool(IProcessRunner processRunner, IFileSystem fileSystem, EAgentConfig config)
    {
        _processRunner = processRunner;
        _fileSystem = fileSystem;
        config.Tools.TryGetValue(Name, out var tc);
        _toolConfig = tc.ValueKind == JsonValueKind.Undefined ? null : tc;
        IsEnabled = ReadCfg(_toolConfig, "enabled", true);
        _workingDir = ReadCfg(_toolConfig, "workingDir", Environment.CurrentDirectory);
    }

    public override object GetConfigSection() => new { enabled = true };

    public override async Task<EToolResult> ExecuteAsync(Dictionary<string, string?> arguments, CancellationToken cancellationToken = default)
    {
        var action = arguments.GetValueOrDefault("action")?.ToLower().Trim();

        if (string.IsNullOrEmpty(action))
            return EToolResult.Failure(Name, "Missing 'action' argument.");

        var cmd = BuildGitCommand(action, arguments);
        if (string.IsNullOrEmpty(cmd))
            return EToolResult.Failure(Name, $"Unknown git action: {action}");

        try
        {
            var result = await _processRunner.ExecuteAsync($"git {cmd}", _workingDir, cancellationToken);

            if (result.ExitCode != 0 && !string.IsNullOrWhiteSpace(result.StdErr))
                return EToolResult.Failure(Name, $"git {action} failed (exit {result.ExitCode}):\n{result.StdErr.Trim()}");

            var output = ParseGitOutput(action, result.StdOut);
            return EToolResult.Success(Name, output);
        }
        catch (Exception ex)
        {
            return EToolResult.Failure(Name, $"git error: {ex.Message}");
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

    private string BuildGitCommand(string action, Dictionary<string, string?> args)
    {
        switch (action)
        {
            case "init": return "init";
            case "status": return "status --porcelain";
            case "diff": return "diff";
            case "diff-staged": return "diff --cached";
            case "add":
            {
                var files = args.GetValueOrDefault("files") ?? ".";
                if (files == "all") files = "-A";
                return $"add {files}";
            }
            case "commit":
            {
                var msg = args.GetValueOrDefault("message") ?? "";
                if (string.IsNullOrEmpty(msg)) return "";
                return $"commit -m \"{msg.Replace("\"", "\\\"")}\"";
            }
            case "push": return "push";
            case "pull": return "pull";
            case "log":
            {
                var max = args.GetValueOrDefault("max_entries") ?? "10";
                return $"log --oneline -{max}";
            }
            case "branch": return "branch -a";
            case "checkout":
            {
                var branch = args.GetValueOrDefault("branch") ?? "";
                if (string.IsNullOrEmpty(branch)) return "";
                return $"checkout {branch}";
            }
            case "current-branch": return "rev-parse --abbrev-ref HEAD";
            default: return "";
        }
    }

    private string ParseGitOutput(string action, string rawOutput)
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
                    sb.AppendLine("✅ Working tree clean — no changes.");
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
                            "M" => "📝", "A" => "➕", "D" => "➖", "R" => "📦", "??" => "❓", _ => "📝"
                        };
                        sb.AppendLine($"  {icon} [{status}] {file}");
                    }
                }
                break;

            case "log":
                if (string.IsNullOrEmpty(output))
                    sb.AppendLine("(No commits yet.)");
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
                    sb.AppendLine("(No branches.)");
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
                    sb.AppendLine("No differences.");
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