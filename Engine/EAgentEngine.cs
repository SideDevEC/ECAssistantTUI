using static ECAssistant.EColor;
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

namespace ECAssistant.Engine;

internal sealed class NullLogger : ILogger
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


public sealed class EAgentEngine : IAsyncDisposable
{
    private LLamaWeights? _weights;
    private LLamaContext? _context;
    private ModelParams? _modelParams;
    // v10.5: Switched to InteractiveExecutor for KV cache reuse.
    // Static prefix (system prompt + tools) is prefilled once at session start.
    // Only new tokens (user msg, tool output, directives) are fed per turn.
    // KV cache persists across turns — major performance improvement.
    private InteractiveExecutor? _executor;
    private readonly List<EToolBase> _tools = new();
    private EMemoryManager? _memoryManager = null;
    private VectorMemoryStore? _vectorMemory = null;
    private SelfCorrectionManager? _selfCorrection = null;
    private ProjectContextManager? _projectContext = null;
    private TaskPlanner? _taskPlanner = null;
    private SecondaryModelLoader? _secondaryModel = null;  // v10.7: for LLM-based decomposition + summarization

       // ── Context Window (replaces raw string list) ───────────
    private readonly ContextWindow _contextWindow;
    private readonly ConversationTranscript _transcript;
    private readonly InferenceParams _inferenceParams;
    private int _turnCount = 0;
    private string? _systemPromptText;

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
    
    // v10.9: Cancellation token for stopping execution mid-stream
    private CancellationTokenSource? _cts;
    public CancellationToken ExecutionToken => _cts?.Token ?? CancellationToken.None;
    public bool IsExecuting => _cts != null;
    
    public void StartExecution() { _cts = new CancellationTokenSource(TimeSpan.FromSeconds(300)); }
    public void StopExecution() { _cts?.Cancel(); _cts = null; }
    public void EndExecution() { _cts = null; }

    /// <summary>Initialize self-correction manager.</summary>
    public void InitializeSelfCorrection(string workingDir)
    {
        _selfCorrection = new SelfCorrectionManager(workingDir);
        EColor.TagBold(EColor.Success(), "SelfCorrect", "Self-correction manager ready.");
    }

    /// <summary>Initialize project context manager and scan project.</summary>
    public async Task InitializeProjectContextAsync(string workingDir)
    {
        _projectContext = new ProjectContextManager(workingDir);
        await _projectContext.InitializeAsync();
        EColor.TagBold(EColor.Success(), "ProjectCtx", $"Project context loaded: {_projectContext.FileCount} files.");
    }

    /// <summary>Initialize task planner for this session.</summary>
    public void InitializeTaskPlanner()
    {
        _taskPlanner = new TaskPlanner();
    }

    /// <summary>Set the secondary model for decomposition + summarization (v10.7).</summary>
    public void SetSecondaryModel(SecondaryModelLoader secondary)
    {
        _secondaryModel = secondary;
        EColor.TagBold(EColor.Success(), "Secondary", "Secondary model attached to engine.");
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
        _vectorMemory = new VectorMemoryStore(storeDir);
        
        Func<string, Task<float[]>> embeddingGenerator = async (text) =>
        {
            await Task.CompletedTask;
            return TfidfEmbed(text);
        };
        
        await _vectorMemory.InitializeAsync(embeddingGenerator);
        EColor.TagBold(EColor.Success(), "VecMem", $"Vector memory ready: {_vectorMemory.Count} entries in {storeDir}");
    }

    /// <summary>Simple TF-IDF style embedding — no external dependencies.</summary>
    private static float[] TfidfEmbed(string text)
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
                var summary = await _secondaryModel.GenerateAsync(prompt, maxTokens: 200);
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
        Program.Gui.WriteLineColored($"[Context] SummaryService wired to {mode}.");
    }

       /// <summary>Create engine with context window support and auto-injected memory.</summary>
     private string _workingDir = "";

