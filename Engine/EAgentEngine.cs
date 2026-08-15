using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;
using System.Runtime.InteropServices;
using LLama.Sampling;
using ECAssistant.Config;
using ECAssistant.Tools;
using ECAssistant.Memory;
using Microsoft.Extensions.Logging;
using ECAssistant.Services;
using ECAssistant.Session;

namespace ECAssistant.Engine;

internal sealed class NullLogger : Microsoft.Extensions.Logging.ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null!;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => false;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, EventId eventId, TState state, Exception? ex, Func<TState, Exception?, string> formatter) { }
}

public sealed class ToolCallResult
{
    public string ToolName { get; set; } = "";
    public Dictionary<string, string?> Args { get; set; } = new();
    public bool IsToolCall { get; set; }
}


public class EAgentEngine : IAsyncDisposable
{
    private LLamaWeights? _weights;
    private LLamaContext? _context;
    private ModelParams? _modelParams;
    private bool _sharesWeights = false; // v10.20: if true, don't dispose _weights in DisposeAsync
    // v10.22: Mock mode flag — when true, skip all LLama native initialization
    internal bool MockMode = false;

    /// <summary>Internal flag set by MockEngine to skip model loading in the base constructor.</summary>
    internal static bool _sForceMockMode = false;
    // v10.5: Switched to InteractiveExecutor for KV cache reuse.
    // Static prefix (system prompt + tools) is prefilled once at session start.
    // Only new tokens (user msg, tool output, directives) are fed per turn.
    // KV cache persists across turns — major performance improvement.
    private InteractiveExecutor? _executor;
    private readonly List<EToolBase> _tools = new(); private readonly TokenCounter _tokenCounter = new();
    private EMemoryManager? _memoryManager = null;
    private VectorMemoryStore? _vectorMemory = null;
    private SelfCorrectionManager? _selfCorrection = null;
    private ProjectContextManager? _projectContext = null;
    private readonly ECAssistant.Interfaces.ILogger? _logger;
    private TaskPlanner? _taskPlanner = null;
    private SecondaryModelLoader? _secondaryModel = null;  // v10.7: for LLM-based decomposition + summarization

       // ── Context Window (replaces raw string list) ───────────
    private readonly ContextWindow _contextWindow;
    private readonly ConversationTranscript _transcript;
    private readonly InferenceParams _inferenceParams;
    private bool _nativeLibConfigured = false;  // v10.16.1: Guard NativeLibraryConfig — one-time init
    private int _turnCount = 0;
    private string? _systemPromptText;

       // -- Session Output (v10.18: replaces direct Gui calls) ----
    private ISessionOutput? _out;
    public ISessionOutput? SessionOutput { get => _out; set => _out = value; }
    // v10.5: KV cache state management
    private bool _isPrefilled = false;           // Has the static prefix been prefilled?
    private string? _cachedStaticPrefix;          // The static prefix that's in the KV cache
    // v10.8: Saved KV cache state for format retry rewind
    private LLama.StatefulExecutorBase.ExecutorBaseState? _savedStateBeforeGen = null;

       // Inference params — always come from config
    private readonly uint _contextSize;
    private readonly int _gpuLayers;
    private readonly int _threads;

    public EMemoryManager Memory => _memoryManager ??= new EMemoryManager();
    public VectorMemoryStore? VectorMemory => _vectorMemory;
    public SelfCorrectionManager? SelfCorrection => _selfCorrection;
    public ProjectContextManager? ProjectContext => _projectContext;
    public TaskPlanner? TaskPlanner => _taskPlanner;
    public SecondaryModelLoader? SecondaryModel => _secondaryModel;  // v10.7
    public IReadOnlyList<EToolBase> Tools => _tools;
    public int TurnCount => _turnCount;
    public ConversationTranscript Transcript => _transcript;
    public ContextWindow ContextWindow => _contextWindow;

    // ── v10.22: Context & KV Cache Status ──
    /// <summary>Current token usage in the context window.</summary>
    public int UsedTokens => _contextWindow.GetTotalTokens();
    /// <summary>Max token budget for the context window.</summary>
    public uint MaxContextTokens => _contextWindow.MaxTokens;
    /// <summary>Percentage of context used (0-100).</summary>
    public int ContextUsagePercent
    {
        get
        {
            var max = (int)_contextWindow.MaxTokens;
            if (max <= 0) return 0;
            return Math.Min(100, _contextWindow.GetTotalTokens() * 100 / max);
        }
    }
    /// <summary>Distance to auto-summarize threshold (tokens). Negative if already past.</summary>
    public int TokensUntilSummarize
    {
        get
        {
            var max = (int)_contextWindow.MaxTokens;
            if (max <= 0) return 0;
            var threshold = max / 2; // auto-summarize at 50%
            return threshold - _contextWindow.GetTotalTokens();
        }
    }
    /// <summary>Whether context is getting close to overflow (>80%).</summary>
    public bool IsContextNearOverflow => ContextUsagePercent >= 80;
    /// <summary>Context status summary for display.</summary>
    public string ContextStatusSummary
    {
        get
        {
            var used = UsedTokens;
            var max = MaxContextTokens;
            var pct = ContextUsagePercent;
            var untilSum = TokensUntilSummarize;
            var warn = pct >= 80 ? " ⚠️" : (pct >= 50 ? " (summarize zone)" : "");
            return $"ctx: {used}/{max} ({pct}%){warn} — {untilSum} tokens until summarize";
        }
    }

    // ── v10.22: KV Cache Info ──
    /// <summary>Context size (KV cache capacity in tokens).</summary>
    public uint KVCacheContextSize => _contextSize;
    /// <summary>Whether the static prefix has been prefilled into KV cache.</summary>
    public bool IsKVCachePrefilled => _isPrefilled;
    /// <summary>Approximate KV cache usage — ratio of used context tokens to context size.</summary>
    public double KVCacheUsageRatio
    {
        get
        {
            if (_contextSize == 0) return 0;
            return Math.Min(1.0, _contextWindow.GetTotalTokens() / (double)_contextSize);
        }
    }
    /// <summary>Approximate KV cache memory estimate in MB (rough: 2 bytes per token per layer × gpu_layers).</summary>
    public double KVCacheEstimatedMB
    {
        get
        {
            // Rough estimate: KV cache = 2 * n_layers * n_ctx * n_embd * sizeof(half)
            // For Qwen3-8B: ~28 layers, 4096 dim → ~2 bytes * 28 * ctxSize * 4096 * 2 / 1M
            // This is very approximate — actual depends on model architecture
            var approxLayers = 28; // reasonable default for 8B models
            var approxDim = 4096;
            return Math.Round(2.0 * approxLayers * _contextSize * approxDim * 2 / (1024 * 1024), 1);
        }
    }
    
    // v10.9: Cancellation token for stopping execution mid-stream
    // v10.9.1: Fixed race condition — don't null _cts in StopExecution
    private CancellationTokenSource? _cts;
    private volatile bool _isExecuting = false;
    private volatile bool _escPressed = false;  // v10.9.2: ESC flag for partial response check
    public CancellationToken ExecutionToken => _cts?.Token ?? CancellationToken.None;
    public bool IsExecuting => _isExecuting;
    
    public void StartExecution() 
    { 
        _cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        _isExecuting = true;
    }
    public void StopExecution() { _cts?.Cancel(); }
    public void EndExecution() 
    { 
        try { _cts?.Dispose(); } catch { }
        _cts = null; 
        _isExecuting = false; 
    }

    // v10.11.1: Expose ESC/stopped state so orchestrator can check without coupling to string matching
    public bool IsExecutionStopped => _escPressed || (_cts?.IsCancellationRequested ?? false);

    // v10.11.1: Rebuild KV cache after an ESC stop or cancellation.
    // The static prefix is re-prefilled, but all conversation tokens are cleared.
    // This prevents stale user messages from the stopped attempt leaking into the next command.
    public virtual async Task RebuildCacheAfterStopAsync()
    {
        if (!_isPrefilled) return;  // nothing to rebuild if never prefilled
        _out?.WriteWarning("[KVCache] Rebuilding after ESC stop...");
        await ResetAndRebuildCacheAsync();
        _out?.WriteSuccess("[KVCache] Cache rebuilt after stop.");
    }

    /// <summary>Initialize self-correction manager.</summary>
    public void InitializeSelfCorrection(string workingDir)
    {
        _selfCorrection = new SelfCorrectionManager(workingDir, _logger);
        _out?.WriteSuccess("[SelfCorrect] Self-correction manager ready.");
    }

