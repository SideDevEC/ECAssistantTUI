using System.Text;
using System.Text.RegularExpressions;
using ECAssistant.Tools;
using ECAssistant.Services;

namespace ECAssistant.Engine;

/// <summary>
/// A single planned tool call — concrete mapping from a sub-task to a tool + args.
/// </summary>
public class PlannedToolCall
{
    /// <summary>Tool name to call (e.g., "EShellAgent").</summary>
    public string ToolName { get; set; } = "";

    /// <summary>Arguments for the tool call.</summary>
    public Dictionary<string, string?> Args { get; set; } = new();

    /// <summary>Which sub-task indices this call covers (0-based).</summary>
    public List<int> CoversSubTasks { get; set; } = new();

    /// <summary>Human-readable description of what this call does.</summary>
    public string Description { get; set; } = "";
}

/// <summary>
/// An execution plan — the output of the mapping phase.
/// Contains grouped/batched tool calls mapped from sub-tasks.
/// </summary>
public class ExecutionPlan
{
    /// <summary>Ordered list of planned tool calls (already batched/grouped).</summary>
    public List<PlannedToolCall> Calls { get; set; } = new();

    /// <summary>Whether mapping succeeded.</summary>
    public bool IsValid { get; set; } = false;

    /// <summary>Error message if mapping failed.</summary>
    public string Error { get; set; } = "";

    /// <summary>Get a formatted string for injection into the LLM prompt.</summary>
    public string ToPromptString()
    {
        if (!IsValid || Calls.Count == 0) return "(No execution plan)";

        var sb = new StringBuilder();
        sb.AppendLine("[EXECUTION PLAN] Follow this plan exactly. Each item is a concrete tool call to make.");
        sb.AppendLine();
        for (int i = 0; i < Calls.Count; i++)
        {
            var call = Calls[i];
            sb.AppendLine($"Call {i + 1}: {call.ToolName}");
            if (!string.IsNullOrEmpty(call.Description))
                sb.AppendLine($"  Purpose: {call.Description}");
            foreach (var arg in call.Args)
                sb.AppendLine($"  <{arg.Key}>{arg.Value}</{arg.Key}>");
            if (call.CoversSubTasks.Count > 0)
                sb.AppendLine($"  Covers steps: {string.Join(", ", call.CoversSubTasks.Select(s => s + 1))}");
            sb.AppendLine();
        }
        sb.AppendLine("Execute each call in order using <toolcall> tags. You can batch multiple calls in one <lm> response.");
        return sb.ToString();
    }
}

/// <summary>
/// Step Mapper — takes decomposed sub-tasks and maps them to concrete tool calls.
/// 
/// This is the second planning phase: 
///   1. TaskPlanner → "what to do" (text steps)
///   2. StepMapper → "how to do it" (which tool, which args, batched or not)
///   3. LLM → executes the plan
/// 
/// Uses the main LLM (not the secondary model) because:
/// - It already has all tool definitions in the KV cache
/// - Better reasoning for tool selection and batching
/// - The plan in context helps the LLM stay on track during execution
/// </summary>
public class StepMapper
{
    private readonly EAgentEngine _engine;

    public StepMapper(EAgentEngine engine)
    {
        _engine = engine;
    }

    /// <summary>
    /// Map sub-tasks to concrete tool calls using the main LLM.
    /// The LLM is asked to produce a <plan>...</plan> block with planned tool calls.
    /// </summary>
    public async Task<ExecutionPlan> MapAsync(List<SubTask> subTasks, string originalGoal)
    {
        if (subTasks.Count <= 1)
        {
            // Single task — no mapping needed, let the LLM handle it directly
            return new ExecutionPlan { IsValid = true, Calls = new() };
        }

        // Build the tool list for the prompt
        var toolList = new StringBuilder();
        foreach (var tool in _engine.Tools)
        {
            toolList.AppendLine($"- {tool.Name}: {tool.Description}");
            var example = tool.GetToolExample();
            if (!string.IsNullOrEmpty(example))
                toolList.AppendLine($"  Example: {example}");
        }

        // Build the steps list
        var stepsList = new StringBuilder();
        for (int i = 0; i < subTasks.Count; i++)
        {
            stepsList.AppendLine($"Step {i + 1}: {subTasks[i].Description}");
        }

        // Build the mapping prompt — asks the LLM to output a <plan> block
        var prompt = $@"You are a task planner. Map each step below to a concrete tool call.

Available tools:
{toolList}

Steps to map:
{stepsList}

Rules:
1. Map EACH step to exactly one tool call.
2. If multiple steps use the same tool with similar args, COMBINE them into one call (e.g., shell commands with semicolons).
3. If a step doesn't need a tool (e.g., just answering a question), skip it.
4. Output ONLY a <plan> block. No thinking, no explanation.

Format:
<plan>
<call>
<tool>ToolName</tool>
<arg_name>arg_value</arg_name>
<covers>1,2,3</covers>
<desc>What this call does</desc>
</call>
<call>
<tool>ToolName</tool>
<arg_name>arg_value</arg_name>
<covers>4</covers>
<desc>What this call does</desc>
</call>
</plan>

The <covers> tag lists which step numbers (1-based) this call covers.
Combine steps into one call when possible (e.g., batch shell commands).

<plan>
";

        // Use the main LLM to generate the plan
        // We feed this as a special prompt and parse the <plan> block
        var response = await _engine.GeneratePlanAsync(prompt);

        return ParsePlan(response, subTasks);
    }