public EAgentEngine(string modelPath, uint contextSize, int gpuLayers, int threadCount, InferenceParams inferenceParams, string workingDir = "")
          {
               // Enable native library logging to verify which backend was loaded (CUDA vs CPU)
           NativeLibraryConfig.All.WithLogCallback(delegate (LLamaLogLevel level, string message)
                {
                 // v9.4: Only log LLAMA errors to console, skip debug/info spam
                if (level == LLamaLogLevel.Error)
                    Program.Gui.LogInternal($"[LLAMA ERROR] {message}");
                });

              // Check CUDA availability at startup for logging
           var sysInfo = SystemInfo.Get();
          if (sysInfo.OSPlatform != null)
                {
                var cudaVer = sysInfo.CudaMajorVersion;
                  if (cudaVer == -1)
                      Logger.Info("CUDA", "No CUDA detected — will run on CPU");
                     else
                        Logger.Info("CUDA", $"Detected: CUDA {cudaVer}");
                        }

               // Report which GPU backends are available
           // v9.4: Suppress SystemInfo dump on startup
           try { Logger.Debug("System", SystemInfo.Get().ToString()); } catch { }

           var parameters = new ModelParams(modelPath)
                     {
                       GpuLayerCount = Math.Clamp(gpuLayers, 0, 100),
                          ContextSize = contextSize,
                           };

              _contextSize = contextSize;
               _gpuLayers = gpuLayers;
               _threads = threadCount;
                  _inferenceParams = inferenceParams;

               _workingDir = string.IsNullOrEmpty(workingDir) ? AppContext.BaseDirectory : workingDir;

               _weights = LLamaWeights.LoadFromFile(parameters);
                _context = _weights.CreateContext(parameters);
          var nullLog = new NullLogger();
            // v10.5: InteractiveExecutor with KV cache reuse.
            // Static prefix is prefilled once, then only new tokens per turn.
            _modelParams = parameters;
            _executor = new InteractiveExecutor(_context, nullLog);

             // ── Initialize tokenizer for accurate token counting ───
           if (_context != null) TokenCounter.Initialize(_context);

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
                              Program.Gui.WriteLineColored($"[Context] Loaded {loaded.MessageCount} messages from previous session.");
                            // v9.9: Show compact summary of previous session
                            var userMsgs = loaded.Messages.Where(m => m.Role == "user").TakeLast(3);
                            if (userMsgs.Any())
                            {
                                EColor.Tag(EColor.Info(), "Last session", string.Join(" | ", userMsgs.Select(m => m.Content.Substring(0, Math.Min(m.Content.Length, 60)))));
                            }
                          }
                    }
              catch (Exception ex)
                   {
                   Program.Gui.LogInternal($"[Context] Failed to load transcript: {ex.Message}");
                        }
                    }

            EColor.TagBold(Cyan, "Engine", $"Model loaded: {modelPath}");
            Logger.Info("Engine", $"Model loaded: {modelPath} | Context: {contextSize} | GPU: {gpuLayers} | Threads: {threadCount}");
             EColor.TagBold(EColor.Info(), "Config", $"ContextSize: {contextSize} tokens | GPU Layers: {_gpuLayers}");

           // Load memory and show how many entries are active
               _memoryManager.Load();
               if (_memoryManager.Count > 0)
                 {
                  EColor.TagBold(EColor.Info(), "Memory", $"Active memories loaded: {_memoryManager.Count}");
                      }
              else
                    {
                    EColor.Tag(EColor.Info(), "Memory", "No prior memory entries found (first session).");
                        }

             // ── Load system prompt from SystemPrompt.md at startup ────────────
            try
             {
                var sysPromptPath = Path.Combine(_workingDir, "SystemPrompt.md");
                if (!File.Exists(sysPromptPath)) sysPromptPath = Path.Combine(AppContext.BaseDirectory, "SystemPrompt.md"); // fallback to build dir
                 if (File.Exists(sysPromptPath))
                      {
                         _systemPromptText = File.ReadAllText(sysPromptPath);
                          Program.Gui.WriteLineColored($"[Config] System prompt loaded from: SystemPrompt.md ({_systemPromptText.Length} chars)");
                      }
                    else
                         {
                           Program.Gui.LogInternal("[!] SystemPrompt.md not found — using empty system prompt.");
                              _systemPromptText = "";
                            }
                          }
             catch (Exception ex)
                  {
                       Program.Gui.LogInternal($"[!] Failed to load SystemPrompt.md: {ex.Message}");
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
    public async Task PrefillStaticPrefix()
    {
        if (_isPrefilled || _executor == null) return;

        _cachedStaticPrefix = BuildSystemToolsPrompt();

        EColor.TagBold(EColor.Info(), "KVCache", $"Prefilling static prefix ({_cachedStaticPrefix.Length} chars)...");
        var startMs = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;

        // Feed the static prefix through the executor as a "prompt run".
        // This populates the KV cache. We don't need the output — just the cache state.
        var sb = new StringBuilder();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            // The anti-prompts will stop generation after the static prefix.
            // We just need the prefill to happen — any generated tokens are discarded.
            await foreach (var token in _executor.InferAsync(_cachedStaticPrefix, _inferenceParams, cts.Token))
            {
                sb.Append(token);
                // Stop early if the model tries to generate content (we just want prefill)
                if (sb.ToString().Contains("\n", StringComparison.Ordinal))
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            EColor.TagBold(EColor.Warn(), "KVCache", "Prefill timed out (120s) — continuing anyway.");
        }

        var elapsedMs = (long)((DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond) - startMs);
        _isPrefilled = true;
        EColor.TagBold(EColor.Success(), "KVCache", $"Static prefix prefilled in {elapsedMs}ms. KV cache active.");
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
            // Feed memory injection (if any) + user message + assistant cue.
            var memoryInject = GetMemoryInjection(userRequest);
            var projectCtx = GetProjectContextInjection(userRequest);
            var taskProgress = GetTaskProgressInjection();
            var failureCtx = GetFailureInjection();

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
            sb.AppendLine("-- Use ONE tool call per response. After the result returns, decide: give <output> if done, or call another tool if needed. --");

            // Generation cue
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
                sb.AppendLine($"-- You have run {toolResultCount} tool calls. If you have enough information, give your final answer with <output>. Only call another tool if you still need more data. --");
            }
            else
            {
                sb.AppendLine("-- Tool results are in history above. If you have the answer, use <output>. If you need another tool call to complete the task, you may call one more. --");
            }

            // Generation cue
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
           var systemTokens = TokenCounter.Count(systemBlock);
           var memoryTokens = string.IsNullOrEmpty(memoryInject) ? 0 : TokenCounter.Count(memoryInject);
           var reserveTokens = systemTokens + memoryTokens + 2048 + 2048; // system + memory + max_tokens + buffer
           var historyBudget = (int)_contextSize - reserveTokens;
           if (historyBudget < 500) historyBudget = 500; // minimum history
           
           // Trim history from the front if it exceeds the budget
           while (windowMessages.Count > 2)
           {
               var histTokens = 0;
               foreach (var m in windowMessages) histTokens += TokenCounter.Count(m.Content);
               if (histTokens <= historyBudget) break;
               windowMessages.RemoveAt(0); // remove oldest
           }
           Logger.Debug("Context", $"Prompt budget: system={systemTokens}, memory={memoryTokens}, history_budget={historyBudget}, msgs={windowMessages.Count}");

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
                                    prefix = "<assistant>";
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
                                    sb.AppendLine("</assistant>");
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
                       sb.AppendLine("-- You have run " + toolResultCount + " tool calls. If you have enough information, give your final answer with <output>. Only call another tool if you still need more data. --");
                   }
                   else
                   {
                       // 1-2 tool calls done — allow continuing if needed
                       sb.AppendLine("-- Tool results are in history above. If you have the answer, use <output>. If you need another tool call to complete the task, you may call one more. --");
                   }
               }
              else
                    {
                       sb.AppendLine("-- Use ONE tool call per response. After the result returns, decide: give <output> if done, or call another tool if needed. --");
                          }

             // v10.4.3: Open <assistant> tag to cue the model to START generating.
             // Without this, the model sees history ending with <user>...</user> and
             // echoes it instead of producing its own response. The open tag tells
             // the model: "now it's your turn to respond as the assistant."
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
            EColor.TagBold(Cyan, "Tool", $"Registered: {tool.Name}");
                }

      /// <summary>Add tool result to both transcript and context window.</summary>
      public void AddToolResult(string toolName, string output)
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
    private static string EscapeToolOutput(string text)
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
        truncated += $"\nTo see more, use: EPowerShellAgent command=Get-Content tool_outputs/{storeKey}.txt -TotalCount N | Select-Object -Skip M";
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
     public void RemoveLastAssistantResponse()
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
         // v10.8: Rewind KV cache to before the bad generation
         if (_savedStateBeforeGen != null && _executor != null)
         {
             try
             {
                 _executor.LoadState(_savedStateBeforeGen);
                 EColor.TagBold(EColor.Info(), "KVCache", "Rewound to pre-generation state (format retry).");
             }
             catch (Exception ex)
             {
                 Logger.Warn("KVCache", $"Failed to rewind KV cache: {ex.Message}");
             }
         }
     }

      /// <summary>Inject a format retry prompt as a user message.</summary>
     public void InjectFormatRetry(string errorMessage)
     {
         _contextWindow.AddUserMessage(errorMessage);
         _transcript.AddUser(errorMessage);
     }

      /// <summary>Clear context window and transcript.</summary>
    public void ClearHistory()
           {
               _contextWindow.Clear();
              _transcript.Messages.Clear();
                _turnCount = 0;
            Program.Gui.WriteLineColored("[Context] History and transcript cleared.");
                }

      /// <summary>Reset the turn counter for a new user request (v10.4.4).
      /// Called by the orchestrator at the start of each ExecuteMultiStep.
      /// This ensures the first GenerateAsync call adds the user message to context.</summary>
    public void ResetTurnCount()
    {
        _turnCount = 0;
    }

    // v10.5: Reset KV cache state for a new user request.
    // Called by the orchestrator at the start of each ExecuteMultiStep.
    // The KV cache keeps the static prefix (system prompt + tools) but
    // the dynamic conversation context is reset.
    // If the cache is getting full, we re-prefill from scratch.
    public void ResetForNewRequest()
    {
        _turnCount = 0;
        // Note: We do NOT reset _isPrefilled here — the static prefix stays cached.
        // The KV cache still has the system prompt + tools.
        // Only the conversation history (added after prefill) needs to be managed.
    }

    // v10.5: Full KV cache reset + re-prefill.
    // Called when context overflows or when we need a clean slate.
    // v10.8.3: Made async — PrefillStaticPrefix is async and must be awaited.
    public async Task ResetAndRebuildCacheAsync()
    {
        if (_executor == null || _context == null) return;
        
        EColor.TagBold(EColor.Warn(), "KVCache", "Full reset — rebuilding from scratch...");
        
        // Dispose current context and executor
        try { _context.Dispose(); } catch { }
        
        // Recreate context and executor
        _context = _weights!.CreateContext(_modelParams!);
        var nullLog = new NullLogger();
        _executor = new InteractiveExecutor(_context, nullLog);
        _isPrefilled = false;
        
        // Re-initialize tokenizer
        TokenCounter.Initialize(_context);
        
        // Re-prefill the static prefix (await!)
        await PrefillStaticPrefix();
        
        EColor.TagBold(EColor.Success(), "KVCache", "Cache rebuilt and prefilled.");
    }

       /// <summary>Generate text from the LLM using incremental KV cache feed (v10.5).</summary>
    /// <param name="userPrompt">The user's original goal/request. Only added to context on turn 1.
    /// On subsequent turns, the context is already populated by AddToolResult + InjectFormatRetry.</param>
    public async Task<string> GenerateAsync(string userPrompt)
           {
               _turnCount++;

            // v10.4 FIX: Only add user message to context on turn 1.
            if (_turnCount == 1)
            {
                _transcript.AddUser(userPrompt);
                _contextWindow.AddUserMessage(userPrompt);
            }

             Logger.Debug("Context", $"Turn {_turnCount} | Budget: {_contextWindow.GetTotalTokens()}/{_contextWindow.MaxTokens} tokens");

            // v10.8: KV cache overflow handling — if context is >80% full, rebuild cache
            // with summarized conversation to prevent garbage/crashes on long sessions
            // v10.8.3: Fixed async, capped convText, proper re-feed
            var tokenBudget = _contextWindow.GetTotalTokens();
            var maxBudget = (int)_contextWindow.MaxTokens;
            if (maxBudget > 0 && tokenBudget > maxBudget * 0.8)
            {
                EColor.TagBold(EColor.Warn(), "KVCache", $"Context at {tokenBudget}/{maxBudget} tokens ({tokenBudget*100/maxBudget}%). Rebuilding cache...");
                
                // v10.8.3: Cap convText to fit secondary model context (4096)
                // Take last N messages that fit in ~3000 chars
                var allMessages = _contextWindow.GetWindowMessages();
                var convSb = new StringBuilder();
                for (int i = allMessages.Count - 1; i >= 0 && convSb.Length < 3000; i--)
                    convSb.Insert(0, $"[{allMessages[i].Role}] {allMessages[i].Content}\n");
                var convText = convSb.ToString();
                
                // Summarize the conversation using secondary model if available
                var summaryText = "";
                if (_secondaryModel != null && _secondaryModel.IsLoaded)
                {
                    summaryText = await _secondaryModel.GenerateAsync(
                        $"Summarize this conversation concisely. Keep facts, decisions, and tool results only. Max 3 sentences. Plain text.\n\n{convText}\n\nSummary:",
                        maxTokens: 200);
                    summaryText = System.Text.RegularExpressions.Regex.Replace(summaryText, @"<[^>]+>", "");
                }
                
                // Clear context window and rebuild KV cache (await!)
                _contextWindow.Clear();
                await ResetAndRebuildCacheAsync();
                
                // Re-inject summary as context
                if (!string.IsNullOrWhiteSpace(summaryText))
                {
                    _contextWindow.AddSystemMessage($"[Previous conversation summary: {summaryText.Trim()}]");
                    EColor.TagBold(EColor.Info(), "KVCache", $"Re-injected summary: {summaryText.Length} chars");
                }
                
                // v10.8.3: After cache rebuild, BuildIncrementalInput turn 2+ needs
                // the tool output and directive in context window. They're already there
                // from AddToolResult + InjectFormatRetry. The summary + current messages
                // are in the context window. BuildIncrementalInput will read them correctly.
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
              Logger.Debug("Engine", $"Incremental input: {incrementalInput.Length} chars, Turn: {_turnCount}");
              
              // v10.5: Dump incremental input to debug file
              var promptDumpPath = Path.Combine(_workingDir, "last_prompt.txt");
              try { File.WriteAllText(promptDumpPath, $"=== INCREMENTAL INPUT (Turn {_turnCount}) ===\n{incrementalInput}\n\n=== STATIC PREFIX (cached) ===\n{_cachedStaticPrefix ?? "(not prefilled)"}"); } catch { }
              
              if (Logger.IsDebugEnabled)
              {
                  EColor.TagBold(EColor.Info(), "IncrementalInput", $"Turn {_turnCount} — {incrementalInput.Length} chars");
                  EColor.WriteLine(EColor.Dim, new string('=', 60));
                  EColor.WriteLine(EColor.Dim, incrementalInput);
                  EColor.WriteLine(EColor.Dim, new string('=', 60));
              }

              var sb = new StringBuilder();

              // v10.8: Save KV cache state before generation for format retry rewind
              try { _savedStateBeforeGen = _executor.GetStateData(); }
              catch { /* if save fails, rewind won't work but generation continues */ }

             using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
              bool timedOut = false;
                 try
                    {
                    var stopTags = new[] { "</toolcall>", "</output>" };
                    EColor.WriteLine(EColor.Yellow + EColor.Bold, $"── Token Stream (Turn {_turnCount}) ── [ESC to stop] ──");
                    Program.Gui.WriteRawDirect(EColor.Dim);
                    var tokenCount = 0;
                    await foreach (var token in _executor.InferAsync(incrementalInput, _inferenceParams, cts.Token))
                         {
                          // v10.9: Check for ESC key press to stop generation
                          if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape)
                          {
                              Program.Gui.BlankLine();
                              EColor.TagBold(EColor.Error(), "Stop", "Generation stopped by user (ESC).");
                              goto inferenceDone;
                          }
                          // v10.9: Check cancellation token from orchestrator
                          if (ExecutionToken.IsCancellationRequested)
                          {
                              Program.Gui.BlankLine();
                              EColor.TagBold(EColor.Warn(), "Stop", "Execution cancelled by user.");
                              goto inferenceDone;
                          }
                          Program.Gui.WriteRawDirect(token);
                           sb.Append(token);
                           tokenCount++;
                           var soFar = sb.ToString();
                           foreach (var stopTag in stopTags)
                           {
                               if (soFar.Contains(stopTag, StringComparison.OrdinalIgnoreCase))
                               {
                                   Program.Gui.BlankLine();
                                   EColor.TagBold(EColor.Info(), "Stop", $"Manual anti-prompt hit: {stopTag} (after {tokenCount} tokens)");
                                   goto inferenceDone;
                               }
                           }
                              }
                        inferenceDone:
                    Program.Gui.WriteRawDirect(EColor.Reset);
                    EColor.WriteLine(EColor.Yellow + EColor.Bold, $"── End Token Stream ({tokenCount} tokens) ──");
                    Program.Gui.BlankLine();
                               }
                          catch (OperationCanceledException)
                                 {
                                   timedOut = true;
                                     Program.Gui.BlankLine();
                                       EColor.TagBold(EColor.Error(), "Timeout", "Inference timed out (90s). Truncating.");
                                           }

              var rawResult = sb.ToString().Trim();
                  string cleanResponse;

              // v10.4.3: Strip leading <assistant> tag if the model echoed it back
              if (rawResult.StartsWith("<assistant>", StringComparison.OrdinalIgnoreCase))
                  rawResult = rawResult.Substring("<assistant>".Length).Trim();
              if (rawResult.EndsWith("</assistant>", StringComparison.OrdinalIgnoreCase))
                  rawResult = rawResult.Substring(0, rawResult.Length - "</assistant>".Length).Trim();

              EColor.WriteLine(EColor.Dim, $"[Engine] Raw ({rawResult.Length} chars): {rawResult.Substring(0, Math.Min(rawResult.Length, 300))}");

                   cleanResponse = ExtractCleanResponse(rawResult);

              EColor.WriteLine(EColor.Dim, $"[Engine] Clean ({cleanResponse.Length} chars): {cleanResponse.Substring(0, Math.Min(cleanResponse.Length, 300))}");

              if (string.IsNullOrEmpty(cleanResponse))
                  cleanResponse = timedOut ? "(Response truncated — model timed out)" : "(Empty response from model)";

                 if (!string.IsNullOrEmpty(cleanResponse) && cleanResponse.Contains("<"))
                    {
                        _transcript.AddAssistant(cleanResponse);
                         _contextWindow.AddAssistantMessage(cleanResponse);
                           }

                 Program.Gui.BlankLine();
                  Logger.Info("Engine", $"Response: {cleanResponse.Length} chars");
                  return cleanResponse;
                    }
              catch (Exception ex)
                   {
               EColor.TagBold(EColor.Error(), "Error", ex.Message);
                     return "[Error] " + ex.Message;
                    }
                 }

       /// <summary>Extract clean LLM response by stripping hallucination noise after </s> or trailing garbage.</summary>
    private static string ExtractCleanResponse(string raw)
        {
           if (string.IsNullOrEmpty(raw)) return "";

            // v9.1: Only extract the FIRST complete meaningful block to prevent repetition loops.
            // The model sometimes generates multiple <thinking>+<toolcall> blocks in one response.
            // We take only the first <thinking>...</thinking> + first <toolcall> or <output> after it.

         // v9.19: Debug logging in ExtractCleanResponse
         Logger.Debug("Extract", $"Input length: {raw.Length}");
         Logger.Debug("Extract", $"Contains <thinking>: {raw.Contains("<thinking>", StringComparison.OrdinalIgnoreCase)}");
         Logger.Debug("Extract", $"Contains </thinking>: {raw.Contains("</thinking>", StringComparison.OrdinalIgnoreCase)}");
         Logger.Debug("Extract", $"Contains <toolcall>: {raw.Contains("<toolcall>", StringComparison.OrdinalIgnoreCase)}");
         Logger.Debug("Extract", $"Contains </toolcall>: {raw.Contains("</toolcall>", StringComparison.OrdinalIgnoreCase)}");
         Logger.Debug("Extract", $"Contains <output>: {raw.Contains("<output>", StringComparison.OrdinalIgnoreCase)}");
         Logger.Debug("Extract", $"Contains </output>: {raw.Contains("</output>", StringComparison.OrdinalIgnoreCase)}");

         // Find the first <thinking> block
         var thinkStart = raw.IndexOf("<thinking>", StringComparison.OrdinalIgnoreCase);
         var thinkEnd = thinkStart >= 0 
             ? raw.IndexOf("</thinking>", thinkStart + 10, StringComparison.OrdinalIgnoreCase) 
             : -1;

         // Find the first <toolcall> or <output> AFTER the thinking block (or from start if no thinking)
         var searchStart = thinkEnd >= 0 ? thinkEnd + 11 : 0;

         var toolcallStart = raw.IndexOf("<toolcall>", searchStart, StringComparison.OrdinalIgnoreCase);
         var outputStart = raw.IndexOf("<output>", searchStart, StringComparison.OrdinalIgnoreCase);

         // Determine which comes first: toolcall or output
         int blockStart = -1;
         string blockTag = "";
         string blockCloseTag = "";

         if (toolcallStart >= 0 && outputStart >= 0)
         {
             if (toolcallStart < outputStart) { blockStart = toolcallStart; blockTag = "<toolcall>"; blockCloseTag = "</toolcall>"; }
             else { blockStart = outputStart; blockTag = "<output>"; blockCloseTag = "</output>"; }
         }
         else if (toolcallStart >= 0) { blockStart = toolcallStart; blockTag = "<toolcall>"; blockCloseTag = "</toolcall>"; }
         else if (outputStart >= 0) { blockStart = outputStart; blockTag = "<output>"; blockCloseTag = "</output>"; }

         var sb = new StringBuilder();

         // Include thinking block if found
         if (thinkStart >= 0 && thinkEnd >= 0)
         {
             var thinkContent = raw.Substring(thinkStart, thinkEnd + 11 - thinkStart).Trim();
             sb.AppendLine(thinkContent);
         }

         // Include the action block (toolcall or output) if found
         if (blockStart >= 0)
         {
             var closePos = raw.IndexOf(blockCloseTag, blockStart + blockTag.Length, StringComparison.OrdinalIgnoreCase);
             if (closePos >= 0)
             {
                 var blockLen = closePos - blockStart + blockCloseTag.Length;
                 sb.Append(raw.Substring(blockStart, blockLen).Trim());
             }
             else
             {
                 // Opening tag but no close — take rest of text
                 sb.Append(raw.Substring(blockStart).Trim());
             }
         }
         else if (thinkStart < 0)
         {
             // No thinking, no toolcall, no output — return raw (will be caught as invalid by orchestrator)
             return raw.Trim();
         }

         var result = sb.ToString().Trim();
         Logger.Debug("Extract", $"Output: {result.Length} chars, starts with: {result.Substring(0, Math.Min(result.Length, 80))}");
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
         EColor.TagBold(EColor.Info(), "Memory", "Loaded.");
             }

      /// <summary>Save memory manager to disk.</summary>
    public void SaveContext()
            { if (_memoryManager != null) _memoryManager.Save(); }

      /// <summary>Save transcript to disk for session resumption.</summary>
    public void SaveTranscript(string? path = null)
           {
             var p = path ?? Path.Combine(_workingDir, "transcript.json");
                _transcript.SaveToDisk(p);
          Program.Gui.WriteLineColored($"[Context] Transcript saved ({_transcript.MessageCount} messages, {_contextWindow.GetTotalTokens()} tokens).");
               }

   public async ValueTask DisposeAsync()
        {
           try { _context?.Dispose(); } catch { }
              foreach (var t in _tools) { if (t is IDisposable d) d.Dispose(); }
                EColor.TagBold(EColor.Info(), "Exit", "Engine disposed.");
                    }
         }
