using static ECAssistant.EColor;

using ECAssistant.Config;
using ECAssistant.Engine;
using ECAssistant.Orchestration;
using ECAssistant.Tools.Shell;
using ECAssistant.Tools.Research;
using ECAssistant.Tools.Background;
using ECAssistant.Tools.Web;
using ECAssistant.Tools.Build;
using ECAssistant.Tools.Git;
using ECAssistant.Tools.Code;
using ECAssistant.Tools;
using ECAssistant.Analysis;
using ECAssistant.UI;
using ECAssistant.Session;
using ECAssistant.Services;
using ECAssistant.Testing;
using LLama.Common;
using LLama.Sampling;

namespace ECAssistant;

public class Program
{
    private static EAgentConfig _config = null!;
    public static EGuiBase Gui = null!;   // the UI instance, set once in Main()
    private static bool _quitRequested;   // set by quit/exit command

[System.STAThread]
    public static async Task<int> Main(string[] args)
            {
                 // ── Test mode: run automated tests ──
                 if (args.Length > 0 && args[0].Equals("--test", StringComparison.OrdinalIgnoreCase))
                 {
                     return await RunTestsAsync(args.Skip(1).ToArray());
                 }

                 // ── UI initialisation — single line, swap anywhere ──
            // v10.21.2: EGuiConsole with ANSI scroll region — output scrolls above, input fixed at bottom
            EGuiConsole.InitConsole();
            Gui = new EGuiConsole();
            // Route EColor output through Gui for scroll region cursor management
            EColor.WriteLineHandler = (s) => Gui.WriteLineColored(s);
            EColor.WriteHandler = (s) => Gui.WriteRaw(s);

            // ── Initialize structured logging (P2) ──
            var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ECAssistant", "ECAssistant.log");
            Logger.Initialize(logPath, (EGuiBase)Gui, LogLevel.Info);

            EColor.TagBold(Cyan, "ECAssistant", "v10.12 — llama-sharp 0.27.0");

                 // Always use user's home directory for ECAssistant
            var userConfigDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ECAssistant");
            Directory.CreateDirectory(userConfigDir);

            // v9.14: Copy appsettings.json from build dir to user dir if not exists
            var configPath = Path.Combine(userConfigDir, "appsettings.json");
            var bundledConfigPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(configPath))
            {
                if (File.Exists(bundledConfigPath))
                {
                    EColor.Tag(EColor.Success(), "Setup", $"Copying default config to: {userConfigDir}");
                    File.Copy(bundledConfigPath, configPath, overwrite: false);
                }
                else
                {
                    EColor.Tag(EColor.Info(), "Setup", "No appsettings.json found. Creating default.");
                    var defaults = new EAgentConfig();
                    File.WriteAllText(configPath, System.Text.Json.JsonSerializer.Serialize(
                        defaults, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                }
            }

                 _config = EAgentConfig.Load(configPath);
              EColor.Tag(EColor.Success(), "Config", $"Loaded from: {Path.GetFullPath(configPath)}");
            ApplyCommandLineArgs(ref _config, args);

             Gui.BlankLine();

            // v9.18: Resolve model path relative to working dir FIRST, then build dir
            var effectiveModelPath = _config.Llm.ModelPath;
            if (!Path.IsPathRooted(effectiveModelPath))
            {
                // Try working dir (~/ECAssistant) first
                var inWorkDir = Path.Combine(userConfigDir, effectiveModelPath);
                // Then try build dir
                var inBuildDir = Path.Combine(AppContext.BaseDirectory, effectiveModelPath);
                
                if (File.Exists(inWorkDir))
                    effectiveModelPath = inWorkDir;
                else if (File.Exists(inBuildDir))
                    effectiveModelPath = inBuildDir;
                else
                    effectiveModelPath = inWorkDir; // use work dir path for error message
            }
           if (!File.Exists(effectiveModelPath))
                      {
                    EColor.TagBold(EColor.Error(), "Error", $"Model not found: {effectiveModelPath}");
                      EColor.Tag(EColor.Info(), "Hint", $"Put your .gguf model in: {userConfigDir} or set full path in appsettings.json");
                     }

            // v9.14: Default working dir to ~/ECAssistant if set to "."
            var effectiveDir = _config.AgentSettings.WorkingDirectory == "." || string.IsNullOrEmpty(_config.AgentSettings.WorkingDirectory)
                ? userConfigDir
                : Path.GetFullPath(_config.AgentSettings.WorkingDirectory);
              Directory.CreateDirectory(effectiveDir);
            EColor.TagBold(EColor.Info(), "WorkDir", effectiveDir);

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
                    EColor.TagBold(EColor.Success(), "Model", "Found at: " + effectiveModelPath);

                     // ── Background Process Manager (must be before tool registration) ──
                    var bgMgr = new BackgroundProcessManager();
                    EColor.TagBold(EColor.Info(), "Background", "Process manager ready.");

                    // ── File Watcher (v9.7: workspace monitoring) ──
                    var fileWatcher = new FileWatcherService(effectiveDir);
                    fileWatcher.Start();

                    // ── Session Manager (v10.21: shared weights, session discovery) ──
                    // Loads model weights ONCE. Sessions are loaded from disk afterwards.
                    var sessionManager = new SessionManager(_config, effectiveModelPath, effectiveDir);

                    // ── Loading phase: animated dots while sessions init ──
                    EColor.TagBold(Cyan, "Sessions", "Discovering sessions...");

                    var discovered = SessionDiscovery.DiscoverSessions(effectiveDir);
                    if (discovered.Count > 0)
                        EColor.Tag(EColor.Info(), "Sessions", $"Found {discovered.Count} session(s): {string.Join(", ", discovered)}");
                    else
                        EColor.Tag(EColor.Info(), "Sessions", "No existing sessions found — creating new 'main' session.");

                    var loading = new LoadingIndicator(Gui);
                    loading.Start("Loading model weights");

                    string activeKey = await sessionManager.LoadSessionsFromDiskAsync(async (session) =>
                    {
                        loading.UpdateLabel($"Initializing session '{session.Key}'");
                        await InitSessionAsync(session, effectiveDir, bgMgr, userConfigDir);
                    });

                    loading.Stop();

                    var mainSession = sessionManager.Main;
                    var activeSession = sessionManager.ActiveSession ?? mainSession;
                    EColor.TagBold(EColor.Success(), "Ready", $"Active session: {activeKey} ({sessionManager.List().Count} total)");
                    Gui.BlankLine();

                   // v10.21.1: Simple console input loop — PromptRaw uses ReadKey (non-blocking)
                    await RunAgentLoop(activeSession, sessionManager, effectiveDir, bgMgr, fileWatcher);
                        }
            else
                     {
                    EColor.Tag(EColor.Error(), "Error", "No model loaded. Use --model or update appsettings.json.");
                  return 1;
                     }

           // Save transcript on exit (cleanup via async)
             var transPath = Path.Combine(effectiveDir, ".sessions", "main", "transcript.json");
            if (File.Exists(transPath))
               {
                // Transcript already auto-saved in loop via "save-context" command
                 Program.Gui.WriteLineColored($"[Context] Transcript saved at: {transPath}");
                }

            return 0;
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

    // ── Test Mode: Automated testing without console interaction ──

    private static async Task<int> RunTestsAsync(string[] testArgs)
    {
        // Resolve model path from ~/ECAssistant/appsettings.json
        var userConfigDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ECAssistant");
        var configPath = Path.Combine(userConfigDir, "appsettings.json");
        var modelPath = "";
        if (File.Exists(configPath))
        {
            var config = EAgentConfig.Load(configPath);
            modelPath = config.Llm.ModelPath;
        }

        // Allow override via --model arg
        for (int i = 0; i < testArgs.Length; i++)
        {
            if (testArgs[i].Equals("--model", StringComparison.OrdinalIgnoreCase) && i + 1 < testArgs.Length)
                modelPath = testArgs[++i];
        }

        if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath))
        {
            Console.WriteLine($"❌ Model not found: {modelPath}");
            Console.WriteLine("   Set model path in ~/ECAssistant/appsettings.json or pass --model /path/to/model.gguf");
            return 1;
        }

