using System.Collections.Concurrent;
using System.Threading.Channels;
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
/// Background sub-agent state.
/// </summary>
public enum BackgroundAgentState
{
    /// <summary>Just created, not started yet.</summary>
    Created,
    /// <summary>Running — actively monitoring/working.</summary>
    Running,
    /// <summary>Waiting for an event (idle between cycles).</summary>
    Waiting,
    /// <summary>Processing an event.</summary>
    Processing,
    /// <summary>Stopped by main agent or timeout.</summary>
    Stopped,
    /// <summary>Crashed with an error.</summary>
    Failed
}

/// <summary>
/// A background event — something that triggers the background agent to act.
/// Either external (email arrived, file changed) or internal (timer, poll).
/// </summary>
public class BackgroundEvent
{
    public string Source { get; set; } = "";
    public string Type { get; set; } = "";
    public string Description { get; set; } = "";
    public Dictionary<string, string> Data { get; set; } = new();
    public DateTime Timestamp { get; } = DateTime.UtcNow;
}

/// <summary>
/// Background sub-agent definition — what to monitor and how to handle it.
/// </summary>
public class BackgroundAgentConfig
{
    /// <summary>Unique name for this background agent.</summary>
    public string Name { get; set; } = "";

    /// <summary>What the agent should do (system prompt for the agent).</summary>
    public string Mission { get; set; } = "";

    /// <summary>The prompt to execute on each event cycle.</summary>
    public string EventPromptTemplate { get; set; } = "";

    /// <summary>How to poll for events (shell command, or empty for event-only).</summary>
    public string PollCommand { get; set; } = "";

    /// <summary>Poll interval in milliseconds (0 = event-only, no polling).</summary>
    public int PollIntervalMs { get; set; } = 0;

    /// <summary>Working directory.</summary>
    public string WorkingDir { get; set; } = "";

    /// <summary>Context size for the agent (default 4096).</summary>
    public uint ContextSize { get; set; } = 4096;

    /// <summary>Max turns per event processing cycle (default 3).</summary>
    public int MaxTurnsPerCycle { get; set; } = 3;

    /// <summary>Max total cycles before auto-stop (0 = unlimited).</summary>
    public int MaxCycles { get; set; } = 0;

    /// <summary>Auto-stop after this many seconds with no events (0 = never).</summary>
    public int IdleTimeoutSeconds { get; set; } = 0;

    /// <summary>Which tools the background agent can use.</summary>
    public List<string> AllowedTools { get; set; } = new();
}

/// <summary>
/// Background sub-agent — long-lived, event-driven agent that runs concurrently
/// with the main agent. Monitors for events, processes them, and pushes notifications.
///
/// Lifecycle:
///   Created → Running → (Waiting ↔ Processing)* → Stopped/Failed
///
/// Event sources:
///   1. Polling — runs a shell command at intervals, if output changes → event
///   2. External — main agent or other code pushes events via PushEvent()
///   3. File watcher — monitors working dir for file changes
///
/// Each event cycle:
///   1. Receive event
///   2. Format event into prompt using EventPromptTemplate
///   3. Run orchestrator for MaxTurnsPerCycle
///   4. Sub-agent can use ENotifyTool to push notifications to main agent
///   5. Return to waiting
/// </summary>
public sealed class BackgroundSubAgent : IAsyncDisposable
{
    private readonly BackgroundAgentConfig _config;
    private readonly NotificationQueue _notifications;
    private readonly string _modelPath;
    private readonly int _gpuLayers;
    private readonly InferenceParams _inferenceParams;
    private readonly Channel<BackgroundEvent> _eventChannel;
    // v10.19.2: Main working dir — temp dirs created inside it
    private readonly string _mainWorkingDir;
    private readonly CancellationTokenSource _cts = new();

    private EAgentEngine? _engine;
    private AgentOrchestrator? _orchestrator;
    private BackgroundAgentState _state = BackgroundAgentState.Created;
    private int _cyclesCompleted = 0;
    private DateTime _lastEventTime = DateTime.UtcNow;
    private Task? _runTask;
    private Timer? _pollTimer;

    /// <summary>Unique ID for this background agent.</summary>
    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>Current state.</summary>
    public BackgroundAgentState State => _state;

    /// <summary>Cycles completed so far.</summary>
    public int CyclesCompleted => _cyclesCompleted;

    /// <summary>Name of this agent.</summary>
    public string Name => _config.Name;

    /// <summary>When this agent was started.</summary>
    public DateTime StartedAt { get; private set; }

    public BackgroundSubAgent(
        BackgroundAgentConfig config,
        NotificationQueue notifications,
        string modelPath,
        int gpuLayers,
        InferenceParams inferenceParams,
        string mainWorkingDir = "")
    {
        _config = config;
        _notifications = notifications;
        _modelPath = modelPath;
        _gpuLayers = gpuLayers;
        _inferenceParams = inferenceParams;
        _mainWorkingDir = mainWorkingDir;
        _eventChannel = Channel.CreateUnbounded<BackgroundEvent>();
    }

