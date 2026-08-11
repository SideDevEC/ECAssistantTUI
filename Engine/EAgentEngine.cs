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
    public bool IsEnabled(LogLevel logLevel) => false;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? ex, Func<TState, Exception?, string> formatter) { }
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
    public IReadOnlyList<EToolBase> Tools => _tools;
    public int TurnCount => _turnCount;
    public ConversationTranscript Transcript => _transcript;
    public ContextWindow ContextWindow => _contextWindow;

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
     public EAgentEngine(string modelPath, uint contextSize, int gpuLayers, int threadCount, InferenceParams inferenceParams)
          {
               // Enable native library logging to verify which backend was loaded (CUDA vs CPU)
           NativeLibraryConfig.All.WithLogCallback(delegate (LLamaLogLevel level, string message)
                {
                 var prefix = level switch
                   {
                      LLamaLogLevel.Debug => "[DEBUG]",
                     LLamaLogLevel.Info => "[INFO]",
                       LLamaLogLevel.Error => "[ERROR]",
                          _ => "[LLAMA]"
                    };
                 Program.Gui.LogInternal($"[LLAMA {prefix}] {message}");
                });

              // Check CUDA availability at startup for logging
           var sysInfo = SystemInfo.Get();
          if (sysInfo.OSPlatform != null)
                {
                var cudaVer = sysInfo.CudaMajorVersion;
                  if (cudaVer == -1)
                      Program.Gui.LogInternal("[CUDA] No CUDA detected — will run on CPU");
                     else
                        Program.Gui.LogInternal($"[CUDA] Detected: CUDA {cudaVer}");
                        }

               // Report which GPU backends are available
           try
                {
                 Program.Gui.LogInternal(SystemInfo.Get().ToString());
                    }
                catch
                    {
                          // Best-effort: don't fail startup if system info read fails
                         }

           var parameters = new ModelParams(modelPath)
                     {
                       GpuLayerCount = Math.Clamp(gpuLayers, 0, 100),
                          ContextSize = contextSize,
                           };

              _contextSize = contextSize;
               _gpuLayers = gpuLayers;
               _threads = threadCount;
                  _inferenceParams = inferenceParams;

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
             var transcriptPath = Path.Combine(AppContext.BaseDirectory, "transcript.json");
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
                          }
                    }
              catch (Exception ex)
                   {
                   Program.Gui.LogInternal($"[Context] Failed to load transcript: {ex.Message}");
                        }
                    }

            EColor.TagBold(Cyan, "Engine", $"Model loaded: {modelPath}");
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
                var sysPromptPath = Path.Combine(AppContext.BaseDirectory, "SystemPrompt.md");
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

           // ── Step 3: Get windowed history from ContextWindow ───
           var windowMessages = _contextWindow.GetWindowMessages();

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

               // ── Step 6: Stop directive ─────────────────────────────
               var hasToolResults = windowMessages.Any(m => m.Role == "tool_output");
                if (hasToolResults)
                     {
                     sb.AppendLine("-- CRITICAL: Tool results are in history above. Produce your final answer with <output>. DO NOT call more tools -- you already have the data. --");
                         }
              else
                    {
                       sb.AppendLine("-- CRITICAL: Use ONE tool call then STOP. When the result returns, produce your final answer with <output>. --");
                          }

             return sb.ToString();
                  }


           /// <summary>Query EMemoryManager for relevant memories and format them for prompt injection.
        /// This is called every turn to give the agent context from past sessions.</summary>
    private string? GetMemoryInjection(string query)
          {
            if (_memoryManager == null) return null;

              // Query memory with keywords extracted from user request
           var results = _memoryManager.Query(query, maxResults: 5);

             // If no memories match or still empty, nothing to inject
           if (string.IsNullOrEmpty(results) || results.StartsWith("(No memories found for:") || results == "(Not initialized)")
               return null;

              // Return formatted memory for injection — the LLM will use these as context
              return results.Trim();
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

             Program.Gui.WriteLineColored($"[Context] Turn {_turnCount} | Budget: {_contextWindow.GetTotalTokens()}/{_contextWindow.MaxTokens} tokens");

           var fullPrompt = BuildFullPrompt(userPrompt);

            try
              {
              EColor.TagBold(EColor.Info(), "Engine", $"Prompt: {fullPrompt.Length} chars, Turn: {_turnCount}");
               EColor.TagBold(EColor.Info(), "Send", "Sending to model...");
                Program.Gui.WriteRawDirect(EColor.Dim + "[Engine] Prompt:" + EColor.Reset + " ");
                 Program.Gui.WriteRawDirect(fullPrompt);
                  Program.Gui.BlankLine();

              var sb = new StringBuilder();
               Program.Gui.WriteRawDirect(EColor.Model() + "[Model]" + EColor.Reset);
                Program.Gui.BlankLine();

             using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
              bool timedOut = false;
                 try
                    {
                    await foreach (var token in _executor.InferAsync(fullPrompt, _inferenceParams, cts.Token))
                         {
                          Program.Gui.WriteRawDirect(EColor.Token() + token + EColor.Reset);
                           sb.Append(token);
                              }
                               }
                          catch (OperationCanceledException)
                                 {
                                   timedOut = true;
                                     Program.Gui.BlankLine();
                                       EColor.TagBold(EColor.Error(), "Timeout", "Inference timed out (90s). Truncating.");
                                           }

              var rawResult = sb.ToString().Trim();
                  string cleanResponse;

              // Debug: show raw model output for troubleshooting
              Program.Gui.WriteLineColored($"[Engine] Raw output ({rawResult.Length} chars): {rawResult.Substring(0, Math.Min(rawResult.Length, 500))}");

                     // Extract clean response — ONLY the last well-formed structured block:
                     // Prefer <output>...</output> or <toolcall>...</toolcall>
                  // Strip everything outside structural tags (hallucination noise).
                   cleanResponse = ExtractCleanResponse(rawResult);

              Program.Gui.WriteLineColored($"[Engine] Clean response ({cleanResponse.Length} chars): {cleanResponse.Substring(0, Math.Min(cleanResponse.Length, 500))}");

              if (string.IsNullOrEmpty(cleanResponse))
                  cleanResponse = timedOut ? "(Response truncated — model timed out)" : "(Empty response from model)";

                 // Store in transcript + context window (unified — no duplicate list)
             if (!string.IsNullOrEmpty(cleanResponse) && cleanResponse.Contains("<"))
                    {
                        _transcript.AddAssistant(cleanResponse);
                         _contextWindow.AddAssistantMessage(cleanResponse);
                           }

                 Program.Gui.BlankLine();
                  EColor.TagBold(EColor.Success(), "Done", $"Response generated: {cleanResponse.Length} chars");
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

            // All reserved block tags — these are the ONLY structured elements we extract
         var openTags = new[]
            {
              "<thinking>", "</thinking>",
             "<toolcall>", "</toolcall>",
                "<output>", "</output>",
                "<user>", "</user>",
               "<tooloutput>", "</tooloutput>"
               };

         // Find ALL tag positions in order of appearance
          var allTags = new List<(string Tag, int Position)>();
         foreach (var tag in openTags)
              {
                 int pos = 0;
                  while ((pos = raw.IndexOf(tag, pos, StringComparison.OrdinalIgnoreCase)) >= 0)
                         {
                        // Avoid duplicates — same tag at same position
                         bool exists = false;
                            foreach (var existing in allTags) {
                              if (existing.Position == pos && string.Equals(existing.Tag, tag, StringComparison.OrdinalIgnoreCase)) {
                                  exists = true;
                                     break;
                                        }
                              }
                          if (!exists)
                               allTags.Add((tag, pos));
                           pos += tag.Length;
                      }
                    }

            // Sort all found tags by position ascending (first to last in text)
            allTags.Sort((a, b) => a.Position.CompareTo(b.Position));

         if (allTags.Count == 0) return raw.Trim();

         // Map open tags to their matching close tags
          var closingMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "<thinking>", "</thinking>" },
                  { "<toolcall>", "</toolcall>" },
                   { "<output>", "</output>" },
               { "<user>", "</user>" },
                 { "<tooloutput>", "</tooloutput>" }
             };

            // Extract every complete block in order
          var resultParts = new List<string>();
         int nextSearchStart = 0;

        foreach (var (tag, tagPos) in allTags)
                {
             if (tagPos < nextSearchStart) continue; // already consumed by previous block

                 string closeTag;
              if (!closingMap.TryGetValue(tag, out closeTag))
                     continue; // Not a recognized open tag, skip

                   int closePos = raw.IndexOf(closeTag, tagPos + tag.Length, StringComparison.OrdinalIgnoreCase);

             if (closePos > tagPos + tag.Length)
                 {
                   // Found matching close — extract the complete block
                 var blockLen = closePos - tagPos + closeTag.Length;
                     resultParts.Add(raw.Substring(tagPos, blockLen).Trim());
                         nextSearchStart = closePos + closeTag.Length;
                              }
                   else
                       {
                        // Opening tag but no matching close — extract rest of text from this point
                          var blockText = raw.Substring(tagPos).Trim();
                               if (!string.IsNullOrEmpty(blockText))
                                   resultParts.Add(blockText);
                                       }
                                  }

         return resultParts.Count > 0 ? string.Join(" ", resultParts) : raw.Trim();
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
             var p = path ?? Path.Combine(AppContext.BaseDirectory, "transcript.json");
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
