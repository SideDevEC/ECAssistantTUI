using System.Diagnostics;
using System.Text;
using ECAssistant.Tools;
using ECAssistant.Session;

namespace ECAssistant.Engine;

/// <summary>
/// Executes dependency-ordered tool call groups in parallel.
///
/// - Independent toolcalls within a group run via Task.WhenAll()
/// - Groups execute sequentially (group N waits for group N-1)
/// - All results are combined into a single output block
/// - Failures in one tool don't block other parallel tools
/// - Per-tool policy checks and approval gates
/// </summary>
public class ParallelToolExecutor
{
    private readonly EAgentEngine _engine;
    private readonly ToolPolicy _toolPolicy;
    private readonly Func<string, Dictionary<string, string?>, Task<EToolResult>> _executeToolFn;
    private readonly Action<string> _log;
    private readonly ISessionOutput? _out;

    /// <summary>
    /// Create the parallel executor.
    /// </summary>
    /// <param name="engine">Engine for tool lookup</param>
    /// <param name="toolPolicy">Policy checker for approvals</param>
    /// <param name="executeToolFn">Function that executes a single tool by name+args</param>
    /// <param name="log">Optional logging callback</param>
    /// <param name="sessionOutput">Optional session output for approval requests</param>
    public ParallelToolExecutor(
        EAgentEngine engine,
        ToolPolicy toolPolicy,
        Func<string, Dictionary<string, string?>, Task<EToolResult>> executeToolFn,
        Action<string>? log = null,
        ISessionOutput? sessionOutput = null)
    {
        _engine = engine;
        _toolPolicy = toolPolicy;
        _executeToolFn = executeToolFn;
        _log = log ?? (_ => { });
        _out = sessionOutput;
    }

    /// <summary>
    /// Execute a batch of tool calls with dependency-aware parallelism.
    /// Returns a combined BatchToolResult with all individual results.
    /// </summary>
    public async Task<BatchToolResult> ExecuteAsync(List<ToolCallRequest> toolCalls, CancellationToken ct = default)
    {
        // Analyze dependencies
        var analyzer = new ToolDependencyAnalyzer(); var groups = analyzer.Analyze(toolCalls);
        var allResults = new List<SingleToolResult>();

        if (groups.Count == 1 && groups[0].ToolCalls.Count == 1)
        {
            // Single tool — no parallelism overhead
            _log($"[Parallel] Single tool call — executing directly: {groups[0].ToolCalls[0]}");
            var tc = groups[0].ToolCalls[0];
            var result = await ExecuteSingleWithPolicy(tc, ct);
            allResults.Add(result);
        }
        else
        {
            // Multiple groups or parallel group
            _log($"[Parallel] Analyzed {toolCalls.Count} tool calls → {groups.Count} dependency group(s):");
            foreach (var g in groups)
                _log($"  {g}");

            for (int gi = 0; gi < groups.Count; gi++)
            {
                var group = groups[gi];

                if (ct.IsCancellationRequested)
                {
                    _log($"[Parallel] Cancelled before group {gi}.");
                    break;
                }

                if (group.IsParallel)
                {
                    // v10.13.1: Pre-check approvals sequentially before launching parallel tasks
                    // to avoid concurrent PromptRaw calls racing on the same console.
                    var approved = new List<ToolCallRequest>();
                    var denied = new List<ToolCallRequest>();
                    foreach (var tc in group.ToolCalls)
                    {
                        if (string.IsNullOrEmpty(tc.ToolName))
                        {
                            denied.Add(tc);
                            continue;
                        }
                        var policy = _toolPolicy.Check(tc.ToolName!, tc.Args);
                        if (policy.NeedsApproval)
                        {
                            _log($"[Policy] {tc}: {policy.Message}");
                            var isApproved = _out?.RequestApproval($"[Policy] Approve {tc.ToolName}#{tc.Index} ({string.Join(", ", tc.Args.Select(kvp => kvp.Key + "=" + StringUtil.Truncate(kvp.Value ?? "", 60)))})?") ?? false;
                            if (isApproved)
                            {
                                _log($"[Policy] Approved: {tc}");
                                approved.Add(tc);
                            }
                            else
                            {
                                _log($"[Policy] DENIED by user: {tc}");
                                denied.Add(tc);
                            }
                        }
                        else if (!policy.CanExecute)
                        {
                            _log($"[Policy] BLOCKED: {tc}: {policy.Message}");
                            denied.Add(tc);
                        }
                        else
                        {
                            approved.Add(tc);
                        }
                    }

                    // Add denied/blocked results immediately
                    foreach (var tc in denied)
                    {
                        allResults.Add(new SingleToolResult
                        {
                            ToolCall = tc,
                            Succeeded = false,
                            Output = "",
                            Error = "[DENIED] User did not approve this tool execution.",
                            ElapsedMs = 0
                        });
                    }

                    // Execute approved tasks in parallel
                    if (approved.Count > 0)
                    {
                        if (approved.Count == 1)
                        {
                            _log($"[Parallel] Group {gi}: 1 call approved, executing...");
                            allResults.Add(await ExecuteSingleNoPolicy(approved[0], ct));
                        }
                        else
                        {
                            _log($"[Parallel] Group {gi}: executing {approved.Count} calls in parallel...");
                            var tasks = approved.Select(tc => ExecuteSingleNoPolicy(tc, ct)).ToArray();
                            var results = await Task.WhenAll(tasks);
                            allResults.AddRange(results);
                        }
                    }
                }
                else
                {
                    _log($"[Parallel] Group {gi}: executing 1 call...");
                    var result = await ExecuteSingleWithPolicy(group.ToolCalls[0], ct);
                    allResults.Add(result);
                }
            }
        }

        return new BatchToolResult { Results = allResults, Groups = groups };
    }

