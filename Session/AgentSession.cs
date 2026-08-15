using System.Text;
using System.Text.Json;
using ECAssistant.Config;
using ECAssistant.Engine;
using ECAssistant.Memory;
using ECAssistant.Orchestration;
using ECAssistant.Services;
using ECAssistant.Interfaces;
using ECAssistant.Tools;
using LLama;
using LLama.Common;
using LLama.Sampling;

namespace ECAssistant.Session;

/// <summary>
/// A fully isolated agent session.
///
/// Each session has:
/// - Its own EAgentEngine (own KV cache via own LLamaContext, shares model weights)
/// - Its own orchestrator
/// - Its own tools (registered independently)
/// - Its own memory
/// - Its own file-based output buffer (JSONL, append-only, persistent)
/// - Its own prompt queue
/// - Its own runner thread (background Task)
/// - Its own stop/cancellation
///
/// Sessions share the same loaded model weights (one GGUF in RAM) but are
/// otherwise completely independent. No shared state, no inter-session communication.
///
/// The session is the UI gateway — all components (orchestrator, engine, tools)
/// get a reference to the session and call session.Write/WriteLine/WriteRaw for output.
/// The session writes to a JSONL file (always) and notifies an attached IUiRenderer (if any).
/// </summary>
public class AgentSession : ISessionOutput, IAsyncDisposable
{
    // ── Identity ──────────────────────────────────────
    public string Key { get; }
    public string? Label { get; set; }
    public DateTime CreatedAt { get; } = DateTime.UtcNow;
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;

    // ── Engine & Orchestration ───────────────────────
    private EAgentEngine _engine;
    private AgentOrchestrator _orchestrator;
    private readonly string _workingDir;
    private readonly string _sessionDir;
    private readonly SubAgentConfig _subAgentConfig;

    // ── Output Buffer (file-based JSONL) ─────────────
    private readonly string _outputFilePath;
    private readonly StreamWriter _outputFile;
    private readonly object _fileLock = new();

    // ── Auto-flushing StringBuilder buffer ────────────
    private readonly StringBuilder _streamBuffer = new();
    private OutputState _currentState = OutputState.Raw;
    private readonly object _bufferLock = new();

    // ── v10.22: Timer-based stream flushing ──
    // Tokens accumulate in _streamBuffer and flush on:
    // 1. WriteLine() / state change (immediate)
    // 2. Timer tick (every 150ms — real-time streaming feel without per-token UI writes)
    private Timer? _streamFlushTimer;
    private const int StreamFlushIntervalMs = 150;
    private DateTime _lastFlushTime = DateTime.MinValue;

    /// <summary>Initialize the stream flush timer. Call once after construction.
    /// Provides real-time token streaming without per-token UI writes.
    /// </summary>
    public void StartStreamFlushTimer()
    {
        _streamFlushTimer = new Timer(_ =>
        {
            try
            {
                lock (_bufferLock)
                {
                    if (_streamBuffer.Length > 0 &&
                        (DateTime.UtcNow - _lastFlushTime).TotalMilliseconds >= StreamFlushIntervalMs)
                    {
                        FlushBuffer();
                    }
                }
            }
            catch { /* timer errors must never crash the session */ }
        }, null, StreamFlushIntervalMs, StreamFlushIntervalMs);
    }

    // ── Output Buffer + Listeners ─────────────────────
    private readonly List<OutputEntry> _outputBuffer = new();
    private readonly List<IUiRenderer> _listeners = new();
    private readonly object _uiLock = new();

    // ── Prompt Queue ───────────────────────────────────
    private readonly Queue<string> _promptQueue = new();
    private readonly object _queueLock = new();

    // ── Run State ─────────────────────────────────────
    private SessionRunState _runState = SessionRunState.Idle;
    private CancellationTokenSource? _executionCts;
    private Task? _runnerTask;
    private readonly object _stateLock = new();
    public SessionRunState RunState => _runState;

    // ── Inference scheduler (shared across all sessions) ──
    private readonly SemaphoreSlim _inferenceLock;

    // ── Last prompt (for status display) ──────────────
    private string _lastPrompt = "";
    public string LastPrompt => _lastPrompt;
    public DateTime RunStartedAt { get; private set; }

    // ── Tool policy ───────────────────────────────────
    private readonly ECAssistant.Tools.ToolPolicy _toolPolicy;
    private readonly ILogger _logger;