        // Parse test filter
        string? filter = null;
        bool verbose = false;
        for (int i = 0; i < testArgs.Length; i++)
        {
            if (testArgs[i].Equals("--filter", StringComparison.OrdinalIgnoreCase) && i + 1 < testArgs.Length)
                filter = testArgs[++i];
            if (testArgs[i].Equals("--verbose", StringComparison.OrdinalIgnoreCase) || testArgs[i].Equals("-v", StringComparison.OrdinalIgnoreCase))
                verbose = true;
        }

        // Select tests
        var allTests = EcaTests.All;
        List<TestScenario> tests;
        if (!string.IsNullOrEmpty(filter))
        {
            tests = allTests.Where(t => t.Name.StartsWith(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            if (tests.Count == 0)
            {
                Console.WriteLine($"No tests match filter '{filter}'. Available:");
                foreach (var t in allTests)
                    Console.WriteLine($"  {t.Name}");
                return 1;
            }
        }
        else
        {
            tests = allTests;
        }

        Console.WriteLine($"\n🧪 Running {tests.Count} test(s) with model: {Path.GetFileName(modelPath)}");
        Console.WriteLine($"   Filter: {filter ?? "(all)"}\n");

        // Run the test suite
        await using var runner = new TestRunner(modelPath) { Verbose = verbose };
        var results = await runner.RunAllAsync(tests);

        // Write detailed results to a log file
        var logPath = Path.Combine(runner._testRootDir, "test_results.log");
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"ECAssistant Test Results — {DateTime.UtcNow:O}");
        sb.AppendLine($"Model: {modelPath}");
        sb.AppendLine();
        foreach (var r in results)
        {
            sb.AppendLine($"{(r.Passed ? "PASS" : "FAIL")} | {r.Name} | {r.Duration.TotalSeconds:F1}s | {r.FailureReason}");
            if (!r.Passed)
            {
                sb.AppendLine($"  Output: {r.FinalOutput}");
                sb.AppendLine();
            }
        }
        File.WriteAllText(logPath, sb.ToString());
        Console.WriteLine($"\n📝 Detailed log: {logPath}");

        // Exit code: 0 if all passed, 1 if any failed
        return results.Any(r => !r.Passed) ? 1 : 0;
    }

