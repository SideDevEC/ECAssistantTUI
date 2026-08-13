using System.Text.Json;
using ECAssistant.Config;
using ECAssistant.Engine;
using ECAssistant.Services;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;

namespace ECAssistant.Session;

/// <summary>
/// Session Manager — creates, tracks, and manages all sessions.
///
/// All sessions share the same loaded model weights (one GGUF in RAM).
/// Each session has its own EAgentEngine with its own KV cache (LLamaContext).
/// Inference is serialized via a shared SemaphoreSlim — only one GenerateAsync runs at a time.
///
/// v10.21: Supports discovering existing sessions on disk and loading them on startup.
/// </summary>
public class SessionManager : IAsyncDisposable
{
    private readonly Dictionary<string, AgentSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _modelPath;
    private readonly ModelParams _modelParams;
    private readonly InferenceParams _inferenceParams;
    private readonly string _workingDir;
    private readonly SubAgentConfig _subAgentConfig;
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);

    /// <summary>Shared model weights — loaded ONCE, shared across all sessions.</summary>
    private readonly LLamaWeights _sharedWeights;

    /// <summary>The currently active session (the one the UI is viewing).</summary>
    public AgentSession? ActiveSession { get; private set; }

    /// <summary>The main session (always exists, always key "main").</summary>
    public AgentSession Main { get; private set; } = null!;

    /// <summary>Config for creating new sessions.</summary>
    private readonly EAgentConfig _config;

    private int _sessionCounter = 0;

    // ── Callbacks for startup loading (v10.21) ──

    /// <summary>Called when a session is being loaded (for UI feedback). Receives session key.</summary>
    public Action<string>? OnSessionLoading { get; set; }

    /// <summary>Called when a session has finished loading. Receives session key.</summary>
    public Action<string>? OnSessionLoaded { get; set; }

    /// <summary>
    /// Create session manager. Loads model weights ONCE (does NOT create sessions yet).
    /// Call LoadSessionsFromDiskAsync() or CreateSession() afterwards.
    /// </summary>
    public SessionManager(EAgentConfig config, string resolvedModelPath, string workingDir)
    {
        _config = config;
        _modelPath = resolvedModelPath;
        _workingDir = workingDir;
        _subAgentConfig = config.SubAgent;

        _modelParams = new ModelParams(_modelPath)
        {
            GpuLayerCount = Math.Clamp(config.Llm.GpuLayers, 0, 100),
            ContextSize = config.Llm.ContextSize,
            Threads = config.Llm.Threads == -1 ? null : config.Llm.Threads,
        };

        _inferenceParams = new InferenceParams
        {
            MaxTokens = config.Inference.MaxTokens,
            AntiPrompts = config.Inference.AntiPrompts.Length > 0
                ? config.Inference.AntiPrompts
                : new[] { "</s>" },
            OverflowStrategy = LLama.Common.ContextOverflowStrategy.TruncateAndReprefill,
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = config.Sampling.Temperature,
                TopP = config.Sampling.TopP,
                TopK = config.Sampling.TopK,
                RepeatPenalty = config.Sampling.RepeatPenalty
            }
        };

        // v10.21: Redirect native llama.cpp C++ logging through callback — keeps console clean.
        // All load_tensors:, repack:, ggml_metal_, llama_context: etc go to file, not stderr/stdout.
        try
        {
            LLama.Native.NativeLogConfig.llama_log_set(delegate (LLamaLogLevel level, string message)
            {
                if (level == LLamaLogLevel.Error)
                    Logger.Error("LLAMA", message);
                else if (level == LLamaLogLevel.Warning)
                    Logger.Warn("LLAMA", message);
                // Info/Debug → file only via Logger, never console
                else
                    Logger.Info("LLAMA", message);
            });
        }
        catch { /* native lib may not be loaded yet — ignore */ }

        // Load model weights ONCE — shared across all sessions
        _sharedWeights = LLamaWeights.LoadFromFile(_modelParams);
    }

    /// <summary>Get a session by key.</summary>
    public AgentSession? Get(string key)
        => _sessions.GetValueOrDefault(key);

    /// <summary>List all sessions.</summary>
    public IReadOnlyList<AgentSession> List()
        => _sessions.Values.ToList();

    /// <summary>Number of sessions.</summary>
    public int Count => _sessions.Count;

    /// <summary>Shared model weights (for sub-agent managers etc.).</summary>
    public LLamaWeights SharedWeights => _sharedWeights;

    /// <summary>Shared model params.</summary>
    public ModelParams SharedModelParams => _modelParams;

    /// <summary>Shared inference params.</summary>
    public InferenceParams InferenceParams => _inferenceParams;

    /// <summary>Shared inference lock.</summary>
    public SemaphoreSlim InferenceLock => _inferenceLock;

    /// <summary>Working directory.</summary>
    public string WorkingDir => _workingDir;

    /// <summary>Sub-agent config.</summary>
    public SubAgentConfig SubAgentConfig => _subAgentConfig;

    /// <summary>Agent config.</summary>
    public EAgentConfig Config => _config;

    // ── Session lifecycle ──────────────────────────────

    /// <summary>
    /// Discover existing sessions on disk and load them.
    /// The most recently modified session becomes active.
    /// If no sessions exist, creates a "main" session.
    /// Returns the key of the session that was set as active.
    /// </summary>
    public async Task<string> LoadSessionsFromDiskAsync(
        Func<AgentSession, Task> initSessionAsync)
    {
        // Migrate legacy transcript if needed
        SessionDiscovery.MigrateLegacyTranscript(_workingDir);
        SessionDiscovery.EnsureSessionsDir(_workingDir);

        // Discover existing sessions
        var discovered = SessionDiscovery.DiscoverSessions(_workingDir);
        string activeKey;

        if (discovered.Count == 0)
        {
            // No sessions on disk — create main
            activeKey = "main";
            OnSessionLoading?.Invoke(activeKey);
            Main = CreateSession(activeKey, label: "Main Session");
            await initSessionAsync(Main);
            OnSessionLoaded?.Invoke(activeKey);
        }
        else
        {
            activeKey = discovered[0]; // most recently modified

            // Load all discovered sessions
            foreach (var key in discovered)
            {
                OnSessionLoading?.Invoke(key);
                var session = CreateSession(key, label: key == "main" ? "Main Session" : key);

                if (key == "main")
                    Main = session;

                await initSessionAsync(session);
                OnSessionLoaded?.Invoke(key);
            }

            // Ensure main always exists
            if (Main == null)
            {
                Main = CreateSession("main", label: "Main Session");
                await initSessionAsync(Main);
            }
        }

        // Set active session
        var activeSession = Get(activeKey) ?? Main;
        ActiveSession = activeSession;
        SessionDiscovery.TouchSessionMeta(_workingDir, activeKey);

        return activeKey;
    }

    /// <summary>
    /// Create a new session with the given key.
    /// The session uses shared model weights but gets its own LLamaContext (own KV cache).
    /// </summary>
    public AgentSession CreateSession(string key, string? label = null)
    {
        if (_sessions.ContainsKey(key))
            throw new InvalidOperationException($"Session already exists: {key}");

        var session = new AgentSession(
            key: key,
            modelPath: _modelPath,
            sharedWeights: _sharedWeights,
            sharedModelParams: _modelParams,
            inferenceParams: _inferenceParams,
            workingDir: _workingDir,
            inferenceLock: _inferenceLock,
            subAgentConfig: _subAgentConfig,
            label: label);

        _sessions[key] = session;
        _sessionCounter++;
        return session;
    }

    /// <summary>Create a new auto-named session.</summary>
    public AgentSession CreateSession(string? label = null)
    {
        var key = $"session-{++_sessionCounter}";
        return CreateSession(key, label);
    }

    /// <summary>Switch the active session to the one with this key.</summary>
    public bool SwitchTo(string key)
    {
        if (!_sessions.TryGetValue(key, out var session)) return false;
        ActiveSession = session;
        SessionDiscovery.TouchSessionMeta(_workingDir, key);
        return true;
    }

    /// <summary>Switch active session by index (1-based, matching display).</summary>
    public bool SwitchTo(int index)
    {
        var list = _sessions.Values.ToList();
        if (index < 1 || index > list.Count) return false;
        ActiveSession = list[index - 1];
        SessionDiscovery.TouchSessionMeta(_workingDir, ActiveSession.Key);
        return true;
    }

    /// <summary>Stop a specific session's execution.</summary>
    public void StopSession(string key)
    {
        if (_sessions.TryGetValue(key, out var session))
            session.Stop();
    }

    /// <summary>Stop all sessions gracefully.</summary>
    public async Task StopAllAsync()
    {
        foreach (var session in _sessions.Values)
        {
            session.Stop();
        }
        // Wait for all runners to finish
        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync();
        }
        _sessions.Clear();
    }

    /// <summary>Close and delete a session.</summary>
    public async Task CloseSessionAsync(string key)
    {
        if (!_sessions.TryGetValue(key, out var session)) return;
        if (session == Main) throw new InvalidOperationException("Cannot close the main session.");

        await session.DisposeAsync();
        _sessions.Remove(key);

        // If this was the active session, switch to main
        if (ActiveSession == session)
            ActiveSession = Main;

        // Clean up session directory
        try
        {
            var sessionDir = Path.Combine(_workingDir, ".sessions", key);
            if (Directory.Exists(sessionDir))
                Directory.Delete(sessionDir, recursive: true);
        }
        catch { }
    }

    /// <summary>Get a status report for all sessions.</summary>
    public string GetStatusReport()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== Sessions ===");
        int i = 1;
        foreach (var session in _sessions.Values)
        {
            var active = session == ActiveSession ? " →" : "  ";
            sb.AppendLine($"{active}{i}. {session.GetStatusSummary()}");
            i++;
        }
        return sb.ToString();
    }

    /// <summary>Get session by index (1-based, matching display).</summary>
    public AgentSession? GetByIndex(int index)
    {
        var list = _sessions.Values.ToList();
        if (index < 1 || index > list.Count) return null;
        return list[index - 1];
    }

    public async ValueTask DisposeAsync()
    {
        await StopAllAsync();
        // Dispose shared weights after all sessions are gone
        try { _sharedWeights.Dispose(); } catch { }
    }
}