    /// <summary>Start the background agent. Returns immediately (runs in background).</summary>
    public Task StartAsync()
    {
        if (_state != BackgroundAgentState.Created)
            throw new InvalidOperationException($"Cannot start agent in state {_state}");

        StartedAt = DateTime.UtcNow;
        _state = BackgroundAgentState.Running;

        EColor.TagBold(Cyan, $"BgAgent:{Name}", $"Started — mission: {_config.Mission}");

        // Start the main event loop in background
        _runTask = Task.Run(() => RunEventLoopAsync(_cts.Token));

        // Start polling timer if configured
        if (_config.PollIntervalMs > 0 && !string.IsNullOrEmpty(_config.PollCommand))
        {
            _pollTimer = new Timer(_ => DoPoll(), null, _config.PollIntervalMs, _config.PollIntervalMs);
            EColor.TagBold(EColor.Dim, $"BgAgent:{Name}", $"Polling every {_config.PollIntervalMs}ms: {_config.PollCommand}");
        }

        return Task.CompletedTask;
    }

    /// <summary>Push an external event to this background agent.</summary>
    public void PushEvent(BackgroundEvent evt)
    {
        if (_state != BackgroundAgentState.Running && _state != BackgroundAgentState.Waiting)
            return;

        _eventChannel.Writer.TryWrite(evt);
        _lastEventTime = DateTime.UtcNow;
    }

    /// <summary>Stop the background agent gracefully.</summary>
    public async Task StopAsync()
    {
        if (_state == BackgroundAgentState.Stopped || _state == BackgroundAgentState.Failed)
            return;

        _state = BackgroundAgentState.Stopped;
        _pollTimer?.Dispose();
        _cts.Cancel();
        _eventChannel.Writer.TryComplete();

        if (_runTask != null)
        {
            try { await _runTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }

        _notifications.Push(Id, Name, $"Background agent '{Name}' stopped after {_cyclesCompleted} cycles.",
            NotificationPriority.Info);

        EColor.TagBold(EColor.Warn(), $"BgAgent:{Name}", $"Stopped ({_cyclesCompleted} cycles).");
    }

    /// <summary>Get a status summary.</summary>
    public string GetStatus()
    {
        var uptime = DateTime.UtcNow - StartedAt;
        return $"{Name} | State: {_state} | Cycles: {_cyclesCompleted} | Uptime: {uptime.TotalSeconds:F0}s";
    }

    // ═══════════════════════════════════════════════════════════════
    //  INTERNAL: Event loop
    // ═══════════════════════════════════════════════════════════════

    private async Task RunEventLoopAsync(CancellationToken ct)
    {
        try
        {
            // Initialize engine once
            await InitializeEngineAsync();

            _state = BackgroundAgentState.Waiting;

            // Check for idle timeout
            var idleCheckTimer = _config.IdleTimeoutSeconds > 0
                ? new Timer(_ => CheckIdleTimeout(), null, 5000, 5000)
                : null;

            // Main event loop
            await foreach (var evt in _eventChannel.Reader.ReadAllAsync(ct))
            {
                if (ct.IsCancellationRequested) break;
                if (_state == BackgroundAgentState.Stopped) break;

                // Check max cycles
                if (_config.MaxCycles > 0 && _cyclesCompleted >= _config.MaxCycles)
                {
                    EColor.TagBold(EColor.Warn(), $"BgAgent:{Name}",
                        $"Max cycles ({_config.MaxCycles}) reached — stopping.");
                    break;
                }

                _state = BackgroundAgentState.Processing;
                _lastEventTime = DateTime.UtcNow;

                try
                {
                    await ProcessEventAsync(evt, ct);
                    _cyclesCompleted++;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    EColor.TagBold(EColor.Error(), $"BgAgent:{Name}",
                        $"Event processing error: {ex.Message}");
                    _notifications.Push(Id, Name,
                        $"Error processing {evt.Type} event: {ex.Message}",
                        NotificationPriority.Warning);
                }

                _state = BackgroundAgentState.Waiting;
            }

            idleCheckTimer?.Dispose();
        }
        catch (OperationCanceledException) { /* Normal shutdown */ }
        catch (Exception ex)
        {
            _state = BackgroundAgentState.Failed;
            EColor.TagBold(EColor.Error(), $"BgAgent:{Name}", $"Fatal error: {ex.Message}");
            _notifications.Push(Id, Name, $"Fatal error: {ex.Message}", NotificationPriority.Critical);
        }
        finally
        {
            if (_engine != null)
            {
                try { await _engine.DisposeAsync(); } catch { }
            }
        }
    }

    /// <summary>Initialize the background agent's engine.</summary>
    private async Task InitializeEngineAsync()
    {
        // v10.19.2: Background agent temp dirs inside main working dir, not OS temp
        var workingDir = string.IsNullOrEmpty(_config.WorkingDir)
            ? Path.Combine(string.IsNullOrEmpty(_mainWorkingDir) ? Path.GetTempPath() : _mainWorkingDir,
                ".bgagents", Id)
            : _config.WorkingDir;
        Directory.CreateDirectory(workingDir);

        _engine = new EAgentEngine(
            modelPath: _modelPath,
            contextSize: _config.ContextSize,
            gpuLayers: _gpuLayers,
            threadCount: -1,
            inferenceParams: _inferenceParams,
            workingDir: workingDir);

        _engine.LoadContext();
        _engine.WireSummaryService();

        // Register tools
        // Note: ESubAgent and EDispatch are intentionally NOT registered for background agents
        // to prevent recursive spawning (background agent spawning background agents).
        // This is by design — background agents are focused workers, not orchestrators.
        var bgMgr = new Services.BackgroundProcessManager();
        _engine.RegisterTool(new Tools.Shell.EShellAgent(workingDir));
        _engine.RegisterTool(new Tools.Background.EBackgroundExecTool(bgMgr, workingDir));
        _engine.RegisterTool(new Tools.Web.EWebSearchTool());
        _engine.RegisterTool(new Tools.Git.EGitTool(workingDir));
        _engine.RegisterTool(new Tools.Code.ECodeEditorTool(workingDir));

        var researchExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".cs", ".md", ".json", ".txt", ".xml", ".sql", ".html", ".css", ".js", ".sh" };
        _engine.RegisterTool(new Tools.Research.EFileResearchTool(workingDir, defaultExtensions: researchExtensions));

        // Register ENotifyTool — lets the agent push notifications to main
        _engine.RegisterTool(new Tools.Notify.ENotifyTool(_notifications, Id, Name));

        await _engine.PrefillStaticPrefix();

        _orchestrator = new AgentOrchestrator(_engine, maxTurns: _config.MaxTurnsPerCycle, maxFailures: 3);

        EColor.TagBold(EColor.Success(), $"BgAgent:{Name}", "Engine ready.");
    }

