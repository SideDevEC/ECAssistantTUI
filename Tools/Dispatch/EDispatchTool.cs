using ECAssistant.Engine;

namespace ECAssistant.Tools.Dispatch;

/// <summary>
/// Background Agent Dispatch Tool — lets the main agent spawn long-lived
/// background sub-agents that monitor for events and push notifications.
///
/// Unlike ESubAgent (synchronous: spawn → wait → result), EDispatch spawns
/// agents that run in the background. The main agent continues working
/// and receives notifications asynchronously.
///
/// Usage:
///   <toolcall>EDispatch<name>Email Monitor</name><mission>Monitor for new emails and summarize them</mission><poll_command>checkmail --new</poll_command><poll_interval>30000</poll_interval></toolcall>
///   <toolcall>EDispatch<name>Build Watcher</name><mission>Watch for file changes and auto-rebuild</mission><poll_command>find . -name '*.cs' -newer .lastbuild</poll_command><poll_interval>5000</poll_interval><event_prompt>Files changed: {event_description}. Run dotnet build and report results. Use ENotify to alert the user.</event_prompt></toolcall>
///
/// Actions:
///   spawn    — Start a new background agent
///   stop     — Stop a running agent by ID
///   stop_all — Stop all background agents
///   status   — Get status of all background agents
///   notify   — Check for pending notifications from background agents
/// </summary>
public class EDispatchTool : EToolBase
{
    private readonly BackgroundAgentManager _manager;

    public EDispatchTool(BackgroundAgentManager manager)
    {
        _manager = manager;
    }

    public override string Name => "EDispatch";

    public override string Description =>
        "Dispatch and manage background sub-agents. Background agents run long-lived in the " +
        "background, monitor for events, and push notifications to the main agent. " +
        "Actions: spawn, stop, stop_all, status, notify. " +
        "Unlike ESubAgent (synchronous), EDispatch agents run async — main agent continues working.";

    public override string UsageExample =>
        "EDispatch(action=\"spawn\", name=\"Email Monitor\", mission=\"Monitor emails\", poll_command=\"checkmail --new\", poll_interval=30000)";

    public override string GetToolRules() =>
        "<action>=spawn|stop|stop_all|status|notify (required). " +
        "spawn: +<name>+<mission>+<poll_command>(optional)+<poll_interval>(optional,ms)+<event_prompt>(optional)+<working_dir>(optional)+<max_cycles>(optional)+<idle_timeout>(optional,s). " +
        "stop: +<agent_id>. " +
        "event_prompt supports {event_type}, {event_description}, {event_source}, {event_data}, {mission}, {agent_name}.";

    public override string GetToolExample() =>
        "<toolcall>EDispatch<action>spawn</action><name>Email Monitor</name><mission>Check for new emails and summarize them</mission><poll_command>echo 'mock: new email from boss'</poll_command><poll_interval>15000</poll_interval></toolcall>\n" +
        "<toolcall>EDispatch<action>status</action></toolcall>\n" +
        "<toolcall>EDispatch<action>stop</action><agent_id>abc123</agent_id></toolcall>";

    public override async Task<EToolResult> ExecuteAsync(Dictionary<string, string?> arguments, CancellationToken cancellationToken = default)
    {
        var action = arguments.GetValueOrDefault("action")?.Trim().ToLower();
        if (string.IsNullOrEmpty(action))
            return EToolResult.Failure(Name, "Missing 'action' argument.");

        return action switch
        {
            "spawn" => await DoSpawn(arguments, cancellationToken),
            "stop" => await DoStop(arguments),
            "stop_all" => await DoStopAll(),
            "status" => DoStatus(),
            "notify" => DoNotify(),
            _ => EToolResult.Failure(Name, $"Unknown action: {action}")
        };
    }

    private async Task<EToolResult> DoSpawn(Dictionary<string, string?> args, CancellationToken ct)
    {
        var name = args.GetValueOrDefault("name")?.Trim();
        var mission = args.GetValueOrDefault("mission")?.Trim();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(mission))
            return EToolResult.Failure(Name, "spawn requires 'name' and 'mission' arguments.");

        var config = new BackgroundAgentConfig
        {
            Name = name,
            Mission = mission,
            PollCommand = args.GetValueOrDefault("poll_command") ?? "",
            EventPromptTemplate = args.GetValueOrDefault("event_prompt") ?? "",
            WorkingDir = args.GetValueOrDefault("working_dir") ?? "",
            AllowedTools = (args.GetValueOrDefault("tools") ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList(),
        };

        if (int.TryParse(args.GetValueOrDefault("poll_interval"), out var pi))
            config.PollIntervalMs = pi;
        if (int.TryParse(args.GetValueOrDefault("max_cycles"), out var mc))
            config.MaxCycles = mc;
        if (int.TryParse(args.GetValueOrDefault("idle_timeout"), out var it))
            config.IdleTimeoutSeconds = it;
        if (uint.TryParse(args.GetValueOrDefault("context_size"), out var cs))
            config.ContextSize = cs;
        if (int.TryParse(args.GetValueOrDefault("max_turns"), out var mt))
            config.MaxTurnsPerCycle = mt;

        try
        {
            var agentId = await _manager.SpawnAsync(config);
            return EToolResult.Success(Name,
                $"✅ Background agent '{name}' spawned (ID: {agentId}). " +
                $"It will run in the background and send notifications via ENotify. " +
                $"Use EDispatch(action=\"status\") to check status or " +
                $"EDispatch(action=\"stop\", agent_id=\"{agentId}\") to stop it.",
                new Dictionary<string, string> { ["agent_id"] = agentId, ["agent_name"] = name });
        }
        catch (Exception ex)
        {
            return EToolResult.Failure(Name, $"Failed to spawn background agent: {ex.Message}");
        }
    }

    private async Task<EToolResult> DoStop(Dictionary<string, string?> args)
    {
        var agentId = args.GetValueOrDefault("agent_id")?.Trim();
        if (string.IsNullOrEmpty(agentId))
            return EToolResult.Failure(Name, "stop requires 'agent_id' argument.");

        var stopped = await _manager.StopAsync(agentId);
        return stopped
            ? EToolResult.Success(Name, $"Stopped background agent {agentId}.")
            : EToolResult.Failure(Name, $"Agent {agentId} not found.");
    }

    private async Task<EToolResult> DoStopAll()
    {
        await _manager.StopAllAsync();
        return EToolResult.Success(Name, "Stopped all background agents.");
    }

    private EToolResult DoStatus()
    {
        var statuses = _manager.GetStatusAll();
        if (statuses.Count == 0)
            return EToolResult.Success(Name, "No background agents running.");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Background Agents ({statuses.Count}):");
        foreach (var s in statuses)
            sb.AppendLine($"  • {s}");
        return EToolResult.Success(Name, sb.ToString());
    }

    private EToolResult DoNotify()
    {
        var notifications = _manager.DrainNotifications();
        if (notifications.Count == 0)
            return EToolResult.Success(Name, "No pending notifications.");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Pending Notifications ({notifications.Count}):");
        foreach (var n in notifications)
            sb.AppendLine($"  {n.ToDisplayString()}");
        return EToolResult.Success(Name, sb.ToString());
    }
}