    /// <summary>Initialize project context manager and scan project.</summary>
    public async Task InitializeProjectContextAsync(string workingDir)
    {
        _projectContext = new ProjectContextManager(workingDir, _logger);
        await _projectContext.InitializeAsync();
        _out?.WriteSuccess($"[ProjectCtx] Project context loaded: {_projectContext.FileCount} files.");
    }

    /// <summary>Initialize task planner for this session.</summary>
    public void InitializeTaskPlanner()
    {
        _taskPlanner = new TaskPlanner(_logger);
    }

    /// <summary>Set the secondary model for decomposition + summarization (v10.7).</summary>
    public void SetSecondaryModel(SecondaryModelLoader secondary)
    {
        _secondaryModel = secondary;
        _out?.WriteSuccess("[Secondary] Secondary model attached to engine.");
    }

    /// <summary>Inject project context into prompt — only for code-related tasks.</summary>
    private string? GetProjectContextInjection(string userRequest)
    {
        if (_projectContext == null) return null;
        
        // Only inject for code-related queries (not for simple questions like "what day is it")
        var codeKeywords = new[] { "code", "file", "build", "compile", "error", "fix", "refactor", 
            "class", "method", "function", "project", "edit", "change", "replace", "add", 
            "remove", "delete", "create", "write", "read", "program", "script", "config",
            ".cs", ".json", ".md", "dotnet", "git", "test", "debug" };
        
        var isCodeRelated = codeKeywords.Any(k => userRequest.Contains(k, StringComparison.OrdinalIgnoreCase));
        if (!isCodeRelated) return null;
        
        return _projectContext.GetProjectSummary();
    }

    /// <summary>Inject task progress into prompt.</summary>
    private string? GetTaskProgressInjection()
    {
        return _taskPlanner?.GetProgressContext();
    }

    /// <summary>Inject failure context into prompt.</summary>
    private string? GetFailureInjection()
    {
        return _selfCorrection?.GetFailureSummary();
    }

    /// <summary>Initialize vector memory store with TF-IDF embeddings (no external deps).</summary>
    public async Task InitializeVectorMemoryAsync(string storeDir)
    {
        _vectorMemory = new VectorMemoryStore(storeDir, _logger);
        
        Func<string, Task<float[]>> embeddingGenerator = async (text) =>
        {
            await Task.CompletedTask;
            return TfidfEmbed(text);
        };
        
        await _vectorMemory.InitializeAsync(embeddingGenerator);
        _out?.WriteSuccess($"[VecMem] Vector memory ready: {_vectorMemory.Count} entries in {storeDir}");
    }

    /// <summary>Simple TF-IDF style embedding — no external dependencies.</summary>
    private float[] TfidfEmbed(string text)
    {
        var tokens = System.Text.RegularExpressions.Regex.Matches(text.ToLower(), @"[a-z0-9]{2,}")
            .Select(m => m.Value)
            .ToList();

        if (tokens.Count == 0) return Array.Empty<float>();

        var termFreq = tokens.GroupBy(t => t)
            .ToDictionary(g => g.Key, g => (float)g.Count() / tokens.Count);

        var dim = 256;
        var vector = new float[dim];
        foreach (var kvp in termFreq)
        {
            var hash = Math.Abs(kvp.Key.GetHashCode()) % dim;
            vector[hash] += kvp.Value;
        }

        var mag = Math.Sqrt(vector.Sum(v => v * v));
        if (mag > 0)
            for (int i = 0; i < dim; i++)
                vector[i] = (float)(vector[i] / mag);

        return vector;
    }

    /// <summary>Wire the SummaryService to use the engine's own LLM for context compaction.</summary>
    public void WireSummaryService()
    {
        _contextWindow.SetSummaryService(new SummaryService(async prompt =>
        {
            // v10.7: Use secondary model for summarization if available (no KV cache interference)
            // v10.7.4: Use GenerateAsync directly — SummaryService.SummarizeAsync already builds
            // its own prompt. Calling _secondaryModel.SummarizeAsync would double-wrap the prompt.
            if (_secondaryModel != null && _secondaryModel.IsLoaded)
            {
                var summary = await _secondaryModel.GenerateAsync(prompt, maxTokens: Math.Max(100, (int)_secondaryModel.ContextSize / 8));
                summary = System.Text.RegularExpressions.Regex.Replace(summary, @"<[^>]+>", "");
                // v10.7.4: Escape angle brackets to prevent fake XML tags in context
                summary = summary.Replace("<", "&lt;").Replace(">", "&gt;");
                return string.IsNullOrWhiteSpace(summary) ? "(Summary generation failed)" : summary;
            }
            
            // Fallback: use a separate StatelessExecutor (doesn't interfere with main KV cache)
            if (_weights == null || _modelParams == null) return "(Summary generation failed)";
            var summaryExecutor = new StatelessExecutor(_weights, _modelParams, new NullLogger());
            var sb = new StringBuilder();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                await foreach (var token in summaryExecutor.InferAsync(prompt, _inferenceParams, cts.Token))
                    sb.Append(token);
            }
            catch (OperationCanceledException)
            {
                // Timeout - return what we have
            }
            var result = sb.ToString().Trim();
            result = System.Text.RegularExpressions.Regex.Replace(result, @"<[^>]+>", "");
            return string.IsNullOrWhiteSpace(result) ? "(Summary generation failed)" : result;
        }));
        var mode = (_secondaryModel != null && _secondaryModel.IsLoaded) ? "secondary model" : "stateless side-executor";
        _out?.WriteInfo($"[Context] SummaryService wired to {mode}.");
    }

    /// <summary>
    /// v10.17: Generate a plan using the main LLM (stateless — does not pollute KV cache).
    /// Used by StepMapper to map sub-tasks to concrete tool calls.
    /// Uses a StatelessExecutor with the same weights + params so the KV cache is untouched.
    /// </summary>
    public async Task<string> GeneratePlanAsync(string prompt)
    {
        if (_weights == null || _modelParams == null)
            return "";

        var executor = new StatelessExecutor(_weights, _modelParams, new NullLogger());
        var sb = new StringBuilder();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            var planInference = new InferenceParams
            {
                MaxTokens = Math.Min(1024, (int)_contextSize / 4),
                AntiPrompts = new[] { "</plan>", "User:", "Question:" },
                OverflowStrategy = LLama.Common.ContextOverflowStrategy.TruncateAndReprefill,
                SamplingPipeline = _inferenceParams.SamplingPipeline,
            };
            await foreach (var token in executor.InferAsync(prompt, planInference, cts.Token))
                sb.Append(token);
        }
        catch (OperationCanceledException)
        {
            // Timeout — return what we have
        }

        var result = sb.ToString().Trim();
        _out?.WriteInfo($"[StepMapper] Plan generated ({result.Length} chars)");
        return result;
    }

       /// <summary>Create engine with context window support and auto-injected memory.</summary>
     private string _workingDir = "";