    /// <summary>
    /// Initialize a session: attach UI, vector memory, project context, tools, secondary model, sub-agents.
    /// Called for each session during startup loading.
    /// </summary>
    private static async Task InitSessionAsync(AgentSession session, string workingDir,
        BackgroundProcessManager bgMgr, string userConfigDir)
    {
        // ── Attach UI renderer to the session ──
        var uiRenderer = new ConsoleUiRenderer(Gui);
        session.AttachUi(uiRenderer);

        // ── Vector Memory (semantic search) ──
        if (_config.VectorMemory.Enabled)
        {
            var vecDir = Path.Combine(workingDir, _config.VectorMemory.Directory);
            await session.InitializeVectorMemoryAsync(vecDir);
        }

        // ── Project Context Manager ──
        await session.InitializeProjectContextAsync();

        // ── Register tools on the session's engine ──
        var psAgent = new EShellAgent(workingDir);
        session.RegisterTool(psAgent);
        session.RegisterTool(new EBackgroundExecTool(bgMgr, workingDir));
        session.RegisterTool(new EWebSearchTool());
        session.RegisterTool(new EDotnetBuildTool(workingDir));
        session.RegisterTool(new EGitTool(workingDir));
        session.RegisterTool(new ECodeEditorTool(workingDir));

        // EFileResearchTool
        {
            var researchExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
               { ".cs", ".md", ".json", ".txt", ".xml", ".ps1", ".sln",
                ".csproj", ".config", ".sql", ".html", ".css", ".js" };

            var toolConfig = _config.Tools.EFileResearchTool;
            foreach (var ext in toolConfig.DefaultExtensions)
                researchExtensions.Add(ext);

            session.RegisterTool(new EFileResearchTool(
                workingDir, defaultExtensions: researchExtensions,
                maxCharsPerFile: toolConfig.MaxCharsPerFile));
        }

        // ── Secondary Model (optional) ──
        if (_config.SecondaryModel.Enabled && !string.IsNullOrEmpty(_config.SecondaryModel.ModelPath))
        {
            var secPath = _config.SecondaryModel.ModelPath;
            if (!Path.IsPathRooted(secPath))
            {
                var secInWork = Path.Combine(userConfigDir, secPath);
                var secInBuild = Path.Combine(AppContext.BaseDirectory, secPath);
                secPath = File.Exists(secInWork) ? secInWork : (File.Exists(secInBuild) ? secInBuild : secInWork);
            }
            var secondary = SecondaryModelLoader.Load(secPath,
                contextSize: _config.SecondaryModel.ContextSize,
                gpuLayers: _config.SecondaryModel.GpuLayers,
                temperature: _config.SecondaryModel.Temperature,
                topP: _config.SecondaryModel.TopP,
                topK: _config.SecondaryModel.TopK,
                repeatPenalty: _config.SecondaryModel.RepeatPenalty,
                maxTokens: _config.SecondaryModel.MaxTokens,
                antiPrompts: _config.SecondaryModel.AntiPrompts);
            if (secondary != null)
            {
                session.SetSecondaryModel(secondary);
            }
        }

        // ── Sub-agents (only if enabled) ──
        if (_config.SubAgent.Enabled)
        {
            await session.InitializeSubAgentsAsync();
        }
    }

/// <summary>
    /// Process a single user input (command or prompt). Called by the console input loop.
    /// </summary>
    private static async Task ProcessInputAsync(
        string input,
        AgentSession activeSession,
        SessionManager sessionManager,
        string workingDir,
        BackgroundProcessManager bgMgr,
        FileWatcherService fileWatcher)
    {
        var agent = activeSession.Engine;
        var orchestrator = activeSession.Orchestrator;

        input = input.Trim();
        if (string.IsNullOrEmpty(input)) return;

        switch (input.ToLower())
        {
            case "quit": case "exit":
                EColor.TagBold(EColor.Info(), "Bye", "Goodbye.");
                Gui.BlankLine();
                await sessionManager.StopAllAsync();
                _quitRequested = true;
                return;
            case "help": await PrintHelp(); return;
            case "tools": ListTools(agent); return;
            case "clear-history": agent.ClearHistory(); return;
            case "save-context":
            {
                var p = Path.Combine(workingDir, ".sessions", activeSession.Key, "transcript.json");
                agent.SaveTranscript(p);
                Gui.BlankLine();
                return;
            }
            case "single":
                EColor.Tag(Info(), "Mode", "Single-turn mode reset.");
                orchestrator.Reset();
                return;
            case "stop":
            {
                var stopSession = sessionManager.ActiveSession;
                if (stopSession != null && stopSession.RunState == SessionRunState.Running)
                {
                    stopSession.Stop();
                    EColor.TagBold(EColor.Error(), "Stop", "Cancelling active session...");
                }
                else { EColor.Tag(Info(), "Stop", "Nothing is running."); }
                return;
            }
            default: break;
        }

        // Session commands that need PromptRaw — for now, handle inline
        // (These are less common commands that need additional input)
        var lowerInput = input.ToLower();
        
        // Route all other inputs as prompts to the active session
        var activeSessionForPrompt = sessionManager.ActiveSession;
        if (activeSessionForPrompt != null)
        {
            activeSessionForPrompt.Prompt(input);
        }
    }