    /// <summary>Process a single event.</summary>
    private async Task ProcessEventAsync(BackgroundEvent evt, CancellationToken ct)
    {
        EColor.TagBold(EColor.Dim, $"BgAgent:{Name}",
            $"Processing event: {evt.Type} — {evt.Description}");

        // Format the prompt using the template
        var prompt = _config.EventPromptTemplate;
        if (string.IsNullOrEmpty(prompt))
            prompt = _config.Mission + "\n\nEvent: " + evt.Description;
        else
            prompt = prompt
                .Replace("{event_type}", evt.Type)
                .Replace("{event_description}", evt.Description)
                .Replace("{event_source}", evt.Source)
                .Replace("{event_data}", string.Join("; ", evt.Data.Select(d => $"{d.Key}={d.Value}")))
                .Replace("{agent_name}", Name)
                .Replace("{mission}", _config.Mission);

        if (_engine == null || _orchestrator == null) return;

        // Reset for new event cycle
        _engine.ResetForNewRequest();
        _orchestrator.Reset();

        _engine.StartExecution();
        var result = await _orchestrator.ExecuteMultiStep(prompt);
        _engine.EndExecution();

        if (result.Status == OrchestratorStatus.GoalAchieved)
        {
            EColor.TagBold(EColor.Success(), $"BgAgent:{Name}",
                $"Event processed ({result.ToolCallsMade} tool calls).");
        }
        else
        {
            EColor.TagBold(EColor.Warn(), $"BgAgent:{Name}",
                $"Event processing incomplete: {result.Status}");
        }
    }

    /// <summary>Poll for events via shell command.</summary>
    // Fix #5: Guard against channel completion race — wrap PushEvent in try/catch
    private void DoPoll()
    {
        if (_state != BackgroundAgentState.Waiting) return;

        try
        {
            var workingDir = _config.WorkingDir;
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = OperatingSystem.IsMacOS() ? "/bin/zsh" : "powershell.exe",
                Arguments = OperatingSystem.IsMacOS() ? $"-c \"{_config.PollCommand}\"" : $"-Command \"{_config.PollCommand}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (Directory.Exists(workingDir))
                psi.WorkingDirectory = workingDir;

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);

            if (!string.IsNullOrWhiteSpace(output))
            {
                try
                {
                    PushEvent(new BackgroundEvent
                    {
                        Source = "poll",
                        Type = "poll_result",
                        Description = $"Poll output: {output.Trim()}",
                        Data = new() { ["output"] = output.Trim() }
                    });
                }
                catch (ChannelClosedException) { /* Agent stopped — ignore */ }
            }
        }
        catch { /* Poll errors are non-fatal */ }
    }

    /// <summary>Check if agent has been idle too long.</summary>
    private void CheckIdleTimeout()
    {
        if (_config.IdleTimeoutSeconds <= 0) return;
        var idle = (DateTime.UtcNow - _lastEventTime).TotalSeconds;
        if (idle > _config.IdleTimeoutSeconds)
        {
            EColor.TagBold(EColor.Warn(), $"BgAgent:{Name}",
                $"Idle timeout ({_config.IdleTimeoutSeconds}s) — stopping.");
            _ = StopAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts.Dispose();
    }
}

