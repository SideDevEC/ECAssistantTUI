using System.Text;
using System.Text.Json;
using ECAssistant.Config;
using ECAssistant.Interfaces;
using ECAssistant.Services;

namespace ECAssistant.Tools.Background;

/// <summary>
/// Background Exec Tool — lets the LLM start long-running processes
/// without blocking the agent loop.
/// </summary>
public class EBackgroundExecTool : EToolBase
{
    private readonly BackgroundProcessManager _mgr;
    private readonly IProcessRunner _processRunner;
    private readonly IFileSystem _fileSystem;
    private readonly JsonElement? _toolConfig;
    private readonly string _workingDir;

    public override string Name => "EBackgroundExec";

    public override string Description =>
        "Start, check, or kill background processes. Non-blocking — lets you run long commands " +
        "like builds while continuing to work. Use action=start to begin, action=status to list, " +
        "action=output to get results, action=kill to terminate.";

    public override string UsageExample =>
        "<toolcall>EBackgroundExec<action>start</action><command>dotnet build</command></toolcall>";

    public override bool IsEnabled { get; protected set; } = true;

    public EBackgroundExecTool(
        BackgroundProcessManager mgr,
        IProcessRunner processRunner,
        IFileSystem fileSystem,
        EAgentConfig config)
    {
        _mgr = mgr;
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

        switch (action)
        {
            case "start":
            {
                var command = arguments.GetValueOrDefault("command");
                if (string.IsNullOrWhiteSpace(command))
                    return EToolResult.Failure(Name, "Missing 'command' argument for action=start.");

                if (cancellationToken.IsCancellationRequested)
                    return EToolResult.Failure(Name, "Background process start was cancelled by user.");

                var id = await _mgr.StartAsync(command, _workingDir);
                return EToolResult.Success(Name, $"Background process started: {id}\nCommand: {command}\nUse EBackgroundExec with action=output and id={id} to check results.");
            }

            case "status":
            {
                var list = _mgr.List();
                if (list.Count == 0)
                    return EToolResult.Success(Name, "No background processes running.");

                var sb = new StringBuilder();
                sb.AppendLine($"Background processes ({list.Count}):");
                foreach (var p in list)
                    sb.AppendLine($"  {p}");
                return EToolResult.Success(Name, sb.ToString());
            }

            case "output":
            {
                var id = arguments.GetValueOrDefault("id");
                if (string.IsNullOrWhiteSpace(id))
                    return EToolResult.Failure(Name, "Missing 'id' argument for action=output.");

                var status = _mgr.GetStatus(id);
                var output = _mgr.GetOutput(id);
                return EToolResult.Success(Name, $"Process {id} — Status: {status}\n\n{output}");
            }

            case "kill":
            {
                var id = arguments.GetValueOrDefault("id");
                if (string.IsNullOrWhiteSpace(id))
                    return EToolResult.Failure(Name, "Missing 'id' argument for action=kill.");

                var killed = _mgr.Kill(id);
                return killed
                    ? EToolResult.Success(Name, $"Killed process: {id}")
                    : EToolResult.Failure(Name, $"Failed to kill process: {id} (not running or not found)");
            }

            default:
                return EToolResult.Failure(Name, $"Unknown action: '{action}'. Use start, status, output, or kill.");
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
}