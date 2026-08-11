using System.Text.Json.Serialization;
using ECAssistant.Engine;
using ECAssistant.Memory;
using ECAssistant.Orchestration;
using ECAssistant.Services;
using ECAssistant.Tools;
using LLama.Common;

namespace ECAssistant.Session;

/// <summary>
/// Session types — determines lifecycle and behavior.
/// </summary>
public enum SessionType
{
    /// <summary>Main interactive session (user-facing)</summary>
    Main,

    /// <summary>Isolated ephemeral session (sub-agent, one-shot tasks)</summary>
    Isolated,

    /// <summary>Named persistent session (addressable by key)</summary>
    Named
}

/// <summary>
/// Session state — tracks lifecycle.
/// </summary>
public enum SessionState
{
    Active,
    Idle,
    Archived
}

/// <summary>
/// A single conversation session — owns its own engine, context, memory, and tools.
/// 
/// Sessions are the core unit of isolation in ECAssistant:
/// - Main session: the primary user-facing conversation
/// - Isolated session: ephemeral, for sub-agent tasks, cleaned up after completion
/// - Named session: persistent, addressable by key, for long-running workflows
/// 
/// Each session has:
/// - Its own EAgentEngine instance (or shared model with separate context)
/// - Its own ContextWindow and ConversationTranscript
/// - Its own memory manager
/// - Its own tool set (with permissions)
/// - Its own model override (can use different models per session)
/// </summary>
public class AgentSession : IAsyncDisposable
{
    public string Key { get; }
    public SessionType Type { get; }
    public SessionState State { get; set; } = SessionState.Active;
    public string? Label { get; set; }
    public DateTime CreatedAt { get; } = DateTime.UtcNow;
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;

    private readonly EAgentEngine _engine;
    private readonly AgentOrchestrator? _orchestrator;
    private readonly ToolPolicy _toolPolicy;

    /// <summary>
    /// Create a new session.
    /// </summary>
    /// <param name="key">Unique session key</param>
    /// <param name="type">Session type (Main, Isolated, Named)</param>
    /// <param name="engine">Agent engine (owns LLM, tools, memory, context)</param>
    /// <param name="toolPolicy">Tool permissions for this session</param>
    /// <param name="maxTurns">Max orchestrator turns</param>
    /// <param name="maxFailures">Max consecutive failures before stop</param>
    /// <param name="label">Optional human-readable label</param>
    public AgentSession(
        string key,
        SessionType type,
        EAgentEngine engine,
        ToolPolicy toolPolicy,
        int maxTurns = 20,
        int maxFailures = 3,
        string? label = null)
    {
        Key = key;
        Type = type;
        Label = label;
        _engine = engine;
        _toolPolicy = toolPolicy;
        _orchestrator = new AgentOrchestrator(_engine, maxTurns, maxFailures, toolPolicy);
    }

    /// <summary>The engine powering this session.</summary>
    public EAgentEngine Engine => _engine;

    /// <summary>The orchestrator managing multi-step execution.</summary>
    public AgentOrchestrator? Orchestrator => _orchestrator;

    /// <summary>The tool policy for this session.</summary>
    public ToolPolicy Policy => _toolPolicy;

    /// <summary>Number of messages in the conversation.</summary>
    public int MessageCount => _engine.Transcript.MessageCount;

    /// <summary>Total tokens used in context window.</summary>
    public int ContextTokens => _engine.ContextWindow.GetTotalTokens();

    /// <summary>Max token budget for this session.</summary>
    public uint MaxTokens => _engine.ContextWindow.MaxTokens;

    /// <summary>Run a multi-step agent task in this session.</summary>
    public async Task<OrchestratorResult> ExecuteAsync(string input)
    {
        LastActivity = DateTime.UtcNow;
        if (_orchestrator == null)
            throw new InvalidOperationException("Session has no orchestrator");
        return await _orchestrator.ExecuteMultiStep(input);
    }

    /// <summary>Clear conversation history for this session.</summary>
    public void ClearHistory() => _engine.ClearHistory();

    /// <summary>Save transcript to disk.</summary>
    public void SaveTranscript(string path) => _engine.SaveTranscript(path);

    /// <summary>Get session status summary.</summary>
    public string GetStatusSummary()
    {
        var typeStr = Type switch
        {
            SessionType.Main => "Main",
            SessionType.Isolated => "Isolated",
            SessionType.Named => $"Named ({Label ?? "unnamed"})",
            _ => Type.ToString()
        };

        return $"Session: {Key} | Type: {typeStr} | State: {State} | " +
               $"Messages: {MessageCount} | Context: {ContextTokens}/{MaxTokens} tokens | " +
               $"Created: {CreatedAt:yyyy-MM-dd HH:mm} | Last: {LastActivity:yyyy-MM-dd HH:mm}";
    }