/// <summary>
/// Manages all background sub-agents — creation, lifecycle, monitoring, and shutdown.
/// </summary>
public sealed class BackgroundAgentManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, BackgroundSubAgent> _agents = new();
    private readonly NotificationQueue _notifications;
    private readonly EAgentEngine _mainEngine;
    private readonly string _modelPath;
    private readonly int _gpuLayers;
    private readonly InferenceParams _inferenceParams;
    // v10.19.2: Main working dir — background agent temp dirs created inside it
    private readonly string _mainWorkingDir;

    /// <summary>The shared notification queue — main orchestrator reads from this.</summary>
    public NotificationQueue Notifications => _notifications;

    /// <summary>Maximum number of concurrent background agents.</summary>
    public int MaxAgents { get; set; } = 5;

    public BackgroundAgentManager(EAgentEngine mainEngine, NotificationQueue? notifications = null, string mainWorkingDir = "")
    {
        _mainEngine = mainEngine;
        _mainWorkingDir = mainWorkingDir;
        _notifications = notifications ?? new NotificationQueue();

        var configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ECAssistant", "appsettings.json");
        var config = File.Exists(configPath) ? EAgentConfig.Load(configPath) : new EAgentConfig();
        _modelPath = config.Llm.ModelPath;
        _gpuLayers = 15;

        _inferenceParams = new InferenceParams
        {
            MaxTokens = config.Inference.MaxTokens,
            AntiPrompts = config.Inference.AntiPrompts,
            OverflowStrategy = LLama.Common.ContextOverflowStrategy.TruncateAndReprefill,
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = config.Sampling.Temperature,
                TopP = config.Sampling.TopP,
                TopK = config.Sampling.TopK,
                RepeatPenalty = config.Sampling.RepeatPenalty
            }
        };
    }

    /// <summary>Spawn a background agent. Returns the agent ID. Runs in background immediately.</summary>
    // Fix #8: Check model file exists before spawning
    public async Task<string> SpawnAsync(BackgroundAgentConfig config)
    {
        if (_agents.Count >= MaxAgents)
            throw new InvalidOperationException($"Max background agents ({MaxAgents}) reached. Stop one first.");

        if (!File.Exists(_modelPath))
            throw new InvalidOperationException($"Model file not found: {_modelPath}");

        if (string.IsNullOrEmpty(config.Name))
            config.Name = $"bg-agent-{_agents.Count + 1}";

        var agent = new BackgroundSubAgent(config, _notifications, _modelPath, _gpuLayers, _inferenceParams, _mainWorkingDir);
        _agents[agent.Id] = agent;

        await agent.StartAsync();
        return agent.Id;
    }

    /// <summary>Stop a specific background agent by ID.</summary>
    public async Task<bool> StopAsync(string agentId)
    {
        if (_agents.TryGetValue(agentId, out var agent))
        {
            await agent.StopAsync();
            _agents.TryRemove(agentId, out _);
            return true;
        }
        return false;
    }

    /// <summary>Stop all background agents.</summary>
    public async Task StopAllAsync()
    {
        var tasks = _agents.Values.Select(a => a.StopAsync());
        await Task.WhenAll(tasks);
        _agents.Clear();
    }

    /// <summary>Push an event to a specific background agent.</summary>
    public void PushEvent(string agentId, BackgroundEvent evt)
    {
        if (_agents.TryGetValue(agentId, out var agent))
            agent.PushEvent(evt);
    }

    /// <summary>Get status of all background agents.</summary>
    public List<string> GetStatusAll()
    {
        return _agents.Values.Select(a => a.GetStatus()).ToList();
    }

    /// <summary>Drain all pending notifications (for main orchestrator to display).</summary>
    public List<AgentNotification> DrainNotifications()
    {
        return _notifications.DrainAll();
    }

    /// <summary>Check if there are pending notifications.</summary>
    public bool HasNotifications => _notifications.HasPending;

    public async ValueTask DisposeAsync()
    {
        await StopAllAsync();
        _notifications.Dispose();
    }
}