/// <summary>Original constructor — loads GGUF from disk. Use this for standalone engines.</summary>
public EAgentEngine(string modelPath, uint contextSize, int gpuLayers, int threadCount, InferenceParams inferenceParams, string workingDir = "", ECAssistant.Interfaces.ILogger? logger = null)
          : this(modelPath, contextSize, gpuLayers, threadCount, inferenceParams, workingDir, sharedWeights: null, sharedModelParams: null, logger)
    {
    }

    /// <summary>
    /// Shared-weights constructor — uses an already-loaded LLamaWeights instance.
    /// Creates its own LLamaContext (own KV cache) from the shared weights.
    /// This is the v10.20 session path: one GGUF in RAM, separate KV caches per session.
    /// </summary>
    /// <param name="modelPath">Path to model (for logging/metadata only — not loaded from disk)</param>
    /// <param name="sharedWeights">Pre-loaded model weights (shared across sessions)</param>
    /// <param name="sharedModelParams">Model params used to create the shared weights (reused for context creation)</param>
    /// <param name="contextSize">Context size for THIS engine's KV cache (can differ from shared weights)</param>
    /// <param name="gpuLayers">GPU layers for THIS engine's context</param>
    /// <param name="threadCount">Thread count for THIS engine</param>
    /// <param name="inferenceParams">Inference params for THIS engine</param>
    /// <param name="workingDir">Working directory</param>
    public EAgentEngine(string modelPath, uint contextSize, int gpuLayers, int threadCount, InferenceParams inferenceParams, string workingDir,
        LLamaWeights? sharedWeights = null, ModelParams? sharedModelParams = null, ECAssistant.Interfaces.ILogger? logger = null)
          {
               _logger = logger ?? new Logger();

               // Enable native library logging — only once (LLamaSharp throws on second config)
               if (!_nativeLibConfigured)
               {
                   _nativeLibConfigured = true;
                   try {
                       NativeLibraryConfig.All.WithLogCallback(delegate (LLamaLogLevel level, string message)
                        {
                         if (level == LLamaLogLevel.Error)
                            _logger?.Error("LLAMA", $"[LLAMA ERROR] {message}");
                        });
                   } catch { /* may already be loaded */ }
               }

           // v10.22: Mock mode — skip all LLama native initialization, just set basic fields
           if (MockMode || _sForceMockMode)
           {
               if (_sForceMockMode) MockMode = true; // promote static flag to instance
               _sForceMockMode = false; // reset static flag
               _contextSize = contextSize;
               _gpuLayers = gpuLayers;
               _threads = threadCount;
               _inferenceParams = inferenceParams;
               _workingDir = string.IsNullOrEmpty(workingDir) ? AppContext.BaseDirectory : workingDir;
               _memoryManager = new EMemoryManager();
               var mockSummarySvc = new SummaryService(null);
               _contextWindow = new ContextWindow(contextSize, mockSummarySvc);
               _transcript = new ConversationTranscript();
               _memoryManager.Load();
               return; // Skip all LLama weight loading, context creation, system prompt loading
           }

           var sysInfo = SystemInfo.Get();
          if (sysInfo.OSPlatform != default)
                {
                var cudaVer = sysInfo.CudaMajorVersion;
                  if (cudaVer == -1)
                      _logger?.Info("CUDA", "No CUDA detected — will run on CPU");
                     else
                        _logger?.Info("CUDA", $"Detected: CUDA {cudaVer}");
                        }

               // Report which GPU backends are available
           // v9.4: Suppress SystemInfo dump on startup
           try { _logger?.Debug("System", SystemInfo.Get().ToString()); } catch { }

              _contextSize = contextSize;
               _gpuLayers = gpuLayers;
               _threads = threadCount;
                  _inferenceParams = inferenceParams;

               _workingDir = string.IsNullOrEmpty(workingDir) ? AppContext.BaseDirectory : workingDir;

               if (sharedWeights != null && sharedModelParams != null)
               {
                   // ── Shared weights path (v10.20 sessions) ──
                   // One GGUF in RAM, each engine gets its own LLamaContext (own KV cache)
                   _weights = sharedWeights;
                   _sharesWeights = true; // Don't dispose shared weights

                   // Create fresh ModelParams for this context (context size may differ per session)
                   var ctxParams = new ModelParams(modelPath)
                   {
                       GpuLayerCount = Math.Clamp(gpuLayers, 0, 100),
                       ContextSize = contextSize,
                       Threads = sharedModelParams.Threads,
                   };
                   _modelParams = ctxParams;
                   _context = _weights.CreateContext(ctxParams);
               }
               else
               {
                   // ── Standalone path (original behavior) ──
                   var parameters = new ModelParams(modelPath)
                     {
                       GpuLayerCount = Math.Clamp(gpuLayers, 0, 100),
                          ContextSize = contextSize,
                           };
                   _modelParams = parameters;
                   _weights = LLamaWeights.LoadFromFile(parameters);
                _context = _weights.CreateContext(parameters);
               }

          var nullLog = new NullLogger();
            // v10.5: InteractiveExecutor with KV cache reuse.
            // Static prefix is prefilled once, then only new tokens per turn.
            _executor = new InteractiveExecutor(_context, nullLog);

             // ── Initialize tokenizer for accurate token counting ───
           if (_context != null) _tokenCounter.Initialize(_context);

          // ── Load memory manager (not lazy — eager on startup) ───
              _memoryManager = new EMemoryManager();

           // ── Initialize context window + transcript ────────────
           var summarySvc = new SummaryService(null); // Will be wired after construction
               _contextWindow = new ContextWindow(contextSize, summarySvc);
                _transcript = new ConversationTranscript();

           // Load any existing transcript from disk for session resumption
             var transcriptPath = Path.Combine(_workingDir, "transcript.json");
            if (File.Exists(transcriptPath))
                  {
                  try
                     {
                     var loaded = ConversationTranscript.LoadFromDisk(transcriptPath);
                    if (loaded != null && loaded.MessageCount > 0)
                          {
                             _transcript.Messages.AddRange(loaded.Messages);
                           foreach (var msg in loaded.Messages)
                                 _contextWindow.AddUserMessage(msg.Content); // restore token budget
                               _out?.WriteInfo($"[Context] Loaded {loaded.MessageCount} messages from previous session.");
                            // v9.9: Show compact summary of previous session
                            var userMsgs = loaded.Messages.Where(m => m.Role == "user").TakeLast(3);
                            if (userMsgs.Any())
                            {
                                 _out?.WriteInfo($"[Last session] {string.Join(" | ", userMsgs.Select(m => StringUtil.Truncate(m.Content, 60)))}");
                            }
                          }
                    }
              catch (Exception ex)
                   {
                   _logger?.Error("Context", $"Failed to load transcript: {ex.Message}");
                        }
                    }

             _out?.WriteInfo($"[Engine] Model loaded: {modelPath}");
            _logger?.Info("Engine", $"Model loaded: {modelPath} | Context: {contextSize} | GPU: {gpuLayers} | Threads: {threadCount}");
              _out?.WriteInfo($"[Config] ContextSize: {contextSize} tokens | GPU Layers: {_gpuLayers}");

           // Load memory and show how many entries are active
               _memoryManager.Load();
               if (_memoryManager.Count > 0)
                 {
                   _out?.WriteInfo($"[Memory] Active memories loaded: {_memoryManager.Count}");
                      }
              else
                    {
                  _out?.WriteInfo("No prior memory entries found (first session).");
                        }

             // v10.16: Load OS-specific system prompt at startup
            // Windows: SystemPrompt.Windows.md, Mac: SystemPrompt.Mac.md
            // Fallback: SystemPrompt.md (generic/legacy)
            try
             {
                var promptFileName = OperatingSystem.IsMacOS() ? "SystemPrompt.Mac.md"
                                   : OperatingSystem.IsWindows() ? "SystemPrompt.Windows.md"
                                   : "SystemPrompt.md";
                var sysPromptPath = Path.Combine(_workingDir, promptFileName);
                if (!File.Exists(sysPromptPath)) sysPromptPath = Path.Combine(AppContext.BaseDirectory, promptFileName); // fallback to build dir
                // v10.16: Final fallback to legacy SystemPrompt.md if OS-specific not found
                if (!File.Exists(sysPromptPath))
                {
                    var legacyPath = Path.Combine(_workingDir, "SystemPrompt.md");
                    if (!File.Exists(legacyPath)) legacyPath = Path.Combine(AppContext.BaseDirectory, "SystemPrompt.md");
                    if (File.Exists(legacyPath)) { sysPromptPath = legacyPath; promptFileName = "SystemPrompt.md"; }
                }
                 if (File.Exists(sysPromptPath))
                      {
                         _systemPromptText = File.ReadAllText(sysPromptPath);
                          _out?.WriteInfo($"[Config] System prompt loaded from: {promptFileName} ({_systemPromptText.Length} chars)");
                      }
                    else
                         {
                           _logger?.Warn("Engine", "No system prompt file found — using empty system prompt.");
                              _systemPromptText = "";
                            }
                          }
             catch (Exception ex)
                  {
                       _logger?.Warn("Engine", $"Failed to load system prompt: {ex.Message}");
                    _systemPromptText = "";
                    }

         }

       /// <summary>Build system+tools prompt — SystemPrompt.md + runtime tool self-registration.</summary>
    /// <remarks>
    /// SystemPrompt.md is tool-agnostic (v3.2). Each registered tool provides its own
    /// Name, Description, Rules, and Examples via ToSystemPromptBlock(). These are
    /// appended at runtime so adding/removing tools requires no SystemPrompt.md edits.
    /// </remarks>
    private string BuildSystemToolsPrompt()
          {
          var sb = new StringBuilder();
           if (!string.IsNullOrEmpty(_systemPromptText))
               sb.AppendLine(_systemPromptText);

           // ── Runtime tool self-registration ──
           // Each tool injects its own rules + examples via ToSystemPromptBlock()
           if (_tools.Count > 0)
           {
               sb.AppendLine();
               sb.AppendLine("## REGISTERED TOOLS");
               sb.AppendLine();
               foreach (var tool in _tools)
               {
                   sb.AppendLine(tool.ToSystemPromptBlock());
                   sb.AppendLine();
               }
           }

         return sb.ToString();
             }

    // v10.5: Prefill the KV cache with the static prefix (system prompt + tools).
    // Called once at session start. After this, only new tokens are fed per turn.
    public virtual async Task PrefillStaticPrefix()
    {
        if (_isPrefilled || _executor == null) return;

        _cachedStaticPrefix = BuildSystemToolsPrompt();

        _out?.WriteInfo($"[KVCache] Prefilling static prefix ({_cachedStaticPrefix.Length} chars)...");
        var startMs = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;

        // Feed the static prefix through the executor as a "prompt run".
        // This populates the KV cache. We don't need the output — just the cache state.
        var sb = new StringBuilder();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            // The anti-prompts will stop generation after the static prefix.
            // We just need the prefill to happen — any generated tokens are discarded.
            await foreach (var token in _executor!.InferAsync(_cachedStaticPrefix, _inferenceParams, cts.Token))
            {
                sb.Append(token);
                // Stop early if the model tries to generate content (we just want prefill)
                if (sb.ToString().Contains("\n", StringComparison.Ordinal))
                    break;
            }
        }
        catch (OperationCanceledException)
        {
             _out?.WriteWarning("[KVCache] Prefill timed out (120s) — continuing anyway.");
        }

        var elapsedMs = (long)((DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond) - startMs);
        _isPrefilled = true;
        _out?.WriteSuccess($"[KVCache] Static prefix prefilled in {elapsedMs}ms. KV cache active.");
    }

    // v10.5: Build only the new tokens to feed since the last turn.
    // Turn 1: memory + user message + <assistant> cue
    // Turn 2+: tool output + directive + <assistant> cue
    private string BuildIncrementalInput(string userRequest)
    {
        var sb = new StringBuilder();

        if (_turnCount == 1)
        {
            // First turn: static prefix already in KV cache.
            // Feed memory injection (if any) + execution plan + user message + assistant cue.
            var memoryInject = GetMemoryInjection(userRequest);
            var projectCtx = GetProjectContextInjection(userRequest);
            var taskProgress = GetTaskProgressInjection();
            var failureCtx = GetFailureInjection();

            // v10.17: Inject execution plan (if any) — mapped tool calls from StepMapper
            var systemMessages = _contextWindow.GetWindowMessages().Where(m => m.Role == "system");
            foreach (var sysMsg in systemMessages)
            {
                if (!string.IsNullOrEmpty(sysMsg.Content))
                    sb.AppendLine(sysMsg.Content);
            }

            if (!string.IsNullOrEmpty(memoryInject))
            {
                sb.AppendLine("> PERSISTENT MEMORY — These are past decisions, patterns, and lessons that may help you:");
                sb.AppendLine(memoryInject);
                sb.AppendLine();
            }
            if (!string.IsNullOrEmpty(projectCtx))
            {
                sb.AppendLine(projectCtx);
                sb.AppendLine();
            }
            if (!string.IsNullOrEmpty(taskProgress))
                sb.AppendLine(taskProgress);
            if (!string.IsNullOrEmpty(failureCtx))
                sb.AppendLine(failureCtx);

            // User message
            sb.AppendLine($"<user>");
            sb.AppendLine(userRequest);
            sb.AppendLine("</user>");
            sb.AppendLine();

            // First-turn directive
            sb.AppendLine("-- Open <lm><thinking>brief</thinking><toolcall>ToolName<arg>value</arg></toolcall></lm> then STOP. --");

            // v10.15.4: Generation cue is just <assistant> — model must output <lm> itself.
            // This forces the model to generate the full <lm>...</lm> structure.
            sb.AppendLine("<assistant>");
        }
        else
        {
            // Subsequent turns: history is already in KV cache.
            // Feed only the new tool output + directive + generation cue.
            var windowMessages = _contextWindow.GetWindowMessages();
            var toolResultCount = windowMessages.Count(m => m.Role == "tool_output");

            // Find the last tool_output message (just added by the orchestrator)
            var lastTool = windowMessages.LastOrDefault(m => m.Role == "tool_output");
            if (lastTool != null)
            {
                sb.AppendLine($"<tooloutput>{lastTool.Source}<result>");
                sb.AppendLine(lastTool.Content);
                sb.AppendLine("</result></tooloutput>");
                sb.AppendLine();
            }

            // Find the last user message (the InjectFormatRetry directive)
            var lastUser = windowMessages.LastOrDefault(m => m.Role == "user");
            if (lastUser != null)
            {
                sb.AppendLine("<user>");
                sb.AppendLine(lastUser.Content);
                sb.AppendLine("</user>");
                sb.AppendLine();
            }

            // Context-aware directive
            if (toolResultCount >= 3)
            {
                sb.AppendLine($"-- Open <lm><thinking>brief</thinking> then <output>answer</output></lm> if done, or <lm><thinking>brief</thinking><toolcall>...</toolcall></lm> if you need more data. You have run {toolResultCount} tool calls. --");
            }
            else
            {
                sb.AppendLine("-- Open <lm><thinking>brief</thinking> then <output>answer</output></lm> if done, or <lm><thinking>brief</thinking><toolcall>...</toolcall></lm> if you need more data. Tool results above. --");
            }

            // v10.15.4: Generation cue is just <assistant> — model must output <lm> itself.
            sb.AppendLine("<assistant>");
        }

        return sb.ToString();
    }


       /// <summary>Build the complete prompt for one generation turn.
        /// Uses ContextWindow to enforce token budget and apply summarization.
         /// Injects relevant memory from EMemoryManager into every prompt.</summary>
     public string BuildFullPrompt(string userRequest)
          {
            // ── Step 1: Get system+tools block (cached from BuildSystemToolsPrompt) ───────
             var systemBlock = BuildSystemToolsPrompt();

               // ── Step 2: Query memory and inject relevant entries ───────────
             var memoryInject = GetMemoryInjection(userRequest);
           var projectCtx = GetProjectContextInjection(userRequest);
           var taskProgress = GetTaskProgressInjection();
           var failureCtx = GetFailureInjection();

           // ── Step 3: Get windowed history from ContextWindow ───
           var windowMessages = _contextWindow.GetWindowMessages();

           // v9.3: Hard cap on history — leave room for system prompt + memory + new user message + max_tokens
           // Reserve: systemBlock tokens + memory tokens + max_tokens (2048) + buffer (2048)
           var systemTokens = _tokenCounter.Count(systemBlock);
           var memoryTokens = string.IsNullOrEmpty(memoryInject) ? 0 : _tokenCounter.Count(memoryInject);
           var reserveTokens = systemTokens + memoryTokens + 2048 + 2048; // system + memory + max_tokens + buffer
           var historyBudget = (int)_contextSize - reserveTokens;
           if (historyBudget < 500) historyBudget = 500; // minimum history
           
           // Trim history from the front if it exceeds the budget
           while (windowMessages.Count > 2)
           {
               var histTokens = 0;
               foreach (var m in windowMessages) histTokens += _tokenCounter.Count(m.Content);
               if (histTokens <= historyBudget) break;
               windowMessages.RemoveAt(0); // remove oldest
           }
           _logger?.Debug("Context", $"Prompt budget: system={systemTokens}, memory={memoryTokens}, history_budget={historyBudget}, msgs={windowMessages.Count}");

             // ── Step 4: Build the final prompt text ────────────────
             var sb = new StringBuilder();

               // System prompt + tool definitions
            sb.AppendLine(systemBlock);
          sb.AppendLine();

             // Memory injection (if any relevant entries found)
           if (!string.IsNullOrEmpty(memoryInject))
                {
                   sb.AppendLine("> PERSISTENT MEMORY — These are past decisions, patterns, and lessons that may help you:\n");
                sb.AppendLine(memoryInject);
                   sb.AppendLine();
               }

              // v10: Project context injection
              if (!string.IsNullOrEmpty(projectCtx))
              {
                  sb.AppendLine(projectCtx);
                  sb.AppendLine();
              }

              // v10: Task progress injection
              if (!string.IsNullOrEmpty(taskProgress))
                  sb.AppendLine(taskProgress);

              // v10: Failure history injection (when agent is struggling)
              if (!string.IsNullOrEmpty(failureCtx))
                  sb.AppendLine(failureCtx);

              // Conversation history (windowed, summarized if needed)
             if (windowMessages.Count > 0)
                  {
                    sb.AppendLine("> PREVIOUS TURNS — READ CAREFULLY BEFORE RESPONDING:");
                     foreach (var msg in windowMessages)
                         {
                             // Build history with proper XML-style tags for LLM consumption
                            string prefix;
                            switch (msg.Role)
                                   {
                                case "user":
                                    prefix = "<user>";
                                    break;
                                case "assistant":
                                    // v10.12: Wrap assistant history in <lm> container
                                    // so the model sees its own past responses in the correct format
                                    prefix = "<assistant><lm>";
                                    break;
                                case "tool_output":
                                    prefix = $"<tooloutput>{msg.Source}<result>";
                                    break;
                                default:
                                    prefix = "[" + msg.Role + "]";
                                    break;
                                   };
                            sb.AppendLine(prefix);
                            sb.AppendLine(msg.Content);
                                  // Close block based on role
                             switch (msg.Role)
                                     {
                                case "user":
                                    sb.AppendLine("</user>");
                                    break;
                                case "assistant":
                                    sb.AppendLine("</lm></assistant>");
                                    break;
                                case "tool_output":
                                    sb.AppendLine("</result></tooloutput>");
                                    break;
                                default:
                                    sb.AppendLine("</user>");
                                    break;
                                     }
                                 }
                            }
               // userPrompt is already stored in contextWindow and rendered above
               // No need to duplicate it at the end of this method

               // ── Step 6: Context-aware directive ─────────────────────────────
               var hasToolResults = windowMessages.Any(m => m.Role == "tool_output");
               var toolResultCount = windowMessages.Count(m => m.Role == "tool_output");
               
               if (hasToolResults)
               {
                   if (toolResultCount >= 3)
                   {
                       // Multiple tool calls done — push toward final answer
                       sb.AppendLine("-- Open <lm><thinking>brief</thinking> then <output>answer</output></lm> if done, or <lm><thinking>brief</thinking><toolcall>...</toolcall></lm> if more data needed. You have run " + toolResultCount + " tool calls. --");
                   }
                   else
                   {
                       // 1-2 tool calls done — allow continuing if needed
                       sb.AppendLine("-- Open <lm><thinking>brief</thinking> then <output>answer</output></lm> if done, or <lm><thinking>brief</thinking><toolcall>...</toolcall></lm> if you need more data. Tool results above. --");
                   }
               }
              else
                    {
                       sb.AppendLine("-- Open <lm><thinking>brief</thinking><toolcall>ToolName<arg>value</arg></toolcall></lm> then STOP. --");
                          }

             // v10.15.4: Open <assistant> tag to cue the model to START generating.
             // The model must output <lm> itself — this forces full tag structure
             // and makes the token stream show both opening and closing tags.
             sb.AppendLine("<assistant>");

             return sb.ToString();
                  }


           /// <summary>Query EMemoryManager for relevant memories and format them for prompt injection.
        /// This is called every turn to give the agent context from past sessions.</summary>
    private string? GetMemoryInjection(string query)
          {
            var sb = new StringBuilder();

            // v9.10: Semantic search via vector memory
            if (_vectorMemory != null && _vectorMemory.IsInitialized && _vectorMemory.Count > 0)
            {
                try
                {
                    var vecResults = _vectorMemory.SearchAsTextAsync(query, maxResults: 3).GetAwaiter().GetResult();
                    if (!vecResults.StartsWith("(No semantic"))
                        sb.AppendLine(vecResults);
                }
                catch { }
            }

            // Keyword search via traditional memory
            if (_memoryManager != null)
            {
                var results = _memoryManager.Query(query, maxResults: 5);
                if (!string.IsNullOrEmpty(results) && !results.StartsWith("(No memories"))
                    sb.AppendLine(results);
            }

            return sb.Length > 0 ? sb.ToString().Trim() : null;
                  }

      /// <summary>Register a tool for the LLM to call.</summary>
   public void RegisterTool(EToolBase tool)
           {
               _tools.Add(tool);
             _out?.WriteInfo($"[Tool] Registered: {tool.Name}");
                }

   /// <summary>Register an ITool implementation (wrapped via ToolAdapter).</summary>
   public void RegisterTool(ECAssistant.Interfaces.ITool tool)
   {
       var adapter = new ToolAdapter(tool);
       _tools.Add(adapter);
       _out?.WriteInfo($"[Tool] Registered: {tool.Name}");
   }

      /// <summary>Add tool result to both transcript and context window.</summary>
      public virtual void AddToolResult(string toolName, string output)
        {
           // v10.5.1: Escape angle brackets in tool output to prevent fake XML tags
           // in conversation history that would break ExtractCleanResponse and ParseLLMDecision.
           // v10.8: Truncate (smart per-tool limit) + escape tool output
           var safeOutput = EscapeToolOutput(TruncateToolOutput(output, toolName));
           
           // Add to transcript AND context window (unified — no legacy string list)
             _transcript.AddToolOutput(safeOutput, toolName);
              _contextWindow.AddToolOutput(safeOutput, toolName);
              
              // v9.8: Auto-save transcript on every tool call to prevent data loss on crash
              try
              {
                  var transcriptPath = Path.Combine(_workingDir, "transcript.json");
                  _transcript.SaveToDisk(transcriptPath);
              }
              catch { /* don't crash on save failure */ }
              }

      /// <summary>Escape < and > in tool output to prevent fake XML tags in history.
      /// Safety net — ensures all tool output is escaped even if a tool forgets.</summary>
    private string EscapeToolOutput(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return text.Replace("<", "&lt;").Replace(">", "&gt;");
    }

    // v10.8: Tool output truncation — smart limits based on tool type.
    // Code/search tools get more room than shell commands.
    private const int MaxToolOutputDefault = 4000;      // General shell commands
    private const int MaxToolOutputCode = 8000;         // Code editor, file research (code content)
    private const int MaxToolOutputSearch = 6000;        // Web search, RAG results

    // v10.8.2: Full output store — when output exceeds the limit, the full
    // output is saved to disk and the LLM gets a truncated version + instructions
    // to retrieve specific parts. This prevents context bloat while keeping
    // the full data available for chained tasks.
    private readonly Dictionary<string, string> _toolOutputStore = new();
    private int _outputStoreCounter = 0;
    private const int MaxStoredOutputs = 20;  // v10.8.3: Prevent unbounded memory growth

    /// <summary>Truncate tool output based on tool type. If output exceeds the limit,
    /// store the full output and give the LLM a way to retrieve specific parts.</summary>
    private string TruncateToolOutput(string text, string toolName = "")
    {
        if (string.IsNullOrEmpty(text)) return text;

        var limit = toolName.ToLowerInvariant() switch
        {
            "ecodeeditor" => MaxToolOutputCode,
            "efileresearchtool" => MaxToolOutputCode,
            "ewebsearch" => MaxToolOutputSearch,
            _ => MaxToolOutputDefault
        };

        if (text.Length <= limit) return text;

        // v10.8.2: Store full output and give LLM a retrieval handle
        _outputStoreCounter++;
        var storeKey = $"output_{_outputStoreCounter}";
        _toolOutputStore[storeKey] = text;
        
        // v10.8.3: Evict oldest stored outputs if too many
        if (_toolOutputStore.Count > MaxStoredOutputs)
        {
            var oldestKey = _toolOutputStore.Keys.OrderBy(k => k).FirstOrDefault();
            if (oldestKey != null) _toolOutputStore.Remove(oldestKey);
        }

        // Save to disk for large outputs (crash recovery + memory)
        try
        {
            var outputPath = Path.Combine(_workingDir, $"tool_outputs/{storeKey}.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllText(outputPath, text);
        }
        catch { /* non-critical */ }

        var truncated = text.Substring(0, limit);
        truncated += $"\n\n[OUTPUT STORED: {text.Length} total chars. Full output saved as {storeKey}.]";
        truncated += $"\nTo see more, use: EShellAgent command=Get-Content tool_outputs/{storeKey}.txt -TotalCount N | Select-Object -Skip M";
        truncated += $"\nOr read a specific part: Get-Content tool_outputs/{storeKey}.txt | Select-Object -Skip {limit/80} -First 50";
        return truncated;
    }

    /// <summary>Check if a stored output exists and return it (or a portion).</summary>
    public string? GetStoredOutput(string key, int offset = 0, int maxChars = 4000)
    {
        if (!_toolOutputStore.TryGetValue(key, out var full)) return null;
        if (offset >= full.Length) return "(Offset beyond output length)";
        var available = full.Length - offset;
        var take = Math.Min(maxChars, available);
        var result = full.Substring(offset, take);
        if (take < available)
            result += $"\n[Showing {take}/{available} chars from offset {offset}. Use higher offset to see more.]";
        return result;
    }

      /// <summary>Remove the last assistant response from history (for format retries).</summary>
     // v10.15: Hybrid approach — fast LoadState rewind as default, full rebuild as fallback.
     // _savedStateBeforeGen is captured BEFORE InferAsync feeds the incremental input,
     // so LoadState should give a clean state without the input tokens. If LoadState
     // fails or has been failing repeatedly, fall back to full ResetAndRebuildCacheAsync.
     private int _consecutiveRewindFailures = 0;
     private const int MaxRewindFailures = 2;  // After this, force full rebuild

     public virtual async Task RemoveLastAssistantResponseAsync()
     {
         _contextWindow.RemoveLastAssistantMessage();
         // Also remove from transcript
         for (int i = _transcript.Messages.Count - 1; i >= 0; i--)
         {
             if (_transcript.Messages[i].Role == "assistant")
             {
                 _transcript.Messages.RemoveAt(i);
                 break;
             }
         }

         // v10.15: Try fast LoadState rewind first (default path)
         bool rewindOK = false;
         if (_consecutiveRewindFailures < MaxRewindFailures && _savedStateBeforeGen != null && _executor != null)
         {
             try
             {
                 await _executor!.LoadState(_savedStateBeforeGen);
                 rewindOK = true;
                 _consecutiveRewindFailures = 0;  // reset on success
               _out?.WriteInfo("[KVCache] Rewound to pre-generation state (format retry, fast path).");
             }
             catch (Exception ex)
             {
                 _consecutiveRewindFailures++;
                 _logger?.Warn("KVCache", $"LoadState rewind failed (attempt {_consecutiveRewindFailures}/{MaxRewindFailures}): {ex.Message}");
             }
         }

         // Fallback: full KV cache rebuild
         if (!rewindOK)
         {
              _out?.WriteWarning("[KVCache] " + (_consecutiveRewindFailures >= MaxRewindFailures
                 ? $"Rewind failed {_consecutiveRewindFailures}x — forcing full rebuild."
                    : "No saved state — forcing full rebuild."));

             await ResetAndRebuildCacheAsync();

             // Re-feed conversation history from context window into the fresh KV cache.
             var messages = _contextWindow.GetWindowMessages();
             if (messages.Count > 0)
             {
               _out?.WriteInfo($"[KVCache] Re-feeding {messages.Count} conversation messages into rebuilt cache...");
                 var historySb = new StringBuilder();
                 foreach (var msg in messages)
                 {
                     switch (msg.Role)
                     {
                         case "user":
                             historySb.AppendLine("<user>");
                             historySb.AppendLine(msg.Content);
                             historySb.AppendLine("</user>");
                             break;
                         case "assistant":
                             historySb.AppendLine("<assistant><lm>");
                             historySb.AppendLine(msg.Content);
                             historySb.AppendLine("</lm></assistant>");
                             break;
                         case "tool_output":
                             historySb.AppendLine($"<tooloutput>{msg.Source}<result>");
                             historySb.AppendLine(msg.Content);
                             historySb.AppendLine("</result></tooloutput>");
                             break;
                         case "system":
                             historySb.AppendLine($"<system>{msg.Content}</system>");
                             break;
                     }
                 }

                 if (historySb.Length > 0 && _executor != null)
                 {
                     try
                     {
                         using var feedCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                         await foreach (var _ in _executor!.InferAsync(historySb.ToString(), _inferenceParams, feedCts.Token))
                             break; // Just prefill, do not generate
                     }
                     catch (OperationCanceledException)
                     {
                         _out?.WriteWarning("[KVCache] History re-feed timed out (60s) — continuing anyway.");
                     }
                   _out?.WriteSuccess($"[KVCache] Re-fed {messages.Count} messages ({historySb.Length} chars) into cache.");
                 }
             }

             _consecutiveRewindFailures = 0;  // reset after successful rebuild
             _out?.WriteSuccess("[KVCache] Cache rebuilt for format retry (fallback path).");
         }
     }

      /// <summary>Inject a format retry prompt as a user message.</summary>
     public virtual void InjectFormatRetry(string errorMessage)
     {
         _contextWindow.AddUserMessage(errorMessage);
         _transcript.AddUser(errorMessage);
     }

     /// <summary>v10.17: Inject an execution plan as a system message.
     /// The LLM sees this before its first turn and follows the planned tool calls.</summary>
     public void InjectExecutionPlan(string planText)
     {
         _contextWindow.AddSystemMessage(planText);
         _transcript.AddSystem(planText);
         _out?.WriteSuccess("[Plan] Execution plan injected into context.");
     }

      /// <summary>Clear context window and transcript.</summary>
    public virtual void ClearHistory()
           {
               _contextWindow.Clear();
              _transcript.Messages.Clear();
                _turnCount = 0;
             _out?.WriteInfo("[Context] History and transcript cleared.");
                }

    // v10.11.1: Clear only the context window (not transcript) — used after ESC stop
    // so the next command starts fresh without stale messages polluting the prompt.
    public void ClearContextWindowOnly()
    {
        _contextWindow.Clear();
        _turnCount = 0;
           _out?.WriteInfo("[Context] Context window cleared (transcript preserved).");
    }

      /// <summary>Reset the turn counter for a new user request (v10.4.4).
      /// Called by the orchestrator at the start of each ExecuteMultiStep.
      /// This ensures the first GenerateAsync call adds the user message to context.</summary>
    public virtual void ResetTurnCount()
    {
        _turnCount = 0;
    }

    // v10.5: Reset KV cache state for a new user request.
    // Called by the orchestrator at the start of each ExecuteMultiStep.
    // The KV cache keeps the static prefix (system prompt + tools) but
    // the dynamic conversation context is reset.
    // If the cache is getting full, we re-prefill from scratch.
    public virtual void ResetForNewRequest()
    {
        _turnCount = 0;
        _escPressed = false;
        // Note: We do NOT reset _isPrefilled here — the static prefix stays cached.
        // The KV cache still has the system prompt + tools.
        // Only the conversation history (added after prefill) needs to be managed.
    }

    // v10.5: Full KV cache reset + re-prefill.
    // Called when context overflows or when we need a clean slate.
    // v10.8.3: Made async — PrefillStaticPrefix is async and must be awaited.
    public virtual async Task ResetAndRebuildCacheAsync()
    {
        if (_executor == null || _context == null) return;
        
        _out?.WriteWarning("[KVCache] Full reset — rebuilding from scratch...");
        
        // Dispose current context and executor
        try { _context.Dispose(); } catch { }
        
        // Recreate context and executor
        _context = _weights!.CreateContext(_modelParams!);
        var nullLog = new NullLogger();
        _executor = new InteractiveExecutor(_context, nullLog);
        _isPrefilled = false;
        
        // Re-initialize tokenizer
        _tokenCounter.Initialize(_context);
        
        // Re-prefill the static prefix (await!)
        await PrefillStaticPrefix();
        
        _out?.WriteSuccess("[KVCache] Cache rebuilt and prefilled.");
    }

       /// <summary>Generate text from the LLM using incremental KV cache feed (v10.5).</summary>
    /// <param name="userPrompt">The user's original goal/request. Only added to context on turn 1.
    /// On subsequent turns, the context is already populated by AddToolResult + InjectFormatRetry.</param>
    public virtual async Task<string> GenerateAsync(string userPrompt)
           {
               _turnCount++;
            _escPressed = false;  // v10.9.2: Reset ESC flag for this turn

            // v10.4 FIX: Only add user message to context on turn 1.
            if (_turnCount == 1)
            {
                _transcript.AddUser(userPrompt);
                _contextWindow.AddUserMessage(userPrompt);
            }

             _logger?.Debug("Context", $"Turn {_turnCount} | Budget: {_contextWindow.GetTotalTokens()}/{_contextWindow.MaxTokens} tokens");

            // v10.8: KV cache overflow handling — if context is >80% full, rebuild cache
            // with summarized conversation to prevent garbage/crashes on long sessions
            // v10.8.3: Fixed async, capped convText, proper re-feed
            var tokenBudget = _contextWindow.GetTotalTokens();
            var maxBudget = (int)_contextWindow.MaxTokens;
            if (maxBudget > 0 && tokenBudget > maxBudget * 0.8)
            {
                _out?.WriteWarning($"[KVCache] Context at {tokenBudget}/{maxBudget} tokens ({tokenBudget*100/maxBudget}%). Rebuilding cache...");
                
                // v10.12.13: Cap convText relative to secondary model context size (75% of it)
                var allMessages = _contextWindow.GetWindowMessages();
                var convSb = new StringBuilder();
                var convCharLimit = (int)(_secondaryModel?.ContextSize ?? 4096) * 3 / 4;  // 75% of context as chars
                for (int i = allMessages.Count - 1; i >= 0 && convSb.Length < convCharLimit; i--)
                    convSb.Insert(0, $"[{allMessages[i].Role}] {allMessages[i].Content}\n");
                var convText = convSb.ToString();
                
                // Summarize the conversation using secondary model if available
                var summaryText = "";
                if (_secondaryModel != null && _secondaryModel.IsLoaded)
                {
                    summaryText = await _secondaryModel.GenerateAsync(
                        $"Summarize this conversation concisely. Keep facts, decisions, and tool results only. Max 3 sentences. Plain text.\n\n{convText}\n\nSummary:",
                        maxTokens: Math.Max(100, (int)_secondaryModel.ContextSize / 8));
                    summaryText = System.Text.RegularExpressions.Regex.Replace(summaryText, @"<[^>]+>", "");
                }
                
                // Clear context window and rebuild KV cache (await!)
                _contextWindow.Clear();
                await ResetAndRebuildCacheAsync();
                
                // Re-inject summary as context
                if (!string.IsNullOrWhiteSpace(summaryText))
                {
                    _contextWindow.AddSystemMessage($"[Previous conversation summary: {summaryText.Trim()}]");
                    _out?.WriteInfo($"[KVCache] Re-injected summary: {summaryText.Length} chars");
                }
                
                // v10.9.4: After overflow rebuild on turn 2+, re-add the latest tool output
                // and user directive so BuildIncrementalInput can find them.
                // Save references BEFORE clearing, then re-add after rebuild.
                // (The messages were saved before Clear() above — allMessages has them)
                if (_turnCount > 1)
                {
                    var lastToolMsg = allMessages.LastOrDefault(m => m.Role == "tool_output");
                    var lastUserMsg = allMessages.LastOrDefault(m => m.Role == "user");
                    if (lastToolMsg != null)
                        _contextWindow.AddToolOutput(lastToolMsg.Content, lastToolMsg.Source ?? "");
                    if (lastUserMsg != null)
                        _contextWindow.AddUserMessage(lastUserMsg.Content);
                }
                
                // For turn 1, re-add the user message.
                if (_turnCount == 1)
                {
                    _transcript.AddUser(userPrompt);
                    _contextWindow.AddUserMessage(userPrompt);
                }
            }

           // v10.5: Build only the new tokens to feed (not the full prompt)
           var incrementalInput = BuildIncrementalInput(userPrompt);

            try
              {
              _logger?.Debug("Engine", $"Incremental input: {incrementalInput.Length} chars, Turn: {_turnCount}");
              
              // v10.5: Dump incremental input to debug file
              var promptDumpPath = Path.Combine(_workingDir, "last_prompt.txt");
              try { File.WriteAllText(promptDumpPath, $"=== INCREMENTAL INPUT (Turn {_turnCount}) ===\n{incrementalInput}\n\n=== STATIC PREFIX (cached) ===\n{_cachedStaticPrefix ?? "(not prefilled)"}"); } catch { }
              
              if (_logger?.IsDebugEnabled == true)
              {
                   _out?.WriteInfo($"[IncrementalInput] Turn {_turnCount} — {incrementalInput.Length} chars");
                  _out?.WriteDim(new string('=', 60));
                  _out?.WriteDim(incrementalInput);
                  _out?.WriteDim(new string('=', 60));
              }

              var sb = new StringBuilder();

              // v10.8: Save KV cache state before generation for format retry rewind
              try { _savedStateBeforeGen = _executor?.GetStateData(); }
              catch { /* if save fails, rewind won't work but generation continues */ }

             using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
              bool timedOut = false;
                 try
                    {
                    // v10.12.15: Only </lm> is a stop tag. No fallbacks.
                    // </output> removed - if model writes </output> inside content (code, HTML),
                    // it would stop early. If model forgets </lm>, generation runs to max_tokens.
                    var stopTags = new[] { "</lm>" };
                    _out?.WriteLine($"── Token Stream (Turn {_turnCount}) ── [ESC to stop] ──", OutputState.Bold);
                    _out?.StartStream(OutputState.Raw);
                    var tokenCount = 0;
                    await foreach (var token in _executor!.InferAsync(incrementalInput, _inferenceParams, cts.Token))
                         {
                          // ESC / cancellation: stop the stream and bail
                          if (ExecutionToken.IsCancellationRequested)
                          {
                              _escPressed = true;
                              _out?.StopStream();
                              _out?.BlankLine();
                              _out?.WriteError("[Stop] Generation stopped by user (ESC).");
                              goto inferenceDone;
                          }
                          _out?.Write(token);
                           sb.Append(token);
                           tokenCount++;
                           var soFar = sb.ToString();
                           foreach (var stopTag in stopTags)
                           {
                               if (soFar.Contains(stopTag, StringComparison.OrdinalIgnoreCase))
                               {
                                   _out?.BlankLine();
                                   _out?.WriteInfo($"[Stop] Manual anti-prompt hit: {stopTag} (after {tokenCount} tokens)");
                                   goto inferenceDone;
                               }
                           }
                              }
                        inferenceDone:
                    _out?.StopStream();
                    _out?.WriteLine($"── End Token Stream ({tokenCount} tokens) ──", OutputState.Bold);
                    _out?.BlankLine();
                               }
                          catch (OperationCanceledException)
                                 {
                                   timedOut = true;
                                     _out?.StopStream();
                                   _out?.BlankLine();
                                   _out?.WriteError("[Timeout] Inference timed out (90s). Truncating.");
                                           }

              var rawResult = sb.ToString().Trim();
                  string cleanResponse;

              // v10.4.3: Strip leading <assistant> tag if the model echoed it back
              if (rawResult.StartsWith("<assistant>", StringComparison.OrdinalIgnoreCase))
                  rawResult = rawResult.Substring("<assistant>".Length).Trim();
              if (rawResult.EndsWith("</assistant>", StringComparison.OrdinalIgnoreCase))
                  rawResult = rawResult.Substring(0, rawResult.Length - "</assistant>".Length).Trim();
              _out?.WriteDim($"[Engine] Raw ({rawResult.Length} chars): {StringUtil.Truncate(rawResult, 500)}");

                   cleanResponse = ExtractCleanResponse(rawResult);

              _out?.WriteDim($"[Engine] Clean ({cleanResponse.Length} chars): {StringUtil.Truncate(cleanResponse, 500)}");

              if (string.IsNullOrEmpty(cleanResponse))
                  cleanResponse = timedOut ? "(Response truncated — model timed out)" : "(Empty response from model)";

                 // v10.9.2: Don't store partial/cancelled/ESC responses in transcript
                 if (ExecutionToken.IsCancellationRequested || _escPressed)
                 {
                      _out?.WriteWarning($"[Engine] Execution stopped — not storing partial response.");
                     // v10.9.2: Rewind KV cache to before this partial generation
                     if (_savedStateBeforeGen != null && _executor != null)
                     {
                         try
                         {
                             await _executor!.LoadState(_savedStateBeforeGen);
                             _out?.WriteInfo("[KVCache] Rewound to pre-generation state (stopped).");
                         }
                         catch (Exception ex)
                         {
                             _logger?.Warn("KVCache", $"Failed to rewind after stop: {ex.Message}");
                         }
                     }
                     return "(Stopped by user)";
                 }

                 if (!string.IsNullOrEmpty(cleanResponse) && cleanResponse.Contains("<"))
                    {
                        _transcript.AddAssistant(cleanResponse);
                         _contextWindow.AddAssistantMessage(cleanResponse);
                           }

                 _out?.BlankLine();
                  _logger?.Info("Engine", $"Response: {cleanResponse.Length} chars");
                  return cleanResponse;
                    }
              catch (Exception ex)
                   {
               _out?.WriteError("[Error] " + ex.Message);
                     return "[Error] " + ex.Message;
                    }
                 }

       /// <summary>Extract clean LLM response by stripping hallucination noise after </s> or trailing garbage.</summary>
    private string ExtractCleanResponse(string raw)
        {
           if (string.IsNullOrEmpty(raw)) return "";

         // v10.12: Extract content from <lm> container first.
         // Everything outside <lm>...</lm> is noise and is ignored.
         // If no <lm> tag found, fall back to raw (for backwards compat / format retries).
         // v10.22: Fallback regex parser — if tags are malformed (missing >, extra chars),
         // try regex extraction before giving up and returning raw.
         var llmStart = raw.IndexOf("<lm>", StringComparison.OrdinalIgnoreCase);
         var llmEnd = raw.IndexOf("</lm>", StringComparison.OrdinalIgnoreCase);
         
         string content;
         if (llmStart >= 0 && llmEnd >= 0 && llmEnd > llmStart)
         {
             // Extract content between <lm> and </lm>
             content = raw.Substring(llmStart + 4, llmEnd - llmStart - 4).Trim();  // <lm> is 4 chars
             _logger?.Debug("Extract", $"Extracted from <lm> container: {content.Length} chars (noise stripped: {raw.Length - content.Length - 9} chars)");  // <lm>+</lm> = 9 chars
         }
         else if (llmStart >= 0 && llmEnd < 0)
         {
             // <lm> opened but never closed — take everything after <lm>
             content = raw.Substring(llmStart + 4).Trim();  // <lm> is 4 chars
             _logger?.Debug("Extract", $"<lm> opened but not closed — taking rest: {content.Length} chars");
         }
         else
         {
             // v10.22: Fallback regex parser — try to find <lm>-like patterns with malformed tags.
             // Matches: <lm (with missing >), <llm>, <l m>, etc.
             var lmRegex = new System.Text.RegularExpressions.Regex(@"<l?m[^>]*>?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
             var lmMatch = lmRegex.Match(raw);
             if (lmMatch.Success)
             {
                 content = raw.Substring(lmMatch.Index + lmMatch.Length).Trim();
                 // Also try to strip a malformed closing tag
                 var closeRegex = new System.Text.RegularExpressions.Regex(@"</?l?m[^>]*>?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                 content = closeRegex.Replace(content, "").Trim();
                 _logger?.Debug("Extract", $"Fallback regex found malformed <lm> tag at {lmMatch.Index}: extracted {content.Length} chars");
             }
             else
             {
                 // No <lm> container — fall back to raw (format retry / backwards compat)
                 content = raw.Trim();
                 _logger?.Debug("Extract", $"No <lm> container found — using raw: {content.Length} chars");
             }
         }

         // v10.13: Extract ALL <toolcall> blocks + first <thinking> + first <output>.
         // The model can batch multiple toolcalls in one response for parallel execution.
         // We preserve the <lm> inner content structure for the orchestrator to parse.

         _logger?.Debug("Extract", $"Content length: {content.Length}");

         // Find the first <thinking> block
         var thinkStart = content.IndexOf("<thinking>", StringComparison.OrdinalIgnoreCase);
         var thinkEnd = thinkStart >= 0 
             ? content.IndexOf("</thinking>", thinkStart + 10, StringComparison.OrdinalIgnoreCase) 
             : -1;

         // v10.13: Find ALL <toolcall>...</toolcall> blocks
         var toolcallBlocks = new List<(int start, int end)>();
         var searchFrom = thinkEnd >= 0 ? thinkEnd + 11 : 0;
         while (searchFrom < content.Length)
         {
             var tcStart = content.IndexOf("<toolcall>", searchFrom, StringComparison.OrdinalIgnoreCase);
             if (tcStart < 0) break;
             var tcEnd = content.IndexOf("</toolcall>", tcStart + 10, StringComparison.OrdinalIgnoreCase);
             if (tcEnd < 0)
             {
                 // No close — take rest of content
                 toolcallBlocks.Add((tcStart, content.Length));
                 break;
             }
             toolcallBlocks.Add((tcStart, tcEnd + 11));  // </toolcall> is 11 chars
             searchFrom = tcEnd + 11;  // skip past </toolcall>
         }

         // Find <output> block — search from after thinking (or from start if no thinking)
         // v10.13.1: Don't search from after last toolcall — model might write <output> before <toolcall>
         var outputSearchFrom = thinkEnd >= 0 ? thinkEnd + 11 : 0;
         var outputStart = content.IndexOf("<output>", outputSearchFrom, StringComparison.OrdinalIgnoreCase);
         int? outputEnd = null;
         if (outputStart >= 0)
         {
             var oc = content.IndexOf("</output>", outputStart + 8, StringComparison.OrdinalIgnoreCase);
             outputEnd = oc >= 0 ? oc + 9 : content.Length;  // </output> is 9 chars
         }

         // Check if we found an <output> before any <toolcall> (takes priority)
         var firstToolcallStart = toolcallBlocks.Count > 0 ? toolcallBlocks[0].start : int.MaxValue;
         bool hasOutputFirst = outputStart >= 0 && outputStart < firstToolcallStart;

         var sb = new StringBuilder();

         // Include thinking block if found
         if (thinkStart >= 0 && thinkEnd >= 0)
         {
             var thinkContent = content.Substring(thinkStart, thinkEnd + 11 - thinkStart).Trim();
             sb.AppendLine(thinkContent);
         }

         if (hasOutputFirst)
         {
             // <output> came before any <toolcall> — this is a direct answer
             var outputLen = outputEnd!.Value - outputStart;
             sb.Append(content.Substring(outputStart, outputLen).Trim());
         }
         else if (toolcallBlocks.Count > 0)
         {
             // v10.13: Include ALL <toolcall> blocks
             foreach (var (tcS, tcE) in toolcallBlocks)
             {
                 var blockContent = content.Substring(tcS, tcE - tcS).Trim();
                 sb.AppendLine(blockContent);
             }
             _logger?.Debug("Extract", $"Extracted {toolcallBlocks.Count} <toolcall> blocks");
         }
         else if (outputStart >= 0)
         {
             // <output> found (after thinking, no toolcalls)
             var outputLen = outputEnd!.Value - outputStart;
             sb.Append(content.Substring(outputStart, outputLen).Trim());
         }
         else if (thinkStart < 0)
         {
             // No thinking, no toolcall, no output — return content as-is (will be caught as invalid by orchestrator)
             return content.Trim();
         }

         var result = sb.ToString().Trim();
         _logger?.Debug("Extract", $"Output: {result.Length} chars, starts with: {StringUtil.Truncate(result, 80)}");
         return result;
           }


    public string QueryMemory(string s, int maxResults = 5)
        => (_memoryManager == null) ? "(Not initialized)" : _memoryManager.Query(s, maxResults: maxResults);

   public void SaveMemory(string k, string c, string cat = "general")
      { if (_memoryManager == null) _memoryManager = new EMemoryManager(); _memoryManager.AddEntry(k, c, cat); }

     /// <summary>Load memory manager from disk (called during constructor now).</summary>
   public void LoadContext()
        {
           if (_memoryManager != null) _memoryManager.Load();
         _out?.WriteInfo("[Memory] Loaded.");
             }

      /// <summary>Save memory manager to disk.</summary>
    public void SaveContext()
            { if (_memoryManager != null) _memoryManager.Save(); }

      /// <summary>Save transcript to disk for session resumption.</summary>
    public void SaveTranscript(string? path = null)
           {
             var p = path ?? Path.Combine(_workingDir, "transcript.json");
                _transcript.SaveToDisk(p);
          _out?.WriteInfo($"[Context] Transcript saved ({_transcript.MessageCount} messages, {_contextWindow.GetTotalTokens()} tokens).");
               }

   public async ValueTask DisposeAsync()
        {
           try { _context?.Dispose(); } catch { }
           if (!_sharesWeights) { try { _weights?.Dispose(); } catch { } }
              foreach (var t in _tools) { if (t is IDisposable d) d.Dispose(); }
                _out?.WriteInfo("[Exit] Engine disposed.");
                    }
         }
