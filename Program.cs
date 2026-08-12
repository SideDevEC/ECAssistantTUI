using static ECAssistant.EColor;

using ECAssistant.Config;
using ECAssistant.Engine;
using ECAssistant.Orchestration;
using ECAssistant.Tools.PowerShell;
using ECAssistant.Tools.Research;
using ECAssistant.Tools.Background;
using ECAssistant.Tools.Web;
using ECAssistant.Tools.Build;
using ECAssistant.Tools.Git;
using ECAssistant.Tools;
using ECAssistant.Analysis;
using ECAssistant.UI;
using ECAssistant.Session;
using ECAssistant.Services;
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

            // ── Initialize structured logging (P2) ──
            var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ECAssistant", "ECAssistant.log");
            Logger.Initialize(logPath, (EGuiBase)Gui, LogLevel.Info);

            EColor.TagBold(Cyan, "ECAssistant", "v9.4 — llama-sharp 0.27.0");

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
                  // v9.3: TruncateAndReprefill handles context overflow gracefully
                  OverflowStrategy = LLama.Common.ContextOverflowStrategy.TruncateAndReprefill,
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

                    // ── Secondary Model (optional, for summarization) ──
                    if (_config.SecondaryModel.Enabled && !string.IsNullOrEmpty(_config.SecondaryModel.ModelPath))
                    {
                        var secPath = Path.GetFullPath(_config.SecondaryModel.ModelPath);
                        var secondary = SecondaryModelLoader.Load(secPath, 
                            contextSize: _config.SecondaryModel.ContextSize,
                            gpuLayers: _config.SecondaryModel.GpuLayers);
                        if (secondary != null)
                            EColor.TagBold(EColor.Success(), "Secondary", $"Model loaded: {Path.GetFileName(secPath)}");
                        else
                            EColor.Tag(EColor.Info(), "Secondary", "Failed to load — will use primary model for summarization.");
                    }

                     // ── Background Process Manager (must be before tool registration) ──
                    var bgMgr = new BackgroundProcessManager();
                    EColor.TagBold(EColor.Info(), "Background", "Process manager ready.");

                    // ── File Watcher (v9.7: workspace monitoring) ──
                    var fileWatcher = new FileWatcherService(effectiveDir);
                    fileWatcher.Start();

                     EColor.TagBold(EColor.Info(), "Init", "Registering tools...");
                    var psAgent = new EPowerShellAgent(effectiveDir);
                      agent.RegisterTool(psAgent);
                    agent.RegisterTool(new EBackgroundExecTool(bgMgr, effectiveDir));
                    agent.RegisterTool(new EWebSearchTool());
                    agent.RegisterTool(new EDotnetBuildTool(effectiveDir));
                    agent.RegisterTool(new EGitTool(effectiveDir));

                    // EFileResearchTool — project-wide file scan for analysis
                       {
                          var researchExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                               { ".cs", ".md", ".json", ".txt", ".xml", ".ps1", ".sln",
                                ".csproj", ".config", ".sql", ".html", ".css", ".js" };

                        var toolConfig = _config.Tools.EFileResearchTool;
                         foreach (var ext in toolConfig.DefaultExtensions)
                             researchExtensions.Add(ext);

                         agent.RegisterTool(new EFileResearchTool(
                             effectiveDir, defaultExtensions: researchExtensions,
                              maxCharsPerFile: toolConfig.MaxCharsPerFile));
                        }

                    EColor.TagBold(EColor.Info(), "Init", $"Tools: {string.Join(", ", agent.Tools.Select(t => t.Name))}");
                      Gui.BlankLine();

                        // Orchestrator limits — fixed defaults, not reusing context config
                    var maxTurns = 5; // Hard limit: 5 turns per task
                    var maxFailures = 3;

                    var orchestrator = new AgentOrchestrator(agent, maxTurns: maxTurns, maxFailures: maxFailures, toolPolicy: new ToolPolicy());

                    // ── Session Manager (P0: session abstraction) ──
                    var sessionManager = new SessionManager(agent, orchestrator.Policy);
                    EColor.TagBold(EColor.Info(), "Session", $"Main session created. Sessions: {sessionManager.List().Count}");



                   await RunAgentLoop(agent, orchestrator, effectiveDir, psAgent, sessionManager, bgMgr, fileWatcher);
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
            var model = Path.GetFileName(_config.Llm.ModelPath);
            EColor.WriteLine(Dim, $"  Model: {model} | Ctx: {_config.Llm.ContextSize} | GPU: {_config.Llm.GpuLayers} | Tokens: {_config.Inference.MaxTokens} | Temp: {_config.Sampling.Temperature}");
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
        SessionManager sessionManager,
        BackgroundProcessManager bgMgr,
        FileWatcherService fileWatcher)
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
                                         var maxTurnsDisplayA = 5;
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

                           // ── Background Exec Commands (P1) ──
                            case "bg-run": case "bg": {
                                var cmd = Gui.PromptRaw("Command: ")?.Trim() ?? "";
                                if (!string.IsNullOrEmpty(cmd)) {
                                    var bgId = await bgMgr.StartAsync(cmd, workingDir);
                                    EColor.TagBold(EColor.Success(), "Background", $"Started: {bgId} — {cmd}");
                                }
                                continue;
                            }
                            case "bg-status": {
                                var list = bgMgr.List();
                                Gui.BlankLine();
                                EColor.TagBold(Cyan, "Background", $"{list.Count} process(es):");
                                foreach (var p in list) Gui.WriteLineColored($"  {p}");
                                Gui.BlankLine();
                                continue;
                            }
                            case "bg-output": {
                                var bgId = Gui.PromptRaw("Process ID: ")?.Trim() ?? "";
                                if (!string.IsNullOrEmpty(bgId)) {
                                    var output = bgMgr.GetOutput(bgId);
                                    var status = bgMgr.GetStatus(bgId);
                                    EColor.TagBold(Cyan, "Background", $"{bgId} — {status}");
                                    Gui.WriteLineColored(output);
                                }
                                continue;
                            }
                            case "bg-kill": {
                                var bgId = Gui.PromptRaw("Process ID: ")?.Trim() ?? "";
                                if (!string.IsNullOrEmpty(bgId)) {
                                    var killed = bgMgr.Kill(bgId);
                                    EColor.TagBold(killed ? EColor.Success() : EColor.Error(), "Background", killed ? $"Killed: {bgId}" : $"Failed to kill: {bgId}");
                                }
                                continue;
                            }
                            case "bg-cleanup": {
                                bgMgr.CleanupFinished();
                                EColor.TagBold(EColor.Info(), "Background", "Finished processes cleaned up.");
                                continue;
                            }

                           // ── File Watcher Commands ──
                            case "watch": {
                                var changes = fileWatcher.GetChangeSummary();
                                Gui.BlankLine();
                                EColor.TagBold(Cyan, "Watch", changes == "(No file changes detected.)" ? "No changes since last check." : "Recent changes:");
                                Gui.WriteLineColored(changes);
                                Gui.BlankLine();
                                continue;
                            }
                            case "watch-start": {
                                fileWatcher.Start();
                                EColor.TagBold(EColor.Success(), "Watch", $"Watching: {fileWatcher.WatchPath}");
                                continue;
                            }
                            case "watch-stop": {
                                fileWatcher.Stop();
                                EColor.TagBold(EColor.Info(), "Watch", "Stopped.");
                                continue;
                            }

                           // ── Clipboard ──
                            case "clipboard-read": {
                                try {
                                    if (OperatingSystem.IsWindows()) {
                                        var clipText = await Task.Run(() => {
                                            var clipExe = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                                                FileName = "powershell.exe",
                                                Arguments = "-NoProfile -Command Get-Clipboard",
                                                UseShellExecute = false,
                                                RedirectStandardOutput = true,
                                                CreateNoWindow = true
                                            });
                                            return clipExe?.StandardOutput.ReadToEnd().Trim() ?? "(empty)";
                                        });
                                        Gui.BlankLine();
                                        EColor.TagBold(Cyan, "Clipboard", clipText.Length > 200 ? clipText.Substring(0, 200) + "..." : clipText);
                                        Gui.BlankLine();
                                    } else {
                                        EColor.Tag(EColor.Info(), "Clipboard", "Windows-only feature.");
                                    }
                                } catch (Exception ex) { EColor.Tag(EColor.Error(), "Clipboard", ex.Message); }
                                continue;
                            }
                            case "clipboard-write": {
                                var text = Gui.PromptRaw("Text to copy: ") ?? "";
                                if (!string.IsNullOrEmpty(text) && OperatingSystem.IsWindows()) {
                                    try {
                                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                                            FileName = "powershell.exe",
                                            Arguments = $"-NoProfile -Command Set-Clipboard -Value '{text.Replace("'", "''")}'",
                                            UseShellExecute = false,
                                            CreateNoWindow = true
                                        })?.WaitForExit();
                                        EColor.TagBold(EColor.Success(), "Clipboard", "Copied to clipboard.");
                                    } catch (Exception ex) { EColor.Tag(EColor.Error(), "Clipboard", ex.Message); }
                                }
                                continue;
                            }

                           // ── Config Hot-Reload ──
                            case "reload-config": {
                                var configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ECAssistant", "appsettings.json");
                                if (File.Exists(configPath)) {
                                    _config = EAgentConfig.Load(configPath);
                                    EColor.TagBold(EColor.Success(), "Config", $"Reloaded from: {configPath}");
                                    EColor.WriteLine(Dim, $"  Model: {Path.GetFileName(_config.Llm.ModelPath)} | Ctx: {_config.Llm.ContextSize} | Tokens: {_config.Inference.MaxTokens} | Temp: {_config.Sampling.Temperature}");
                                } else {
                                    EColor.TagBold(EColor.Error(), "Config", $"Not found: {configPath}");
                                }
                                continue;
                            }

                           // ── Model Hot-Swap ──
                            case "swap-model": {
                                var newModel = Gui.PromptRaw("Model path (or filename in app dir): ")?.Trim();
                                if (!string.IsNullOrEmpty(newModel)) {
                                    if (!File.Exists(newModel)) {
                                        newModel = Path.Combine(AppContext.BaseDirectory, newModel);
                                    }
                                    if (File.Exists(newModel)) {
                                        EColor.TagBold(EColor.Info(), "Swap", $"Unloading current model...");
                                        await agent.DisposeAsync();
                                        var newParams = new InferenceParams {
                                            MaxTokens = _config.Inference.MaxTokens,
                                            AntiPrompts = _config.Inference.AntiPrompts.Length > 0
                                                ? _config.Inference.AntiPrompts
                                                : new string[] { "</s>" },
                                            OverflowStrategy = LLama.Common.ContextOverflowStrategy.TruncateAndReprefill,
                                            SamplingPipeline = new DefaultSamplingPipeline {
                                                Temperature = _config.Sampling.Temperature,
                                                TopP = _config.Sampling.TopP,
                                                TopK = _config.Sampling.TopK,
                                                RepeatPenalty = _config.Sampling.RepeatPenalty
                                            }
                                        };
                                        agent = new EAgentEngine(newModel, _config.Llm.ContextSize, _config.Llm.GpuLayers, _config.Llm.Threads == -1 ? Environment.ProcessorCount : _config.Llm.Threads, newParams);
                                        agent.LoadContext();
                                        agent.WireSummaryService();
                                        foreach (var t in agent.Tools) { } // tools already registered in constructor
                                        EColor.TagBold(EColor.Success(), "Swap", $"Model loaded: {Path.GetFileName(newModel)}");
                                    } else {
                                        EColor.TagBold(EColor.Error(), "Swap", $"Model not found: {newModel}");
                                    }
                                }
                                continue;
                            }

                           // ── Logging Commands (P2) ──
                            case "log": {
                                Gui.BlankLine();
                                EColor.TagBold(Cyan, "Log", $"File: {Logger.LogFilePath} ({Logger.LogFileSize} bytes)");
                                Gui.BlankLine();
                                var recent = Logger.GetRecentLines(30);
                                Gui.WriteLineColored(recent);
                                Gui.BlankLine();
                                continue;
                            }
                            case "log-level": {
                                var lvl = Gui.PromptRaw("Level (debug/info/warn/error): ")?.Trim().ToLower() ?? "";
                                var parsed = lvl switch {
                                    "debug" => LogLevel.Debug,
                                    "info" => LogLevel.Info,
                                    "warn" => LogLevel.Warn,
                                    "error" => LogLevel.Error,
                                    _ => LogLevel.Info
                                };
                                Logger.SetLevel(parsed);
                                EColor.TagBold(EColor.Success(), "Log", $"Level set to: {parsed}");
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
                             var maxTurnsDisplayB = 5;
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
             EColor.WriteLine(Yellow + Bold, "   bg-run <cmd>          Start a background process");
            EColor.WriteLine(Yellow + Bold, "   bg-status             List background processes");
           EColor.WriteLine(Yellow + Bold, "   bg-output <id>        Get output from a background process");
           EColor.WriteLine(Yellow + Bold, "   bg-kill <id>          Kill a background process");
          EColor.WriteLine(Yellow + Bold, "   bg-cleanup            Remove finished processes from tracking");
           EColor.WriteLine(Yellow + Bold, "   watch                 Show recent file changes");
          EColor.WriteLine(Yellow + Bold, "   watch-start           Start watching for file changes");
         EColor.WriteLine(Yellow + Bold, "   watch-stop            Stop watching");
           EColor.WriteLine(Yellow + Bold, "   reload-config         Reload appsettings.json without restart");
          EColor.WriteLine(Yellow + Bold, "   swap-model            Switch to a different GGUF model at runtime");
           EColor.WriteLine(Yellow + Bold, "   clipboard-read        Read from Windows clipboard");
          EColor.WriteLine(Yellow + Bold, "   clipboard-write       Write to Windows clipboard");
           EColor.WriteLine(Yellow + Bold, "   log                   Show recent log entries");
          EColor.WriteLine(Yellow + Bold, "   log-level             Set log level (debug/info/warn/error)");
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
