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
    private InteractiveExecutor? _executor;
    private readonly List<EToolBase> _tools = new();
    private EMemoryManager? _memoryManager = null;
    private VectorMemoryStore? _vectorMemory = null;
    private SelfCorrectionManager? _selfCorrection = null;
    private ProjectContextManager? _projectContext = null;
    private TaskPlanner? _taskPlanner = null;

       // ── Context Window (replaces raw string list) ───────────
    private readonly ContextWindow _contextWindow;
    private readonly ConversationTranscript _transcript;
    private readonly InferenceParams _inferenceParams;
    private int _turnCount = 0;
    private string? _systemPromptText;

       // Inference params — always come from config
    private readonly uint _contextSize;
    private readonly int _gpuLayers;
    private readonly int _threads;

    public EMemoryManager Memory => _memoryManager ??= new EMemoryManager();
    public VectorMemoryStore? VectorMemory => _vectorMemory;
    public SelfCorrectionManager? SelfCorrection => _selfCorrection;
    public ProjectContextManager? ProjectContext => _projectContext;
    public TaskPlanner? TaskPlanner => _taskPlanner;
    public IReadOnlyList<EToolBase> Tools => _tools;
    public int TurnCount => _turnCount;
    public ConversationTranscript Transcript => _transcript;
    public ContextWindow ContextWindow => _contextWindow;

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
            // Use the engine to generate a summary from the given prompt
            var sb = new StringBuilder();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                await foreach (var token in _executor!.InferAsync(prompt, _inferenceParams, cts.Token))
                    sb.Append(token);
            }
            catch (OperationCanceledException)
            {
                // Timeout - return what we have
            }
            var result = sb.ToString().Trim();
            // Strip any XML tags - summaries should be plain text
            result = System.Text.RegularExpressions.Regex.Replace(result, @"<[^>]+>", "");
            return string.IsNullOrWhiteSpace(result) ? "(Summary generation failed)" : result;
        }));
        Program.Gui.WriteLineColored("[Context] SummaryService wired to LLM engine.");
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
           // Add to transcript AND context window (unified — no legacy string list)
             _transcript.AddToolOutput(output, toolName);
              _contextWindow.AddToolOutput(output, toolName);
              
              // v9.8: Auto-save transcript on every tool call to prevent data loss on crash
              try
              {
                  var transcriptPath = Path.Combine(_workingDir, "transcript.json");
                  _transcript.SaveToDisk(transcriptPath);
              }
              catch { /* don't crash on save failure */ }
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

       /// <summary>Generate text from the LLM, with conversation context.</summary>
    public async Task<string> GenerateAsync(string userPrompt)
           {
               _turnCount++;

            // Add user message to transcript + context window (unified — no separate string list)
              _transcript.AddUser(userPrompt);
                _contextWindow.AddUserMessage(userPrompt);

             Logger.Debug("Context", $"Turn {_turnCount} | Budget: {_contextWindow.GetTotalTokens()}/{_contextWindow.MaxTokens} tokens");
            Logger.Debug("Context", $"Turn {_turnCount} | Budget: {_contextWindow.GetTotalTokens()}/{_contextWindow.MaxTokens} tokens");

           var fullPrompt = BuildFullPrompt(userPrompt);

            try
              {
              Logger.Debug("Engine", $"Prompt: {fullPrompt.Length} chars, Turn: {_turnCount}");

              var sb = new StringBuilder();


             using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
              bool timedOut = false;
                 try
                    {
                    // v9.2: Manual anti-prompt enforcement — break when we see closing tags
                    // LLamaSharp's built-in anti-prompt matching may not catch all cases
                    // with tokenized tags like </toolcall>. We check the accumulated output.
                    var stopTags = new[] { "</toolcall>", "</output>" };
                    Program.Gui.WriteRawDirect(EColor.Dim);
                    await foreach (var token in _executor.InferAsync(fullPrompt, _inferenceParams, cts.Token))
                         {
                          Program.Gui.WriteRawDirect(token);
                           sb.Append(token);
                           // Check if accumulated output contains a stop tag
                           var soFar = sb.ToString();
                           foreach (var stopTag in stopTags)
                           {
                               if (soFar.Contains(stopTag, StringComparison.OrdinalIgnoreCase))
                               {
                                   Program.Gui.BlankLine();
                                   EColor.TagBold(EColor.Info(), "Stop", $"Manual anti-prompt hit: {stopTag}");
                                   goto inferenceDone;
                               }
                           }
                              }
                        inferenceDone:
                    Program.Gui.WriteRawDirect(EColor.Reset);
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

              // v9.19: Show raw model output on console for debugging
              EColor.WriteLine(EColor.Dim, $"[Engine] Raw ({rawResult.Length} chars): {rawResult.Substring(0, Math.Min(rawResult.Length, 300))}");

                     // Extract clean response — ONLY the last well-formed structured block:
                     // Prefer <output>...</output> or <toolcall>...</toolcall>
                  // Strip everything outside structural tags (hallucination noise).
                   cleanResponse = ExtractCleanResponse(rawResult);

              // v9.19: Show clean response on console for debugging
              EColor.WriteLine(EColor.Dim, $"[Engine] Clean ({cleanResponse.Length} chars): {cleanResponse.Substring(0, Math.Min(cleanResponse.Length, 300))}");

              if (string.IsNullOrEmpty(cleanResponse))
                  cleanResponse = timedOut ? "(Response truncated — model timed out)" : "(Empty response from model)";

                 // Store in transcript + context window (unified — no duplicate list)
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
