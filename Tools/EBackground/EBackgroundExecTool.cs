using static ECAssistant.EColor;

using ECAssistant.Services;

namespace ECAssistant.Tools.Background;

/// <summary>
/// Background Exec Tool — lets the LLM start long-running processes
/// without blocking the agent loop.
/// 
/// The LLM can:
///   - Start a build in background: <toolcall>EBackgroundExec<command>dotnet build</command><action>start</action></toolcall>
///   - Check status: <toolcall>EBackgroundExec<action>status</action></toolcall>
///   - Get output: <toolcall>EBackgroundExec<id>bg-1</id><action>output</action></toolcall>
///   - Kill: <toolcall>EBackgroundExec<id>bg-1</id><action>kill</action></toolcall>
/// </summary>
public class EBackgroundExecTool : EToolBase
{
    private readonly BackgroundProcessManager _mgr;
    private readonly string _workingDir;

    public EBackgroundExecTool(BackgroundProcessManager mgr, string workingDir)
    {
        _mgr = mgr;
        _workingDir = workingDir;
    }

    public override string Name => "EBackgroundExec";

    public override string Description =>
        "Start, check, or kill background processes. Non-blocking — lets you run long commands " +
        "like builds while continuing to work. Use action=start to begin, action=status to list, " +
        "action=output to get results, action=kill to terminate.";

    public override string UsageExample =>
        "EBackgroundExec(command=\"dotnet build\", action=\"start\")";

    public override string GetToolRules() =>
        "<action>=start|status|output|kill. start:+<command>. output/kill:+<id>. For long commands.";


    public override string GetToolExample() =>
        "<toolcall>EBackgroundExec<command>dotnet build</command><action>start</action></toolcall>\n" +
        "<toolcall>EBackgroundExec<action>status</action></toolcall>";

    public override async Task<EToolResult> ExecuteAsync(Dictionary<string, string?> arguments)
    {
        var action = arguments.GetValueOrDefault("action")?.ToLower().Trim();

        switch (action)
        {
            case "start":
            {
                var command = arguments.GetValueOrDefault("command");
                if (string.IsNullOrWhiteSpace(command))
                    return EToolResult.Failure(Name, "Missing 'command' argument for action=start.");

                var id = await _mgr.StartAsync(command, _workingDir);
                return EToolResult.Success(Name,
                    $"Background process started: {id}\nCommand: {command}\nUse EBackgroundExec with action=output and id={id} to check results.",
                    new Dictionary<string, string> { ["process_id"] = id });
            }

            case "status":
            {
                var list = _mgr.List();
                if (list.Count == 0)
                    return EToolResult.Success(Name, "No background processes running.");

                var sb = new System.Text.StringBuilder();
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
                return EToolResult.Success(Name,
                    $"Process {id} — Status: {status}\n\n{output}",
                    new Dictionary<string, string> { ["status"] = status.ToString() });
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
                return EToolResult.Failure(Name,
                    $"Unknown action: '{action}'. Use start, status, output, or kill.");
        }
    }
}