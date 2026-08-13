using static ECAssistant.EColor;

using System.Text;
using System.Text.RegularExpressions;
using ECAssistant.Engine;
using ECAssistant.Tools;
using ECAssistant.Services;
using ECAssistant.UI;
using ECAssistant.Session;

namespace ECAssistant.Orchestration;

/// <summary>
/// Orchestrator — the decision-making brain for multi-step agent workflows.
/// 
/// v2.2: Clean response parsing only.
/// The engine (EAgentEngine) already strips noise via ExtractCleanResponse().
/// We simply detect what type of structured block was returned:
///    1. <toolcall>...</toolcall> → extract tool name + args, execute, continue loop
///    2. <output>...</output> → extract answer, return to user, stop loop
///    3. Neither → error (invalid response), stop loop
/// </summary>
public sealed class AgentOrchestrator : IAsyncDisposable
{
    private readonly EAgentEngine _engine;
    private readonly ISessionOutput? _out;
    private int _turnCount = 0;
    private readonly List<string> _toolCallLog = new();
    private readonly List<string> _completedSteps = new();
    private int _formatRetries = 0;
    private const int MaxFormatRetries = 2;

     // v10.6: TaskPlanner for chained multi-step tasks
    private List<SubTask>? _subTasks = null;
    private int _currentSubTask = 0;
     // v10.17: Execution plan from StepMapper
    private ExecutionPlan? _executionPlan = null;

      // ─── Hard Limits ──────────────────────
    private int _maxTurns;   // v10.6: changed from readonly to allow dynamic adjustment
    private readonly int _maxFailuresBeforeStop;

      // ─── Whitelist of valid tool names ─────
    private readonly HashSet<string> _toolWhitelist = new(StringComparer.OrdinalIgnoreCase);

      // ─── Tool Policy (permissions + approval) ─────
    private readonly ToolPolicy _toolPolicy;

     // v10.18: Sub-agent manager (lazy-init, created when first sub-agent tool is registered)
    private SubAgentManager? _subAgentManager;
    public SubAgentManager? SubAgentManager => _subAgentManager;

      /// <summary>Create orchestrator with config.</summary>
    public AgentOrchestrator(
        EAgentEngine engine,
        ISessionOutput? sessionOutput = null,
        int maxTurns = 5,
        int maxFailures = 3,
        ToolPolicy? toolPolicy = null)
              {
                  _engine = engine;
                  _out = sessionOutput;
                  _maxTurns = Math.Max(1, maxTurns);
                  _maxFailuresBeforeStop = maxFailures;
                  _toolPolicy = toolPolicy ?? new ToolPolicy();

            foreach (var tool in _engine.Tools)
                   _toolWhitelist.Add(tool.Name);
              }

      /// <summary>v10.18: Initialize sub-agent support. Creates SubAgentManager and registers ESubAgent tool.</summary>
     public void InitializeSubAgents(string defaultWorkingDir)
      {
          _subAgentManager = new SubAgentManager(_engine, defaultWorkingDir);
          _engine.RegisterTool(new Tools.SubAgent.ESubAgentTool(_subAgentManager, defaultWorkingDir));
          _toolWhitelist.Add("ESubAgent");
          _toolPolicy.SetPermission("ESubAgent", ToolPermissionLevel.Allowed, "Sub-agent spawning");
         _out?.WriteInfo("Sub-agent system initialized and ESubAgent tool registered.");
      }

           /// <summary>v10.18: Initialize sub-agents AND rebuild KV cache to include ESubAgent in system prompt.</summary>
     public async Task InitializeSubAgentsAsync(string defaultWorkingDir)
      {
         InitializeSubAgents(defaultWorkingDir);
          // Rebuild KV cache so ESubAgent appears in the tool list the LLM sees
         _out?.WriteWarning("Rebuilding KV cache to include ESubAgent...");
         await _engine.ResetAndRebuildCacheAsync();
      }

      /// <summary>Get the tool policy instance (for runtime modification).</summary>
    public ToolPolicy Policy => _toolPolicy;

