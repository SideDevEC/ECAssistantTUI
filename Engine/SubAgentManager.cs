using System.Text;
using ECAssistant.Session;
using System.Collections.Concurrent;
using LLama;
using LLama.Common;
using LLama.Sampling;
using ECAssistant.Config;
using ECAssistant.Tools;
using ECAssistant.Orchestration;
using ECAssistant.Services;
using ECAssistant.Interfaces;

namespace ECAssistant.Engine;

/// <summary>
/// Sub-agent task definition — what the main agent wants a sub-agent to do.
/// </summary>

public sealed class SubAgentManager : IDisposable
{
    private readonly EAgentEngine _mainEngine;
    private readonly string _modelPath;
    private readonly int _gpuLayers;
    private readonly int _threadCount;
    private readonly InferenceParams _inferenceParams;
    private readonly EAgentConfig _config;
    private readonly ConcurrentDictionary<string, ActiveSubAgent> _activeSubAgents = new();
    private readonly List<EAgentEngine> _childEngines = new();
    private readonly object _lock = new();
    // v10.19.2: Main working dir — sub-agent temp dirs created inside it
    private readonly string _mainWorkingDir;
    private readonly ISessionOutput? _out;
    private readonly ILogger _logger;

    /// <summary>Maximum concurrent sub-agents.</summary>
    public int MaxConcurrent { get; set; } = 3;

    /// <summary>Default context size for sub-agents — from subagent config.</summary>
    public uint DefaultContextSize { get; set; } = 16384;

    /// <summary>Default max turns for sub-agents — from subagent config.</summary>
    public int DefaultMaxTurns { get; set; } = 5;

    /// <summary>Default timeout in seconds for sub-agents — from subagent config.</summary>
    public int DefaultTimeoutSeconds { get; set; } = 120;

    /// <summary>Default max retries for sub-agents — from subagent config.</summary>
    public int DefaultMaxRetries { get; set; } = 1;

    /// <summary>Default max tool calls for sub-agents — from subagent config.</summary>
    public int DefaultMaxToolCalls { get; set; } = 20;

    /// <summary>All currently active sub-agent handles (for monitoring/cancellation).</summary>
    public IReadOnlyDictionary<string, ActiveSubAgent> ActiveAgents => _activeSubAgents;