    /// <summary>
    /// Simple console input loop — uses Gui.PromptRaw (ReadKey-based, non-blocking).
    /// Replaces the Terminal.Gui event loop. Output from sessions goes to Console.Write
    /// via ConsoleUiRenderer, input comes from PromptRaw on the main thread.
    /// </summary>
    private static async Task RunAgentLoop(
        AgentSession activeSession,
        SessionManager sessionManager,
        string workingDir,
        BackgroundProcessManager bgMgr,
        FileWatcherService fileWatcher)
    {
        Gui.BlankLine();
        EColor.TagBold(Cyan, "ECLoop", "Type your request (help | quit)");
        Gui.WriteLine("===========================================");
        EColor.TagBold(EColor.Info(), "Mode", "The agent decides tools automatically.");
        Gui.BlankLine();

        while (true)
        {
            var input = Gui.PromptRaw(Cyan + "> " + Reset)?.Trim();
            if (string.IsNullOrEmpty(input)) continue;

            await ProcessInputAsync(input, activeSession, sessionManager, workingDir, bgMgr, fileWatcher);

            // Check if active session changed (e.g. session switch)
            var currentActive = sessionManager.ActiveSession;
            if (currentActive != null && currentActive != activeSession)
            {
                activeSession = currentActive;
            }

            // If quit was requested, exit the loop (and the application)
            if (_quitRequested)
                return;
        }
    }

    private static string TruncatePrompt(string prompt, int maxLen = 60)
    {
        if (string.IsNullOrEmpty(prompt)) return "";
        return prompt.Length <= maxLen ? prompt : prompt.Substring(0, maxLen) + "...";
    }

    private static void ListTools(EAgentEngine agent)
    {
        Gui.BlankLine();
        EColor.TagBold(Cyan, "Tools", $"{agent.Tools.Count} registered:");
        Gui.BlankLine();
        foreach (var t in agent.Tools)
        {
            EColor.WriteLine(Yellow + Bold, $"  {t.Name}");
            EColor.WriteLine(EColor.Dim, $"    {t.Description}");
            EColor.WriteLine(EColor.Dim, $"    Example: {t.UsageExample}");
            Gui.BlankLine();
        }
    }