      /// <summary>Execute multi-step workflow autonomously.</summary>
    public async Task<OrchestratorResult> ExecuteMultiStep(string goal)
              {
         // v10.4.4: Reset turn counters at the start of each new user request.
         // v10.5: Also reset engine for new request (KV cache keeps static prefix).
        Reset();
         _engine.ResetForNewRequest();

         // v10.5: Prefill the static prefix into KV cache if not done yet.
        await _engine.PrefillStaticPrefix();

         // v10.6: Decompose the request into sub-tasks using TaskPlanner
         // v10.7: Try secondary model (LLM) first, fall back to keyword-based
        var planner = _engine.TaskPlanner;
        if (planner != null)
         {
            List<SubTask>? decomposed = null;

             // v10.7: Try secondary model for LLM-based decomposition
            var secondary = _engine.SecondaryModel;
            if (secondary != null && secondary.IsLoaded)
             {
                _out?.WriteInfo("Using secondary model for task decomposition...");
                var steps = await secondary.DecomposeTaskAsync(goal);
                if (steps != null && steps.Count > 0)
                 {
                    decomposed = steps.Select(s => new SubTask { Description = s, Status = SubTaskStatus.Pending }).ToList();
                    _out?.WriteSuccess($"Secondary model produced {decomposed.Count} steps.");
                 }
                else
                 {
                    _out?.WriteWarning("Secondary model failed — falling back to keywords.");
                 }
             }

             // Fallback: keyword-based decomposition
            if (decomposed == null || decomposed.Count == 0)
             {
                decomposed = planner.Decompose(goal);
             }
            else
             {
                 // Populate planner with LLM-generated steps so GetProgressContext works
                planner.Decompose(string.Join(" then ", decomposed.Select(s => s.Description)));
             }

             _subTasks = decomposed;
             _currentSubTask = 0;

            if (_subTasks.Count > 1)
             {
                 _subTasks[0].Status = SubTaskStatus.InProgress;
                 // v10.6: Dynamic turn limit — allow 2 turns per sub-task + 2 buffer for output/retries
                 _maxTurns = Math.Max(_maxTurns, _subTasks.Count * 2 + 2);
                _out?.WriteInfo($"Decomposed into {_subTasks.Count} steps — max turns adjusted to {_maxTurns}");
                for (int i = 0; i < _subTasks.Count; i++)
                    _out?.WriteDim($"  Step {i+1}: {_subTasks[i].Description}");
                _out?.BlankLine();
             }

             // v10.17: Step Mapping — map sub-tasks to concrete tool calls
            ExecutionPlan? executionPlan = null;
            if (_subTasks != null && _subTasks.Count > 1)
             {
                var mapper = new StepMapper(_engine);
                _out?.WriteInfo("Mapping steps to tool calls...");
                executionPlan = await mapper.MapAsync(_subTasks, goal);
                 _executionPlan = executionPlan; // Store for advancement logic
                if (executionPlan.IsValid)
                 {
                    _out?.WriteSuccess($"Plan: {executionPlan.Calls.Count} call(s):");
                    for (int i = 0; i < executionPlan.Calls.Count; i++)
                     {
                        var c = executionPlan.Calls[i];
                        _out?.WriteDim($"  Call {i+1}: {c.ToolName} — covers steps {string.Join(",", c.CoversSubTasks.Select(s => s+1))} — {c.Description}");
                     }
                    _out?.BlankLine();
                 }
                else
                 {
                    _out?.WriteWarning($"Mapping failed: {executionPlan.Error} — LLM will plan ad-hoc.");
                 }

                 // Inject the execution plan into context so the LLM follows it
                if (executionPlan != null && executionPlan.IsValid)
                 {
                     _engine.InjectExecutionPlan(executionPlan.ToPromptString());
                 }
             }
         }

        _out?.WriteLine($"[Orchestrator] Starting for: {goal}");
        _out?.WriteLine($"[Orchestrator] Max turns: {_maxTurns}, Failures limit: {_maxFailuresBeforeStop}");

            while (_turnCount < _maxTurns)
                   {
                 // v10.9: Check for user cancellation before each turn
                if (_engine.ExecutionToken.IsCancellationRequested)
                 {
                    _out?.WriteWarning("Execution cancelled by user. Stopping.");
                    var cancelSummary = FormatTurnLog();
                    return new OrchestratorResult
                     {
                        FinalOutput = $"Execution cancelled by user.\n\n{cancelSummary}",
                        ToolCallsMade = _turnCount,
                        Status = OrchestratorStatus.GoalAchieved   // not an error — user chose to stop
                     };
                 }
            Logger.Info("Orchestrator", $"Turn {_turnCount + 1}/{_maxTurns}");

                  // Step 1: Ask the LLM to decide what to do (with full context of tools + history)
              var llmResponse = await _engine.GenerateAsync(goal);

              // v10.11.1: Check if generation was stopped by user (ESC) — bail out immediately,
              // don't attempt format retries on the "(Stopped by user)" string.
             if (llmResponse == "(Stopped by user)" || _engine.IsExecutionStopped)
             {
                _out?.WriteWarning("Generation was stopped by user (ESC). Not retrying.");

                 // v10.18.1: Cancel all active sub-agents when main agent is stopped
                 _subAgentManager?.CancelAll();

                 // v10.11.1: Clean up stale context from the stopped attempt so the next
                 // command starts fresh. The KV cache static prefix is preserved.
                 _engine.ClearContextWindowOnly();
                 // v10.11.1: Rebuild KV cache to remove stale user message tokens from the stopped attempt.
                 // The static prefix (system prompt + tools) is re-prefilled fresh.
                await _engine.RebuildCacheAfterStopAsync();
                var stopSummary = FormatTurnLog();
                return new OrchestratorResult
                 {
                    FinalOutput = $"Execution stopped by user (ESC).\n\n{stopSummary}",
                    ToolCallsMade = _turnCount,
                    Status = OrchestratorStatus.GoalAchieved   // not an error — user chose to stop
                 };
             }

             _out?.WriteDim($"[Orchestrator] Response ({llmResponse.Length} chars): {EGuiBase.Truncate(llmResponse, 200)}");

                  // Step 2: Parse the clean LLM output — detect which block type was returned
              var decision = ParseLLMDecision(llmResponse);
              _out?.WriteDim($"[Orchestrator] Parse result: WantsToolCall={decision.WantsToolCall}, WantsDirectAnswer={decision.WantsDirectAnswer}, ToolCalls={decision.ToolCallCount}, ToolName={decision.ToolName}");

              if (decision.WantsToolCall)
                        {
                 _formatRetries = 0; // reset on valid tool call

                 // v10.13: Multi-tool parallel execution
                if (decision.IsMultiCall)
                 {
                    Logger.Info("Orchestrator", $"Multi-tool call: {decision.ToolCallCount} tools");
                    _out?.WriteInfo($"Multi-tool call: {decision.ToolCallCount} tools — analyzing dependencies...");

                     // Create parallel executor
                    var parallelExec = new ParallelToolExecutor(
                         _engine,
                         _toolPolicy,
                        ExecuteTool,
                        msg => _out?.WriteDim(msg));

                     // Execute all tool calls with dependency-aware parallelism
                    var batchResult = await parallelExec.ExecuteAsync(decision.ToolCalls, _engine.ExecutionToken);

                     // Display summary
                    var consoleSummary = ParallelToolExecutor.FormatConsoleSummary(batchResult);
                    if (batchResult.AllSucceeded)
                         _out?.WriteSuccess($"Batch: {consoleSummary}");
                    else
                         _out?.WriteWarning($"Batch: {consoleSummary}");
                    Logger.Info("Orchestrator", $"Batch result: {consoleSummary}");

                     // Combine all results into one output block for the LLM
                    var combinedOutput = ParallelToolExecutor.CombineResults(batchResult);
                    _out?.WriteLine($"[Orchestrator] Batch output:\n{EGuiBase.Truncate(combinedOutput, 2000)}");

                     // v10.13.1: Log one summary entry per batch (not per tool) for accurate streak detection
                    var okCount = batchResult.Results.Count(r => r.Succeeded);
                    var failCount = batchResult.Results.Count(r => !r.Succeeded);
                    if (failCount > 0)
                         _toolCallLog.Add($"BATCH FAIL: {failCount}/{batchResult.Results.Count} tools failed");
                    else
                         _toolCallLog.Add($"BATCH OK: {okCount}/{batchResult.Results.Count} tools succeeded");

                     // Track completed steps per tool (for summary)
                    foreach (var r in batchResult.Results)
                     {
                        var stepCmd = r.ToolCall.Args.GetValueOrDefault("command") ?? r.ToolCall.Args.GetValueOrDefault("action") ?? "";
                        var stepDesc = $"{r.ToolCall.ToolName}: {EGuiBase.Truncate(stepCmd, 80)}";
                         _completedSteps.Add(stepDesc);
                     }

                     // v10.16.2: Conservative batch sub-task advancement.
                     // Advance one sub-task per successful tool in the batch.
                     // The LLM decides when ALL steps are done via <output>.
                    if (_subTasks != null && _subTasks.Count > 1)
                     {
                        if (failCount == 0 && okCount > 0)
                         {
                             // All tools succeeded — advance one sub-task per successful tool
                            var toAdvance = Math.Min(okCount, _subTasks.Count - _currentSubTask);
                            for (int i = 0; i < toAdvance; i++)
                                AdvanceSubTask(true, "Batch", $"Batch tool {i + 1}/{toAdvance} succeeded");
                         }
                        else if (okCount == 0 && failCount > 0)
                         {
                            AdvanceSubTask(false, "Batch", $"{failCount} tools failed");
                         }
                        else if (okCount > 0 && failCount > 0)
                         {
                             // Mixed — advance succeeded ones, leave failed one pending
                            var toAdvance = Math.Min(okCount, _subTasks.Count - _currentSubTask);
                            for (int i = 0; i < toAdvance; i++)
                                AdvanceSubTask(true, "Batch", $"Batch tool {i + 1}/{toAdvance} succeeded");
                         }
                     }

                     // Add combined result to conversation history (one block)
                     _engine.AddToolResult("Batch", combinedOutput);

                     // Check for failure streak — one batch = one turn in streak detection
                    if (failCount > 0)
                     {
                        if (IsFailureStreak(_maxFailuresBeforeStop))
                         {
                            _out?.WriteLine($"[Orchestrator] Too many consecutive failure turns ({_maxFailuresBeforeStop}). Stopping.");
                            return new OrchestratorResult
                             {
                                FinalOutput = $"Stopped after {_maxFailuresBeforeStop} consecutive failure turns during batch execution.\nFailed tools: {string.Join(", ", batchResult.Results.Where(r => !r.Succeeded).Select(r => r.ToolCall.ToolName))}",
                                ToolCallsMade = _turnCount + 1,
                                Status = OrchestratorStatus.TurnsExhausted
                             };
                         }
                     }

                     // Inject step-aware directive
                    var stepDirective = BuildStepDirective();
                     _engine.InjectFormatRetry(stepDirective);

                    _out?.WriteDim($"[Orchestrator] Batch complete, looping back to LLM (turn {_turnCount + 1})...");
                     _turnCount++;
                    continue;
                 }

                 // ── Single tool call (original path) ──
                Logger.Info("Orchestrator", $"Tool call: {decision.ToolName}");

                var argsDict = decision.Args;

                 // ── Tool Policy Check ──
                var policyDecision = _toolPolicy.Check(decision.ToolName!, argsDict);
                if (policyDecision.NeedsApproval)
                 {
                    _out?.WriteLine($"[Policy] {policyDecision.Message}");
                     // In console mode, ask the user directly
                    _out?.WriteLine($"[Policy] Approve execution of {decision.ToolName} with args: {string.Join(", ", argsDict.Select(kvp => kvp.Key + "=" + (kvp.Value ?? "(null)")))}?");
                    var approval = Program.Gui.PromptRaw("[y/N] ")?.Trim().ToLower();
                    if (approval != "y" && approval != "yes")
                     {
                        _out?.WriteLine($"[Policy] Tool execution DENIED by user: {decision.ToolName}");
                         _engine.AddToolResult(decision.ToolName!, "[DENIED] User did not approve this tool execution.");
                         _turnCount++;
                        continue;
                     }
                    _out?.WriteLine($"[Policy] Approved by user.");
                 }
                else if (!policyDecision.CanExecute)
                 {
                    _out?.WriteLine($"[Policy] {policyDecision.Message}");
                     _engine.AddToolResult(decision.ToolName!, $"[BLOCKED] {policyDecision.Message}");
                     _turnCount++;
                    continue;
                 }

                var startMs = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;

                    try
                          {
                         // v10.9.3: Check cancellation before executing tool
                        if (_engine.ExecutionToken.IsCancellationRequested)
                         {
                            _out?.WriteWarning("Execution cancelled before tool call.");
                            return new OrchestratorResult
                             {
                                FinalOutput = "Execution cancelled by user.",
                                ToolCallsMade = _turnCount,
                                Status = OrchestratorStatus.GoalAchieved
                             };
                         }
                        var result = await ExecuteTool(decision.ToolName, argsDict);
                        var elapsedMs = (long)((DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond) - startMs);

                            if (result.Succeeded)
                                 _out?.WriteSuccess($"[Tool] {decision.ToolName}: OK ({elapsedMs}ms)");
                            else
                                 _out?.WriteError($"[Tool] {decision.ToolName}: FAIL ({elapsedMs}ms)");
                            Logger.Info("Orchestrator", $"Tool: {decision.ToolName} = {(result.Succeeded ? "SUCCESS" : "FAILURE")} ({elapsedMs}ms)");

                        if (result.Succeeded)
                                 {
                                _out?.WriteLine($"[Orchestrator] Output:\n{(result.Output != null ? EGuiBase.Truncate(result.Output, 2000) : "(no output)")}");

                                     // Log for LLM context
                                    var logEntry = $"Tool:{decision.ToolName} \u2192 OK\nOutput: {(result.Output != null ? EGuiBase.Truncate(result.Output, 1000) : "(no output)")}";
                                        _toolCallLog.Add(logEntry);

                                     // Add tool result to conversation history
                                            var toolOutput = result.Output!;

                                     // Track completed step
                                    var stepCmd = argsDict.GetValueOrDefault("command") ?? "";
                                    var stepDesc = $"{decision.ToolName}: {EGuiBase.Truncate(stepCmd, 80)}";
                                     _completedSteps.Add(stepDesc);

                                     // v10.17: Sub-task advancement based on execution plan.
                                     // If the plan says this call covers multiple steps, advance all of them.
                                     // If no plan or call not in plan, advance one (conservative default).
                                    if (_subTasks != null && _subTasks.Count > 1)
                                     {
                                        var plannedCall = _executionPlan?.Calls.FirstOrDefault(c => c.ToolName.Equals(decision.ToolName!, StringComparison.OrdinalIgnoreCase));
                                        if (plannedCall != null && plannedCall.CoversSubTasks.Count > 1)
                                         {
                                             // Advance all steps the plan says this call covers
                                            foreach (var stepIdx in plannedCall.CoversSubTasks)
                                             {
                                                if (_currentSubTask < _subTasks.Count)
                                                    AdvanceSubTask(true, decision.ToolName!, stepDesc);
                                             }
                                         }
                                        else
                                         {
                                             // No plan mapping — advance one conservatively
                                            AdvanceSubTask(true, decision.ToolName!, stepDesc);
                                         }
                                     }

                                     _engine.AddToolResult(decision.ToolName!, toolOutput);

                                     // v10.6: Inject step-aware directive with sub-task context
                                    var stepDirective = BuildStepDirective();
                                     _engine.InjectFormatRetry(stepDirective);

                                _out?.WriteDim($"[Orchestrator] Tool succeeded, looping back to LLM (turn {_turnCount + 1})...");
                                 }
                        else
                                 {
                                 // v10.6: Advance sub-task tracking on failure
                                AdvanceSubTask(false, decision.ToolName!, $"Tool failed: {result.Error}");

                                 // v10.15.1: Log the failure for streak detection
                                 _toolCallLog.Add($"Tool:{decision.ToolName} \u2192 FAIL: {result.Error}");

                                 // v10.15.1: Feed the error back to the LLM so it knows the tool failed
                                 _engine.AddToolResult(decision.ToolName!, $"[ERROR] Tool failed: {result.Error}");

                                if (IsFailureStreak(_maxFailuresBeforeStop))
                                          {
                                        _out?.WriteLine($"[Orchestrator] Too many failures ({_maxFailuresBeforeStop} in a row). Stopping.");
                                            return new OrchestratorResult
                                                     {
                                                    FinalOutput = $"Stopped after {_maxFailuresBeforeStop} consecutive failures on tool: {decision.ToolName}",
                                                    ToolCallsMade = _turnCount + 1,
                                                    Status = OrchestratorStatus.TurnsExhausted
                                                     };
                                          }

                                 // v10.15.1: Inject directive so LLM knows to retry or report
                                var failDirective = BuildStepDirective();
                                 _engine.InjectFormatRetry(failDirective);
                                 }
                          }
                     catch (Exception ex)
                             {
                            _out?.WriteLine($"[Orchestrator] Tool exception: {ex.Message}");
                                     _toolCallLog.Add($"Tool:{decision.ToolName} \u2192 EXCEPTION: {ex.Message}");
                            continue;
                             }

                                 // Always increment turn and loop back — let LLM decide next action
                                 _turnCount++;
                        continue;
                      }
            else if (decision.WantsDirectAnswer)
                       {
                _out?.WriteLine("[Orchestrator] LLM gave direct answer (<output>). Stopping.");
                    return new OrchestratorResult
                             {
                            FinalOutput = decision.AnswerText!,
                            ToolCallsMade = _turnCount + 1,
                            Status = OrchestratorStatus.GoalAchieved
                             };
                       }
            else
                       {
                  // v9.12: Format retry — model produced text without tags, retry with strong reminder
                 _formatRetries++;
                if (_formatRetries <= MaxFormatRetries)
                 {
                    Logger.Warn("Orchestrator", $"No tags (attempt {_formatRetries}/{MaxFormatRetries}). Removing bad response, retrying.");

                     // Remove the bad assistant response from history so model doesn't learn from it
                    await _engine.RemoveLastAssistantResponseAsync();

                     // Inject as a user-level message (not tool result) for stronger signal
                     _engine.InjectFormatRetry(
                         "Your last response was REJECTED — you did not use the required XML tags.\n" +
                         "You MUST respond using this EXACT format:\n" +
                         "<lm><thinking>brief reasoning</thinking><output>your answer</output></lm>\n" +
                         "Do NOT write any text outside the <lm> container. Do NOT skip the tags.\n" +
                         "Now answer the previous question using the correct format.");
                     _turnCount++;
                    continue;
                 }
                else
                 {
                    Logger.Error("Orchestrator", $"No tags after {MaxFormatRetries} retries. Stopping.");
                    return new OrchestratorResult
                             {
                            FinalOutput = $"Invalid response after {MaxFormatRetries} retries. The model did not use required tags.\nLast response:\n{llmResponse}",
                            ToolCallsMade = _turnCount + 1,
                            Status = OrchestratorStatus.TurnsExhausted
                             };
                 }
                       }
                  }

                    // Max turns reached
        _out?.WriteLine("[Orchestrator] Max turns reached. Stopping.");
        var summaryText = FormatTurnLog();
        if (string.IsNullOrEmpty(summaryText)) summaryText = "(No useful output in the last turn.)";
            return new OrchestratorResult
                     {
                    FinalOutput = $"Reached max turns ({_maxTurns}). Last response was empty or unhelpful.\n\n{summaryText}",
                    ToolCallsMade = _turnCount,
                    Status = OrchestratorStatus.TurnsExhausted
                     };
              }