    /// <summary>
    /// Execute a single tool call WITHOUT policy check (already pre-approved).
    /// Used inside Task.WhenAll for parallel execution after pre-approval.
    /// </summary>
    private async Task<SingleToolResult> ExecuteSingleNoPolicy(ToolCallRequest tc, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            if (string.IsNullOrEmpty(tc.ToolName))
            {
                return new SingleToolResult
                {
                    ToolCall = tc,
                    Succeeded = false,
                    Output = "",
                    Error = "Tool name was empty",
                    ElapsedMs = sw.ElapsedMilliseconds
                };
            }

            if (ct.IsCancellationRequested)
            {
                return new SingleToolResult
                {
                    ToolCall = tc,
                    Succeeded = false,
                    Output = "",
                    Error = "Cancelled before execution",
                    ElapsedMs = sw.ElapsedMilliseconds
                };
            }

            var result = await _executeToolFn(tc.ToolName!, tc.Args);
            sw.Stop();

            return new SingleToolResult
            {
                ToolCall = tc,
                Succeeded = result.Succeeded,
                Output = result.Succeeded ? result.Output : "",
                Error = result.Succeeded ? "" : result.Error,
                ElapsedMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log($"[Parallel] Exception in {tc}: {ex.Message}");
            return new SingleToolResult
            {
                ToolCall = tc,
                Succeeded = false,
                Output = "",
                Error = ex.Message,
                ElapsedMs = sw.ElapsedMilliseconds
            };
        }
    }

    /// <summary>
    /// Execute a single tool call with policy check and approval gate.
    /// Used for single-tool path and sequential groups.
    /// Returns a SingleToolResult with success/failure info.
    /// </summary>
    private async Task<SingleToolResult> ExecuteSingleWithPolicy(ToolCallRequest tc, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            if (string.IsNullOrEmpty(tc.ToolName))
            {
                return new SingleToolResult
                {
                    ToolCall = tc,
                    Succeeded = false,
                    Output = "",
                    Error = "Tool name was empty",
                    ElapsedMs = sw.ElapsedMilliseconds
                };
            }

            // Policy check
            var policyDecision = _toolPolicy.Check(tc.ToolName!, tc.Args);
            if (policyDecision.NeedsApproval)
            {
                _log($"[Policy] {tc}: {policyDecision.Message}");
                var isApproved = _out?.RequestApproval($"[Policy] Approve {tc.ToolName}#{tc.Index} ({string.Join(", ", tc.Args.Select(kvp => kvp.Key + "=" + StringUtil.Truncate(kvp.Value ?? "", 60)))})?") ?? false;

                if (!isApproved)
                {
                    _log($"[Policy] DENIED by user: {tc}");
                    return new SingleToolResult
                    {
                        ToolCall = tc,
                        Succeeded = false,
                        Output = "",
                        Error = "[DENIED] User did not approve this tool execution.",
                        ElapsedMs = sw.ElapsedMilliseconds
                    };
                }
                _log($"[Policy] Approved: {tc}");
            }
            else if (!policyDecision.CanExecute)
            {
                _log($"[Policy] BLOCKED: {tc}: {policyDecision.Message}");
                return new SingleToolResult
                {
                    ToolCall = tc,
                    Succeeded = false,
                    Output = "",
                    Error = $"[BLOCKED] {policyDecision.Message}",
                    ElapsedMs = sw.ElapsedMilliseconds
                };
            }

            if (ct.IsCancellationRequested)
            {
                return new SingleToolResult
                {
                    ToolCall = tc,
                    Succeeded = false,
                    Output = "",
                    Error = "Cancelled before execution",
                    ElapsedMs = sw.ElapsedMilliseconds
                };
            }

            // Execute
            var result = await _executeToolFn(tc.ToolName!, tc.Args);
            sw.Stop();

            return new SingleToolResult
            {
                ToolCall = tc,
                Succeeded = result.Succeeded,
                Output = result.Succeeded ? result.Output : "",
                Error = result.Succeeded ? "" : result.Error,
                ElapsedMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log($"[Parallel] Exception in {tc}: {ex.Message}");
            return new SingleToolResult
            {
                ToolCall = tc,
                Succeeded = false,
                Output = "",
                Error = ex.Message,
                ElapsedMs = sw.ElapsedMilliseconds
            };
        }
    }