    public async ValueTask DisposeAsync()
    {
        State = SessionState.Archived;
        await _engine.DisposeAsync();
    }
}

/// <summary>
/// Session Manager — creates, tracks, and manages all sessions.
/// 
/// In the current architecture, the main session shares the single EAgentEngine
/// (since LLamaSharp loads one model at a time). Isolated/named sessions can either
/// share the engine (with separate context windows) or create their own engine
/// (different model).
/// 
/// For now, all sessions share the same LLamaSharp model but have separate
/// context windows, transcripts, and tool policies.
/// </summary>
public class SessionManager : IAsyncDisposable
{
    private readonly Dictionary<string, AgentSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly EAgentEngine _sharedEngine;
    private readonly ToolPolicy _defaultPolicy;
    private int _isolatedCounter = 0;
    private int _namedCounter = 0;

    /// <summary>Create session manager with a shared engine.</summary>
    public SessionManager(EAgentEngine engine, ToolPolicy? defaultPolicy = null)
    {
        _sharedEngine = engine;
        _defaultPolicy = defaultPolicy ?? new ToolPolicy();

        // Create the main session automatically
        var mainSession = new AgentSession(
            key: "main",
            type: SessionType.Main,
            engine: engine,
            toolPolicy: _defaultPolicy,
            label: "Main Session");

        _sessions["main"] = mainSession;
    }

    /// <summary>Get the main session.</summary>
    public AgentSession Main => _sessions["main"];

    /// <summary>Get a session by key.</summary>
    public AgentSession? Get(string key)
        => _sessions.GetValueOrDefault(key);

    /// <summary>List all sessions.</summary>
    public IReadOnlyList<AgentSession> List()
        => _sessions.Values.ToList();

    /// <summary>List sessions filtered by type.</summary>
    public IReadOnlyList<AgentSession> ListByType(SessionType type)
        => _sessions.Values.Where(s => s.Type == type).ToList();

    /// <summary>List active sessions.</summary>
    public IReadOnlyList<AgentSession> ListActive()
        => _sessions.Values.Where(s => s.State == SessionState.Active).ToList();

    /// <summary>
    /// Create an isolated session for a sub-agent task.
    /// Uses the shared engine but with its own context/transcript.
    /// Note: In the current single-engine architecture, isolated sessions
    /// share the same engine. Future versions should create separate engines.
    /// </summary>
    public AgentSession CreateIsolated(string? taskName = null, string? label = null)
    {
        var key = $"isolated-{++_isolatedCounter}";
        var session = new AgentSession(
            key: key,
            type: SessionType.Isolated,
            engine: _sharedEngine, // Shares model — has its own context window
            toolPolicy: new ToolPolicy(), // Fresh policy for isolation
            label: label ?? taskName);

        _sessions[key] = session;
        return session;
    }

    /// <summary>
    /// Create a named session (persistent, addressable by key).
    /// </summary>
    public AgentSession CreateNamed(string name, ToolPolicy? policy = null)
    {
        var key = $"named-{name}";
        if (_sessions.ContainsKey(key))
            throw new InvalidOperationException($"Session already exists: {key}");

        var session = new AgentSession(
            key: key,
            type: SessionType.Named,
            engine: _sharedEngine,
            toolPolicy: policy ?? new ToolPolicy(),
            label: name);

        _sessions[key] = session;
        _namedCounter++;
        return session;
    }

    /// <summary>Remove and dispose a session.</summary>
    public async Task RemoveAsync(string key)
    {
        if (_sessions.TryGetValue(key, out var session))
        {
            await session.DisposeAsync();
            _sessions.Remove(key);
        }
    }

    /// <summary>Get session status summary for all sessions.</summary>
    public string GetStatusReport()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"=== Session Manager ===");
        sb.AppendLine($"Total sessions: {_sessions.Count}");
        sb.AppendLine($"  Main: {_sessions.Values.Count(s => s.Type == SessionType.Main)}");
        sb.AppendLine($"  Isolated: {_sessions.Values.Count(s => s.Type == SessionType.Isolated)}");
        sb.AppendLine($"  Named: {_sessions.Values.Count(s => s.Type == SessionType.Named)}");
        sb.AppendLine();

        foreach (var session in _sessions.Values)
        {
            sb.AppendLine($"  {session.GetStatusSummary()}");
        }

        return sb.ToString();
    }

    /// <summary>Cleanup idle/archived sessions.</summary>
    public async Task CleanupAsync()
    {
        var toRemove = _sessions
            .Where(kvp => kvp.Value.State == SessionState.Archived || kvp.Value.Type == SessionType.Isolated)
            .Where(kvp => kvp.Key != "main")
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in toRemove)
            await RemoveAsync(key);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
            await session.DisposeAsync();
        _sessions.Clear();
    }
}