    public SubAgentManager(EAgentEngine mainEngine, string mainWorkingDir = "", ILogger? logger = null, ISessionOutput? sessionOutput = null,
        EAgentConfig? config = null, string? modelPath = null)
    {
        _mainEngine = mainEngine;
        _mainWorkingDir = mainWorkingDir;
        _logger = logger ?? new Logger();
        _out = sessionOutput;

        // v10.23: Config injected, not read from ~/ECAssistant/appsettings.json
        _config = config ?? new EAgentConfig();
        _modelPath = modelPath ?? _config.Llm.ModelPath;
        _gpuLayers = _config.SubAgent.GpuLayers;
        DefaultContextSize = _config.SubAgent.ContextSize;
        _threadCount = _config.SubAgent.Threads;
        MaxConcurrent = _config.SubAgent.MaxConcurrent;
        DefaultMaxTurns = _config.SubAgent.MaxTurns;
        DefaultTimeoutSeconds = _config.SubAgent.TimeoutSeconds;
        DefaultMaxToolCalls = _config.SubAgent.MaxToolCalls;
        DefaultMaxRetries = _config.SubAgent.MaxRetries;

        _inferenceParams = new InferenceParams
        {
            MaxTokens = _config.Inference.MaxTokens,
            AntiPrompts = _config.Inference.AntiPrompts,
            OverflowStrategy = LLama.Common.ContextOverflowStrategy.TruncateAndReprefill,
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = _config.Sampling.Temperature,
                TopP = _config.Sampling.TopP,
                TopK = _config.Sampling.TopK,
                RepeatPenalty = _config.Sampling.RepeatPenalty
            }
        };
    }

    // ═══════════════════════════════════════════════════════════════
    //  PUBLIC API
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Run a single sub-agent task with retry and full error handling.</summary>
    public async Task<SubAgentResult> RunAsync(SubAgentTask task)
    {
        SubAgentResult? lastResult = null;
        var maxAttempts = task.MaxRetries + 1;
        var currentTask = task; // Fix #9: Don't mutate caller's task

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            var result = await RunSingleAsync(currentTask, attempt);

            if (result.Succeeded)
            {
                if (attempt > 0)
                    _out?.WriteTag("SubAgent", $"Succeeded on retry #{attempt}", OutputState.Success);
                return result;
            }

            lastResult = result;

            // Check if we should retry
            if (attempt < maxAttempts - 1)
            {
                var shouldRetry = result.Error?.Kind switch
                {
                    SubAgentErrorKind.Timeout => true,
                    SubAgentErrorKind.TurnsExhausted => true,
                    SubAgentErrorKind.ToolFailure => true,
                    SubAgentErrorKind.CancelledByMainAgent => false, // Don't retry cancellations
                    SubAgentErrorKind.ResourceLimitExceeded => false, // Don't retry resource limits
                    SubAgentErrorKind.Exception => false, // Don't retry exceptions (likely code bug)
                    _ => false
                };

                if (!shouldRetry) break;

                _out?.WriteTag("SubAgent",
                    $"Retry {attempt + 1}/{currentTask.MaxRetries} after {result.Error?.Kind} — waiting {currentTask.RetryDelayMs}ms...", OutputState.Warning);

                await Task.Delay(currentTask.RetryDelayMs);

                // Adjust task for retry — increase timeout and turns
                currentTask = new SubAgentTask
                {
                    Description = currentTask.Description,
                    Prompt = currentTask.Prompt,
                    WorkingDir = currentTask.WorkingDir, // Reuse same dir — partial results are there
                    AllowedTools = currentTask.AllowedTools,
                    ContextSize = currentTask.ContextSize,
                    MaxTurns = currentTask.MaxTurns + 2, // Give more turns on retry
                    TimeoutSeconds = currentTask.TimeoutSeconds + 30, // More time on retry
                    MaxToolCalls = currentTask.MaxToolCalls,
                    MaxDiskBytes = currentTask.MaxDiskBytes,
                    MaxRetries = 0, // No recursive retries
                };
            }
        }

        // All retries exhausted
        if (lastResult != null && lastResult.Error != null)
        {
            lastResult.Error = new SubAgentError
            {
                Kind = SubAgentErrorKind.MaxRetriesExceeded,
                Message = $"Failed after {maxAttempts} attempt(s). Last error: {lastResult.Error.Message}",
                SuccessfulActions = lastResult.Error.SuccessfulActions,
                FailedActions = lastResult.Error.FailedActions,
                FilesModified = lastResult.Error.FilesModified,
                PartialOutput = lastResult.Error.PartialOutput,
                Status = lastResult.Error.Status,
                RetryAttempt = maxAttempts - 1,
            };
        }

        return lastResult ?? new SubAgentResult { Succeeded = false, Error = new SubAgentError { Kind = SubAgentErrorKind.Exception, Message = "No result returned" } };
    }

    /// <summary>Run multiple sub-agents in parallel (Level 2 parallelism).</summary>
    public async Task<List<SubAgentResult>> RunParallelAsync(List<SubAgentTask> tasks)
    {
        if (tasks.Count == 0) return new();

        var concurrent = Math.Min(tasks.Count, MaxConcurrent);
        _out?.WriteTag("SubAgent",
            $"Spawning {tasks.Count} sub-agent(s) ({concurrent} concurrent)", OutputState.Info);

        using var semaphore = new SemaphoreSlim(concurrent);
        var tasksWithSem = tasks.Select(async task =>
        {
            await semaphore.WaitAsync();
            try { return await RunAsync(task); }
            finally { semaphore.Release(); }
        });

        var results = (await Task.WhenAll(tasksWithSem)).ToList();

        var succeeded = results.Count(r => r.Succeeded);
        var failed = results.Count(r => !r.Succeeded);
        _out?.WriteTag("SubAgent",
            $"All {results.Count} sub-agents done: {succeeded} succeeded, {failed} failed", OutputState.Info);

        return results;
    }

    /// <summary>v10.18.1: Cancel a specific sub-agent by ID.</summary>
    public void CancelSubAgent(string subAgentId)
    {
        if (_activeSubAgents.TryGetValue(subAgentId, out var agent))
        {
            _out?.WriteTag("SubAgent", $"Cancelling {subAgentId}: {agent.Description}", OutputState.Warning);
            agent.Cts.Cancel();
            agent.Engine?.StopExecution();
        }
    }

    /// <summary>v10.18.1: Cancel ALL active sub-agents (called when main agent gets ESC).</summary>
    public void CancelAll()
    {
        var count = _activeSubAgents.Count;
        if (count == 0) return;

        _out?.WriteTag("SubAgent", $"Cancelling all {count} active sub-agent(s)...", OutputState.Warning);

        foreach (var agent in _activeSubAgents.Values)
        {
            try
            {
                agent.Cts.Cancel();
                agent.Engine?.StopExecution();
            }
            catch { }
        }

        _out?.WriteTag("SubAgent", $"Cancelled {count} sub-agent(s).", OutputState.Warning);
    }

    /// <summary>v10.18.1: Get status of all active sub-agents.</summary>
    public List<(string Id, string Description, TimeSpan Elapsed, string WorkingDir)> GetActiveStatus()
    {
        return _activeSubAgents.Values.Select(a =>
        {
            var elapsed = DateTime.UtcNow - a.StartedAt;
            var workDir = GetField<string>(a.Engine ?? new object(), "_workingDir") ?? "?";
            return (a.Id, a.Description, elapsed, workDir);
        }).ToList();
    }

    // ═══════════════════════════════════════════════════════════════
    //  INTERNAL: Single execution with full error handling
    // ═══════════════════════════════════════════════════════════════

    private async Task<SubAgentResult> RunSingleAsync(SubAgentTask task, int retryAttempt)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        EAgentEngine? childEngine = null;
        var handle = new ActiveSubAgent { Description = task.Description, TaskDef = task };

        try
        {
            _out?.WriteTag("SubAgent", $"Starting{(retryAttempt > 0 ? $" (retry #{retryAttempt})" : "")}: {task.Description}", OutputState.Info);

            // v10.19.2: Sub-agent temp dirs inside main working dir, not OS temp
            var workingDir = string.IsNullOrEmpty(task.WorkingDir)
                ? Path.Combine(string.IsNullOrEmpty(_mainWorkingDir) ? Path.GetTempPath() : _mainWorkingDir,
                    ".subagents", Guid.NewGuid().ToString("N")[..8])
                : task.WorkingDir;
            Directory.CreateDirectory(workingDir);

            // #4: Resource limits — snapshot working dir before execution
            var dirBefore = SnapshotDirectory(workingDir);

            // Create engine
            childEngine = new EAgentEngine(
                modelPath: _modelPath,
                contextSize: task.ContextSize,
                gpuLayers: _gpuLayers,
                threadCount: _threadCount,
                inferenceParams: _inferenceParams,
                workingDir: workingDir,
                logger: _logger);

            handle.Engine = childEngine;
            _activeSubAgents[handle.Id] = handle;
            lock (_lock) _childEngines.Add(childEngine);

            childEngine.LoadContext();
            childEngine.WireSummaryService();

            // Register tools
            var bgMgr = new Services.BackgroundProcessManager();
            var processRunner = new Services.ProcessRunner();
            var fileSystem = new Services.FileSystemAdapter();
            var httpClient = new Services.HttpClientAdapter();

            // v10.24: Pass EAgentConfig to tools instead of ConfigProvider
            childEngine.RegisterTool(new Tools.Shell.EShellAgent(processRunner, _config, workingDir));
            childEngine.RegisterTool(new Tools.Background.EBackgroundExecTool(bgMgr, processRunner, fileSystem, _config));
            childEngine.RegisterTool(new Tools.Web.EWebSearchTool(httpClient, _config));
            childEngine.RegisterTool(new Tools.Build.EDotnetBuildTool(processRunner, _config));
            childEngine.RegisterTool(new Tools.Git.EGitTool(processRunner, fileSystem, _config));
            childEngine.RegisterTool(new Tools.Code.ECodeEditorTool(fileSystem, _config));

            var researchExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".cs", ".md", ".json", ".txt", ".xml", ".sql", ".html", ".css", ".js", ".sh" };
            childEngine.RegisterTool(new Tools.Research.EFileResearchTool(fileSystem, _config));

            await childEngine.PrefillStaticPrefix();

            // Link cancellation tokens — main agent ESC → sub-agent cancellation
            // Fix #2: Actually pass the linked token to the child engine
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                handle.Cts.Token,
                _mainEngine.ExecutionToken);
            linkedCts.CancelAfter(TimeSpan.FromSeconds(task.TimeoutSeconds));

            var orchestrator = new AgentOrchestrator(childEngine, sessionOutput: null, maxTurns: task.MaxTurns, maxFailures: 3, logger: _logger);

            // Fix #2: Start execution with the linked token so ESC + timeout both work
            childEngine.StartExecution();
            // Override the engine's CTS with our linked one by stopping execution and restarting
            // Actually, we can't inject the token directly, but we can wire cancellation:
            // When linkedCts fires, it cancels the child engine via StopExecution
            _ = Task.Run(() =>
            {
                try { linkedCts.Token.WaitHandle.WaitOne(); }
                catch { }
                if (linkedCts.Token.IsCancellationRequested)
                    childEngine.StopExecution();
            });

            var orchResult = await orchestrator.ExecuteMultiStep(task.Prompt);
            childEngine.EndExecution();

            // #3: Partial results — snapshot working dir after execution
            // Fix #3: Use Dictionary<string, DateTime> for proper modification tracking
            var dirAfter = SnapshotDirectory(workingDir);
            var filesCreated = dirAfter.Keys.Except(dirBefore.Keys).ToList();
            var filesModified = GetModifiedFiles(workingDir, dirBefore);

            var result = new SubAgentResult
            {
                Succeeded = orchResult.Status == OrchestratorStatus.GoalAchieved,
                FinalOutput = orchResult.FinalOutput ?? "",
                ToolCallsMade = orchResult.ToolCallsMade,
                FilesCreated = filesCreated.Select(f => Path.GetFileName(f)).ToList(),
                FilesModified = filesModified.Select(f => Path.GetFileName(f)).ToList(),
                ToolCallLog = orchestrator.GetToolCallLog().ToList(),
            };

            // #1: Structured error
            if (!result.Succeeded)
            {
                result.Error = new SubAgentError
                {
                    Kind = orchResult.Status switch
                    {
                        OrchestratorStatus.TurnsExhausted => SubAgentErrorKind.TurnsExhausted,
                        _ => SubAgentErrorKind.ToolFailure
                    },
                    Message = $"Sub-agent status: {orchResult.Status}",
                    AttemptedAction = task.Description,
                    // Fix #4: Parse actual success/failure from log entries instead of odd/even heuristic
                    SuccessfulActions = result.ToolCallLog.Where(l => l.Contains("OK", StringComparison.OrdinalIgnoreCase) || l.Contains("succeeded", StringComparison.OrdinalIgnoreCase)).ToList(),
                    FailedActions = result.ToolCallLog.Where(l => l.Contains("FAIL", StringComparison.OrdinalIgnoreCase) || l.Contains("failed", StringComparison.OrdinalIgnoreCase)).ToList(),
                    FilesModified = result.FilesModified,
                    PartialOutput = result.FinalOutput,
                    Status = orchResult.Status,
                    RetryAttempt = retryAttempt,
                };
            }

            // #4: Check resource limits
            var totalDiskUsed = GetDirectorySize(workingDir);
            if (task.MaxDiskBytes > 0 && totalDiskUsed > task.MaxDiskBytes)
            {
                result.Succeeded = false;
                result.Error = new SubAgentError
                {
                    Kind = SubAgentErrorKind.ResourceLimitExceeded,
                    Message = $"Disk limit exceeded: {totalDiskUsed / 1024 / 1024:F1}MB > {task.MaxDiskBytes / 1024 / 1024}MB",
                    FilesModified = result.FilesModified,
                    PartialOutput = result.FinalOutput,
                    Status = orchResult.Status,
                    RetryAttempt = retryAttempt,
                };
            }
            if (task.MaxToolCalls > 0 && result.ToolCallsMade > task.MaxToolCalls)
            {
                result.Succeeded = false;
                result.Error = new SubAgentError
                {
                    Kind = SubAgentErrorKind.ResourceLimitExceeded,
                    Message = $"Tool call limit exceeded: {result.ToolCallsMade} > {task.MaxToolCalls}",
                    FilesModified = result.FilesModified,
                    PartialOutput = result.FinalOutput,
                    Status = orchResult.Status,
                    RetryAttempt = retryAttempt,
                };
            }

            sw.Stop();
            result.Duration = sw.Elapsed;

            var icon = result.Succeeded ? "✅" : "❌";
            _out?.WriteTag("SubAgent",
                $"{icon} Completed: {task.Description} ({sw.Elapsed.TotalSeconds:F1}s, {result.ToolCallsMade} tool calls)" +
                (filesCreated.Count > 0 ? $" | Files: {string.Join(", ", result.FilesCreated)}" : ""));

            return result;
        }
        catch (OperationCanceledException)
        {
            childEngine?.EndExecution();
            var isMainCancel = _mainEngine.ExecutionToken.IsCancellationRequested;
            return new SubAgentResult
            {
                Succeeded = false,
                Duration = sw.Elapsed,
                Error = new SubAgentError
                {
                    Kind = isMainCancel ? SubAgentErrorKind.CancelledByMainAgent : SubAgentErrorKind.Timeout,
                    Message = isMainCancel ? "Cancelled by main agent (ESC)" : $"Timeout after {task.TimeoutSeconds}s",
                    AttemptedAction = task.Description,
                    PartialOutput = "(no output — cancelled before completion)",
                    RetryAttempt = retryAttempt,
                },
            };
        }
        catch (Exception ex)
        {
            childEngine?.EndExecution();
            return new SubAgentResult
            {
                Succeeded = false,
                Duration = sw.Elapsed,
                Error = new SubAgentError
                {
                    Kind = SubAgentErrorKind.Exception,
                    Message = ex.Message,
                    AttemptedAction = task.Description,
                    PartialOutput = ex.InnerException?.Message ?? "",
                    RetryAttempt = retryAttempt,
                },
            };
        }
        finally
        {
            _activeSubAgents.TryRemove(handle.Id, out _);
            handle.Cts.Dispose();

            if (childEngine != null)
            {
                try { await childEngine.DisposeAsync(); } catch { }
                lock (_lock) _childEngines.Remove(childEngine);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  HELPERS: File snapshots, directory size
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Snapshot all files in a directory (relative paths + modification times).</summary>
    private Dictionary<string, DateTime> SnapshotDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return new();

        var files = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(dir, f);
                files[rel] = File.GetLastWriteTimeUtc(f);
            }
        }
        catch { }
        return files;
    }

    /// <summary>Get files that existed before but have different modification time.</summary>
    // Fix #3: Actually compare modification times instead of flagging all pre-existing files
    private List<string> GetModifiedFiles(string dir, Dictionary<string, DateTime> beforeFiles)
    {
        var modified = new List<string>();
        try
        {
            foreach (var (relPath, beforeTime) in beforeFiles)
            {
                var fullPath = Path.Combine(dir, relPath);
                if (File.Exists(fullPath))
                {
                    var afterTime = File.GetLastWriteTimeUtc(fullPath);
                    if (afterTime != beforeTime)
                        modified.Add(fullPath);
                }
            }
        }
        catch { }
        return modified;
    }

    /// <summary>Get total size of a directory in bytes.</summary>
    private long GetDirectorySize(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        try
        {
            return Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length);
        }
        catch { return 0; }
    }

    public void Dispose()
    {
        CancelAll();
        lock (_lock)
        {
            foreach (var e in _childEngines)
            {
                try { e.DisposeAsync().AsTask().Wait(1000); } catch { }
            }
            _childEngines.Clear();
        }
    }

    private T? GetField<T>(object obj, string fieldName)
    {
        var field = obj.GetType().GetField(fieldName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return field?.GetValue(obj) is T value ? value : default;
    }
}