    /// <summary>
    /// Create a new fully isolated session with shared model weights.
    /// </summary>
    /// <param name="key">Unique session key (e.g. "main", "watcher")</param>
    /// <param name="modelPath">Path to GGUF model file (for logging/metadata)</param>
    /// <param name="sharedWeights">Pre-loaded shared model weights (one GGUF in RAM)</param>
    /// <param name="sharedModelParams">Model params used to load the shared weights</param>
    /// <param name="inferenceParams">Inference params (max tokens, temperature, etc.)</param>
    /// <param name="workingDir">Working directory for this session</param>
    /// <param name="inferenceLock">Shared semaphore for serializing inference across sessions</param>
    /// <param name="subAgentConfig">Sub-agent config (or null to disable)</param>
    /// <param name="label">Optional human-readable label</param>
    public AgentSession(
        string key,
        string modelPath,
        LLamaWeights sharedWeights,
        ModelParams sharedModelParams,
        InferenceParams inferenceParams,
        string workingDir,
        SemaphoreSlim inferenceLock,
        SubAgentConfig? subAgentConfig = null,
        string? label = null,
        ILogger? logger = null)
    {
        _logger = logger ?? new Logger();
        Key = key;
        Label = label;
        _workingDir = workingDir;
        _inferenceLock = inferenceLock;
        _subAgentConfig = subAgentConfig ?? new SubAgentConfig();
        _toolPolicy = new ECAssistant.Tools.ToolPolicy();

        // Create session directory
        _sessionDir = Path.Combine(workingDir, ".sessions", key);
        Directory.CreateDirectory(_sessionDir);

        // Open output file (append mode, UTF-8, auto-flush)
        _outputFilePath = Path.Combine(_sessionDir, "ui_output.jsonl");
        _outputFile = new StreamWriter(_outputFilePath, append: true, Encoding.UTF8) { AutoFlush = true };

        // Create engine with SHARED weights — gets own LLamaContext (own KV cache)
        var ctxSize = sharedModelParams.ContextSize ?? 16384;
        var gpuLayers = sharedModelParams.GpuLayerCount;
        var threads = (sharedModelParams.Threads ?? -1) == -1 ? Environment.ProcessorCount : (int)sharedModelParams.Threads!;

        _engine = new EAgentEngine(
            modelPath: modelPath,
            contextSize: ctxSize,
            gpuLayers: gpuLayers,
            threadCount: threads,
            inferenceParams: inferenceParams,
            workingDir: workingDir,
            sharedWeights: sharedWeights,
            sharedModelParams: sharedModelParams,
            logger: _logger);

        _engine.LoadContext();
        _engine.WireSummaryService();

        // Create orchestrator
        _orchestrator = new AgentOrchestrator(_engine, sessionOutput: this, maxTurns: 5, maxFailures: 3, toolPolicy: _toolPolicy, logger: _logger);

        // Wire engine output through this session
        _engine.SessionOutput = this;

        // Initialize self-correction, project context, task planner
        _engine.InitializeSelfCorrection(workingDir);
        _engine.InitializeTaskPlanner();

        WriteSystem($"Session '{key}' created.");
    }

    /// <summary>The engine powering this session.</summary>
    public EAgentEngine Engine => _engine;

    /// <summary>The orchestrator managing multi-step execution.</summary>
    public AgentOrchestrator Orchestrator => _orchestrator;

    /// <summary>The tool policy for this session.</summary>
    public ECAssistant.Tools.ToolPolicy Policy => _toolPolicy;

    /// <summary>Number of messages in the conversation.</summary>
    public int MessageCount => _engine.Transcript.MessageCount;

    /// <summary>Total tokens used in context window.</summary>
    public int ContextTokens => _engine.ContextWindow.GetTotalTokens();

    /// <summary>Max token budget for this session.</summary>
    public uint MaxTokens => _engine.ContextWindow.MaxTokens;

    /// <summary>Number of queued prompts.</summary>
    public int QueueCount
    {
        get
        {
            lock (_queueLock) return _promptQueue.Count;
        }
    }

    /// <summary>Rename this session's label.</summary>
    public void Rename(string newLabel)
    {
        Label = newLabel;
        LastActivity = DateTime.UtcNow;
        WriteSystem($"Session renamed to '{newLabel}'.");
    }

