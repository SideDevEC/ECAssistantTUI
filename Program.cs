using static ECAssistant.EColor;

using ECAssistant.Config;
using ECAssistant.Engine;
using ECAssistant.Orchestration;
using ECAssistant.Tools.PowerShell;
using ECAssistant.Tools.Research;
using ECAssistant.Tools.FileOps;
using ECAssistant.Tools;
using ECAssistant.Analysis;
using ECAssistant.UI;
using ECAssistant.Session;
using LLama.Common;
using LLama.Sampling;

namespace ECAssistant;

public class Program
{
    private static EAgentConfig _config = null!;
    public static EGuiBase Gui = null!;   // the UI instance, set once in Main()

[System.STAThread]
    public static async Task<int> Main(string[] args)
            {
                 // ── UI initialisation — single line, swap anywhere ──
             Gui = new EGuiConsole();

            EColor.TagBold(Cyan, "ECAssistant", "llama-sharp 0.27.0");
             EColor.TagBold(Cyan, "Tools", "PowerShell - Extensible");
              Gui.BlankLine();

                 // Always use user's home directory for appsettings.json
            var userConfigDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ECAssistant");
            Directory.CreateDirectory(userConfigDir);

            var configPath = Path.Combine(userConfigDir, "appsettings.json");
            if (!File.Exists(configPath))
                     {
                    EColor.Tag(EColor.Info(), "Setup", "No appsettings.json found. Creating default.");
                      Gui.WriteLine($"             {userConfigDir}");
                    var defaults = new EAgentConfig();
                 File.WriteAllText(configPath, System.Text.Json.JsonSerializer.Serialize(
                         defaults, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                     }

                 _config = EAgentConfig.Load(configPath);
              EColor.Tag(EColor.Success(), "Config", $"Loaded from: {Path.GetFullPath(configPath)}");
            ApplyCommandLineArgs(ref _config, args);

             Gui.BlankLine();
             EColor.TagBold(Cyan, "Config", "Summary:");
              ShowConfigSummary();
               Gui.BlankLine();

            var effectiveModelPath = Path.GetFullPath(_config.Llm.ModelPath);
           if (!File.Exists(effectiveModelPath))
                      {
                    EColor.TagBold(EColor.Error(), "Error", $"Model not found: {effectiveModelPath}");
                      EColor.Tag(EColor.Info(), "Hint", "Update appsettings.json or pass --model argument.");
                     }

            var effectiveDir = Path.GetFullPath(_config.AgentSettings.WorkingDirectory);
              Directory.CreateDirectory(effectiveDir);

           var rootDir = Path.GetFullPath(_config.RootPath);
            Directory.CreateDirectory(rootDir);
               Directory.CreateDirectory(Path.Combine(rootDir, _config.Memory.DataPath));
            Directory.CreateDirectory(Path.Combine(rootDir, _config.Workspace.Path));
              EColor.TagBold(EColor.Info(), "Root", $"{rootDir}");
              Gui.BlankLine();

                // Build inference params from config — ALL values come from appsettings.json
            var inferenceParams = new InferenceParams
                   {
                MaxTokens = _config.Inference.MaxTokens,
                    AntiPrompts = _config.Inference.AntiPrompts.Length > 0
                            ? _config.Inference.AntiPrompts
                              : new string[] { "</s>" },
                  SamplingPipeline = new DefaultSamplingPipeline
                             {
                      Temperature = _config.Sampling.Temperature,
                       TopP = _config.Sampling.TopP,
                        TopK = _config.Sampling.TopK,
                       RepeatPenalty = _config.Sampling.RepeatPenalty
                              }
                    };

            if (File.Exists(effectiveModelPath))
                       {
                    EColor.TagBold(EColor.Success(), "Model", "Loaded successfully.");
                    var agent = new EAgentEngine(
                         modelPath: effectiveModelPath,
                        contextSize: _config.Llm.ContextSize,
                         gpuLayers: _config.Llm.GpuLayers,
                        threadCount: _config.Llm.Threads,
                          inferenceParams: inferenceParams
                           );

                   agent.LoadContext();
                    agent.WireSummaryService(); // Wire LLM-based context compaction

                     EColor.TagBold(EColor.Info(), "Init", "Registering tools...");
                    var psAgent = new EPowerShellAgent(effectiveDir);
                      agent.RegisterTool(psAgent);

                       {
                          // Use config values — no hardcoded defaults
                        var researchExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                               { ".cs", ".md", ".json", ".txt", ".xml", ".ps1", ".sln",
                                ".csproj", ".config", ".sql", ".html", ".css", ".js" };

                          // Override from config if available
                        var toolConfig = _config.Tools.EFileResearchTool;
                         foreach (var ext in toolConfig.DefaultExtensions)
                             researchExtensions.Add(ext);

                         agent.RegisterTool(new EFileResearchTool(
                             effectiveDir, defaultExtensions: researchExtensions,
                              maxCharsPerFile: toolConfig.MaxCharsPerFile));
                        }

                    // ── File Operation Tools (P0: proper file ops) ──
                    agent.RegisterTool(new EFileReadTool(effectiveDir));
                    agent.RegisterTool(new EFileWriteTool(effectiveDir));
                    agent.RegisterTool(new EFileEditTool(effectiveDir));
                    agent.RegisterTool(new EFileCopyTool(effectiveDir));
                    agent.RegisterTool(new EDirListTool(effectiveDir));
                    agent.RegisterTool(new EFileSearchTool(effectiveDir));

                    EColor.TagBold(EColor.Info(), "Init", $"Tools: {string.Join(", ", agent.Tools.Select(t => t.Name))}");
                      Gui.BlankLine();

                        // Orchestrator limits from config — all flow through appsettings
                    var maxTurns = _config.ContextManagement.KeepLast; // reuse keep_last as turn limit
                    if (maxTurns <= 0) maxTurns = 5;
                    var maxFailures = _config.ContextManagement.ShiftGuardrailThreshold;
                      if (maxFailures <= 0) maxFailures = 3;

                    var orchestrator = new AgentOrchestrator(agent, maxTurns: maxTurns, maxFailures: maxFailures, toolPolicy: new ToolPolicy());

                    // ── Session Manager (P0: session abstraction) ──
                    var sessionManager = new SessionManager(agent, orchestrator.Policy);
                    EColor.TagBold(EColor.Info(), "Session", $"Main session created. Sessions: {sessionManager.List().Count}");

                   await RunAgentLoop(agent, orchestrator, effectiveDir, psAgent, sessionManager);
                        }
            else
                     {
                    EColor.Tag(EColor.Error(), "Error", "No model loaded. Use --model or update appsettings.json.");
                  return 1;
                     }

           // Save transcript on exit (cleanup via async)
             var transPath = Path.Combine(AppContext.BaseDirectory, "transcript.json");
            if (File.Exists(transPath))
               {
                // Transcript already auto-saved in loop via "save-context" command
                 Program.Gui.WriteLineColored($"[Context] Transcript saved at: {transPath}");
                }

            return 0;
             }

    private static void ShowConfigSummary()
            {
            EColor.WriteLine(Yellow + Bold, $"   Model: {_config.Llm.ModelPath}");
              EColor.WriteLine(Yellow + Bold, $"   ContextSize: {_config.Llm.ContextSize} tokens");
            EColor.WriteLine(Yellow + Bold, $"   GPU Layers: {_config.Llm.GpuLayers}/100");
             EColor.WriteLine(Yellow + Bold, $"   Threads: {(_config.Llm.Threads == -1 ? "auto" : _config.Llm.Threads.ToString())}");
            EColor.WriteLine(Yellow + Bold, $"   Max Tokens per turn: {_config.Inference.MaxTokens}");
              EColor.WriteLine(Yellow + Bold, $"   Temperature: {_config.Sampling.Temperature}");
             EColor.WriteLine(Yellow + Bold, $"   Working Directory: {Path.GetFullPath(_config.AgentSettings.WorkingDirectory)}");
           EColor.WriteLine(Yellow + Bold, $"   Allowed Extensions: {string.Join(", ", _config.AgentSettings.AllowedExtensions)}");
           }

    private static void ApplyCommandLineArgs(ref EAgentConfig config, string[] args)
             {
            for (int i = 0; i < args.Length; i++)
                      {
                    var arg = args[i].ToLower().TrimStart('-');
                     switch (arg)
                               {
                            case "model":   if (i + 1 < args.Length) config.Llm.ModelPath = args[++i]; break;
                            case "dir":     if (i + 1 < args.Length) config.AgentSettings.WorkingDirectory = Path.GetFullPath(args[++i]); break;
                             case "ctx":
                            case "contextsize":
                                  if (i + 1 < args.Length && uint.TryParse(args[++i], out uint ctx)) config.Llm.ContextSize = ctx;
                               break;
                            case "gpu":
                              case "gpulayers":
                                case "gpu_layers":
                                  if (i + 1 < args.Length && int.TryParse(args[++i], out int layers)) config.Llm.GpuLayers = Math.Clamp(layers, 0, 100);
                                break;
                               case "threads":
                                case "threadcount":
                                     if (i + 1 < args.Length && int.TryParse(args[++i], out int thr)) config.Llm.Threads = thr;
                              break;
                               case "temp":
                                case "temperature":
                                    if (i + 1 < args.Length && float.TryParse(args[++i], out float t)) config.Sampling.Temperature = Math.Clamp(t, 0.0f, 2.0f);
                              break;
                                   }
                           }
                  }

    private static async Task RunAgentLoop(
        EAgentEngine agent,
           AgentOrchestrator orchestrator,
         string workingDir,
        EPowerShellAgent psAgent,
        SessionManager sessionManager)
              {
            Gui.BlankLine();
             EColor.TagBold(Cyan, "ECLoop", "Type your request (help | quit)");
                Gui.WriteLine("===========================================");
           EColor.TagBold(EColor.Info(), "Mode", "The agent decides tools automatically.");
            EColor.TagBold(Info(), "Hint", "Use <thinking>... </thinking>, then <toolcall>... or <output>");
             Gui.BlankLine();

                // Show transcript status if resuming
              var transPath = Path.Combine(AppContext.BaseDirectory, "transcript.json");
               if (File.Exists(transPath))
                     {
                 var trans = ConversationTranscript.LoadFromDisk(transPath);
                  if (trans != null)
                   {
                      EColor.Tag(EColor.Info(), "Context", $"Resuming session: {trans.MessageCount} messages loaded from transcript.");
                       Gui.BlankLine();
                    }
                }

            while (true)
                        {
                    Gui.WriteRaw(Cyan + "> " + Reset);
                  var input = Gui.PromptRaw(Cyan + "> " + Reset)?.Trim();

                   if (string.IsNullOrEmpty(input)) continue;

                   switch (input.ToLower())
                                 {
                           case "quit":   case "exit":
                              EColor.TagBold(EColor.Info(), "Bye", "Goodbye.");
                               Gui.BlankLine();
                            // Save transcript before exit
                             var path = Path.Combine(AppContext.BaseDirectory, "transcript.json");
                             agent.SaveTranscript(path);
                                 return;
                           case "help":    await PrintHelp(); continue;
                              case "tools":    ListTools(agent); continue;
                               case "clear-history":  agent.ClearHistory(); continue;
                            case "save-context":  { var p = Path.Combine(AppContext.BaseDirectory, "transcript.json"); agent.SaveTranscript(p); Gui.BlankLine(); } continue;

                         case "single":    EColor.Tag(Info(), "Mode", "Single-turn mode reset."); orchestrator.Reset(); continue;

                                 // --- FILE PICKER: Open native OS dialog, read file as prompt ---
                              case "file-pick":
                                          {
                                      var pickResult = await EPowerShellAgent.FilePickerPromptAsync();
                                      if (pickResult == null)
                                                 {
                                    EColor.Tag(Error(), "Pick", "No file selected or cancelled.");
                                  continue;
                                          }

                                       EColor.TagBold(Success(), "Pick", $"File loaded: {pickResult.Length} chars");
                                         Gui.WriteLine($"--- PREVIEW (first {_config.Tools.EFileResearchTool.MaxCharsPerFile} chars) ---");
                                     Gui.WriteLine(pickResult.Substring(0, Math.Min(pickResult.Length, _config.Tools.EFileResearchTool.MaxCharsPerFile)));
                                          if (pickResult.Length > _config.Tools.EFileResearchTool.MaxCharsPerFile) Gui.WriteLine("... [truncated]");
                                      Gui.WriteLine("--- END PREVIEW ---");
                                           Gui.BlankLine();

                                              // Send the file content as the prompt to the agent
                                       EColor.TagBold(Cyan, "Agent", "Sending file content to LLM...");
                                         var ticFile = DateTime.Now;
                                        try
                                                    {
                                            var orchestratorResult = await orchestrator.ExecuteMultiStep(pickResult);
                                         Gui.BlankLine();
                                         var maxTurnsDisplayA = _config.ContextManagement.KeepLast > 0 ? _config.ContextManagement.KeepLast : 5;
                                              EColor.TagBold(Success(), "Agent", $"Turns: {orchestratorResult.ToolCallsMade}/{maxTurnsDisplayA} | Status: {orchestratorResult.Status}");
                                            if (!string.IsNullOrEmpty(orchestratorResult.FinalOutput))
                                             EColor.WriteLine(Bold, orchestratorResult.FinalOutput);
                                          }
                                        catch (Exception ex)
                                                     {
                                         EColor.TagBold(Error(), "Error", ex.Message);
                                           if (ex.InnerException != null) EColor.Tag(Info(), "Detail", ex.InnerException.Message);
                                              }

                                          if (_config.Interface.ShowElapsedTime)
                                                    {
                                           var elapsed = DateTime.Now - ticFile;
                                              EColor.WriteLine(Dim, $"[<took {elapsed.TotalMilliseconds:N0}ms>]");
                                         }
                                     continue;
                                     }

                                case "memory-save": {
                                   var key = Gui.PromptRaw("Enter memory key: ")?.Trim() ?? "";
                                 if (string.IsNullOrEmpty(key)) { EColor.Tag(Info(), "Memory", "Empty key - nothing saved."); continue; }
                              var content = Gui.PromptRaw("Enter content: ")?.Trim() ?? "";
                                   if (!string.IsNullOrEmpty(content)) { agent.SaveMemory(key, content, "user"); EColor.Tag(Success(), "Memory", "Saved."); }
                                    else { EColor.Tag(Info(), "Memory", "Empty content - nothing saved."); }
                                     continue;
                                       }

                               case "memory-query": {
                                   var query = Gui.PromptRaw("Search query: ")?.Trim() ?? "";
                                  if (!string.IsNullOrEmpty(query)) { EColor.WriteLine(Bold, agent.QueryMemory(query)); }
                                     else { EColor.Tag(Info(), "Memory", "Empty query - nothing found."); }
                                    continue;
                                       }

                                 case "memory-stats": {
                                var stats = agent.Memory.GetStats();
                                  EColor.WriteLine(Bold, stats); continue;
                                       }

                                case "clear-context":  {             // New: Clear context window (not just transcript)
                                      agent.ClearHistory();
                                     Gui.BlankLine();
                                     EColor.Tag(Success(), "Context", "Window cleared. Agent starts fresh with no history.");
                                    continue;
                                        }

                           case "analyze-project":
                              case "context-analyze": {
                               EColor.TagBold(Info(), "Analyzer", "Starting cross-file analysis...");
                                  var analyzer = new ECAssistant.Analysis.EContextAnalyzer(workingDir);
                                   await analyzer.AnalyzeProjectAsync();
                                    EColor.WriteLine(Bold, analyzer.GetDebugContextSummary());
                               continue;
                                     }

                            case "interactive-decision": {
                                 EColor.TagBold(Cyan, "Decision", "Starting...");
                                var decisionLoop = new ECAssistant.Engine.EDecisionLoop(agent);
                                  await decisionLoop.ExecuteInteractiveLoop("Analyze project structure.");
                               continue;
                                   }

                           // ── Session Commands (P0) ──
                            case "sessions": case "session-list": {
                                Gui.BlankLine();
                                EColor.TagBold(Cyan, "Sessions", sessionManager.GetStatusReport());
                                continue;
                            }
                            case "session-status": {
                                Gui.BlankLine();
                                EColor.TagBold(Cyan, "Main Session", sessionManager.Main.GetStatusSummary());
                                continue;
                            }
                            case "session-create": {
                                var name = Gui.PromptRaw("Session name: ")?.Trim() ?? "";
                                if (!string.IsNullOrEmpty(name)) {
                                    var s = sessionManager.CreateNamed(name);
                                    EColor.TagBold(EColor.Success(), "Session", $"Created named session: {s.Key}");
                                }
                                continue;
                            }
                            case "session-cleanup": {
                                await sessionManager.CleanupAsync();
                                EColor.TagBold(EColor.Info(), "Session", "Cleaned up idle/isolated sessions.");
                                continue;
                            }

                           case "?":  case "/?": PrintUsage(); continue;

                         default: break;
                        }

                var ticMain = DateTime.Now;

                 try
                                 {
                              var orchestratorResult = await orchestrator.ExecuteMultiStep(input);
                                Gui.BlankLine();
                             var maxTurnsDisplayB = _config.ContextManagement.KeepLast > 0 ? _config.ContextManagement.KeepLast : 5;
                               EColor.TagBold(Success(), "Agent", $"Turns: {orchestratorResult.ToolCallsMade}/{maxTurnsDisplayB} | Status: {orchestratorResult.Status}");
                             if (!string.IsNullOrEmpty(orchestratorResult.FinalOutput))
                                EColor.WriteLine(Bold, orchestratorResult.FinalOutput);
                              }
                    catch (Exception ex)
                                 {
                            EColor.TagBold(Error(), "Error", ex.Message);
                               if (ex.InnerException != null) EColor.Tag(Info(), "Detail", ex.InnerException.Message);
                             }

                    if (_config.Interface.ShowElapsedTime)
                                  {
                           var elapsed = DateTime.Now - ticMain;
                              EColor.WriteLine(Dim, $"[<took {elapsed.TotalMilliseconds:N0}ms>]");
                            }
                   }
             }

    private static async Task PrintHelp()
              {
               Gui.BlankLine();
             EColor.TagBold(Cyan, "Commands", "");
                  EColor.WriteLine(Yellow + Bold, "         <type your request>   Multi-step agent execution");
            EColor.WriteLine(Yellow + Bold, "    quit / exit           Exit the program (saves transcript)");
              EColor.WriteLine(Yellow + Bold, "       help                  Show this help text");
            EColor.WriteLine(Yellow + Bold, "        tools                 List registered tools");
             EColor.WriteLine(Yellow + Bold, "     clear-history         Clear conversation history only");
            EColor.WriteLine(Yellow + Bold, "      save-context          Save transcript to disk for session resumption");
             EColor.WriteLine(Yellow + Bold, "    memory-save / query / stats    Memory commands");
                EColor.WriteLine(Yellow + Bold, "   file-pick             Open native file picker dialog, read file as prompt");
                EColor.WriteLine(Yellow + Bold, "   sessions             List all sessions");
                EColor.WriteLine(Yellow + Bold, "   session-status        Show main session status");
                EColor.WriteLine(Yellow + Bold, "   session-create        Create a named session");
                EColor.WriteLine(Yellow + Bold, "   session-cleanup       Clean up idle/isolated sessions");
             Gui.BlankLine();
            EColor.Tag(Info(), "Response", "Agent uses XML-style tags: <thinking>, <toolcall>, <output>.");
             }

    private static void ListTools(EAgentEngine agent)
            {
              Gui.BlankLine();
             EColor.TagBold(Cyan, "Tools", "Registered tools:");
            foreach (var tool in agent.Tools)
                     {
                 EColor.WriteLine(Yellow + Bold, $"         - {tool.Name}");
                EColor.WriteLine(Dim, $"          {tool.Description.Substring(0, Math.Min(tool.Description.Length, 120))}");
                  Gui.BlankLine();
               }
             }

     private static void PrintUsage()
              {
                 Gui.BlankLine();
             EColor.TagBold(Cyan, "Usage", "Type a request and the agent will decide tools automatically.");
                EColor.Tag(Info(), "Hint", "Use <thinking> then <toolcall> or <output>. See help for commands.");
                 }
          }
