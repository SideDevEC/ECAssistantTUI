using ECAssistant.Engine;
using ECAssistant.Orchestration;

namespace ECAssistant.Tools.SubAgent;

/// <summary>
/// Sub-Agent Spawn Tool — allows the main agent to spawn isolated sub-agents
/// for complex tasks. Sub-agents share the loaded model weights but have
/// their own context window, KV cache, and tool set.
///
/// v10.18.1: Structured error handling, retry, resource limits, cancellation.
///
/// Usage:
///   <toolcall>ESubAgent<task>Research the codebase structure and report file count</task></toolcall>
///   <toolcall>ESubAgent<task>Fix the bug in line 42</task><working_dir>/path/to/project</working_dir><tools>EShellAgent,ECodeEditor,EDotnetBuild</tools></toolcall>
///   <toolcall>ESubAgent<task>Write unit tests for the auth module</task><context_size>8192</context_size><max_turns>8</max_turns></toolcall>
/// </summary>
public class ESubAgentTool : EToolBase
{
    private readonly SubAgentManager _manager;
    private readonly string _defaultWorkingDir;

    public ESubAgentTool(SubAgentManager manager, string defaultWorkingDir)
    {
        _manager = manager;
        _defaultWorkingDir = defaultWorkingDir;
    }

    public override string Name => "ESubAgent";

    public override string Description =>
        "Spawn a sub-agent for a complex subtask. The sub-agent runs independently with its own " +
        "context window and tool set, then returns a result. Use for tasks that need deep focus " +
        "or might fill up the main context. Supports parallel sub-agents via multiple toolcalls. " +
        "Includes automatic retry, resource limits, and structured error reporting.";

    public override string UsageExample =>
        "ESubAgent(task=\"Research the codebase\")";

    public override string GetToolRules() =>
        "<task>=description of what the sub-agent should do (required). " +
        "<working_dir>=override working directory (optional). " +
        "<tools>=comma-separated tool names to allow (optional, empty=all). " +
        "<context_size>=context window size (optional, default 4096). " +
        "<max_turns>=max turns for sub-agent (optional, default 5). " +
        "<timeout>=timeout in seconds (optional, default 120). " +
        "<max_retries>=auto-retry attempts on failure (optional, default 1). " +
        "Multiple ESubAgent toolcalls in one response run in PARALLEL.";

    public override string GetToolExample() =>
        "<toolcall>ESubAgent<task>Analyze the project structure and list all .cs files</task></toolcall>\n" +
        "<toolcall>ESubAgent<task>Fix the bug</task><tools>EShellAgent,ECodeEditor</tools><context_size>8192</context_size></toolcall>";

    public override async Task<EToolResult> ExecuteAsync(Dictionary<string, string?> arguments, CancellationToken cancellationToken = default)
    {
        var taskDesc = arguments.GetValueOrDefault("task")?.Trim();
        if (string.IsNullOrEmpty(taskDesc))
            return EToolResult.Failure(Name, "Missing 'task' argument.");

        // Parse optional arguments
        var workingDir = arguments.GetValueOrDefault("working_dir") ?? _defaultWorkingDir;
        var toolsStr = arguments.GetValueOrDefault("tools") ?? "";
        var allowedTools = string.IsNullOrEmpty(toolsStr)
            ? new List<string>()
            : toolsStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        uint contextSize = 4096;
        if (uint.TryParse(arguments.GetValueOrDefault("context_size"), out var cs))
            contextSize = cs;

        int maxTurns = 5;
        if (int.TryParse(arguments.GetValueOrDefault("max_turns"), out var mt))
            maxTurns = mt;

        int timeoutSeconds = 120;
        if (int.TryParse(arguments.GetValueOrDefault("timeout"), out var ts))
            timeoutSeconds = ts;

        int maxRetries = 1;
        if (int.TryParse(arguments.GetValueOrDefault("max_retries"), out var mr))
            maxRetries = mr;

        var task = new SubAgentTask
        {
            Description = taskDesc,
            Prompt = taskDesc,
            WorkingDir = workingDir,
            AllowedTools = allowedTools,
            ContextSize = contextSize,
            MaxTurns = maxTurns,
            TimeoutSeconds = timeoutSeconds,
            MaxRetries = maxRetries,
        };

        try
        {
            var result = await _manager.RunAsync(task);

            // v10.18.1: Use structured result for output
            var output = result.ToContextString();

            var metadata = new Dictionary<string, string>
            {
                ["succeeded"] = result.Succeeded.ToString(),
                ["tool_calls"] = result.ToolCallsMade.ToString(),
                ["duration_s"] = result.Duration.TotalSeconds.ToString("F1"),
                ["files_created"] = string.Join(",", result.FilesCreated),
            };

            if (result.Error != null)
            {
                metadata["error_kind"] = result.Error.Kind.ToString();
                metadata["retry_attempt"] = result.Error.RetryAttempt.ToString();
            }

            return result.Succeeded
                ? EToolResult.Success(Name, output, metadata)
                : EToolResult.Failure(Name, output, metadata);
        }
        catch (Exception ex)
        {
            return EToolResult.Failure(Name, $"Sub-agent execution failed: {ex.Message}");
        }
    }
}