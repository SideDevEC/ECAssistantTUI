using System.Text;
using LLama;
using LLama.Common;
using LLama.Sampling;
using ECAssistant.Config;
using ECAssistant.Tools;
using ECAssistant.UI;
using ECAssistant.Orchestration;
using static ECAssistant.EColor;

namespace ECAssistant.Engine;

/// <summary>
/// Sub-agent task definition — what the main agent wants a sub-agent to do.
/// </summary>
public class SubAgentTask
{
    public string Description { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string WorkingDir { get; set; } = "";
    public List<string> AllowedTools { get; set; } = new();
    public uint ContextSize { get; set; } = 4096;
    public int MaxTurns { get; set; } = 5;
    public int TimeoutSeconds { get; set; } = 120;
}

/// <summary>
/// Sub-agent result — what the sub-agent returns to the main agent.
/// </summary>
public class SubAgentResult
{
    public bool Succeeded { get; set; }
    public string FinalOutput { get; set; } = "";
    public int ToolCallsMade { get; set; }
    public string Error { get; set; } = "";
    public TimeSpan Duration { get; set; }
}

/// <summary>
/// Sub-Agent Manager — spawns isolated agent sessions for complex tasks.
///
/// v10.18 architecture:
/// - Each sub-agent gets its own EAgentEngine (own KV cache, context, tools)
/// - Model weights are reloaded from disk (OS page cache makes this fast — ~2s after first load)
/// - Sub-agents run concurrently via Task.WhenAll with semaphore-limited concurrency
/// - Each sub-agent has its own orchestrator and sandboxed working directory
/// - ESubAgentTool wraps this manager so the main LLM can spawn sub-agents
///
/// Parallelism:
/// - Level 1 (within sub-agent): ParallelToolExecutor handles multiple <toolcall> blocks
/// - Level 2 (across sub-agents): Multiple ESubAgent toolcalls run via Task.WhenAll
/// </summary>
public sealed class SubAgentManager : IDisposable
{
    private readonly EAgentEngine _mainEngine;
    private readonly string _modelPath;
    private readonly int _gpuLayers;
    private readonly InferenceParams _inferenceParams;
    private readonly EAgentConfig _config;
    private readonly List<EAgentEngine> _childEngines = new();
    private readonly object _lock = new();

    /// <summary>Maximum concurrent sub-agents.</summary>
    public int MaxConcurrent { get; set; } = 3;

    public SubAgentManager(EAgentEngine mainEngine)
    {
        _mainEngine = mainEngine;

        // Extract config from main engine via reflection
        _modelPath = GetField<string>(_mainEngine, "_workingDir") ?? AppContext.BaseDirectory;
        _gpuLayers = 15; // Match production config

        // Get model path from config
        var configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ECAssistant", "appsettings.json");
        _config = File.Exists(configPath) ? EAgentConfig.Load(configPath) : new EAgentConfig();
        _modelPath = _config.Llm.ModelPath;
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

    /// <summary>Run a single sub-agent task to completion.</summary>
    public async Task<SubAgentResult> RunAsync(SubAgentTask task)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        EAgentEngine? childEngine = null;

        try
        {
            EColor.TagBold(Cyan, "SubAgent", $"Starting: {task.Description}");

            var workingDir = string.IsNullOrEmpty(task.WorkingDir)
                ? Path.Combine(Path.GetTempPath(), "eca-subagent", Guid.NewGuid().ToString("N")[..8])
                : task.WorkingDir;
            Directory.CreateDirectory(workingDir);

            // Create a new engine for this sub-agent
            childEngine = new EAgentEngine(
                modelPath: _modelPath,
                contextSize: task.ContextSize,
                gpuLayers: _gpuLayers,
                threadCount: -1,
                inferenceParams: _inferenceParams,
                workingDir: workingDir);

            lock (_lock) _childEngines.Add(childEngine);

            childEngine.LoadContext();
            childEngine.WireSummaryService();

            // Register tools — filter by AllowedTools if specified
            var bgMgr = new Services.BackgroundProcessManager();
            childEngine.RegisterTool(new Tools.Shell.EShellAgent(workingDir));
            childEngine.RegisterTool(new Tools.Background.EBackgroundExecTool(bgMgr, workingDir));
            childEngine.RegisterTool(new Tools.Web.EWebSearchTool());
            childEngine.RegisterTool(new Tools.Build.EDotnetBuildTool(workingDir));
            childEngine.RegisterTool(new Tools.Git.EGitTool(workingDir));
            childEngine.RegisterTool(new Tools.Code.ECodeEditorTool(workingDir));

            var researchExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".cs", ".md", ".json", ".txt", ".xml", ".sql", ".html", ".css", ".js", ".sh" };
            childEngine.RegisterTool(new Tools.Research.EFileResearchTool(workingDir, defaultExtensions: researchExtensions));

            // Remove tools not in AllowedTools if specified
            if (task.AllowedTools.Count > 0)
            {
                // Note: We can't remove tools after registration, but the orchestrator
                // will use the tool whitelist to filter. For now, all tools are available.
            }

            await childEngine.PrefillStaticPrefix();

            var orchestrator = new AgentOrchestrator(childEngine, maxTurns: task.MaxTurns, maxFailures: 3);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(task.TimeoutSeconds));
            childEngine.StartExecution();

