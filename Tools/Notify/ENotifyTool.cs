using ECAssistant.Engine;

namespace ECAssistant.Tools.Notify;

/// <summary>
/// Notification Tool — lets background sub-agents push notifications to the
/// main agent and console UI. The main orchestrator drains these between turns
/// and displays them to the user.
///
/// Usage:
///   <toolcall>ENotify<message>New email from Boss: Review by Friday</message></toolcall>
///   <toolcall>ENotify<message>Build failed: line 42 syntax error</message><priority>critical</priority></toolcall>
///   <toolcall>ENotify<message>3 files changed, running tests...</message><priority>info</priority></toolcall>
/// </summary>
public class ENotifyTool : EToolBase
{
    private readonly NotificationQueue _queue;
    private readonly string _agentId;
    private readonly string _agentName;

    public ENotifyTool(NotificationQueue queue, string agentId, string agentName)
    {
        _queue = queue;
        _agentId = agentId;
        _agentName = agentName;
    }

    public override string Name => "ENotify";

    public override string Description =>
        "Send a notification to the main agent and user. Use this to report important findings, " +
        "status updates, or alerts. The notification appears immediately in the console. " +
        "Use priority=critical for urgent issues that need user attention.";

    public override string UsageExample =>
        "ENotify(message=\"Build succeeded\", priority=\"info\")";

    public override string GetToolRules() =>
        "<message>=notification text (required). " +
        "<priority>=info|warning|critical (optional, default=info). " +
        "<requires_attention>=true|false (optional, default=false — set true to interrupt main agent).";

    public override string GetToolExample() =>
        "<toolcall>ENotify<message>New email from Boss: Review by Friday</message></toolcall>\n" +
        "<toolcall>ENotify<message>Build failed at line 42</message><priority>critical</priority><requires_attention>true</requires_attention></toolcall>";

    public override Task<EToolResult> ExecuteAsync(Dictionary<string, string?> arguments, CancellationToken cancellationToken = default)
    {
        var message = arguments.GetValueOrDefault("message")?.Trim();
        if (string.IsNullOrEmpty(message))
            return Task.FromResult(EToolResult.Failure(Name, "Missing 'message' argument."));

        var priorityStr = arguments.GetValueOrDefault("priority")?.Trim().ToLower() ?? "info";
        var priority = priorityStr switch
        {
            "critical" => NotificationPriority.Critical,
            "warning" => NotificationPriority.Warning,
            _ => NotificationPriority.Info
        };

        var requiresAttention = bool.TryParse(arguments.GetValueOrDefault("requires_attention"), out var ra) && ra;

        var notification = new AgentNotification
        {
            AgentId = _agentId,
            AgentName = _agentName,
            Message = message,
            Priority = priority,
            RequiresAttention = requiresAttention,
            Data = arguments
                .Where(a => a.Key != "message" && a.Key != "priority" && a.Key != "requires_attention")
                .ToDictionary(a => a.Key, a => a.Value ?? ""),
        };

        _queue.Push(notification);

        return Task.FromResult(EToolResult.Success(Name,
            $"✅ Notification sent: [{priority}] {message}", new Dictionary<string, string>
            {
                ["notification_id"] = notification.Id,
                ["priority"] = priority.ToString(),
            }));
    }
}