    /// <summary>Get detailed session info: KV cache, context, memory, run state.</summary>
    public string GetDetailedInfo()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"=== Session '{Key}' ===");
        if (Label != null) sb.AppendLine($"Label: {Label}");
        sb.AppendLine($"State: {_runState}");
        sb.AppendLine($"Created: {CreatedAt:O}");
        sb.AppendLine($"Last Activity: {LastActivity:O}");
        sb.AppendLine();
        sb.AppendLine($"── Context ──");
        sb.AppendLine($"  Tokens: {Engine.UsedTokens}/{Engine.MaxContextTokens} ({Engine.ContextUsagePercent}%)");
        sb.AppendLine($"  Until summarize: {Engine.TokensUntilSummarize} tokens");
        sb.AppendLine($"  Near overflow: {(Engine.IsContextNearOverflow ? "⚠️ Yes" : "No")}");
        sb.AppendLine();
        sb.AppendLine($"── KV Cache ──");
        sb.AppendLine($"  Context size: {Engine.KVCacheContextSize} tokens");
        sb.AppendLine($"  Prefilled: {(Engine.IsKVCachePrefilled ? "Yes" : "No")}");
        sb.AppendLine($"  Usage ratio: {Engine.KVCacheUsageRatio:P1}");
        sb.AppendLine($"  Est. memory: {Engine.KVCacheEstimatedMB} MB (approx)");
        sb.AppendLine();
        sb.AppendLine($"── Session ──");
        sb.AppendLine($"  Messages: {MessageCount}");
        sb.AppendLine($"  Prompt queue: {QueueCount}");
        if (_runState == SessionRunState.Running)
        {
            var elapsed = DateTime.UtcNow - RunStartedAt;
            sb.AppendLine($"  Running for: {elapsed.TotalSeconds:F0}s");
            sb.AppendLine($"  Last prompt: {TruncatePrompt(LastPrompt)}");
        }
        sb.AppendLine();
        sb.AppendLine($"── Tools ──");
        sb.AppendLine($"  Registered: {Engine.Tools.Count}");
        foreach (var t in Engine.Tools)
            sb.AppendLine($"    {t.Name}");
        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════
    //  UI OUTPUT METHODS — the session is the UI gateway
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Append a raw token to the stream buffer. No UI push — tokens accumulate
    /// and are flushed to the UI as a batch "stream" entry when WriteLine() is
    /// called or state changes. This prevents per-token cursor repositioning issues.
    /// </summary>
    public void WriteRaw(string token)
    {
        lock (_bufferLock)
        {
            _streamBuffer.Append(token);
        }
    }

    /// <summary>Write raw text directly — bypasses cursor tracking for token streaming.</summary>
    public void WriteRawDirect(string token)
    {
        lock (_bufferLock)
        {
            // Flush any buffered content first, then write directly through UI renderer
            if (_streamBuffer.Length > 0)
                FlushBuffer();
            foreach (var l in _listeners) { try { l.OnRawDirect(token); } catch {} };
        }
    }

    /// <summary>Write text with a state. If state changes, flush buffer first.</summary>
    public void Write(string text, OutputState state)
    {
        lock (_bufferLock)
        {
            if (state != _currentState && _streamBuffer.Length > 0)
            {
                FlushBuffer();
            }
            _currentState = state;
            _streamBuffer.Append(text);
        }
    }

    /// <summary>Write a line with a state. Flushes buffer first if not empty.</summary>
    public void WriteLine(string text, OutputState state = OutputState.Info)
    {
        lock (_bufferLock)
        {
            // Flush accumulated stream buffer first (if not empty)
            if (_streamBuffer.Length > 0)
            {
                FlushBuffer();
            }

            _currentState = state;

            // Write the line entry
            var entry = new OutputEntry
            {
                Type = "line",
                Text = text,
                State = state,
                Ts = DateTime.UtcNow.ToString("O")
            };

            WriteEntryToFile(entry);
            NotifyUi(entry);
        }
    }

    /// <summary>Write a blank line.</summary>
    public void BlankLine()
    {
        WriteLine("", OutputState.Info);
    }

    /// <summary>Write a system-level message.</summary>
    public void WriteSystem(string text)
    {
        WriteLine(text, OutputState.System);
    }

    /// <summary>Write a colored line (legacy compat — maps to WriteLine with state).</summary>
    public void WriteLineColored(string text, OutputState state = OutputState.Info)
    {
        WriteLine(text, state);
    }

    /// <summary>Write an info message.</summary>
    public void WriteInfo(string text) => WriteLine(text, OutputState.Info);

    /// <summary>Write a success message.</summary>
    public void WriteSuccess(string text) => WriteLine(text, OutputState.Success);

    /// <summary>Write a warning.</summary>
    public void WriteWarning(string text) => WriteLine(text, OutputState.Warning);

    /// <summary>Write an error.</summary>
    public void WriteError(string text) => WriteLine(text, OutputState.Error);

    /// <summary>Write dim text.</summary>
    public void WriteDim(string text) => WriteLine(text, OutputState.Dim);

    /// <summary>
    /// Flush the internal stream buffer as a "stream" entry to the file + UI.
    /// Called internally on state changes, before WriteLine, and by the timer.
    /// </summary>
    private void FlushBuffer()
    {
        if (_streamBuffer.Length == 0) return;

        var text = _streamBuffer.ToString();
        _streamBuffer.Clear();
        _lastFlushTime = DateTime.UtcNow;

        var entry = new OutputEntry
        {
            Type = "stream",
            Text = text,
            State = _currentState,
            Ts = DateTime.UtcNow.ToString("O")
        };

        WriteEntryToFile(entry);
        NotifyUi(entry);
    }

    /// <summary>Write an output entry to the JSONL file (thread-safe).</summary>
    private void WriteEntryToFile(OutputEntry entry)
    {
        lock (_fileLock)
        {
            try
            {
                var json = JsonSerializer.Serialize(entry);
                _outputFile.WriteLine(json);
            }
            catch { /* don't crash on file I/O errors */ }
        }
    }

    /// <summary>Add entry to output buffer and notify all listeners.</summary>
    private void NotifyUi(OutputEntry entry)
    {
        lock (_uiLock)
        {
            _outputBuffer.Add(entry);
            foreach (var listener in _listeners)
            {
                try { listener.OnOutput(entry); }
                catch { /* don't let UI errors crash the session */ }
            }
        }
    }

    // ═══════════════════════════════════════════════════
    //  UI ATTACHMENT
    // ═══════════════════════════════════════════════════

    /// <summary>Add a listener for live output notifications.</summary>
    public void AddListener(IUiRenderer listener)
    {
        lock (_uiLock)
        {
            _listeners.Add(listener);
        }

        // Notify current state
        listener.OnStateChanged(_runState);

        // Notify current queue
        lock (_queueLock)
        {
            listener.OnQueueChanged(_promptQueue.ToList());
        }
    }

    /// <summary>Remove a listener.</summary>
    public void RemoveListener(IUiRenderer listener)
    {
        lock (_uiLock)
        {
            _listeners.Remove(listener);
        }
    }

    /// <summary>Get a copy of the output buffer (for UI to display history).</summary>
    public List<OutputEntry> GetOutputBuffer()
    {
        lock (_uiLock)
        {
            return _outputBuffer.ToList();
        }
    }

    /// <summary>Read the full output history from the JSONL file.</summary>
    public List<OutputEntry> ReadOutputHistory()
    {
        var entries = new List<OutputEntry>();
        try
        {
            // Flush any pending buffer first
            lock (_bufferLock)
            {
                if (_streamBuffer.Length > 0)
                    FlushBuffer();
            }

            lock (_fileLock)
            {
                _outputFile.Flush();
                foreach (var line in File.ReadLines(_outputFilePath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var entry = JsonSerializer.Deserialize<OutputEntry>(line);
                    if (entry != null) entries.Add(entry);
                }
            }
        }
        catch { /* return what we have */ }
        return entries;
    }

    /// <summary>Read the last N output entries (for peek command).</summary>
    public List<OutputEntry> ReadOutputHistory(int lastN)
    {
        var all = ReadOutputHistory();
        if (all.Count <= lastN) return all;
        return all.Skip(all.Count - lastN).ToList();
    }

    // ═══════════════════════════════════════════════════
    //  PROMPT QUEUE & EXECUTION
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Send a prompt to this session.
    /// If idle → starts execution in own thread immediately.
    /// If running → queues the prompt for after current execution.
    /// </summary>
    public void Prompt(string input)
    {
        LastActivity = DateTime.UtcNow;

        lock (_stateLock)
        {
            if (_runState == SessionRunState.Idle)
            {
                _lastPrompt = input;
                StartRunner(input);
            }
            else
            {
                // Session is running — queue the prompt
                lock (_queueLock)
                {
                    _promptQueue.Enqueue(input);
                }
                NotifyQueueChanged();
                WriteSystem($"[Queued] Prompt added to queue (position {QueueCount})");
            }
        }
    }

    /// <summary>Start the runner thread for a prompt.</summary>
    private void StartRunner(string prompt)
    {
        SetRunState(SessionRunState.Running);
        RunStartedAt = DateTime.UtcNow;
        _executionCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));

        _engine.StartExecution();

        _runnerTask = Task.Run(async () =>
        {
            await RunExecutionLoop(prompt, _executionCts.Token);
        });
    }

    /// <summary>
    /// The runner loop — executes a prompt, then checks the queue for more.
    /// Continues until queue is empty, then goes idle.
    /// </summary>
    private async Task RunExecutionLoop(string prompt, CancellationToken ct)
    {
        string currentPrompt = prompt;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // v10.22: Show inference waiting indicator if lock is contended
                if (_inferenceLock.CurrentCount == 0)
                {
                    WriteDim("[Waiting] Another session is generating — waiting for model...");
                }
                // Acquire inference lock (serialize across sessions)
                await _inferenceLock.WaitAsync(ct);
                try
                {
                    // Prefill static prefix if needed
                    await _engine.PrefillStaticPrefix();

                    // Execute
                    var result = await _orchestrator.ExecuteMultiStep(currentPrompt);

                    // Show final output
                    if (!string.IsNullOrEmpty(result.FinalOutput))
                    {
                        WriteLine(result.FinalOutput, OutputState.Bold);
                    }
                }
                finally
                {
                    _inferenceLock.Release();
                }
            }
            catch (OperationCanceledException)
            {
                WriteWarning("Execution cancelled.");
                break;
            }
            catch (Exception ex)
            {
                WriteError($"Execution error: {ex.Message}");
                if (ex.InnerException != null)
                    WriteDim($"Detail: {ex.InnerException.Message}");
                break;
            }
            finally
            {
                _engine.EndExecution();
            }

            // Check queue for next prompt
            string? nextPrompt = null;
            lock (_queueLock)
            {
                if (_promptQueue.Count > 0)
                {
                    nextPrompt = _promptQueue.Dequeue();
                    NotifyQueueChanged();
                }
            }

            if (nextPrompt == null)
            {
                // Queue empty — go idle
                break;
            }

            WriteSystem($"[Queue] Starting next prompt: {TruncatePrompt(nextPrompt)}");
            currentPrompt = nextPrompt;
            _lastPrompt = currentPrompt;
            LastActivity = DateTime.UtcNow;

            // Continue loop with next prompt
        }

        // Session is now idle
        SetRunState(SessionRunState.Idle);
        _executionCts?.Dispose();
        _executionCts = null;
    }

    /// <summary>Stop the current execution (scoped to this session only).</summary>
    public void Stop()
    {
        lock (_stateLock)
        {
            if (_runState == SessionRunState.Running)
            {
                SetRunState(SessionRunState.Stopping);
                _engine.StopExecution();
                _executionCts?.Cancel();
                WriteWarning("Execution stopped by user.");

                // Clear the runner but don't clear the queue
                // The runner will exit and set state to Idle
            }
        }
    }

    /// <summary>Get the current prompt queue.</summary>
    public List<string> GetQueue()
    {
        lock (_queueLock) return _promptQueue.ToList();
    }

    /// <summary>Remove a prompt from the queue by index.</summary>
    public bool RemoveFromQueue(int index)
    {
        lock (_queueLock)
        {
            var queueList = _promptQueue.ToList();
            if (index < 0 || index >= queueList.Count) return false;
            queueList.RemoveAt(index);

            _promptQueue.Clear();
            foreach (var p in queueList) _promptQueue.Enqueue(p);
            NotifyQueueChanged();
            return true;
        }
    }

    /// <summary>Clear the entire prompt queue.</summary>
    public void ClearQueue()
    {
        lock (_queueLock)
        {
            _promptQueue.Clear();
            NotifyQueueChanged();
        }
    }

    // ═══════════════════════════════════════════════════
    //  STATE MANAGEMENT
    // ═══════════════════════════════════════════════════

    private void SetRunState(SessionRunState state)
    {
        _runState = state;
        lock (_uiLock)
        {
            foreach (var l in _listeners) { try { l.OnStateChanged(state); } catch {} }
        }
    }

    private void NotifyQueueChanged()
    {
        lock (_uiLock)
        {
            try
            {
                lock (_queueLock)
                {
                    foreach (var l in _listeners) { try { l.OnQueueChanged(_promptQueue.ToList()); } catch {} }
                }
            }
            catch { }
        }
    }

    // ═══════════════════════════════════════════════════
    //  REGISTRATION (tools, vector memory, project context)
    // ═══════════════════════════════════════════════════

    /// <summary>Register a tool for this session's engine.</summary>
    public void RegisterTool(EToolBase tool)
    {
        _engine.RegisterTool(tool);
    }

    /// <summary>Register an ITool implementation (wrapped via ToolAdapter).</summary>
    public void RegisterTool(ECAssistant.Interfaces.ITool tool)
    {
        _engine.RegisterTool(tool);
    }

    /// <summary>Initialize vector memory for this session.</summary>
    public async Task InitializeVectorMemoryAsync(string storeDir)
    {
        await _engine.InitializeVectorMemoryAsync(storeDir);
    }

    /// <summary>Initialize project context for this session.</summary>
    public async Task InitializeProjectContextAsync()
    {
        await _engine.InitializeProjectContextAsync(_workingDir);
    }

    /// <summary>Initialize sub-agents for this session.</summary>
    public async Task InitializeSubAgentsAsync()
    {
        if (_subAgentConfig.Enabled)
        {
            await _orchestrator.InitializeSubAgentsAsync(_workingDir);
        }
    }

    /// <summary>Set secondary model for this session.</summary>
    public void SetSecondaryModel(SecondaryModelLoader secondary)
    {
        _engine.SetSecondaryModel(secondary);
    }

    /// <summary>Clear conversation history for this session.</summary>
    public void ClearHistory()
    {
        _engine.ClearHistory();
    }

    /// <summary>Save transcript to disk.</summary>
    public void SaveTranscript()
    {
        var path = Path.Combine(_sessionDir, "transcript.json");
        _engine.SaveTranscript(path);
    }

    // ═══════════════════════════════════════════════════
    //  STATUS & DISPOSAL
    // ═══════════════════════════════════════════════════

    /// <summary>Get session status summary for display.</summary>
    public string GetStatusSummary()
    {
        var stateStr = _runState switch
        {
            SessionRunState.Idle => "idle",
            SessionRunState.Running => $"running ({(DateTime.UtcNow - RunStartedAt).TotalSeconds:F0}s)",
            SessionRunState.Stopping => "stopping",
            _ => _runState.ToString().ToLowerInvariant()
        };

        var queueStr = QueueCount > 0 ? $"  queue: {QueueCount}" : "";
        var labelStr = Label != null ? $" ({Label})" : "";
        var ctxStr = $"  ctx: {Engine.UsedTokens}/{Engine.MaxContextTokens}";
        return $"[{Key}]{labelStr}  {stateStr}{ctxStr}{queueStr}";
    }

    /// <summary>Truncate a prompt for display.</summary>
    private string TruncatePrompt(string prompt, int maxLen = 60)
    {
        if (string.IsNullOrEmpty(prompt)) return "";
        return prompt.Length <= maxLen ? prompt : prompt.Substring(0, maxLen) + "...";
    }

    public async ValueTask DisposeAsync()
    {
        // Stop execution if running
        Stop();

        // Wait for runner to finish
        if (_runnerTask != null)
        {
            try { await _runnerTask; } catch { }
        }

        // Save transcript
        try { SaveTranscript(); } catch { }

        // Flush and close output file
        lock (_bufferLock) { FlushBuffer(); }
        lock (_fileLock) { try { _outputFile.Dispose(); } catch { } }

        // Dispose timer
        try { _streamFlushTimer?.Dispose(); _streamFlushTimer = null; } catch { }

        // Dispose engine
        await _engine.DisposeAsync();
        await _orchestrator.DisposeAsync();
    }
}