      // ─── Content Cleaning ──────────────────


      // ─── Multi-Tool Parsing (v10.13) ────────────

     /// <summary>
     /// Parse the LLM's clean response — detect one or more <toolcall> blocks or an <output> block.
     ///
     /// The engine already strips noise via ExtractCleanResponse() and extracts ALL <toolcall> blocks.
     /// We parse them into a list of ToolCallRequest objects for the ParallelToolExecutor.
     ///
     /// Priority: if <toolcall> blocks exist, they take precedence over <output>.
     /// A response with both <toolcall> and <output> is treated as tool calls (output is ignored).
     /// </summary>
    private static LLMDecision ParseLLMDecision(string response)
                 {
            var trimmed = response.Trim();

              // v10.13: Find ALL <toolcall>...</toolcall> blocks
            var toolCalls = new List<ToolCallRequest>();
            var searchFrom = 0;
            while (searchFrom < trimmed.Length)
             {
                var tcStart = trimmed.IndexOf("<toolcall>", searchFrom, StringComparison.OrdinalIgnoreCase);
                if (tcStart < 0) break;
                var tcEnd = trimmed.IndexOf("</toolcall>", tcStart + 10, StringComparison.OrdinalIgnoreCase);
                string blockContent;
                if (tcEnd < 0)
                 {
                     // No close — take rest
                    blockContent = trimmed.Substring(tcStart + 10).Trim();
                    searchFrom = trimmed.Length;
                 }
                else
                 {
                    blockContent = trimmed.Substring(tcStart + 10, tcEnd - tcStart - 10).Trim();
                    searchFrom = tcEnd + 11;   // </toolcall> is 11 chars
                 }

                var tc = ParseToolCallBlock(blockContent, toolCalls.Count + 1);
                if (tc.ToolName != null)
                    toolCalls.Add(tc);
             }

            if (toolCalls.Count > 0)
             {
                 // Found one or more <toolcall> blocks
                return new LLMDecision(toolCalls);
             }

              // Check for <output>...</output> block
            var outputOpenIdx = trimmed.IndexOf("<output>", StringComparison.OrdinalIgnoreCase);
            int? outputCloseIdx = null;
            if (outputOpenIdx >= 0)
               {
                var closePos = trimmed.IndexOf("</output>", outputOpenIdx + "<output>".Length, StringComparison.OrdinalIgnoreCase);
                if (closePos > outputOpenIdx + "<output>".Length)
                    outputCloseIdx = closePos;
               }

            if (outputOpenIdx >= 0 && outputCloseIdx.HasValue)
                   {
                  // Found an <output> block — extract the answer text
                var answer = trimmed.Substring(
                    outputOpenIdx + "<output>".Length,
                    outputCloseIdx.Value - outputOpenIdx - "<output>".Length).Trim();
                return LLMDecision.DirectAnswer(answer);
                   }

              // Neither block found — invalid response
            return LLMDecision.Unknown();
                 }

/// <summary>Parses a single <toolcall> block content to extract tool name and arguments.</summary>
    private static ToolCallRequest ParseToolCallBlock(string toolcallContent, int index)
           {
              // Extract tool name: first word before any '<', space, or end of string
            var ltIdx = toolcallContent.IndexOf('<');
            var spIdx = toolcallContent.IndexOf(' ');
            if (spIdx >= 0 && (ltIdx < 0 || spIdx < ltIdx))
                ltIdx = spIdx;
            var nameLen = ltIdx >= 0 ? ltIdx : toolcallContent.Length;
            var toolName = toolcallContent.Substring(0, nameLen).Trim();

              // Extract all <argname>value</argname> tags from content
             // Use Singleline so . matches newlines (for multi-line content args)
            var args = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var argMatches = Regex.Matches(toolcallContent, @"<([a-zA-Z_][\w]*)>(.*?)</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline);

            Program.Gui.WriteLineColored($"[Parse] Toolcall #{index} content: {toolcallContent}");
            Program.Gui.WriteLineColored($"[Parse] Regex matches: {argMatches.Count}");
            foreach (Match m in argMatches)
                {
                var key = m.Groups[1].Value;
                var value = m.Groups[2].Value;
                Program.Gui.WriteLineColored($"[Parse] Arg: {key} = {EGuiBase.Truncate(value, 100)}");
                if (!string.IsNullOrEmpty(key)) args[key] = value;
                }

             // Fallback: if no args were found with closing tags, try the old regex
             // (captures up to next </ which handles cases where LLM omits closing tag name)
            if (args.Count == 0)
             {
                var fallbackMatches = Regex.Matches(toolcallContent, @"<([a-zA-Z_][\w]*)>(.*?)(?=</|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                foreach (Match m in fallbackMatches)
                 {
                    var key = m.Groups[1].Value;
                    var value = m.Groups[2].Value.Trim();
                    if (!string.IsNullOrEmpty(key) && !args.ContainsKey(key))
                        args[key] = value;
                 }
             }

            return new ToolCallRequest { ToolName = toolName, Args = args, Index = index };
           }

      // ─── Tool Execution ──────────────────────

      /// <summary>Check if recent tool calls have all failed.</summary>
    private bool IsFailureStreak(int threshold)
               {
        if (_toolCallLog.Count < threshold) return false;
        var lastN = _toolCallLog.TakeLast(threshold);
             // v10.13.1: Match FAIL, ERR, EXCEPTION, BATCH FAIL, and DENIED
            return lastN.All(log => log.Contains("ERR") || log.Contains("EXCEPTION") || log.Contains("FAIL") || log.Contains("DENIED"));
               }

      /// <summary>Execute a tool call by name with args dictionary.</summary>
    private async Task<EToolResult> ExecuteTool(string toolName, Dictionary<string, string?> args)
              {
        var tool = _engine.Tools.FirstOrDefault(t => t.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase));
        if (tool == null)
            throw new InvalidOperationException($"Unknown tool: {toolName}");

        Logger.Debug("Orchestrator", $"Executing: {tool.Name}");
         // v10.9.3: Pass execution cancellation token to tool
        return await tool.ExecuteAsync(args, _engine.ExecutionToken);
              }

      /// <summary>v10.18.1: Get tool call log for sub-agent partial results.</summary>
     public IReadOnlyList<string> GetToolCallLog() => _toolCallLog.AsReadOnly();

      /// <summary>Format tool call log for final summary output.</summary>
    private string FormatTurnLog()
               {
        if (_toolCallLog.Count == 0) return "";
        var sb = new StringBuilder();
            sb.AppendLine("--- Tool Call History ---");
        foreach (var logEntry in _toolCallLog)
            sb.AppendLine($"            - {logEntry}");
        sb.AppendLine("--- End ---");
            return sb.ToString();
               }

      /// <summary>Reset the orchestrator state.</summary>
    public void Reset()
               {
                   _turnCount = 0;
                   _toolCallLog.Clear();
                   _formatRetries = 0;
                   // v10.6: Reset sub-task state
                   _subTasks = null;
                   _currentSubTask = 0;
                   _executionPlan = null; // v10.17: Reset execution plan
               }

     // v10.6: Build step-aware directive that tells the LLM which sub-task to focus on.
     // This is injected after each tool result to guide the LLM through chained tasks.
    private string BuildStepDirective()
     {
        var sb = new StringBuilder();

        sb.AppendLine("The tool has returned its result above. Now respond to the user.");
        sb.AppendLine("Open <lm><thinking>brief reasoning</thinking> then either <output>your answer</output></lm> if done, or <lm><thinking>brief reasoning</thinking><toolcall>...</toolcall></lm> if you need more data.");
        sb.AppendLine("Do NOT write plain text. Use the tags.");
        sb.AppendLine("IMPORTANT: Check [TASK PROGRESS] below. If you completed multiple steps in a single tool call (e.g. batch shell command), the progress tracker may only show one as completed. Check the tool output above — if you covered all remaining steps, use <output> to finish. If steps genuinely remain, use <toolcall>.");
        sb.AppendLine("Do NOT retry steps that already succeeded — check the tool output above to see what was already done.");

         // v10.6: If we have sub-tasks, inject step context
        if (_subTasks != null && _subTasks.Count > 1)
         {
            sb.AppendLine();
            sb.AppendLine($"[TASK PROGRESS] You are on step {_currentSubTask + 1} of {_subTasks.Count}:");

            for (int i = 0; i < _subTasks.Count; i++)
             {
                var status = _subTasks[i].Status switch
                 {
                    SubTaskStatus.Completed => "[OK]",
                    SubTaskStatus.Failed => "[FAIL]",
                    SubTaskStatus.InProgress => "[...]",
                     _ => "[ ]"
                 };
                var marker = i == _currentSubTask ? " >> " : "     ";
                 // v10.7.4: Escape angle brackets in step descriptions to prevent fake XML tags
            var safeDesc = _subTasks[i].Description.Replace("<", "&lt;").Replace(">", "&gt;");
            sb.AppendLine($"{marker}{status} {safeDesc}");
             }

             // Give explicit instruction for the current step
            if (_currentSubTask < _subTasks.Count)
             {
                var current = _subTasks[_currentSubTask];
                if (current.Status == SubTaskStatus.Pending || current.Status == SubTaskStatus.InProgress)
                 {
                    sb.AppendLine();
                    var safeCurrent = _subTasks[_currentSubTask].Description.Replace("<", "&lt;").Replace(">", "&gt;");
                    sb.AppendLine($"> CURRENT STEP: {safeCurrent}");
                    sb.AppendLine("Focus on completing THIS step. If the previous tool result gives you what you need, proceed to this step.");
                 }
             }

             // Check if all steps are done
            var allDone = _subTasks.All(s => s.Status == SubTaskStatus.Completed || s.Status == SubTaskStatus.Failed);
            var anyFailed = _subTasks.Any(s => s.Status == SubTaskStatus.Failed);
            if (allDone)
             {
                sb.AppendLine();
                if (anyFailed)
                 {
                    sb.AppendLine("All steps have been attempted. Some FAILED. Check the tool results above.");
                    sb.AppendLine("If you can fix the failed steps, use <toolcall>. If not, use <output> to report what happened.");
                 }
                else
                 {
                    sb.AppendLine("All steps are complete! Give your final <output> summarizing what was done.");
                 }
             }
         }

        return sb.ToString();
     }

     // v10.6: Advance sub-task tracking based on tool result
    // v10.22: Post-hoc effect matching — check actual tool effects against remaining sub-tasks
    private void AdvanceSubTask(bool success, string toolName, string description)
     {
        if (_subTasks == null || _subTasks.Count <= 1) return;
        if (_currentSubTask >= _subTasks.Count) return;

        var current = _subTasks[_currentSubTask];
        if (success)
         {
            current.Status = SubTaskStatus.Completed;
            current.CompletedAt = DateTime.UtcNow;
            _out?.WriteSuccess($"Step {_currentSubTask + 1}/{_subTasks.Count} completed: {current.Description}");
             _currentSubTask++;

             // v10.22: Post-hoc effect matching — check if subsequent sub-tasks were also completed
             // by this single tool call (e.g., one shell command created 3 files covering 3 steps)
             MatchEffectsToSubTasks(toolName, description);

             // Mark next sub-task as in-progress
            if (_currentSubTask < _subTasks.Count)
             {
                 _subTasks[_currentSubTask].Status = SubTaskStatus.InProgress;
                _out?.WriteInfo($"-> Next step: {_subTasks[_currentSubTask].Description}");
             }
         }
        else
         {
            current.Status = SubTaskStatus.Failed;
            current.FailureReason = $"Tool {toolName} failed";
            _out?.WriteError($"Step {_currentSubTask + 1}/{_subTasks.Count} failed: {current.Description}");
             _currentSubTask++;

            if (_currentSubTask < _subTasks.Count)
             {
                 _subTasks[_currentSubTask].Status = SubTaskStatus.InProgress;
                _out?.WriteWarning($"-> Skipping to next step: {_subTasks[_currentSubTask].Description}");
             }
         }
     }

    /// <summary>
    /// v10.22: Post-hoc effect matching — check if a completed tool call's actual effects
    /// also satisfy subsequent pending sub-tasks. For example, if one shell command creates
    /// 3 files and the plan had 3 steps for creating each file, this detects that all 3 are done.
    /// 
    /// Uses keyword matching between the tool description/output and sub-task descriptions.
    /// Conservative: only advances if there's a clear match (keyword overlap > 60%).
    /// </summary>
    private void MatchEffectsToSubTasks(string toolName, string toolDescription)
    {
        if (_subTasks == null || _currentSubTask >= _subTasks.Count) return;

        var descWords = toolDescription.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2)
            .ToHashSet();

        // Also get the last tool output from context for richer matching
        var windowMsgs = _engine.ContextWindow.GetWindowMessages();
        var lastTool = windowMsgs.LastOrDefault(m => m.Role == "tool_output");
        if (lastTool != null)
        {
            var outputWords = lastTool.Content.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 2);
            foreach (var w in outputWords) descWords.Add(w);
        }

