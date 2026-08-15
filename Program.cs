
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
using ECAssistant.Tools.Reader;
using ECAssistant.Tools;
using ECAssistant.Analysis;
using ECAssistant.UI;
using ECAssistant.Session;
using ECAssistant.Services;
using ECAssistant.Interfaces;
using ECAssistant.Testing;
using LLama.Common;
using LLama.Sampling;

namespace ECAssistant;

public class Program
{
    private static EAgentConfig _config = null!;
    private static ILogger _logger = null!;
    public static EGuiBase Gui = null!;   // the UI instance, set once in Main()
    private static EColor _color = null!;  // color formatter instance
    private static bool _quitRequested;
    private static ConsoleUiRenderer? _activeUi;   // set by quit/exit command

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
            new EGuiConsole().InitConsole();
            Gui = new EGuiConsole();
            _color = new EColor();
            // Route EColor output through Gui for scroll region cursor management
            _color.WriteLineHandler = (s) => Gui.WriteLineColored(s);
            _color.WriteHandler = (s) => Gui.WriteRaw(s);

            // ── Initialize structured logging (P2) ──
            var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ECAssistant", "ECAssistant.log");
            _logger = new Logger(logPath, (EGuiBase)Gui, LogLevel.Info);

            _color.TagBold(_color.Cyan, "ECAssistant", "v10.12 — llama-sharp 0.27.0");

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
                    _color.Tag(_color.Green, "Setup", $"Copying default config to: {userConfigDir}");
                    File.Copy(bundledConfigPath, configPath, overwrite: false);
                }
                else
                {
                    _color.Tag(_color.Cyan, "Setup", "No appsettings.json found. Creating default.");
                    var defaults = new EAgentConfig();
                    File.WriteAllText(configPath, System.Text.Json.JsonSerializer.Serialize(
                        defaults, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                }
            }

                 { var fs = new Services.FileSystemAdapter(); var loader = new Config.ConfigLoader(fs); _config = loader.Load(configPath); }
              _color.Tag(_color.Green, "Config", $"Loaded from: {Path.GetFullPath(configPath)}");
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
                    _color.TagBold(_color.Red, "Error", $"Model not found: {effectiveModelPath}");
                      _color.Tag(_color.Cyan, "Hint", $"Put your .gguf model in: {userConfigDir} or set full path in appsettings.json");
                     }

            // v9.14: Default working dir to ~/ECAssistant if set to "."
            var effectiveDir = _config.AgentSettings.WorkingDirectory == "." || string.IsNullOrEmpty(_config.AgentSettings.WorkingDirectory)
                ? userConfigDir
                : Path.GetFullPath(_config.AgentSettings.WorkingDirectory);
              Directory.CreateDirectory(effectiveDir);
            _color.TagBold(_color.Cyan, "WorkDir", effectiveDir);

           var rootDir = Path.GetFullPath(_config.RootPath);
            Directory.CreateDirectory(rootDir);
               Directory.CreateDirectory(Path.Combine(rootDir, _config.Memory.DataPath));
            Directory.CreateDirectory(Path.Combine(rootDir, _config.Workspace.Path));
              _color.TagBold(_color.Cyan, "Root", $"{rootDir}");
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
                    _color.TagBold(_color.Green, "Model", "Found at: " + effectiveModelPath);

                     // ── Background Process Manager (must be before tool registration) ──
                    var bgMgr = new BackgroundProcessManager();
                    _color.TagBold(_color.Cyan, "Background", "Process manager ready.");

                    // ── File Watcher (v9.7: workspace monitoring) ──
                    var fileWatcher = new FileWatcherService(effectiveDir, logger: _logger);
                    fileWatcher.Start();

                    // ── Session Manager (v10.21: shared weights, session discovery) ──
                    // Loads model weights ONCE. Sessions are loaded from disk afterwards.
                    var sessionManager = new SessionManager(_config, effectiveModelPath, effectiveDir, _logger);

                    // ── Loading phase: animated dots while sessions init ──
                    _color.TagBold(_color.Cyan, "Sessions", "Discovering sessions...");

                    var discovered = new SessionDiscovery().DiscoverSessions(effectiveDir);
                    if (discovered.Count > 0)
                        _color.Tag(_color.Cyan, "Sessions", $"Found {discovered.Count} session(s): {string.Join(", ", discovered)}");
                    else
                        _color.Tag(_color.Cyan, "Sessions", "No existing sessions found — creating new 'main' session.");

                    var loading = new LoadingIndicator(Gui, _color);
                    loading.Start("Loading model weights");

                    string activeKey = await sessionManager.LoadSessionsFromDiskAsync(async (session) =>
                    {
                        loading.UpdateLabel($"Initializing session '{session.Key}'");
                        await InitSessionAsync(session, effectiveDir, bgMgr, userConfigDir);
                    });

                    loading.Stop();

                    var mainSession = sessionManager.Main;
                    var activeSession = sessionManager.ActiveSession ?? mainSession;
                    _color.TagBold(_color.Green, "Ready", $"Active session: {activeKey} ({sessionManager.List().Count} total)");
                    Gui.BlankLine();

                   // v10.21.1: Simple console input loop — PromptRaw uses ReadKey (non-blocking)
                    await RunAgentLoop(activeSession, sessionManager, effectiveDir, bgMgr, fileWatcher, userConfigDir);
                        }
            else
                     {
                    _color.Tag(_color.Red, "Error", "No model loaded. Use --model or update appsettings.json.");
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
        // v10.22: --mock flag runs model-independent tests with MockEngine (no GGUF needed)
        bool useMock = testArgs.Any(a => a.Equals("--mock", StringComparison.OrdinalIgnoreCase));

        // Resolve model path from ~/ECAssistant/appsettings.json
        var userConfigDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ECAssistant");
        var configPath = Path.Combine(userConfigDir, "appsettings.json");
        var modelPath = "";
        if (File.Exists(configPath))
        {
            var fs2 = new Services.FileSystemAdapter(); var loader2 = new Config.ConfigLoader(fs2); var config = loader2.Load(configPath);
            modelPath = config.Llm.ModelPath;
        }

        // Allow override via --model arg
        for (int i = 0; i < testArgs.Length; i++)
        {
            if (testArgs[i].Equals("--model", StringComparison.OrdinalIgnoreCase) && i + 1 < testArgs.Length)
                modelPath = testArgs[++i];
        }

        if (!useMock && (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath)))
        {
            Console.WriteLine($"❌ Model not found: {modelPath}");
            Console.WriteLine("   Set model path in ~/ECAssistant/appsettings.json or pass --model /path/to/model.gguf");
            Console.WriteLine("   Or use --mock for model-independent tests (no GGUF needed).");
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
        // v10.22: In mock mode, use MockScenarios instead of All (no real model needed)
        var allTests = useMock ? EcaTests.MockScenarios : EcaTests.All;
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

        Console.WriteLine($"\n🧪 Running {tests.Count} test(s) with {(useMock ? "MOCK ENGINE (no model)" : $"model: {Path.GetFileName(modelPath)}")}");
        Console.WriteLine($"   Filter: {filter ?? "(all)"}\n");

        // Run the test suite
        await using var runner = new TestRunner(useMock ? "/mock/model.gguf" : modelPath) { Verbose = verbose, UseMockEngine = useMock };
        var results = await runner.RunAllAsync(tests);

        // Write detailed results to a log file
        var logPath = Path.Combine(runner._testRootDir, "test_results.log");
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"ECAssistant Test Results — {DateTime.UtcNow:O}");
        sb.AppendLine($"Model: {(useMock ? "MOCK ENGINE" : modelPath)}");
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
        var uiRenderer = new ConsoleUiRenderer(Gui, _color); _activeUi = uiRenderer;
        session.AddListener(uiRenderer);

        // v10.22: Start timer-based stream flushing for real-time token output
        session.StartStreamFlushTimer();

        // ── Vector Memory (semantic search) ──
        if (_config.VectorMemory.Enabled)
        {
            var vecDir = Path.Combine(workingDir, _config.VectorMemory.Directory);
            await session.InitializeVectorMemoryAsync(vecDir);
        }

        // ── Project Context Manager ──
        await session.InitializeProjectContextAsync();

        // ── Register tools on the session's engine ──
        var fileSystem = new FileSystemAdapter();
        var processRunner = new ProcessRunner();
        var configPath = Path.Combine(userConfigDir, "appsettings.json");
        var configProvider = new ConfigProvider(fileSystem, configPath);
        var colorFormatter = new ColorFormatter();
        var httpClient = new HttpClientAdapter();

        var psAgent = new EShellAgent(processRunner, configProvider, colorFormatter, workingDir);
        session.RegisterTool(psAgent);
        session.RegisterTool(new EBackgroundExecTool(bgMgr, processRunner, fileSystem, configProvider, colorFormatter));
        session.RegisterTool(new EWebSearchTool(httpClient, configProvider, colorFormatter));
        session.RegisterTool(new EDotnetBuildTool(processRunner, configProvider, colorFormatter));
        session.RegisterTool(new EGitTool(processRunner, fileSystem, configProvider, colorFormatter));
        session.RegisterTool(new ECodeEditorTool(fileSystem, configProvider, colorFormatter));

        // v10.22: EFileReader — controlled file reading with offset/limit/token budget
        session.RegisterTool(new EFileReaderTool(fileSystem, configProvider, colorFormatter));

        // v10.22: EWebFetch — fetch URL content as plain text
        session.RegisterTool(new EWebFetchTool(httpClient, configProvider, colorFormatter));

        // EFileResearchTool
        {
            session.RegisterTool(new EFileResearchTool(fileSystem, configProvider, colorFormatter));
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
                antiPrompts: _config.SecondaryModel.AntiPrompts, logger: _logger);
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
        FileWatcherService fileWatcher,
        string userConfigDir)
    {
        var agent = activeSession.Engine;
        var orchestrator = activeSession.Orchestrator;

        input = input.Trim();
        if (string.IsNullOrEmpty(input)) return;

        switch (input.ToLower())
        {
            case "quit": case "exit":
                _color.TagBold(_color.Cyan, "Bye", "Goodbye.");
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
                _color.Tag(_color.Cyan, "Mode", "Single-turn mode reset.");
                orchestrator.Reset();
                return;
            case "stop":
            {
                var stopSession = sessionManager.ActiveSession;
                if (stopSession != null && stopSession.RunState == SessionRunState.Running)
                {
                    stopSession.Stop();
                    _color.TagBold(_color.Red, "Stop", "Cancelling active session...");
                }
                else { _color.Tag(_color.Cyan, "Stop", "Nothing is running."); }
                return;
            }
            case "context-status":
            {
                var s = sessionManager.ActiveSession;
                if (s != null)
                {
                    Gui.BlankLine();
                    _color.TagBold(_color.Cyan, "Context", s.Engine.ContextStatusSummary);
                    Gui.BlankLine();
                }
                return;
            }
            default: break;
        }

        // Session commands that need argument parsing
        var lowerInput = input.ToLower();
        var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0].ToLower();
        var arg = parts.Length > 1 ? parts[1].Trim() : "";

        // ── v10.22: Session management commands ──
        switch (cmd)
        {
            case "sessions":
            {
                Gui.BlankLine();
                Gui.WriteLineColored(sessionManager.GetStatusReport());
                Gui.BlankLine();
                return;
            }
            case "session":
            {
                // session <n> — switch to session by index or key
                if (string.IsNullOrEmpty(arg))
                {
                    _color.Tag(_color.Cyan, "Session", "Usage: session <n> | session <key>");
                    return;
                }
                if (int.TryParse(arg, out var idx))
                {
                    if (sessionManager.SwitchTo(idx))
                    {
                        var newActive = sessionManager.ActiveSession!;
                        // Re-attach UI
                        foreach (var s in sessionManager.List())
                            if (s != newActive) if (_activeUi != null) s.RemoveListener(_activeUi);
                        var ui = new ConsoleUiRenderer(Gui, _color); _activeUi = ui;
                        newActive.AddListener(ui);
                        Gui.BlankLine();
                        _color.TagBold(_color.Green, "Session", $"Switched to [{newActive.Key}] {newActive.GetStatusSummary()}");
                        Gui.BlankLine();
                    }
                    else _color.TagBold(_color.Red, "Session", $"No session at index {idx}");
                }
                else if (sessionManager.SwitchTo(arg))
                {
                    var newActive = sessionManager.ActiveSession!;
                    foreach (var s in sessionManager.List())
                        if (s != newActive) if (_activeUi != null) s.RemoveListener(_activeUi);
                    var ui = new ConsoleUiRenderer(Gui, _color); _activeUi = ui;
                    newActive.AddListener(ui);
                    Gui.BlankLine();
                    _color.TagBold(_color.Green, "Session", $"Switched to [{newActive.Key}]");
                    Gui.BlankLine();
                }
                else _color.TagBold(_color.Red, "Session", $"No session with key '{arg}'");
                return;
            }
            case "session-new":
            {
                var name = string.IsNullOrEmpty(arg) ? $"session-{DateTime.UtcNow:HHmmss}" : arg;
                try
                {
                    var newSession = sessionManager.CreateSession(name, label: arg);
                    await InitSessionAsync(newSession, workingDir, bgMgr, userConfigDir);
                    Gui.BlankLine();
                    _color.TagBold(_color.Green, "Session", $"Created [{name}]. Use 'session <index>' to switch.");
                    Gui.BlankLine();
                }
                catch (Exception ex)
                {
                    _color.TagBold(_color.Red, "Session", $"Failed: {ex.Message}");
                }
                return;
            }
            case "session-stop":
            {
                if (int.TryParse(arg, out var stopIdx))
                {
                    var s = sessionManager.GetByIndex(stopIdx);
                    if (s != null) { s.Stop(); _color.TagBold(_color.Yellow, "Session", $"Stopped [{s.Key}]."); }
                    else _color.TagBold(_color.Red, "Session", $"No session at index {stopIdx}");
                }
                return;
            }
            case "session-close":
            {
                if (int.TryParse(arg, out var closeIdx))
                {
                    try
                    {
                        await sessionManager.CloseSessionAsync(sessionManager.GetByIndex(closeIdx)?.Key ?? "");
                        _color.TagBold(_color.Green, "Session", $"Closed session {closeIdx}.");
                    }
                    catch (Exception ex) { _color.TagBold(_color.Red, "Session", $"Failed: {ex.Message}"); }
                }
                return;
            }
            case "session-peek":
            {
                if (int.TryParse(arg, out var peekIdx))
                {
                    var s = sessionManager.GetByIndex(peekIdx);
                    if (s != null)
                    {
                        Gui.BlankLine();
                        _color.TagBold(_color.Cyan, "Peek", $"[{s.Key}] last 5 lines:");
                        var history = s.ReadOutputHistory(5);
                        foreach (var e in history)
                            Gui.WriteLineColored($"  {e.Text}");
                        Gui.BlankLine();
                    }
                }
                return;
            }
            case "session-rename":
            {
                // session-rename <n> <newlabel>
                var renameParts = arg.Split(' ', 2);
                if (renameParts.Length == 2 && int.TryParse(renameParts[0], out var renameIdx))
                {
                    if (sessionManager.RenameSession(renameIdx, renameParts[1]))
                        _color.TagBold(_color.Green, "Session", $"Renamed session {renameIdx} to '{renameParts[1]}'");
                    else
                        _color.TagBold(_color.Red, "Session", $"No session at index {renameIdx}");
                }
                else
                {
                    _color.Tag(_color.Cyan, "Session", "Usage: session-rename <n> <label>");
                }
                return;
            }
            case "session-info":
            {
                // session-info [n] — detailed info about a session
                AgentSession? infoSession;
                if (int.TryParse(arg, out var infoIdx))
                    infoSession = sessionManager.GetByIndex(infoIdx);
                else
                    infoSession = sessionManager.ActiveSession;
                if (infoSession != null)
                {
                    Gui.BlankLine();
                    Gui.WriteLineColored(infoSession.GetDetailedInfo());
                    Gui.BlankLine();
                }
                else _color.Tag(_color.Cyan, "Session", "No active session.");
                return;
            }
            case "session-queue":
            {
                var q = sessionManager.ActiveSession?.GetQueue() ?? new List<string>();
                Gui.BlankLine();
                if (q.Count == 0)
                    _color.Tag(_color.Cyan, "Queue", "Empty");
                else
                {
                    _color.TagBold(_color.Cyan, "Queue", $"{q.Count} pending:");
                    for (int i = 0; i < q.Count; i++)
                        Gui.WriteLineColored($"  {i}: {TruncatePrompt(q[i])}");
                }
                Gui.BlankLine();
                return;
            }
            case "session-queue-remove":
            {
                if (int.TryParse(arg, out var qi))
                {
                    if (sessionManager.ActiveSession?.RemoveFromQueue(qi) == true)
                        _color.TagBold(_color.Green, "Queue", $"Removed prompt {qi}");
                    else _color.TagBold(_color.Red, "Queue", $"No prompt at index {qi}");
                }
                return;
            }
            case "session-queue-clear":
            {
                sessionManager.ActiveSession?.ClearQueue();
                _color.Tag(_color.Green, "Queue", "Cleared.");
                return;
            }
        }

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
        FileWatcherService fileWatcher,
        string userConfigDir)
    {
        Gui.BlankLine();
        _color.TagBold(_color.Cyan, "ECLoop", "Type your request (help | quit)");
        Gui.WriteLine("===========================================");
        _color.TagBold(_color.Cyan, "Mode", "The agent decides tools automatically.");
        Gui.BlankLine();

        while (true)
        {
            var input = Gui.PromptRaw(_color.Cyan + "> " + _color.Reset)?.Trim();
            if (string.IsNullOrEmpty(input)) continue;

            await ProcessInputAsync(input, activeSession, sessionManager, workingDir, bgMgr, fileWatcher, userConfigDir);

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
        _color.TagBold(_color.Cyan, "Tools", $"{agent.Tools.Count} registered:");
        Gui.BlankLine();
        foreach (var t in agent.Tools)
        {
            _color.WriteLine(_color.Yellow + _color.Bold, $"  {t.Name}");
            _color.WriteLine(_color.Dim, $"    {t.Description}");
            _color.WriteLine(_color.Dim, $"    Example: {t.UsageExample}");
            Gui.BlankLine();
        }
    }

    private static async Task PrintHelp()
    {
        Gui.BlankLine();
        _color.TagBold(_color.Cyan, "Commands", "");
        Gui.BlankLine();
        _color.WriteLine(_color.Yellow + _color.Bold, "  <type request>       Multi-step agent execution");
        _color.WriteLine(_color.Yellow + _color.Bold, "  stop                 Stop the running session (keeps app alive)");
        _color.WriteLine(_color.Yellow + _color.Bold, "  ESC                  Stop generation mid-stream (during token output)");
        _color.WriteLine(_color.Yellow + _color.Bold, "  quit / exit          Stop all sessions and exit the application");
        _color.WriteLine(_color.Yellow + _color.Bold, "  help                 Show this help");
        _color.WriteLine(_color.Yellow + _color.Bold, "  tools                List registered tools");
        Gui.BlankLine();
        _color.WriteLine(_color.Dim, "  Context:");
        _color.WriteLine(_color.Yellow + _color.Bold, "  clear-history        Clear conversation history");
        _color.WriteLine(_color.Yellow + _color.Bold, "  save-context         Save transcript to disk");
        _color.WriteLine(_color.Yellow + _color.Bold, "  file-pick            Open file picker, send to LLM");
        Gui.BlankLine();
        _color.WriteLine(_color.Dim, "  Memory:");
        _color.WriteLine(_color.Yellow + _color.Bold, "  memory-save          Save a memory entry");
        _color.WriteLine(_color.Yellow + _color.Bold, "  memory-query         Search memory");
        _color.WriteLine(_color.Yellow + _color.Bold, "  memory-stats         Memory statistics");
        _color.WriteLine(_color.Yellow + _color.Bold, "  vecmem-stats         Vector memory statistics");
        _color.WriteLine(_color.Yellow + _color.Bold, "  vecmem-search        Semantic memory search");
        _color.WriteLine(_color.Yellow + _color.Bold, "  vecmem-add           Add vector memory entry");
        Gui.BlankLine();
        _color.WriteLine(_color.Dim, "  Sessions (v10.20):");
        _color.WriteLine(_color.Yellow + _color.Bold, "  sessions             List all sessions with status");
        _color.WriteLine(_color.Yellow + _color.Bold, "  session <n>          Switch to session n (render history + live)");
        _color.WriteLine(_color.Yellow + _color.Bold, "  session-new <name>   Create a new session");
        _color.WriteLine(_color.Yellow + _color.Bold, "  session-stop <n>     Stop session n's execution");
        _color.WriteLine(_color.Yellow + _color.Bold, "  session-close <n>    Close and delete session n");
        _color.WriteLine(_color.Yellow + _color.Bold, "  session-peek <n>     Quick glance at session n's output");
        _color.WriteLine(_color.Yellow + _color.Bold, "  session-rename <n> <label>  Rename session n");
        _color.WriteLine(_color.Yellow + _color.Bold, "  session-info [n]     Detailed session info (KV cache, context, tools)");
        _color.WriteLine(_color.Yellow + _color.Bold, "  session-queue         Show active session's prompt queue");
        _color.WriteLine(_color.Yellow + _color.Bold, "  session-queue-remove <i>  Remove prompt i from queue");
        _color.WriteLine(_color.Yellow + _color.Bold, "  session-queue-clear  Clear active session's queue");
        Gui.BlankLine();
        _color.WriteLine(_color.Dim, "  Context:");
        _color.WriteLine(_color.Yellow + _color.Bold, "  context-status       Show context window usage and summarize threshold");;
        Gui.BlankLine();
        _color.WriteLine(_color.Dim, "  Background:");
        _color.WriteLine(_color.Yellow + _color.Bold, "  bg-run <cmd>         Start a background process");
        _color.WriteLine(_color.Yellow + _color.Bold, "  bg-status            List background processes");
        _color.WriteLine(_color.Yellow + _color.Bold, "  bg-output <id>       Get output from a process");
        _color.WriteLine(_color.Yellow + _color.Bold, "  bg-kill <id>         Kill a background process");
        _color.WriteLine(_color.Yellow + _color.Bold, "  bg-cleanup           Remove finished processes");
        Gui.BlankLine();
        _color.WriteLine(_color.Dim, "  Files & Watch:");
        _color.WriteLine(_color.Yellow + _color.Bold, "  watch                Show recent file changes");
        _color.WriteLine(_color.Yellow + _color.Bold, "  watch-start          Start watching for changes");
        _color.WriteLine(_color.Yellow + _color.Bold, "  watch-stop           Stop watching");
        Gui.BlankLine();
        _color.WriteLine(_color.Dim, "  System:");
        _color.WriteLine(_color.Yellow + _color.Bold, "  reload-config        Reload appsettings.json");
        _color.WriteLine(_color.Yellow + _color.Bold, "  swap-model           Switch GGUF model at runtime");
        _color.WriteLine(_color.Yellow + _color.Bold, "  clipboard-read       Read from Windows clipboard");
        _color.WriteLine(_color.Yellow + _color.Bold, "  clipboard-write      Write to Windows clipboard");
        _color.WriteLine(_color.Yellow + _color.Bold, "  log                  Show recent log entries");
        _color.WriteLine(_color.Yellow + _color.Bold, "  log-level            Set log level (debug/info/warn/error)");
        Gui.BlankLine();
        await Task.CompletedTask;
    }
}