    /// <summary>
    /// Combine all results from a batch into a single output string for the LLM.
    /// Format:
    /// <tooloutput>Batch<result>
    /// [Tool 1: ToolName] Output: ...
    /// [Tool 2: ToolName] Output: ...
    /// </result></tooloutput>
    /// </summary>
   // Stateless utility — no mutable state
    public static string CombineResults(BatchToolResult batch)
    {
        if (batch.Results.Count == 1)
        {
            var r = batch.Results[0];
            if (r.Succeeded)
                return r.Output;
            return $"[FAILED] {r.ToolCall.ToolName}: {r.Error}";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"[BATCH: {batch.Results.Count} tool calls executed in {batch.Groups.Count} group(s)]");

        foreach (var r in batch.Results)
        {
            sb.AppendLine();
            sb.AppendLine($"--- Tool {r.ToolCall.Index}: {r.ToolCall.ToolName} ({(r.Succeeded ? "OK" : "FAIL")}, {r.ElapsedMs}ms) ---");

            if (r.Succeeded)
                sb.AppendLine(r.Output);
            else
                sb.AppendLine($"ERROR: {r.Error}");
        }

        sb.AppendLine();
        sb.AppendLine($"[END BATCH — {batch.Results.Count(r => r.Succeeded)}/{batch.Results.Count} succeeded]");
        return sb.ToString();
    }

    /// <summary>
    /// Format a short summary for console display (not for LLM).
    /// </summary>
   // Stateless utility — no mutable state
    public static string FormatConsoleSummary(BatchToolResult batch)
    {
        if (batch.Results.Count == 1)
        {
            var r = batch.Results[0];
            return $"{r.ToolCall.ToolName}: {(r.Succeeded ? "OK" : "FAIL")} ({r.ElapsedMs}ms)";
        }

        var parts = batch.Results.Select(r => $"{r.ToolCall.ToolName}#{r.ToolCall.Index}:{(r.Succeeded ? "OK" : "FAIL")}");
        var ok = batch.Results.Count(r => r.Succeeded);
        return $"[{ok}/{batch.Results.Count} OK] {string.Join(" | ", parts)} ({batch.Groups.Count} groups)";
    }
}

/// <summary>Result of a single tool execution within a batch.</summary>
public class SingleToolResult
{
    public ToolCallRequest ToolCall { get; set; } = null!;  // Must be set by caller
    public bool Succeeded { get; set; }
    public string Output { get; set; } = "";
    public string Error { get; set; } = "";
    public long ElapsedMs { get; set; }
}

/// <summary>Combined result of an entire batch execution.</summary>
public class BatchToolResult
{
    public List<SingleToolResult> Results { get; set; } = new();
    public List<DependencyGroup> Groups { get; set; } = new();

    public bool AllSucceeded => Results.All(r => r.Succeeded);
    public bool AnySucceeded => Results.Any(r => r.Succeeded);
    public int SuccessCount => Results.Count(r => r.Succeeded);
}