        int matched = 0;
        while (_currentSubTask < _subTasks.Count)
        {
            var task = _subTasks[_currentSubTask];
            if (task.Status != SubTaskStatus.Pending && task.Status != SubTaskStatus.InProgress) break;

            var taskWords = task.Description.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 2)
                .ToList();
            if (taskWords.Count == 0) break;

            var overlap = taskWords.Count(w => descWords.Contains(w));
            var matchRatio = (double)overlap / taskWords.Count;

            if (matchRatio >= 0.6)
            {
                task.Status = SubTaskStatus.Completed;
                task.CompletedAt = DateTime.UtcNow;
                _out?.WriteSuccess($"Step {_currentSubTask + 1}/{_subTasks.Count} auto-detected as done: {task.Description} (effect match {matchRatio:P0})");
                _currentSubTask++;
                matched++;
            }
            else break;
        }

        if (matched > 0)
            _out?.WriteInfo($"Post-hoc matching: {matched} additional sub-task(s) completed by effect overlap.");
    }

    public async ValueTask DisposeAsync()
     {
        try { _subAgentManager?.Dispose(); } catch { }
        await Task.CompletedTask;
     }
}

// ─── Decision Result (v10.13) ─────────────────────────

/// <summary>LLM's structured decision about what to do next.
/// v10.13: Supports multiple tool calls for parallel execution.</summary>
public class LLMDecision
{
           // Tool call fields (v10.13: multiple calls)
    public bool WantsToolCall { get; }
    public List<ToolCallRequest> ToolCalls { get; }