    /// <summary>Parse the LLM's <plan> response into an ExecutionPlan.</summary>
    private ExecutionPlan ParsePlan(string response, List<SubTask> subTasks)
    {
        var plan = new ExecutionPlan();

        if (string.IsNullOrWhiteSpace(response))
        {
            plan.Error = "Empty response from LLM";
            return plan;
        }

        // Extract <plan>...</plan> block
        var planStart = response.IndexOf("<plan>", StringComparison.OrdinalIgnoreCase);
        var planEnd = response.IndexOf("</plan>", StringComparison.OrdinalIgnoreCase);

        string planContent;
        if (planStart >= 0 && planEnd >= 0 && planEnd > planStart)
        {
            planContent = response.Substring(planStart + 6, planEnd - planStart - 6);
        }
        else
        {
            // No <plan> tags — try to parse the raw response
            planContent = response;
        }

        // Parse <call>...</call> blocks
        var callMatches = Regex.Matches(planContent, @"<call>(.*?)</call>", RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match callMatch in callMatches)
        {
            var callContent = callMatch.Groups[1].Value;

            var plannedCall = new PlannedToolCall();

            // Extract <tool>...</tool>
            var toolMatch = Regex.Match(callContent, @"<tool>(.*?)</tool>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (toolMatch.Success)
                plannedCall.ToolName = toolMatch.Groups[1].Value.Trim();

            // Extract <desc>...</desc>
            var descMatch = Regex.Match(callContent, @"<desc>(.*?)</desc>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (descMatch.Success)
                plannedCall.Description = descMatch.Groups[1].Value.Trim();

            // Extract <covers>...</covers> — comma-separated step numbers (1-based)
            var coversMatch = Regex.Match(callContent, @"<covers>(.*?)</covers>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (coversMatch.Success)
            {
                var coversStr = coversMatch.Groups[1].Value.Trim();
                foreach (var part in coversStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (int.TryParse(part, out int stepNum))
                        plannedCall.CoversSubTasks.Add(stepNum - 1); // Convert to 0-based
                }
            }

            // Extract all other <argname>value</argname> tags as tool arguments
            var argMatches = Regex.Matches(callContent, @"<(\w+)>(.*?)</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match argMatch in argMatches)
            {
                var tagName = argMatch.Groups[1].Value;
                var tagValue = argMatch.Groups[2].Value.Trim();

                // Skip structural tags
                if (tagName.Equals("tool", StringComparison.OrdinalIgnoreCase) ||
                    tagName.Equals("desc", StringComparison.OrdinalIgnoreCase) ||
                    tagName.Equals("covers", StringComparison.OrdinalIgnoreCase))
                    continue;

                plannedCall.Args[tagName] = tagValue;
            }

            if (!string.IsNullOrEmpty(plannedCall.ToolName))
                plan.Calls.Add(plannedCall);
        }

        // v10.17.2: Deduplicate calls — LLM sometimes produces duplicate calls covering the same steps.
        // Keep only the first call for each unique set of (ToolName + Args + CoversSubTasks).
        var deduped = new List<PlannedToolCall>();
        var seen = new HashSet<string>();
        foreach (var call in plan.Calls)
        {
            var key = $"{call.ToolName}|{string.Join(",", call.Args.OrderBy(a => a.Key).Select(a => $"{a.Key}={a.Value}"))}|{string.Join(",", call.CoversSubTasks.OrderBy(x => x))}";
            if (seen.Add(key))
                deduped.Add(call);
        }
        if (deduped.Count < plan.Calls.Count)
        {
            Logger.Info("StepMapper", $"Deduplicated: {plan.Calls.Count} → {deduped.Count} calls");
            plan.Calls = deduped;
        }

        // Validate: every sub-task should be covered
        var uncovered = new List<int>();
        for (int i = 0; i < subTasks.Count; i++)
        {
            if (!plan.Calls.Any(c => c.CoversSubTasks.Contains(i)))
                uncovered.Add(i);
        }

        if (uncovered.Count > 0)
        {
            // Not all steps are covered — that's OK, the LLM will handle them ad-hoc
            Logger.Warn("StepMapper", $"Steps not covered by plan: {string.Join(", ", uncovered.Select(s => s + 1))} — LLM will handle ad-hoc");
        }

        plan.IsValid = plan.Calls.Count > 0;

        if (plan.IsValid)
        {
            Logger.Info("StepMapper", $"Plan: {plan.Calls.Count} call(s) covering {plan.Calls.SelectMany(c => c.CoversSubTasks).Distinct().Count()}/{subTasks.Count} steps");
        }
        else
        {
            plan.Error = "No valid tool calls found in plan";
        }

        return plan;
    }
}