            var orchResult = await orchestrator.ExecuteMultiStep(task.Prompt);

            childEngine.EndExecution();

            var result = new SubAgentResult
            {
                Succeeded = orchResult.Status == OrchestratorStatus.GoalAchieved,
                FinalOutput = orchResult.FinalOutput ?? "",
                ToolCallsMade = orchResult.ToolCallsMade,
                Error = orchResult.Status != OrchestratorStatus.GoalAchieved ? $"Status: {orchResult.Status}" : "",
            };

            sw.Stop();
            result.Duration = sw.Elapsed;

            var icon = result.Succeeded ? "✅" : "❌";
            EColor.TagBold(result.Succeeded ? EColor.Success() : EColor.Error(), "SubAgent",
                $"{icon} Completed: {task.Description} ({sw.Elapsed.TotalSeconds:F1}s, {result.ToolCallsMade} tool calls)");

            return result;
        }
        catch (OperationCanceledException)
        {
            childEngine?.EndExecution();
            return new SubAgentResult { Succeeded = false, Error = $"Timeout after {task.TimeoutSeconds}s", Duration = sw.Elapsed };
        }
        catch (Exception ex)
        {
            childEngine?.EndExecution();
            return new SubAgentResult { Succeeded = false, Error = ex.Message, Duration = sw.Elapsed };
        }
        finally
        {
            if (childEngine != null)
            {
                try { await childEngine.DisposeAsync(); } catch { }
                lock (_lock) _childEngines.Remove(childEngine);
            }
        }
    }

    /// <summary>Run multiple sub-agents in parallel (Level 2 parallelism).</summary>
    public async Task<List<SubAgentResult>> RunParallelAsync(List<SubAgentTask> tasks)
    {
        if (tasks.Count == 0) return new();

        var concurrent = Math.Min(tasks.Count, MaxConcurrent);
        EColor.TagBold(EColor.Info(), "SubAgent",
            $"Spawning {tasks.Count} sub-agent(s) ({concurrent} concurrent)");

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
        EColor.TagBold(EColor.Info(), "SubAgent",
            $"All {results.Count} sub-agents done: {succeeded} succeeded, {failed} failed");

        return results;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var e in _childEngines)
            {
                try { e.DisposeAsync().AsTask().Wait(1000); } catch { }
            }
            _childEngines.Clear();
        }
    }

    private static T? GetField<T>(object obj, string fieldName)
    {
        var field = obj.GetType().GetField(fieldName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return field?.GetValue(obj) is T value ? value : default;
    }
}