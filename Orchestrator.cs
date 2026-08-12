using static ECAssistant.EColor;

using System.Text;
using System.Text.RegularExpressions;
using ECAssistant.Engine;
using ECAssistant.Tools;
using ECAssistant.Services;

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
    private int _turnCount = 0;
    private readonly List<string> _toolCallLog = new();
    private string? _originalGoal = null;
    private readonly List<string> _completedSteps = new();
    private int _formatRetries = 0;
    private const int MaxFormatRetries = 2;

    // v10.6: TaskPlanner for chained multi-step tasks
    private List<SubTask>? _subTasks = null;
    private int _currentSubTask = 0;

     // ─── Hard Limits ──────────────────────
    private int _maxTurns;  // v10.6: changed from readonly to allow dynamic adjustment
    private readonly int _maxFailuresBeforeStop;

     // ─── Whitelist of valid tool names ─────
    private readonly HashSet<string> _toolWhitelist = new(StringComparer.OrdinalIgnoreCase);

     // ─── Tool Policy (permissions + approval) ─────
    private readonly ToolPolicy _toolPolicy;

     /// <summary>Create orchestrator with config.</summary>
    public AgentOrchestrator(
        EAgentEngine engine,
        int maxTurns = 5,
        int maxFailures = 3,
        ToolPolicy? toolPolicy = null)
             {
                 _engine = engine;
                 _maxTurns = Math.Max(1, maxTurns);
                 _maxFailuresBeforeStop = maxFailures;
                 _toolPolicy = toolPolicy ?? new ToolPolicy();

            foreach (var tool in _engine.Tools)
                  _toolWhitelist.Add(tool.Name);
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
                EColor.TagBold(EColor.Info(), "Decompose", "Using secondary model for task decomposition...");
                var steps = await secondary.DecomposeTaskAsync(goal);
                if (steps != null && steps.Count > 0)
                {
                    decomposed = steps.Select(s => new SubTask { Description = s, Status = SubTaskStatus.Pending }).ToList();
                    EColor.TagBold(EColor.Success(), "Decompose", $"Secondary model produced {decomposed.Count} steps.");
                }
                else
                {
                    EColor.TagBold(EColor.Warn(), "Decompose", "Secondary model failed — falling back to keywords.");
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
                EColor.TagBold(EColor.Info(), "TaskPlanner", $"Decomposed into {_subTasks.Count} steps — max turns adjusted to {_maxTurns}");
                for (int i = 0; i < _subTasks.Count; i++)
                    EColor.WriteLine(EColor.Dim, $"  Step {i+1}: {_subTasks[i].Description}");
                EColor.WriteLine(EColor.Reset, "");
            }
        }
        
        Program.Gui.WriteLineColored($"[Orchestrator] Starting for: {goal}");
        Program.Gui.WriteLineColored($"[Orchestrator] Max turns: {_maxTurns}, Failures limit: {_maxFailuresBeforeStop}\n");

            while (_turnCount < _maxTurns)
                  {
                // v10.9: Check for user cancellation before each turn
                if (_engine.ExecutionToken.IsCancellationRequested)
                {
                    EColor.TagBold(EColor.Warn(), "Orchestrator", "Execution cancelled by user. Stopping.");
                    var cancelSummary = FormatTurnLog();
                    return new OrchestratorResult
                    {
                        FinalOutput = $"Execution cancelled by user.\n\n{cancelSummary}",
                        ToolCallsMade = _turnCount,
                        Status = OrchestratorStatus.GoalAchieved  // not an error — user chose to stop
                    };
                }
            Logger.Info("Orchestrator", $"Turn {_turnCount + 1}/{_maxTurns}");

                 // Step 1: Ask the LLM to decide what to do (with full context of tools + history)
              var llmResponse = await _engine.GenerateAsync(goal);

             // v10.11.1: Check if generation was stopped by user (ESC) — bail out immediately,
             // don't attempt format retries on the "(Stopped by user)" string.
             if (llmResponse == "(Stopped by user)" || _engine.IsExecutionStopped)
            {
                EColor.TagBold(EColor.Warn(), "Orchestrator", "Generation was stopped by user (ESC). Not retrying.");
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
                    Status = OrchestratorStatus.GoalAchieved  // not an error — user chose to stop
                };
            }

             // v9.19: Log what the engine returned BEFORE trimming
              EColor.WriteLine(EColor.Dim, $"[Orchestrator] Before trim ({llmResponse.Length} chars): {llmResponse.Substring(0, Math.Min(llmResponse.Length, 200))}");

             // Trim pass: cut at first </toolcall> or </output> boundary, keep tags included
              llmResponse = TrimToFirstClosingTag(llmResponse);
              EColor.WriteLine(EColor.Dim, $"[Orchestrator] After trim ({llmResponse.Length} chars): {llmResponse.Substring(0, Math.Min(llmResponse.Length, 200))}");

                 // Step 2: Parse the clean LLM output — detect which block type was returned
              var decision = ParseLLMDecision(llmResponse);
              EColor.WriteLine(EColor.Dim, $"[Orchestrator] Parse result: WantsToolCall={decision.WantsToolCall}, WantsDirectAnswer={decision.WantsDirectAnswer}, ToolName={decision.ToolName}");

              if (decision.WantsToolCall)
                       {
                _formatRetries = 0; // reset on valid tool call
                    Logger.Info("Orchestrator", $"Tool call: {decision.ToolName}");
                
                var argsDict = decision.Args;

                // ── Tool Policy Check ──
                var policyDecision = _toolPolicy.Check(decision.ToolName!, argsDict);
                if (policyDecision.NeedsApproval)
                {
                    Program.Gui.WriteLineColored($"[Policy] {policyDecision.Message}");
                    // In console mode, ask the user directly
                    Program.Gui.WriteLineColored($"[Policy] Approve execution of {decision.ToolName} with args: {string.Join(", ", argsDict.Select(kvp => kvp.Key + "=" + (kvp.Value ?? "(null)")))}?");
                    var approval = Program.Gui.PromptRaw("[y/N] ")?.Trim().ToLower();
                    if (approval != "y" && approval != "yes")
                    {
                        Program.Gui.WriteLineColored($"[Policy] Tool execution DENIED by user: {decision.ToolName}");
                        _engine.AddToolResult(decision.ToolName!, "[DENIED] User did not approve this tool execution.");
                        _turnCount++;
                        continue;
                    }
                    Program.Gui.WriteLineColored($"[Policy] Approved by user.");
                }
                else if (!policyDecision.CanExecute)
                {
                    Program.Gui.WriteLineColored($"[Policy] {policyDecision.Message}");
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
                            EColor.TagBold(EColor.Warn(), "Orchestrator", "Execution cancelled before tool call.");
                            return new OrchestratorResult
                            {
                                FinalOutput = "Execution cancelled by user.",
                                ToolCallsMade = _turnCount,
                                Status = OrchestratorStatus.GoalAchieved
                            };
                        }
                        var result = await ExecuteTool(decision.ToolName, argsDict);
                        var elapsedMs = (long)((DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond) - startMs);
                        
                            EColor.Tag(result.Succeeded ? EColor.Success() : EColor.Error(), "Tool", $"{decision.ToolName}: {(result.Succeeded ? "OK" : "FAIL")} ({elapsedMs}ms)");
                            Logger.Info("Orchestrator", $"Tool: {decision.ToolName} = {(result.Succeeded ? "SUCCESS" : "FAILURE")} ({elapsedMs}ms)");

                        if (result.Succeeded)
                                {
                                Program.Gui.WriteLineColored($"[Orchestrator] Output:\n{result.Output?.Substring(0, Math.Min(result.Output.Length, 500))}");

                                    // Log for LLM context
                                    var logEntry = $"Tool:{decision.ToolName} \u2192 OK\nOutput: {(result.Output != null ? result.Output.Substring(0, Math.Min(result.Output.Length, 300)) : "(no output)")}";
                                       _toolCallLog.Add(logEntry);

                                    // Add tool result to conversation history
                                            var toolOutput = result.Output!;
                                            
                                    // Track completed step
                                    var stepCmd = argsDict.GetValueOrDefault("command") ?? "";
                                    var stepDesc = $"{decision.ToolName}: {stepCmd.Substring(0, Math.Min(stepCmd.Length, 80))}";
                                    _completedSteps.Add(stepDesc);
                                    
                                    // v10.6: Advance sub-task tracking on success
                                    AdvanceSubTask(true, decision.ToolName!, stepDesc);
                                    
                                    _engine.AddToolResult(decision.ToolName!, toolOutput);
                                    
                                    // v10.6: Inject step-aware directive with sub-task context
                                    var stepDirective = BuildStepDirective();
                                    _engine.InjectFormatRetry(stepDirective);

                                EColor.WriteLine(EColor.Dim, $"[Orchestrator] Tool succeeded, looping back to LLM (turn {_turnCount + 1})...");
                                }
                        else
                                {
                                // v10.6: Advance sub-task tracking on failure
                                AdvanceSubTask(false, decision.ToolName!, $"Tool failed: {result.Error}");

                                if (IsFailureStreak(_maxFailuresBeforeStop))
                                         {
                                        Program.Gui.WriteLineColored($"[Orchestrator] Too many failures ({_maxFailuresBeforeStop} in a row). Stopping.\n");
                                            return new OrchestratorResult 
                                                    { 
                                                    FinalOutput = $"Stopped after {_maxFailuresBeforeStop} consecutive failures on tool: {decision.ToolName}",
                                                    ToolCallsMade = _turnCount + 1,
                                                    Status = OrchestratorStatus.TurnsExhausted 
                                                    };
                                         }
                                }
                         }
                     catch (Exception ex)
                            {
                            Program.Gui.WriteLineColored($"[Orchestrator] Tool exception: {ex.Message}");
                                    _toolCallLog.Add($"Tool:{decision.ToolName} \u2192 EXCEPTION: {ex.Message}");
                            continue;
                            }

                                // Always increment turn and loop back — let LLM decide next action
                                _turnCount++;
                        continue;
                     }
            else if (decision.WantsDirectAnswer)
                      {
                Program.Gui.WriteLineColored("[Orchestrator] LLM gave direct answer (<output>). Stopping.\n");
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
                    _engine.RemoveLastAssistantResponse();
                    
                    // Inject as a user-level message (not tool result) for stronger signal
                    _engine.InjectFormatRetry(
                        "Your last response was REJECTED — you did not use the required XML tags.\n" +
                        "You MUST respond using this EXACT format:\n" +
                        "<llm><thinking>brief reasoning</thinking><output>your answer</output></llm>\n" +
                        "Do NOT write any text outside the <llm> container. Do NOT skip the tags.\n" +
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
        Program.Gui.WriteLineColored("[Orchestrator] Max turns reached. Stopping.\n");
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

     /// <summary>
     /// Trim the raw LLM response at the first closing tag boundary.
     /// 
     /// Cuts everything AFTER the first </toolcall> or </output> (whichever comes first).
     /// The closing tag itself IS kept — truncation happens after it closes.
     /// This removes trailing noise/hallucination while preserving complete structured blocks.
     /// 
     /// No changes to EAgentEngine or ExtractCleanResponse — this is orchestration-only.
     /// </summary>
    private static string TrimToFirstClosingTag(string rawResponse)
             {
        if (string.IsNullOrEmpty(rawResponse)) return rawResponse;

        var toolcallCloseIdx = rawResponse.IndexOf("</toolcall>", StringComparison.OrdinalIgnoreCase);
        var outputCloseIdx = rawResponse.IndexOf("</output>", StringComparison.OrdinalIgnoreCase);
        // v10.12: Also check for </llm> container close
        var llmCloseIdx = rawResponse.IndexOf("</llm>", StringComparison.OrdinalIgnoreCase);

         // Find whichever comes first (ignore negative/unused indices)
        int cutAt = -1;
        string cutTag = "";
        
        // Find the earliest of all three closing tags
        if (llmCloseIdx >= 0) { cutAt = llmCloseIdx; cutTag = "</llm>"; }
        if (toolcallCloseIdx >= 0 && (cutAt < 0 || toolcallCloseIdx < cutAt)) { cutAt = toolcallCloseIdx; cutTag = "</toolcall>"; }
        if (outputCloseIdx >= 0 && (cutAt < 0 || outputCloseIdx < cutAt)) { cutAt = outputCloseIdx; cutTag = "</output>"; }

         // No closing tag found — return as-is (nothing to trim)
        if (cutAt < 0) return rawResponse;

         // Cut AFTER the closing tag: include the full </tag> text, drop everything after
        var tagNameLen = cutTag.Length;
        var trimmed = rawResponse.Substring(0, cutAt + tagNameLen);

         // Trim trailing whitespace from the cut point
        return trimmed.TrimEnd();
             }

     // ─── Clean Response Parsing (v2.2) ────────────
    
     /// <summary>
     /// Parse the LLM's clean response — detect exactly one structured block.
     /// 
     /// The engine already strips noise via ExtractCleanResponse().
     /// We only need to check: does it contain <toolcall>, <output>, or neither?
     /// 
     /// Priority order: <toolcall> takes precedence (LLM may output both).
     /// </summary>
    private static LLMDecision ParseLLMDecision(string response)
                {
            var trimmed = response.Trim();

             // Check for <toolcall>...</toolcall> block first (takes priority over <output>)
            var toolcallOpenIdx = trimmed.IndexOf("<toolcall>", StringComparison.OrdinalIgnoreCase);
            int? toolcallCloseIdx = null;
            if (toolcallOpenIdx >= 0)
              {
                var closePos = trimmed.IndexOf("</toolcall>", toolcallOpenIdx + "<toolcall>".Length, StringComparison.OrdinalIgnoreCase);
                if (closePos > toolcallOpenIdx + "<toolcall>".Length)
                    toolcallCloseIdx = closePos;
              }

            if (toolcallOpenIdx >= 0 && toolcallCloseIdx.HasValue)
                  {
                 // Found a <toolcall> block — extract its content and parse
                var blockContent = trimmed.Substring(
                    toolcallOpenIdx + "<toolcall>".Length, 
                    toolcallCloseIdx.Value - toolcallOpenIdx - "<toolcall>".Length).Trim();
                return ParseToolCallBlock(blockContent);
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
                return new LLMDecision(false, null, new Dictionary<string, string?>(), answer);
                  }

             // Neither block found — invalid response
            return LLMDecision.Unknown();
                }

/// <summary>Parses a single <toolcall> block content to extract tool name and arguments.</summary>
    private static LLMDecision ParseToolCallBlock(string toolcallContent)
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

            Program.Gui.WriteLineColored($"[Parse] Toolcall content: {toolcallContent}");
            Program.Gui.WriteLineColored($"[Parse] Regex matches: {argMatches.Count}");
            foreach (Match m in argMatches)
               {
                var key = m.Groups[1].Value;
                var value = m.Groups[2].Value;
                Program.Gui.WriteLineColored($"[Parse] Arg: {key} = {value.Substring(0, Math.Min(value.Length, 100))}");
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

            return new LLMDecision(true, toolName, args);
          }

     // ─── Tool Execution ──────────────────────

     /// <summary>Check if recent tool calls have all failed.</summary>
    private bool IsFailureStreak(int threshold)
              {
        if (_toolCallLog.Count < threshold) return false;
        var lastN = _toolCallLog.TakeLast(threshold);
            return lastN.All(log => log.Contains("ERR") || log.Contains("EXCEPTION"));
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

     /// <summary>Format tool call log for final summary output.</summary>
    private string FormatTurnLog()
              {
        if (_toolCallLog.Count == 0) return "";
        var sb = new StringBuilder();
            sb.AppendLine("--- Tool Call History ---");
        foreach (var logEntry in _toolCallLog)
            sb.AppendLine($"           - {logEntry}");
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
              }

    // v10.6: Build step-aware directive that tells the LLM which sub-task to focus on.
    // This is injected after each tool result to guide the LLM through chained tasks.
    private string BuildStepDirective()
    {
        var sb = new StringBuilder();
        
        sb.AppendLine("The tool has returned its result above. Now respond to the user.");
        sb.AppendLine("Open <llm><thinking>brief reasoning</thinking> then either <output>your answer</output></llm> if done, or <llm><thinking>brief reasoning</thinking><toolcall>...</toolcall></llm> if you need more data.");
        sb.AppendLine("Do NOT write plain text. Use the tags.");
        
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
                var marker = i == _currentSubTask ? " >> " : "    ";
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
            if (allDone)
            {
                sb.AppendLine();
                sb.AppendLine("All steps are complete! Give your final <output> summarizing what was done.");
            }
        }
        
        return sb.ToString();
    }

    // v10.6: Advance sub-task tracking based on tool result
    private void AdvanceSubTask(bool success, string toolName, string description)
    {
        if (_subTasks == null || _subTasks.Count <= 1) return;
        if (_currentSubTask >= _subTasks.Count) return;
        
        var current = _subTasks[_currentSubTask];
        if (success)
        {
            current.Status = SubTaskStatus.Completed;
            current.CompletedAt = DateTime.UtcNow;
            EColor.TagBold(EColor.Success(), "TaskPlanner", $"Step {_currentSubTask + 1}/{_subTasks.Count} completed: {current.Description}");
            _currentSubTask++;
            
            // Mark next sub-task as in-progress
            if (_currentSubTask < _subTasks.Count)
            {
                _subTasks[_currentSubTask].Status = SubTaskStatus.InProgress;
                EColor.TagBold(EColor.Info(), "TaskPlanner", $"-> Next step: {_subTasks[_currentSubTask].Description}");
            }
        }
        else
        {
            current.Status = SubTaskStatus.Failed;
            current.FailureReason = $"Tool {toolName} failed";
            EColor.TagBold(EColor.Error(), "TaskPlanner", $"Step {_currentSubTask + 1}/{_subTasks.Count} failed: {current.Description}");
            _currentSubTask++;
            
            if (_currentSubTask < _subTasks.Count)
            {
                _subTasks[_currentSubTask].Status = SubTaskStatus.InProgress;
                EColor.TagBold(EColor.Warn(), "TaskPlanner", $"-> Skipping to next step: {_subTasks[_currentSubTask].Description}");
            }
        }
    }

    public async ValueTask DisposeAsync() => await Task.CompletedTask;
}

// ─── Decision Result (v2.2) ──────────────────────

/// <summary>LLM's structured decision about what to do next.</summary>
public class LLMDecision
{
          // Tool call fields
    public bool WantsToolCall { get; }
    public string? ToolName { get; }
    public Dictionary<string, string?> Args { get; }

          // Direct answer field
    public bool WantsDirectAnswer { get; }
    public string? AnswerText { get; }

    public LLMDecision(
        bool wantsToolCall, 
        string? toolName, 
        Dictionary<string, string?> args, 
        string? answerText = null)
              {
        WantsToolCall = wantsToolCall;
        ToolName = toolName;
        Args = args ?? new Dictionary<string, string>();
        WantsDirectAnswer = answerText != null;
            AnswerText = answerText;
              }

    public static LLMDecision ToolCall(string name, Dictionary<string, string?> dict)
                => new(true, name, dict);

    public static LLMDecision DirectAnswer(string answer)
                => new(false, null, new Dictionary<string, string>(), answer);

    public static LLMDecision Unknown()
                => new(false, null, new Dictionary<string, string>(), null);
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
