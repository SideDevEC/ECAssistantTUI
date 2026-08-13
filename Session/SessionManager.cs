using System.Text.Json;
using ECAssistant.Config;
using ECAssistant.Engine;
using LLama.Common;
using LLama.Sampling;

namespace ECAssistant.Session;

/// <summary>
/// Session Manager — creates, tracks, and manages all sessions.
///
/// All sessions share the same loaded model weights (one GGUF in RAM).
/// Each session has its own EAgentEngine with its own KV cache.
/// Inference is serialized via a shared SemaphoreSlim — only one GenerateAsync runs at a time.
///
/// The SessionManager creates sessions, lists them, switches between them,
/// and handles global stop/cleanup.
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

    /// <summary>The currently active session (the one the UI is viewing).</summary>
    public AgentSession? ActiveSession { get; private set; }

    /// <summary>The main session (always exists, always key "main").</summary>
    public AgentSession Main { get; }

    /// <summary>Config for creating new sessions.</summary>
    private readonly EAgentConfig _config;

    private int _sessionCounter = 0;

    /// <summary>
    /// Create session manager. Loads model weights once, creates the main session.
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

        // Create main session
        Main = CreateSession("main", label: "Main Session");
        ActiveSession = Main;
    }

    /// <summary>Get a session by key.</summary>
    public AgentSession? Get(string key)
        => _sessions.GetValueOrDefault(key);

    /// <summary>List all sessions.</summary>
    public IReadOnlyList<AgentSession> List()
        => _sessions.Values.ToList();

    /// <summary>Number of sessions.</summary>
    public int Count => _sessions.Count;

    /// <summary>
    /// Create a new session with the given key.
    /// </summary>
    public AgentSession CreateSession(string key, string? label = null)
    {
        if (_sessions.ContainsKey(key))
            throw new InvalidOperationException($"Session already exists: {key}");

        var session = new AgentSession(
            key: key,
            modelPath: _modelPath,
            modelParams: _modelParams,
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
        return true;
    }

    /// <summary>Switch active session by index (1-based, matching display).</summary>
    public bool SwitchTo(int index)
    {
        var list = _sessions.Values.ToList();
        if (index < 1 || index > list.Count) return false;
        ActiveSession = list[index - 1];
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
    }
}