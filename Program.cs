
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
// v10.23.3: App now uses AgentConfigBuilder from Core

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

                 // ── UI initialisation — create GUI in buffering mode ──
            // Output is queued until InitConsole() enters alternate buffer and flushes
            var guiConsole = new EGuiConsole();
            Gui = guiConsole;
            _color = new EColor();

            // ── Initialize structured logging ──
            var userConfigDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ECAssistant");
            var logPath = Path.Combine(userConfigDir, "ECAssistant.log");
            _logger = new Logger(logPath, LogLevel.Info);

            Gui.WriteLineColored(_color.Cyan + _color.Bold + "[ECAssistant] v10.23.3 — llama-sharp 0.27.0" + _color.Reset);

            // v10.23.3: Use AgentConfigBuilder from Core — same flow as library consumers
            // Working dir: ~/ECAssistant/eca-data/ (appends eca-data to base path)
            // Migrate existing ~/ECAssistant/appsettings.json to eca-data/ on first run
            var builder = AgentConfigBuilder.Create()
                .WorkingDirectory(userConfigDir);  // → ~/ECAssistant/eca-data/
            ApplyCommandLineArgsToBuilder(ref builder, args);

            // Migrate: if old appsettings.json exists in ~/ECAssistant/ but not in eca-data/, copy it
            var ecaDataDir = Path.Combine(userConfigDir, "eca-data");
            var oldConfigPath = Path.Combine(userConfigDir, "appsettings.json");
            var newConfigPath = Path.Combine(ecaDataDir, "appsettings.json");
            if (File.Exists(oldConfigPath) && !File.Exists(newConfigPath))
            {
                Directory.CreateDirectory(ecaDataDir);
                File.Copy(oldConfigPath, newConfigPath, overwrite: false);
                Gui.WriteLineColored(_color.Green + "[Setup] " + $"Migrated config to: {newConfigPath}" + _color.Reset);
            }

            _config = builder.Build();
            // Override working dir to ~/ECAssistant for backward compat (existing sessions, memory, etc.)
            _config.RootPath = userConfigDir;
            _config.AgentSettings.WorkingDirectory = userConfigDir;

            Gui.WriteLineColored(_color.Green + "[Config] " + $"Loaded from: {newConfigPath}" + _color.Reset);

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
                    Gui.WriteLineColored(_color.Red + _color.Bold + "[Error] " + ($"Model not found: {effectiveModelPath}") + _color.Reset);
                      Gui.WriteLineColored(_color.Cyan + "[Hint] " + ($"Put your .gguf model in: {userConfigDir} or set full path in appsettings.json") + _color.Reset);
                     }

            // v9.14: Default working dir to ~/ECAssistant if set to "."
            var effectiveDir = _config.AgentSettings.WorkingDirectory == "." || string.IsNullOrEmpty(_config.AgentSettings.WorkingDirectory)
                ? userConfigDir
                : Path.GetFullPath(_config.AgentSettings.WorkingDirectory);
              Directory.CreateDirectory(effectiveDir);
            Gui.WriteLineColored(_color.Cyan + _color.Bold + "[WorkDir] " + (effectiveDir) + _color.Reset);

           var rootDir = Path.GetFullPath(_config.RootPath);
            Directory.CreateDirectory(rootDir);
               Directory.CreateDirectory(Path.Combine(rootDir, _config.Memory.DataPath));
            Directory.CreateDirectory(Path.Combine(rootDir, _config.Workspace.Path));
              Gui.WriteLineColored(_color.Cyan + _color.Bold + "[Root] " + ($"{rootDir}") + _color.Reset);
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
                    Gui.WriteLineColored(_color.Green + _color.Bold + "[Model] " + ("Found at: " + effectiveModelPath) + _color.Reset);

                     // ── Background Process Manager (must be before tool registration) ──
                    var bgMgr = new BackgroundProcessManager();
                    Gui.WriteLineColored(_color.Cyan + _color.Bold + "[Background] Process manager ready." + _color.Reset);

                    // ── File Watcher (v9.7: workspace monitoring) ──
                    var fileWatcher = new FileWatcherService(effectiveDir, logger: _logger);
                    fileWatcher.Start();

                    // ── Session Manager (v10.21: shared weights, session discovery) ──
                    // Loads model weights ONCE. Sessions are loaded from disk afterwards.
                    var sessionManager = new SessionManager(_config, effectiveModelPath, effectiveDir, _logger);

                    // ── Loading phase: animated dots while sessions init ──
                    Gui.WriteLineColored(_color.Cyan + _color.Bold + "[Sessions] Discovering sessions..." + _color.Reset);

                    var discovered = new SessionDiscovery().DiscoverSessions(effectiveDir);
                    if (discovered.Count > 0)
                        Gui.WriteLineColored(_color.Cyan + "[Sessions] " + $"Found {discovered.Count} session(s): {string.Join(", ", discovered)}" + _color.Reset);
                    else
                        Gui.WriteLineColored(_color.Cyan + "[Sessions] No existing sessions found — creating new 'main' session." + _color.Reset);

                    var loading = new LoadingIndicator(Gui, _color);
                    loading.Start("Loading model weights");

                    string activeKey = await sessionManager.LoadSessionsFromDiskAsync(async (session) =>
                    {
                        loading.UpdateLabel($"Initializing session '{session.Key}'");
                        // v10.23: Use SessionBuilder from Core (replaces old InitSessionAsync)
                        var builder = new SessionBuilder(_config, effectiveDir, userConfigDir, _logger, bgMgr);
                        // Attach console UI listener before building
                        var uiRenderer = new ConsoleUiRenderer(Gui, _color); _activeUi = uiRenderer;
                        session.AddListener(uiRenderer);
                        await builder.BuildAsync(session);
                    });

                    loading.Stop();

                    var mainSession = sessionManager.Main;
                    var activeSession = sessionManager.ActiveSession ?? mainSession;
                    Gui.WriteLineColored(_color.Green + _color.Bold + "[Ready] " + $"Active session: {activeKey} ({sessionManager.List().Count} total)" + _color.Reset);
                    Gui.BlankLine();

                    // ── Enter alternate buffer + flush all startup output at once ──
                    guiConsole.InitConsole();

                   // v10.21.1: Simple console input loop — PromptRaw uses ReadKey (non-blocking)
                    await RunAgentLoop(activeSession, sessionManager, effectiveDir, bgMgr, fileWatcher, userConfigDir);
                        }
            else
                     {
                    // Flush buffered startup output, then show error
                    guiConsole.InitConsole();
                    Gui.WriteLineColored(_color.Red + "[Error] No model loaded. Use --model or update appsettings.json." + _color.Reset);
                    if (Gui is EGuiConsole gc1) gc1.ShutdownConsole();
                  return 1;
                     }

           // Save transcript on exit (cleanup via async)
             var transPath = Path.Combine(effectiveDir, ".sessions", "main", "transcript.json");
            if (File.Exists(transPath))
               {
                // Transcript already auto-saved in loop via "save-context" command
                 Gui.WriteLineColored(_color.Cyan + _color.Bold + "[Context] " + ($"Transcript saved at: {transPath}") + _color.Reset);
                }

            // Shutdown TUI — leave alternate buffer, restore terminal
            if (Gui is EGuiConsole gc)
                gc.ShutdownConsole();

            return 0;
             }

    // v10.23.3: Apply command-line args to AgentConfigBuilder (seeds initial JSON only)
    private static void ApplyCommandLineArgsToBuilder(ref AgentConfigBuilder builder, string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i].ToLower().TrimStart('-');
            switch (arg)
            {
                case "model":     if (i + 1 < args.Length) builder.WithModel(args[++i]); break;
                case "ctx":
                case "contextsize":
                    if (i + 1 < args.Length && uint.TryParse(args[++i], out uint ctx)) builder.ContextSize(ctx);
                    break;
                case "gpu":
                case "gpulayers":
                case "gpu_layers":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int layers)) builder.GpuLayers(Math.Clamp(layers, 0, 100));
                    break;
                case "threads":
                case "threadcount":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int thr)) builder.Threads(thr);
                    break;
                case "temp":
                case "temperature":
                    if (i + 1 < args.Length && float.TryParse(args[++i], out float t)) builder.Temperature(Math.Clamp(t, 0.0f, 2.0f));
                    break;
            }
        }
    }

    // ── Test Mode: Automated testing without console interaction ──

    private static async Task<int> RunTestsAsync(string[] testArgs)
    {
        // v10.22: --mock flag runs model-independent tests with MockEngine (no GGUF needed)
        bool useMock = testArgs.Any(a => a.Equals("--mock", StringComparison.OrdinalIgnoreCase));

        // v10.23.3: Use AgentConfigBuilder to load config
        var userConfigDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ECAssistant");
        var config = AgentConfigBuilder.Create()
            .WorkingDirectory(userConfigDir)
            .Build();
        var modelPath = config.Llm.ModelPath;

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
                Gui.WriteLineColored(_color.Cyan + _color.Bold + "[Bye] Goodbye." + _color.Reset);
                Gui.BlankLine();
                await sessionManager.StopAllAsync();
                _quitRequested = true;
                return;
            case "clear": Gui.ClearCanvas(); return;
            case "help": ShowHelpLayer(); return;
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
                Gui.WriteLineColored(_color.Cyan + "[Mode] Single-turn mode reset." + _color.Reset);
                orchestrator.Reset();
                return;
            case "stop":
            {
                var stopSession = sessionManager.ActiveSession;
                if (stopSession != null && stopSession.RunState == SessionRunState.Running)
                {
                    stopSession.Stop();
                    Gui.WriteLineColored(_color.Red + _color.Bold + "[Stop] Cancelling active session..." + _color.Reset);
                }
                else { Gui.WriteLineColored(_color.Cyan + "[Stop] Nothing is running." + _color.Reset); }
                return;
            }
            case "context-status":
            {
                var s = sessionManager.ActiveSession;
                if (s != null)
                {
                    Gui.BlankLine();
                    Gui.WriteLineColored(_color.Cyan + _color.Bold + "[Context] " + (s.Engine.ContextStatusSummary) + _color.Reset);
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
                    Gui.WriteLineColored(_color.Cyan + "[Session] Usage: session <n> | session <key>" + _color.Reset);
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
                        // Clear screen and render the new session's output history
                        Gui.ClearCanvas();
                        var history = newActive.ReadOutputHistory();
                        if (_activeUi != null) _activeUi.RenderHistory(history);
                        Gui.WriteLineColored(_color.Green + _color.Bold + "[Session] " + $"Switched to [{newActive.Key}] {newActive.GetStatusSummary()}" + _color.Reset);
                        Gui.BlankLine();
                    }
                    else Gui.WriteLineColored(_color.Red + _color.Bold + "[Session] " + $"No session at index {idx}" + _color.Reset);
                }
                else if (sessionManager.SwitchTo(arg))
                {
                    var newActive = sessionManager.ActiveSession!;
                    foreach (var s in sessionManager.List())
                        if (s != newActive) if (_activeUi != null) s.RemoveListener(_activeUi);
                    var ui = new ConsoleUiRenderer(Gui, _color); _activeUi = ui;
                    newActive.AddListener(ui);
                    // Clear screen and render the new session's output history
                    Gui.ClearCanvas();
                    var history = newActive.ReadOutputHistory();
                    if (_activeUi != null) _activeUi.RenderHistory(history);
                    Gui.WriteLineColored(_color.Green + _color.Bold + "[Session] " + $"Switched to [{newActive.Key}]" + _color.Reset);
                    Gui.BlankLine();
                }
                else Gui.WriteLineColored(_color.Red + _color.Bold + "[Session] " + $"No session with key '{arg}'" + _color.Reset);
                return;
            }
            case "session-new":
            {
                var name = string.IsNullOrEmpty(arg) ? $"session-{DateTime.UtcNow:HHmmss}" : arg;
                try
                {
                    var newSession = sessionManager.CreateSession(name, label: arg);
                    var builder = new SessionBuilder(_config, workingDir, userConfigDir, _logger, bgMgr);
                    // Attach console UI listener
                    var uiRenderer = new ConsoleUiRenderer(Gui, _color); _activeUi = uiRenderer;
                    newSession.AddListener(uiRenderer);
                    await builder.BuildAsync(newSession);
                    Gui.BlankLine();
                    Gui.WriteLineColored(_color.Green + _color.Bold + "[Session] " + ($"Created [{name}]. Use 'session <index>' to switch.") + _color.Reset);
                    Gui.BlankLine();
                }
                catch (Exception ex)
                {
                    Gui.WriteLineColored(_color.Red + _color.Bold + "[Session] " + ($"Failed: {ex.Message}") + _color.Reset);
                }
                return;
            }
            case "session-stop":
            {
                if (int.TryParse(arg, out var stopIdx))
                {
                    var s = sessionManager.GetByIndex(stopIdx);
                    if (s != null) { s.Stop(); Gui.WriteLineColored(_color.Yellow + _color.Bold + "[Session] " + ($"Stopped [{s.Key}].") + _color.Reset); }
                    else Gui.WriteLineColored(_color.Red + _color.Bold + "[Session] " + ($"No session at index {stopIdx}") + _color.Reset);
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
                        Gui.WriteLineColored(_color.Green + _color.Bold + "[Session] " + ($"Closed session {closeIdx}.") + _color.Reset);
                    }
                    catch (Exception ex) { Gui.WriteLineColored(_color.Red + _color.Bold + "[Session] " + ($"Failed: {ex.Message}") + _color.Reset); }
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
                        Gui.WriteLineColored(_color.Cyan + _color.Bold + "[Peek] " + ($"[{s.Key}] last 5 lines:") + _color.Reset);
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
                        Gui.WriteLineColored(_color.Green + _color.Bold + "[Session] " + ($"Renamed session {renameIdx} to '{renameParts[1]}'") + _color.Reset);
                    else
                        Gui.WriteLineColored(_color.Red + _color.Bold + "[Session] " + ($"No session at index {renameIdx}") + _color.Reset);
                }
                else
                {
                    Gui.WriteLineColored(_color.Cyan + "[Session] Usage: session-rename <n> <label>" + _color.Reset);
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
                else Gui.WriteLineColored(_color.Cyan + "[Session] No active session." + _color.Reset);
                return;
            }
            case "session-queue":
            {
                var q = sessionManager.ActiveSession?.GetQueue() ?? new List<string>();
                Gui.BlankLine();
                if (q.Count == 0)
                    Gui.WriteLineColored(_color.Cyan + "[Queue] Empty" + _color.Reset);
                else
                {
                    Gui.WriteLineColored(_color.Cyan + _color.Bold + "[Queue] " + ($"{q.Count} pending:") + _color.Reset);
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
                        Gui.WriteLineColored(_color.Green + _color.Bold + "[Queue] " + ($"Removed prompt {qi}") + _color.Reset);
                    else Gui.WriteLineColored(_color.Red + _color.Bold + "[Queue] " + ($"No prompt at index {qi}") + _color.Reset);
                }
                return;
            }
            case "session-queue-clear":
            {
                sessionManager.ActiveSession?.ClearQueue();
                Gui.WriteLineColored(_color.Green + "[Queue] Cleared." + _color.Reset);
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
        Gui.WriteLineColored(_color.Cyan + _color.Bold + $"[{activeSession.Key}] Type your request (help | quit)" + _color.Reset);
        Gui.WriteLine("===========================================");
        Gui.WriteLineColored(_color.Cyan + _color.Bold + "[Mode] The agent decides tools automatically." + _color.Reset);
        Gui.BlankLine();

        // Set ESC handler + silent input check — stops the active session
        if (Gui is EGuiConsole console)
        {
            console.SetHandlers(onSubmit: null, onEscape: () =>
            {
                var stopSession = sessionManager.ActiveSession;
                if (stopSession != null && stopSession.RunState == SessionRunState.Running)
                {
                    stopSession.Stop();
                }
            });
            // Silent input while session is running
            console.SetSilentInputCheck(() =>
            {
                var s = sessionManager.ActiveSession;
                return s != null && s.RunState == SessionRunState.Running;
            });
        }

        while (true)
        {
            // Set initial silent state based on session state before reading input
            if (Gui is EGuiConsole console2)
            {
                var s = sessionManager.ActiveSession;
                console2.SetSilentInputInitial(s != null && s.RunState == SessionRunState.Running);
            }

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
        Gui.WriteLineColored(_color.Cyan + _color.Bold + "[Tools] " + ($"{agent.Tools.Count} registered:") + _color.Reset);
        Gui.BlankLine();
        foreach (var t in agent.Tools)
        {
            Gui.WriteLineColored(_color.Yellow + _color.Bold +  $"  {t.Name}" + _color.Reset);
            Gui.WriteLineColored(_color.Dim +  $"    {t.Description}" + _color.Reset);
            Gui.WriteLineColored(_color.Dim +  $"    Example: {t.UsageExample}" + _color.Reset);
            Gui.BlankLine();
        }
    }

    private static void ShowHelpLayer()
    {
        if (Gui is EGuiConsole console)
        {
            var helpLines = BuildHelpLines();
            console.PushLayer(new HelpLayer(_color, helpLines));
        }
        else
        {
            // Non-ANSI fallback: print inline
            foreach (var line in BuildHelpLines())
                Gui.WriteLineColored(line);
            Gui.BlankLine();
        }
    }

    private static string[] BuildHelpLines()
    {
        return new[]
        {
            $"{_color.Cyan}{_color.Bold}  Commands{_color.Reset}",
            "",
            $"{_color.Yellow}{_color.Bold}  <type request>       Multi-step agent execution{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  stop                 Stop the running session (keeps app alive){_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  ESC                  Stop generation mid-stream (during token output){_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  clear                Clear console output{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  quit / exit          Stop all sessions and exit the application{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  help                 Show this help{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  tools                List registered tools{_color.Reset}",
            "",
            $"{_color.Dim}  Context:{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  clear-history        Clear conversation history{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  save-context         Save transcript to disk{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  file-pick            Open file picker, send to LLM{_color.Reset}",
            "",
            $"{_color.Dim}  Memory:{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  memory-save          Save a memory entry{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  memory-query         Search memory{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  memory-stats         Memory statistics{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  vecmem-stats         Vector memory statistics{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  vecmem-search        Semantic memory search{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  vecmem-add           Add vector memory entry{_color.Reset}",
            "",
            $"{_color.Dim}  Sessions:{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  sessions             List all sessions with status{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  session <n>          Switch to session n (render history + live){_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  session-new <name>   Create a new session{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  session-stop <n>     Stop session n's execution{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  session-close <n>    Close and delete session n{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  session-peek <n>     Quick glance at session n's output{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  session-rename <n> <label>  Rename session n{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  session-info [n]     Detailed session info (KV cache, context, tools){_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  session-queue         Show active session's prompt queue{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  session-queue-remove <i>  Remove prompt i from queue{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  session-queue-clear  Clear active session's queue{_color.Reset}",
            "",
            $"{_color.Dim}  Context:{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  context-status       Show context window usage and summarize threshold{_color.Reset}",
            "",
            $"{_color.Dim}  Background:{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  bg-run <cmd>         Start a background process{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  bg-status            List background processes{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  bg-output <id>       Get output from a process{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  bg-kill <id>         Kill a background process{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  bg-cleanup           Remove finished processes{_color.Reset}",
            "",
            $"{_color.Dim}  Files & Watch:{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  watch                Show recent file changes{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  watch-start          Start watching for changes{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  watch-stop           Stop watching{_color.Reset}",
            "",
            $"{_color.Dim}  System:{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  reload-config        Reload appsettings.json{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  swap-model           Switch GGUF model at runtime{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  clipboard-read       Read from Windows clipboard{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  clipboard-write      Write to Windows clipboard{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  log                  Show recent log entries{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  log-level            Set log level (debug/info/warn/error){_color.Reset}",
        };
    }
}