           // Legacy single-call accessors (backwards compat)
    public string? ToolName => ToolCalls.FirstOrDefault()?.ToolName;
    public Dictionary<string, string?> Args => ToolCalls.FirstOrDefault()?.Args ?? new Dictionary<string, string?>();

           // Direct answer field
    public bool WantsDirectAnswer { get; }
    public string? AnswerText { get; }

     /// <summary>Single tool call (backwards compat).</summary>
    public LLMDecision(
        bool wantsToolCall,
        string? toolName,
        Dictionary<string, string?> args,
        string? answerText = null)
               {
        WantsToolCall = wantsToolCall;
        ToolCalls = wantsToolCall && toolName != null
             ? new List<ToolCallRequest> { new() { ToolName = toolName, Args = args ?? new Dictionary<string, string?>(), Index = 1 } }
             : new List<ToolCallRequest>();
        WantsDirectAnswer = answerText != null;
            AnswerText = answerText;
               }

     /// <summary>Multiple tool calls (v10.13).</summary>
    public LLMDecision(List<ToolCallRequest> toolCalls)
     {
        WantsToolCall = toolCalls.Count > 0;
        ToolCalls = toolCalls;
        WantsDirectAnswer = false;
        AnswerText = null;
     }

     /// <summary>Number of tool calls in this decision.</summary>
    public int ToolCallCount => ToolCalls.Count;

     /// <summary>Is this a multi-call (parallel) decision?</summary>
    public bool IsMultiCall => ToolCalls.Count > 1;

    public static LLMDecision ToolCall(string name, Dictionary<string, string?> dict)
                 => new(true, name, dict);

    public static LLMDecision DirectAnswer(string answer)
                 => new(false, null, new Dictionary<string, string?>(), answer);

    public static LLMDecision Unknown()
                 => new(false, null, new Dictionary<string, string?>(), null);
}

/// <summary>Result from the orchestrator after execution completes.</summary>
public class OrchestratorResult
{
    public string FinalOutput { get; set; } = "";
    public int ToolCallsMade { get; set; }
    public OrchestratorStatus Status { get; set; }
}

/// <summary>Status code for orchestrator completion.</summary>
public enum OrchestratorStatus
{
    GoalAchieved,
    TurnsExhausted
}
