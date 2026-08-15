using System.Text;
using System.Text.Json;
using ECAssistant.Interfaces;

namespace ECAssistant.Tools.Git;

/// <summary>
/// Git Integration Tool — wraps common git operations with structured output.
/// Returns clean, parsed results instead of raw git output.
///
/// Usage:
///   EGitTool(action="status")
///   EGitTool(action="diff")
///   EGitTool(action="commit", message="fix: update config")
///   EGitTool(action="log", max_entries="5")
/// </summary>
public class EGitTool : ITool
{
    private readonly IProcessRunner _processRunner;
    private readonly IFileSystem _fileSystem;
    private readonly IConfigProvider _configProvider;
    private readonly IColorFormatter _colorFormatter;

    private string WorkingDir => _configProvider.GetValue("git.workingDir", Environment.CurrentDirectory);

    public EGitTool(IProcessRunner processRunner, IFileSystem fileSystem, IConfigProvider configProvider, IColorFormatter colorFormatter)
    {
        _processRunner = processRunner;
        _fileSystem = fileSystem;
        _configProvider = configProvider;
        _colorFormatter = colorFormatter;
    }

    public string Name => "EGitTool";

    public string Description =>
        "Git operations with structured output. Actions: init, status, diff, commit, push, pull, log, " +
        "add, branch, checkout. Better than raw shell for git — parses output into clean format.";

    public Interfaces.ToolPolicy GetPolicy() => Interfaces.ToolPolicy.Allowed(Name);

    public async Task<string> ExecuteAsync(string input, CancellationToken ct = default)
    {
        var args = ParseInput(input);
        var action = args.GetValueOrDefault("action")?.ToLower().Trim();

        if (string.IsNullOrEmpty(action))
            return $"[{Name}] ERROR: Missing 'action' argument.";

        var cmd = BuildGitCommand(action, args);
        if (string.IsNullOrEmpty(cmd))
            return $"[{Name}] ERROR: Unknown git action: {action}";

        try
        {
            var result = await _processRunner.ExecuteAsync($"git {cmd}", WorkingDir, ct);

            if (result.ExitCode != 0 && !string.IsNullOrWhiteSpace(result.StdErr))
                return $"[{Name}] ERROR: git {action} failed (exit {result.ExitCode}):\n{result.StdErr.Trim()}";

            var output = ParseGitOutput(action, result.StdOut);
            return $"[{Name}] {output}";
        }
        catch (Exception ex)
        {
            return $"[{Name}] ERROR: git error: {ex.Message}";
        }
    }

    private Dictionary<string, string> ParseInput(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return new Dictionary<string, string>();

        try
        {
            using var doc = JsonDocument.Parse(input);
            var dict = new Dictionary<string, string>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                    dict[prop.Name] = prop.Value.GetString() ?? string.Empty;
                else
                    dict[prop.Name] = prop.Value.ToString();
            }
            return dict;
        }
        catch
        {
            var dict = new Dictionary<string, string>();
            var pairs = input.Split('&');
            foreach (var pair in pairs)
            {
                var eq = pair.IndexOf('=');
                if (eq > 0)
                    dict[pair[..eq].Trim()] = pair[(eq + 1)..].Trim();
            }
            return dict;
        }
    }

    private string BuildGitCommand(string action, Dictionary<string, string> args)
    {
        switch (action)
        {
            case "init":
                return "init";
            case "status":
                return "status --porcelain";
            case "diff":
                return "diff";
            case "diff-staged":
                return "diff --cached";
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
            case "push":
                return "push";
            case "pull":
                return "pull";
            case "log":
            {
                var max = args.GetValueOrDefault("max_entries") ?? "10";
                return $"log --oneline -{max}";
            }
            case "branch":
                return "branch -a";
            case "checkout":
            {
                var branch = args.GetValueOrDefault("branch") ?? "";
                if (string.IsNullOrEmpty(branch)) return "";
                return $"checkout {branch}";
            }
            case "current-branch":
                return "rev-parse --abbrev-ref HEAD";
            default:
                return "";
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