    private static async Task PrintHelp()
    {
        Gui.BlankLine();
        EColor.TagBold(Cyan, "Commands", "");
        Gui.BlankLine();
        EColor.WriteLine(Yellow + Bold, "  <type request>       Multi-step agent execution");
        EColor.WriteLine(Yellow + Bold, "  stop                 Stop the running session (keeps app alive)");
        EColor.WriteLine(Yellow + Bold, "  ESC                  Stop generation mid-stream (during token output)");
        EColor.WriteLine(Yellow + Bold, "  quit / exit          Stop all sessions and exit the application");
        EColor.WriteLine(Yellow + Bold, "  help                 Show this help");
        EColor.WriteLine(Yellow + Bold, "  tools                List registered tools");
        Gui.BlankLine();
        EColor.WriteLine(EColor.Dim, "  Context:");
        EColor.WriteLine(Yellow + Bold, "  clear-history        Clear conversation history");
        EColor.WriteLine(Yellow + Bold, "  save-context         Save transcript to disk");
        EColor.WriteLine(Yellow + Bold, "  file-pick            Open file picker, send to LLM");
        Gui.BlankLine();
        EColor.WriteLine(EColor.Dim, "  Memory:");
        EColor.WriteLine(Yellow + Bold, "  memory-save          Save a memory entry");
        EColor.WriteLine(Yellow + Bold, "  memory-query         Search memory");
        EColor.WriteLine(Yellow + Bold, "  memory-stats         Memory statistics");
        EColor.WriteLine(Yellow + Bold, "  vecmem-stats         Vector memory statistics");
        EColor.WriteLine(Yellow + Bold, "  vecmem-search        Semantic memory search");
        EColor.WriteLine(Yellow + Bold, "  vecmem-add           Add vector memory entry");
        Gui.BlankLine();
        EColor.WriteLine(EColor.Dim, "  Sessions (v10.20):");
        EColor.WriteLine(Yellow + Bold, "  sessions             List all sessions with status");
        EColor.WriteLine(Yellow + Bold, "  session <n>          Switch to session n (render history + live)");
        EColor.WriteLine(Yellow + Bold, "  session-new <name>   Create a new session");
        EColor.WriteLine(Yellow + Bold, "  session-stop <n>     Stop session n's execution");
        EColor.WriteLine(Yellow + Bold, "  session-close <n>    Close and delete session n");
        EColor.WriteLine(Yellow + Bold, "  session-peek <n>     Quick glance at session n's output");
        EColor.WriteLine(Yellow + Bold, "  session-queue         Show active session's prompt queue");
        EColor.WriteLine(Yellow + Bold, "  session-queue-remove <i>  Remove prompt i from queue");
        EColor.WriteLine(Yellow + Bold, "  session-queue-clear  Clear active session's queue");
        Gui.BlankLine();
        EColor.WriteLine(EColor.Dim, "  Background:");
        EColor.WriteLine(Yellow + Bold, "  bg-run <cmd>         Start a background process");
        EColor.WriteLine(Yellow + Bold, "  bg-status            List background processes");
        EColor.WriteLine(Yellow + Bold, "  bg-output <id>       Get output from a process");
        EColor.WriteLine(Yellow + Bold, "  bg-kill <id>         Kill a background process");
        EColor.WriteLine(Yellow + Bold, "  bg-cleanup           Remove finished processes");
        Gui.BlankLine();
        EColor.WriteLine(EColor.Dim, "  Files & Watch:");
        EColor.WriteLine(Yellow + Bold, "  watch                Show recent file changes");
        EColor.WriteLine(Yellow + Bold, "  watch-start          Start watching for changes");
        EColor.WriteLine(Yellow + Bold, "  watch-stop           Stop watching");
        Gui.BlankLine();
        EColor.WriteLine(EColor.Dim, "  System:");
        EColor.WriteLine(Yellow + Bold, "  reload-config        Reload appsettings.json");
        EColor.WriteLine(Yellow + Bold, "  swap-model           Switch GGUF model at runtime");
        EColor.WriteLine(Yellow + Bold, "  clipboard-read       Read from Windows clipboard");
        EColor.WriteLine(Yellow + Bold, "  clipboard-write      Write to Windows clipboard");
        EColor.WriteLine(Yellow + Bold, "  log                  Show recent log entries");
        EColor.WriteLine(Yellow + Bold, "  log-level            Set log level (debug/info/warn/error)");
        Gui.BlankLine();
        await Task.CompletedTask;
    }
}