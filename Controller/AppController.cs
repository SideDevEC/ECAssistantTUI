using ECAssistant.Core;
using System.Collections.Concurrent;
using ECAssistant.Core.Config;
using ECAssistant.Core.Engine;
using ECAssistant.Core.Orchestration;
using ECAssistant.Core.Setup;
using ECAssistant.Core.Tools;
using ECAssistant.Core.Tools.Shell;
using ECAssistant.Core.Tools.Background;
using ECAssistant.TUI.UI;
using ECAssistant.Core.Session;
using ECAssistant.TUI.Session;
using ECAssistant.Core.Services;
using ECAssistant.Core.Analysis;
using ECAssistant.Core.Interfaces;

namespace ECAssistant.TUI.Controller;

/// <summary>
/// Application controller — the binder between EGuiConsole, layers, and Core.
///
/// Created by Program.cs. Owns the EGuiConsole, all layers, and the session
/// manager. Receives callbacks from EGuiConsole (OnPrompt, OnEscapePressed)
/// and decides what to do: parse commands, switch layers, route prompts.
///
/// Program.cs only interacts with this class. Nothing above the controller
/// knows about EGuiConsole, layers, or Core sessions.
/// </summary>
public sealed class AppController : IAppController
{
    // ── Dependencies (injected from Program.cs) ──
    private readonly EAgentConfig _config;
    private readonly string _modelPath;

    /// <summary>
    /// Access the active session's vector memory store.
    /// Null until sessions are initialized. Exposed for library consumers
    /// (e.g. ECSQL PatternExtractor) that need to access vector memory.
    /// </summary>
    public ECAssistant.Core.Memory.VectorMemoryStore? VectorMemory =>
        _sessionManager?.ActiveSession?.VectorMemory;

    /// <summary>
    /// Access the active session for library consumers.
    /// </summary>
    public ECAssistant.Core.Session.AgentSession? ActiveSession =>
        _sessionManager?.ActiveSession;
    private readonly string _workingDir;
    private readonly string _userConfigDir;
    private readonly ILogger _logger;
    private readonly EColor _color;
    private readonly List<EToolBase>? _externalTools;
    
    // ── Owned objects ──
    private readonly IGuiConsole _console;
    private readonly ConcurrentDictionary<string, BaseLayer> _layers = new();
    private BaseLayer? _activeLayer;
    private string? _previousLayerKey; // for help → return to prior layer
    private readonly List<string> _helpTopicStack = new(); // /menu submenu navigation (topic back-stack)
    private string _currentHelpTopic = "";
    
    // ── Core objects ──
    private SessionManager? _sessionManager;
    private readonly BackgroundProcessManager _bgMgr;
    private readonly IAiSetupResetter _setupResetter;
    private FileWatcherService? _fileWatcher;
    
    // ── Session → Layer mapping ──
    // Each Core session has a corresponding SessionLayer
    private readonly ConcurrentDictionary<string, SessionLayer> _sessionLayers = new();

    // ── Session → Renderer mapping ──
    // Each Core session has a ConsoleUiRenderer bridging listener events to
    // its layer; kept so it can be disposed/listener-removed on close.
    private readonly ConcurrentDictionary<string, ConsoleUiRenderer> _renderers = new();
    
    // ── Startup layer (always present, never deleted) ──
    private StartupLayer? _startupLayer;
    
    // v12.9: matches the latest version referenced in code comments (AnsiInputParser delegation).
    private const string VersionString = "v12.9";
    
    public AppController(
        IGuiConsole console,
        EAgentConfig config,
        string modelPath,
        string workingDir,
        string userConfigDir,
        ILogger logger)
        : this(console, config, modelPath, workingDir, userConfigDir, logger, null, null, null)
    {
    }

    public AppController(
        IGuiConsole console,
        EAgentConfig config,
        string modelPath,
        string workingDir,
        string userConfigDir,
        ILogger logger,
        List<EToolBase>? externalTools)
        : this(console, config, modelPath, workingDir, userConfigDir, logger, externalTools, null, null)
    {
    }

    public AppController(
        IGuiConsole console,
        EAgentConfig config,
        string modelPath,
        string workingDir,
        string userConfigDir,
        ILogger logger,
        List<EToolBase>? externalTools,
        BackgroundProcessManager? backgroundProcesses,
        FileWatcherService? fileWatcher,
        IAiSetupResetter? setupResetter = null)
    {
        _config = config;
        _modelPath = modelPath;
        _workingDir = workingDir;
        _userConfigDir = userConfigDir;
        _logger = logger;
        _color = new EColor();
        _externalTools = externalTools;
        _bgMgr = backgroundProcesses ?? new BackgroundProcessManager();
        _setupResetter = setupResetter ?? new AiSetupResetter();
        _fileWatcher = fileWatcher;
        _console = console;
        _console.SetCallbacks(OnPrompt, OnEscapePressed);
        
        // ── Create the startup layer immediately — it's always present ──
        _startupLayer = new StartupLayer(_color);
        _layers[_startupLayer.Name] = _startupLayer;
    }
    
    // ═══════════════════════════════════════════════════
    //  LIFECYCLE
    // ═══════════════════════════════════════════════════
    
    /// <summary>
    /// Start the application. Initializes everything, enters the input loop.
    /// Blocks until the user quits.
    /// </summary>
    public async Task<int> RunAsync()
    {
        // ── Initialize ──
        var initResult = await InitializeAppAsync();
        if (initResult != 0)
            return initResult;

        // ── Input loop ──
        while (_sessionManager != null && !_console.IsQuitRequested)
        {
            _sessionManager.MarkUserActivity();
            var s = _sessionManager.ActiveSession;
            _console.SetSilentInputInitial(s != null && s.RunState == SessionRunState.Running);
            _console.PromptRaw("");
        }

        // ── Shutdown ──
        await ShutdownAppAsync();
        return 0;
    }

    /// <summary>
    /// When no usable models are detected, run the first-run installer wizard
    /// (model-catalog.json → HuggingFace download → llm-server.json config).
    /// </summary>
    private async Task RunFirstRunSetupIfNeededAsync()
    {
        await RunSetupFlowAsync(onlyIfNeeded: true);
    }

    /// <summary>
    /// Full installation flow: local/remote choice, model catalog + downloads, config wiring.
    /// With onlyIfNeeded=false it runs even when models are already configured (/reinstall).
    /// </summary>
    private async Task RunSetupFlowAsync(bool onlyIfNeeded)
    {
        try
        {
            var llmRoot = Path.Combine(_userConfigDir, "llm");
            var modelsDir = Path.Combine(llmRoot, "models");
            var catalogPath = Path.Combine(_workingDir, "model-catalog.json");
            var serverConfigPath = Path.Combine(llmRoot, "llm-server.json");
            var appsettingsPath = Path.Combine(_userConfigDir, "appsettings.json");

            var catalog = ModelCatalogDocument.Load(catalogPath);
            var validationError = catalog.Validate();
            if (validationError != null)
            {
                _console.WriteLineColored(_color.Yellow + $"[Setup] model-catalog.json is invalid: {validationError} — skipping first-run setup." + _color.Reset);
                return;
            }

            var detector = new FirstRunDetector(modelsDir, serverConfigPath);
            var status = detector.Evaluate(catalog.Models);
            if (onlyIfNeeded && !status.NeedsSetup) return;

            // Heads-up when first-run fires but a previous setup exists (remote config, etc.)
            if (File.Exists(appsettingsPath))
            {
                var existing = File.ReadAllText(appsettingsPath);
                if (existing.Contains("\"llm_providers\"", StringComparison.OrdinalIgnoreCase))
                    _console.WriteLineColored(_color.Yellow +
                        "[Setup] Detected a previous AI configuration — the wizard below will replace it. " +
                        "(Use /reinstall for a full reset.)" + _color.Reset);
            }

            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ECAssistant-Installer/1.0");
            var installer = new ModelInstallerService(http, modelsDir, serverConfigPath, appsettingsPath);
            // v12.7: TUI and Console share the SAME staged installer wizard (initial install == /reinstall).
            var wizard = new SetupWizard(new TuiSetupUi(_console));
            await wizard.RunAsync(new WizardContext
            {
                AppsettingsPath = appsettingsPath,
                UserConfigDir = _userConfigDir,
                Catalog = catalog,
                InstalledEntryIds = status.InstalledEntryIds,
                Installer = installer,
                Probe = new RemoteModelProbe(),
                GpuLayers = 0,
                ModelsDir = modelsDir
            });
        }
        catch (Exception ex)
        {
            // L6: surface the full exception to the logger, not just the message.
            _logger.Error("Setup", $"First-run setup failed: {ex}");
            _console.WriteLineColored(_color.Yellow + $"[Setup] First-run setup skipped: {ex.Message}" + _color.Reset);
        }
    }

    /// <summary>
    /// One-time initialization: services, session manager, layers, watchdog.
    /// Returns 0 on success, 1 on failure.
    /// </summary>
    private async Task<int> InitializeAppAsync()
    {
        // Bind startup layer
        _startupLayer!.BindToConsole(_console);
        _console.SetActiveLayer(_startupLayer);
        _activeLayer = _startupLayer;
        _console.WriteLineColored(_color.Cyan + _color.Bold + $"[ECAssistant] {VersionString}" + _color.Reset);

        // Background process manager (initialized in constructor — never null here)
        _console.WriteLineColored(_color.Cyan + _color.Bold + "[Background] Process manager ready." + _color.Reset);

        // File watcher
        _fileWatcher ??= new FileWatcherService(_workingDir, logger: _logger);
        _fileWatcher.Start();

        // Session manager + LLM server connection
        _console.WriteLineColored(_color.Cyan + _color.Bold + "[Sessions] Discovering sessions..." + _color.Reset);

        // ── First-run setup: offer model downloads from the editable catalog ──
        await RunFirstRunSetupIfNeededAsync();

        var discovered = new SessionDiscovery().DiscoverSessions(_workingDir);
        if (discovered.Count > 0)
            _console.WriteLineColored(_color.Cyan + "[Sessions] " + $"Found {discovered.Count} session(s): {string.Join(", ", discovered)}" + _color.Reset);
        else
            _console.WriteLineColored(_color.Cyan + "[Sessions] No existing sessions found — creating new 'main' session." + _color.Reset);

        _sessionManager = new SessionManager(_config, _modelPath, _workingDir, _logger);

        try
        {
            await _sessionManager.InitializeAsync();
        }
        catch (Exception ex)
        {
            _console.WriteLineColored(_color.Red + _color.Bold + $"[LLM Server] {ex.Message}" + _color.Reset);
            _console.WriteLineColored(_color.Yellow + "Start the LLM server first: cd ~/agent/ECAssistant/ECAssistantLLM/bin/Debug/net8.0 && dotnet ECAssistant.LLM.dll" + _color.Reset);
            return 1;
        }

        // Load sessions from disk
        var loading = new LoadingIndicator(_console, _color);
        loading.Start("Loading model weights");

        string activeKey;
        try
        {
            activeKey = await _sessionManager.LoadSessionsFromDiskAsync(async (session) =>
            {
                loading.UpdateLabel($"Initializing session '{session.Key}'");
                var builder = new SessionBuilder(_config, _workingDir, _userConfigDir, _logger, _bgMgr);

                var layer = new SessionLayer(session.Key);
                layer.CoreSession = session;
                layer.Color = _color;
                layer.WorkingDir = _workingDir;
                _layers[layer.Name] = layer;
                _sessionLayers[session.Key] = layer;

                var renderer = new ConsoleUiRenderer(layer, _color, session.GetStreamBuffer, (msg) => PromptApproval(msg));
                session.AddListener(renderer);
                _renderers[session.Key] = renderer;

                await builder.BuildAsync(session, _externalTools);
            });
        }
        catch (ModelLoadException mle)
        {
            loading.Stop();
            _console.WriteLineColored(_color.Red + _color.Bold + mle.ToDiagnosticString() + _color.Reset);
            _logger.Error("Startup", $"Model load failed: {mle.Phase}: {mle.Message}");
            _console.WriteLineColored(_color.Yellow + "\nFix the configuration in appsettings.json and restart." + _color.Reset);
            return 1;
        }
        catch (Exception ex)
        {
            loading.Stop();
            _console.WriteLineColored(_color.Red + _color.Bold + $"Failed to initialize: {ex.GetType().Name}: {ex.Message}" + _color.Reset);
            _logger.Error("Startup", $"Init failed: {ex}");
            _console.WriteLineColored(_color.Yellow + "\nFix the configuration and restart." + _color.Reset);
            return 1;
        }

        loading.Stop();

        // M8: null-check the active session before dereferencing it.
        var activeSession = _sessionManager.ActiveSession ?? _sessionManager.Main;
        if (activeSession == null)
        {
            _console.WriteLineColored(_color.Yellow + "[Ready] No active session — staying on the home screen." + _color.Reset);
            _console.BlankLine();
            _console.InitConsole();
            UpdateStartupLayerStatus();
            return 0;
        }

        _console.WriteLineColored(_color.Green + _color.Bold + "[Ready] " + $"Active session: {activeKey} ({_sessionManager.List().Count} total)" + _color.Reset);
        _console.BlankLine();

        // Enter alternate buffer
        _console.InitConsole();
        UpdateStartupLayerStatus();

        // Switch to active session layer
        if (_sessionManager.List().Count > 0)
        {
            var activeLayerKey = $"session:{activeKey}";
            if (_layers.TryGetValue(activeLayerKey, out var layerToBind))
            {
                SwitchToLayer(activeLayerKey);
                _console.WriteLineColored(_color.Cyan + _color.Bold + $"[{activeSession.Key}] Type your request (/menu for commands)" + _color.Reset);
                _console.WriteLine("===========================================");
                _console.WriteLineColored(_color.Cyan + _color.Bold + "[Mode] The agent decides tools automatically." + _color.Reset);
                _console.BlankLine();
            }
            else
            {
                // M8: the active session's layer was not created — fall back to home.
                _console.WriteLineColored(_color.Yellow + $"[Ready] No UI layer for active session '{activeKey}' — staying on the home screen." + _color.Reset);
            }
        }

        // Silent input check
        _console.SetSilentInputCheck(() =>
        {
            var s = _sessionManager?.ActiveSession;
            return s != null && s.RunState == SessionRunState.Running;
        });

        // Idle watchdog
        if (_sessionManager.IsLocalMode)
            _sessionManager.StartIdleWatchdog(idleTimeoutMin: 15);

        return 0;
    }

    /// <summary>
    /// Shutdown: stop sessions, disconnect from LLM server, restore console.
    /// </summary>
    private async Task ShutdownAppAsync()
    {
        // Stop stream pollers and detach renderers before disposing sessions
        foreach (var renderer in _renderers.Values)
            renderer.Dispose();
        _renderers.Clear();

        if (_sessionManager != null)
        {
            _sessionManager.StopAll();
            try { await _sessionManager.DisposeAsync(); }
            catch (Exception ex)
            {
                _console.WriteLineColored(_color.Red + $"[Shutdown] Error disposing session manager: {ex.Message}" + _color.Reset);
            }
        }
        _console.ShutdownConsole();
    }

    // ═══════════════════════════════════════════════════
    //  APPROVAL PROMPT
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Prompt the user for tool approval. Blocks until user responds.
    /// Called by ConsoleUiRenderer when a tool requires approval.
    /// </summary>
    private bool PromptApproval(string message)
    {
        _console.WriteLineColored(_color.Yellow + _color.Bold + $"\n⚠ APPROVAL REQUIRED" + _color.Reset);
        _console.WriteLineColored(_color.Yellow + message + _color.Reset);

        var input = _console.PromptColored("Approve? (y/n): ")?.Trim().ToLowerInvariant();
        var approved = input == "y" || input == "yes";

        _console.WriteLineColored(approved
            ? _color.Green + "✓ Approved" + _color.Reset
            : _color.Red + "✗ Denied" + _color.Reset);

        return approved;
    }
    
    // ═══════════════════════════════════════════════════
    //  LAYER MANAGEMENT
    // ═══════════════════════════════════════════════════
    
    /// <summary>Switch the active layer. Unbinds old, binds new, repaints.</summary>
    private void SwitchToLayer(string layerKey)
    {
        if (!_layers.TryGetValue(layerKey, out var newLayer))
            return;
        
        // Unbind old layer
        if (_activeLayer != null)
            _activeLayer.UnbindFromConsole();
        
        _console.SetActiveLayer(null);
        
        // Bind new layer
        newLayer.BindToConsole(_console);
        _console.SetActiveLayer(newLayer);
        _activeLayer = newLayer;
    }
    
    /// <summary>Switch to a layer and remember the previous one (for help → return).</summary>
    private void PushOverlayLayer(string layerKey)
    {
        if (_activeLayer != null)
            _previousLayerKey = _activeLayer.Name;
        SwitchToLayer(layerKey);
    }
    
    /// <summary>Return to the previous layer (from an overlay like help).</summary>
    private void PopOverlayLayer()
    {
        if (_previousLayerKey != null && _layers.ContainsKey(_previousLayerKey))
        {
            SwitchToLayer(_previousLayerKey);
            _previousLayerKey = null;
        }
    }
    
    // ═══════════════════════════════════════════════════
    //  CALLBACKS FROM EGUICONSOLE
    // ═══════════════════════════════════════════════════
    
    /// <summary>Called when user hits Enter. Receives the full input string.
    /// Controller handles only layer-switching commands. Everything else
    /// is delegated to the active layer's ProcessInput.
    /// </summary>
    /// <summary>
    /// Called when user hits Enter. Receives the full input string.
    /// Controller handles only layer-switching commands. Everything else
    /// is delegated to the active layer's ProcessInput.
    ///
    /// M1: genuinely async — no .GetAwaiter().GetResult() on the input thread.
    /// The console invokes this from its own input loop, so we dispatch onto a
    /// background task (same pattern as RunCommandTask) and let the continuation
    /// run off the input thread. This avoids blocking the input thread while an
    /// idle-reconnect or command completes.
    /// </summary>
    private void OnPrompt(string input)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // v12.6: wait for any in-progress idle-reconnect to fully restore the
                // client + KV sessions before processing. Messages raced the reconnect
                // before and were lost.
                await (_sessionManager?.MarkUserActivityAsync() ?? Task.CompletedTask).ConfigureAwait(false);
                await HandlePrompt(input).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Central exception guard for the async prompt path
                ReportCommandError("prompt", ex);
            }
        });
    }

    private async Task HandlePrompt(string input)
    {
        input = input.Trim();
        if (string.IsNullOrEmpty(input)) return;
        
        // ── Controller handles layer-switching commands only ──
        if (input.StartsWith("/"))
        {
            // Lowercase only the command word — user args (session names, labels, keys) keep their case
            var parts = input[1..].Trim().Split(' ', 2);
            var cmd = parts[0].ToLowerInvariant().Trim();
            var arg = parts.Length > 1 ? parts[1].Trim() : "";
            
            switch (cmd)
            {
                case "quit":
                case "exit":
                    _console.WriteLineColored(_color.Cyan + _color.Bold + "[Bye] Goodbye." + _color.Reset);
                    _console.BlankLine();
                    _console.Quit();
                    return;
                
                case "home":
                    GoHome();
                    return;
                
                case "menu":
                case "help":
                    ShowHelp(arg);
                    return;
                
                case "reinstall":
                    RunCommandTask(() => ReinstallAsync(), "reinstall");
                    return;
                
                case "config":
                    ShowConfig();
                    return;

                case "session":
                    SwitchSession(arg);
                    return;

                case "session-new":
                    RunCommandTask(() => CreateNewSessionAsync(arg), "session-new");
                    return;
            }
        }
        
        // ── Everything else goes to the active layer ──
        if (_activeLayer != null)
        {
            bool handled = _activeLayer.ProcessInput(input);
            if (handled) return;
        }
        
        // Layer returned false — it needs the controller for SessionManager commands
        HandleSessionManagerCommand(input);
    }
    
    /// <summary>Called when user hits ESC (with empty input buffer).</summary>
    private void OnEscapePressed()
    {
        if (_activeLayer is HelpLayer)
        {
            // ESC in /menu → back to previous topic, or out of the menu
            HelpGoBack();
        }
        else if (_activeLayer is ConfigLayer)
        {
            // ESC on config → return to prior layer
            PopOverlayLayer();
        }
        else if (_activeLayer is StartupLayer)
        {
            // ESC on startup → nothing to do
        }
        else
        {
            // ESC on session → stop running session
            var stopSession = _sessionManager?.ActiveSession;
            if (stopSession != null && stopSession.RunState == SessionRunState.Running)
            {
                stopSession.Stop();
                _console.WriteLineColored(_color.Red + _color.Bold + "[Stop] Cancelling active session..." + _color.Reset);
            }
        }
    }
    
    // ═══════════════════════════════════════════════════
    //  SESSION MANAGER COMMANDS (deferred by layers)
    // ═══════════════════════════════════════════════════
    
    /// <summary>
    /// Handle commands that layers deferred to the controller because they
    /// need access to SessionManager. These are session management commands
    /// that don't involve layer switching.
    /// </summary>
    private void HandleSessionManagerCommand(string input)
    {
        if (!input.StartsWith("/"))
        {
            // Non-command that wasn't handled by any layer — just ignore
            return;
        }
        
        var parts = input[1..].Trim().Split(' ', 2);
        var cmd = parts[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1].Trim() : "";
        
        switch (cmd)
        {
            case "sessions":
                ShowSessionList();
                return;
            
            case "session-peek":
                PeekSession(arg);
                return;
            
            case "session-stop":
                StopSession(arg);
                return;
            
            case "session-close":
                RunCommandTask(() => CloseSessionAsync(arg), "session-close");
                return;
            
            case "session-rename":
                RenameSession(arg);
                return;
            
            case "session-info":
                SessionInfo(arg);
                return;
            
            case "session-queue":
                ShowQueue();
                return;
            
            case "session-queue-remove":
                RemoveFromQueue(arg);
                return;
            
            case "session-queue-clear":
                _sessionManager?.ActiveSession?.ClearQueue();
                _console.WriteLineColored(_color.Green + "[Queue] Cleared." + _color.Reset);
                return;
            
            case "tools":
                ListTools();
                return;
            
            default:
                _console.WriteLineColored(_color.Red + $"[Error] Unknown command: {input}" + _color.Reset);
                return;
        }
    }
    
    // ═══════════════════════════════════════════════════
    //  COMMAND IMPLEMENTATIONS
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Central exception guard for async command handlers. Runs the work as a
    /// fire-and-forget task (never async void) and reports any unhandled
    /// exception to the console + logger instead of crashing the process.
    /// </summary>
    private void RunCommandTask(Func<Task> command, string label)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await command().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ReportCommandError(label, ex);
            }
        });
    }

    private void ReportCommandError(string label, Exception ex)
    {
        _logger.Error("Command:" + label, $"Unhandled error in /{label}", ex);
        try
        {
            _console.WriteLineColored(_color.Red + _color.Bold + $"[{label}] " + $"Unhandled error: {ex.GetType().Name}: {ex.Message}" + _color.Reset);
        }
        catch { /* M5: console unavailable — logger already has the details */ }
    }
    
    private void GoHome()
    {
        UpdateStartupLayerStatus();
        SwitchToLayer("startup");
    }
    
    /// <summary>Update the startup layer with current app status info.</summary>
    private void UpdateStartupLayerStatus()
    {
        if (_startupLayer == null) return;
        
        int sessionCount = _sessionManager?.List().Count ?? 0;
        string activeKey = _sessionManager?.ActiveSession?.Key ?? "none";
        string configPath = Path.Combine(_userConfigDir, "appsettings.json");
        
        // Background tasks info from config (replaces secondary model in v11.2+)
        bool decomposeLlm = _config.BackgroundTasks?.Decompose?.UseLlm ?? false;
        bool summarizeLlm = _config.BackgroundTasks?.Summarize?.UseLlm ?? false;
        string bgStatus = (decomposeLlm || summarizeLlm) ? "enabled" : "disabled";
        
        _startupLayer.UpdateStatus(VersionString, _modelPath, bgStatus, decomposeLlm || summarizeLlm, _workingDir, configPath, sessionCount, activeKey);
    }
    
    private void ShowHelp(string topic = "")
    {
        // Navigation: entering a submenu from within help pushes the current topic
        // so ESC walks back topic-by-topic before leaving the menu entirely.
        if (_activeLayer is HelpLayer)
            _helpTopicStack.Add(_currentHelpTopic);
        else
            _helpTopicStack.Clear();

        _currentHelpTopic = topic;
        RenderHelp(topic);
    }

    /// <summary>ESC inside /menu: back to previous topic, or out of the menu entirely.</summary>
    private void HelpGoBack()
    {
        if (_helpTopicStack.Count > 0)
        {
            var previous = _helpTopicStack[^1];
            _helpTopicStack.RemoveAt(_helpTopicStack.Count - 1);
            _currentHelpTopic = previous;
            RenderHelp(previous);
        }
        else
        {
            _currentHelpTopic = "";
            PopOverlayLayer();
        }
    }

    private void RenderHelp(string topic)
    {
        var helpLayer = new HelpLayer(_color, BuildHelpLines(topic));
        helpLayer.BuildContent();
        _layers["help"] = helpLayer;

        if (_activeLayer is HelpLayer)
            SwitchToLayer("help"); // already in menu — rebind to trigger repaint
        else
            PushOverlayLayer("help");
    }
    
    private void ShowConfig()
    {
        var configLayer = new ConfigLayer(_color);
        configLayer.BuildFromConfig(_config, _modelPath);
        _layers["config"] = configLayer;
        PushOverlayLayer("config");
    }
    
    private string[] BuildHelpLines(string topic = "")
    {
        var t = topic.Trim().ToLowerInvariant();
        var header = $"{_color.Cyan}{_color.Bold}  Commands (use / prefix){_color.Reset}";
        var footer = $"{_color.Dim}  Tip: /menu <topic> for details — topics: sessions, context, background, ai{_color.Reset}";

        switch (t)
        {
            case "sessions":
                return new[]
                {
                    $"{_color.Cyan}{_color.Bold}  Sessions{_color.Reset}",
                    "",
                    $"{_color.Yellow}{_color.Bold}  /sessions             List all sessions with status{_color.Reset}",
                    $"{_color.Yellow}{_color.Bold}  /session <n>          Switch to session n{_color.Reset}",
                    $"{_color.Yellow}{_color.Bold}  /session-new <name>   Create a new session{_color.Reset}",
                    $"{_color.Yellow}{_color.Bold}  /session-stop <n>     Stop session n's execution{_color.Reset}",
                    $"{_color.Yellow}{_color.Bold}  /session-close <n>    Close and delete session n{_color.Reset}",
                    $"{_color.Yellow}{_color.Bold}  /session-peek <n>     Quick glance at session n's output{_color.Reset}",
                    $"{_color.Yellow}{_color.Bold}  /session-rename <n> <label>  Rename session n{_color.Reset}",
                    $"{_color.Yellow}{_color.Bold}  /session-info [n]     Detailed session info{_color.Reset}",
                    "",
                    footer,
                };

            case "context":
                return new[]
                {
                    $"{_color.Cyan}{_color.Bold}  Context & Memory{_color.Reset}",
                    "",
                    $"{_color.Yellow}{_color.Bold}  /clear-history        Clear conversation history{_color.Reset}",
                    $"{_color.Yellow}{_color.Bold}  /save-context         Save transcript to disk{_color.Reset}",
                    $"{_color.Yellow}{_color.Bold}  /context-status       Show context window usage{_color.Reset}",
                    "",
                    footer,
                };

            case "background":
                return new[]
                {
                    $"{_color.Cyan}{_color.Bold}  Background processes{_color.Reset}",
                    "",
                    $"{_color.Yellow}{_color.Bold}  /bg-run <cmd>         Start a background process{_color.Reset}",
                    $"{_color.Yellow}{_color.Bold}  /bg-status            List background processes{_color.Reset}",
                    $"{_color.Yellow}{_color.Bold}  /bg-output <id>       Get output from a process{_color.Reset}",
                    $"{_color.Yellow}{_color.Bold}  /bg-kill <id>         Kill a background process{_color.Reset}",
                    $"{_color.Yellow}{_color.Bold}  /bg-cleanup           Remove finished processes{_color.Reset}",
                    "",
                    footer,
                };

            case "ai":
                return new[]
                {
                    $"{_color.Cyan}{_color.Bold}  AI / Installation{_color.Reset}",
                    "",
                    $"{_color.Yellow}{_color.Bold}  /reinstall            Reset AI setup (stops server, deletes keys) + reinstall{_color.Reset}",
                    $"{_color.Yellow}{_color.Bold}  /config               Show configuration values{_color.Reset}",
                    "",
                    footer,
                };
        }

        return new[]
        {
            $"{_color.Cyan}{_color.Bold}  Essentials{_color.Reset}",
            "",
            $"{_color.Yellow}{_color.Bold}  <type request>        Multi-step agent execution (no / prefix){_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  /stop / ESC           Stop the running session / generation{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  /clear                Clear console output{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  /home                 Go to home/startup screen{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  /tools                List registered tools{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  /quit or /exit        Stop all sessions and exit{_color.Reset}",
            "",
            $"{_color.Cyan}{_color.Bold}  Categories — /menu <topic>{_color.Reset}",
            "",
            $"{_color.Yellow}{_color.Bold}  /menu sessions        Session management (11 commands){_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  /menu context         History & context window (3 commands){_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  /menu background      Background processes (5 commands){_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  /menu ai              AI provider & installation (2 commands){_color.Reset}",
            "",
            footer,};
    }
    
    private void ListTools()
    {
        var session = _sessionManager?.ActiveSession;
        if (session == null) return;
        var agent = session.Engine;
        
        _console.BlankLine();
        _console.WriteLineColored(_color.Cyan + _color.Bold + $"[Tools] {agent.Tools.Count} registered:" + _color.Reset);
        _console.BlankLine();
        foreach (var t in agent.Tools)
        {
            _console.WriteLineColored(_color.Yellow + _color.Bold + $"  {t.Name}" + _color.Reset);
            _console.WriteLineColored(_color.Dim + $"    {t.Description}" + _color.Reset);
            _console.WriteLineColored(_color.Dim + $"    Example: {t.UsageExample}" + _color.Reset);
            _console.BlankLine();
        }
    }
    
    private void ShowSessionList()
    {
        if (_sessionManager == null) return;
        _console.BlankLine();
        var sessions = _sessionManager.List();
        int idx = 1;
        foreach (var s in sessions)
        {
            string marker = (s == _sessionManager.ActiveSession) ? " →" : "  ";
            string state = s.RunState.ToString().ToLower();
            _console.WriteLineColored($"{marker} {idx} [{s.Key}] {s.Label} ({state})");
            idx++;
        }
        _console.BlankLine();
    }
    
    private void SwitchSession(string arg)
    {
        if (_sessionManager == null || string.IsNullOrEmpty(arg))
        {
            _console.WriteLineColored(_color.Cyan + "[Session] Usage: session <n> | session <key>" + _color.Reset);
            return;
        }
        
        bool switched;
        if (int.TryParse(arg, out var idx))
        {
            switched = _sessionManager.SwitchTo(idx);
        }
        else
        {
            switched = _sessionManager.SwitchTo(arg);
        }
        
        if (switched)
        {
            var newActive = _sessionManager.ActiveSession!;
            var layerKey = $"session:{newActive.Key}";
            if (_layers.TryGetValue(layerKey, out var layer))
            {
                SwitchToLayer(layerKey);
                _console.WriteLineColored(_color.Green + _color.Bold + "[Session] " + $"Switched to [{newActive.Key}] {newActive.GetStatusSummary()}" + _color.Reset);
                _console.BlankLine();
            }
            else
            {
                // L3: the session switched but its UI layer was not created.
                _console.WriteLineColored(_color.Yellow + $"[Session] Switched to [{newActive.Key}], but no UI layer exists for it." + _color.Reset);
            }
        }
        else
        {
            _console.WriteLineColored(_color.Red + _color.Bold + "[Session] " + $"No session '{arg}'" + _color.Reset);
        }
    }
    
    private async Task CreateNewSessionAsync(string arg)
    {
        if (_sessionManager == null) return;

        var name = string.IsNullOrEmpty(arg) ? $"session-{DateTime.UtcNow:HHmmss}" : arg;
        var newSession = _sessionManager.CreateSession(name, label: arg);
        var builder = new SessionBuilder(_config, _workingDir, _userConfigDir, _logger, _bgMgr);

        // Create a SessionLayer for the new session
        var layer = new SessionLayer(newSession.Key);
        layer.CoreSession = newSession;
        layer.Color = _color;
        layer.WorkingDir = _workingDir;
        _layers[layer.Name] = layer;
        _sessionLayers[newSession.Key] = layer;

        // Create ConsoleUiRenderer that writes to the layer's buffer
        var renderer = new ConsoleUiRenderer(layer, _color, newSession.GetStreamBuffer, (msg) => PromptApproval(msg));
        newSession.AddListener(renderer);
        _renderers[newSession.Key] = renderer;

        await builder.BuildAsync(newSession, _externalTools);
        _console.BlankLine();
        _console.WriteLineColored(_color.Green + _color.Bold + "[Session] " + $"Created [{name}]. Use '/session <index>' to switch." + _color.Reset);
        _console.BlankLine();
    }
    
    /// <summary>
    /// /reinstall — reset AI configuration to first-run state: stop LLM server, delete API keys,
    /// reset provider config, delete generated server config (models are kept),
    /// then re-run the installation wizard. Requires explicit yes/no confirmation.
    /// </summary>
    private async Task ReinstallAsync()
    {
        _console.WriteLineColored(_color.Red + _color.Bold + "⚠ REINSTALL — reset your AI configuration:" + _color.Reset);
        _console.WriteLineColored(_color.Yellow + "  • Stop the local LLM server (if running)" + _color.Reset);
        _console.WriteLineColored(_color.Yellow + "  • Delete ALL stored API keys (keys/ folder)" + _color.Reset);
        _console.WriteLineColored(_color.Yellow + "  • Reset provider settings (remote/local) to first-run state" + _color.Reset);
        _console.WriteLineColored(_color.Yellow + "  • Delete the generated llm-server.json" + _color.Reset);
        _console.WriteLineColored(_color.Yellow + "  • Downloaded models are KEPT — the wizard detects them" + _color.Reset);
        _console.WriteLineColored(_color.Yellow + "  • Active conversations are stopped and will not carry over" + _color.Reset);
        _console.BlankLine();

        var answer = _console.PromptColored("Proceed with reinstall? (yes/no): ")?.Trim().ToLowerInvariant();
        if (answer != "y" && answer != "yes")
        {
            _console.WriteLineColored(_color.Green + "[Reinstall] Cancelled — nothing was changed." + _color.Reset);
            return;
        }

        try
        {
            // 1. Stop all sessions + the local LLM server
            if (_sessionManager != null)
            {
                _sessionManager.StopAll();
                await _sessionManager.StopLocalServerAsync();
            }

            // v12.7: verify the server actually went down before touching config files.
            if (!await WaitForServerShutdownAsync(10))
            {
                _console.WriteLineColored(_color.Red + _color.Bold +
                    "[Reinstall] The LLM server did not shut down. " +
                    "Close any other running ECAssistant application (other terminals/windows) and run /reinstall again." +
                    _color.Reset);
                return;
            }
            _console.WriteLineColored(_color.Green + "[Reinstall] LLM server stopped." + _color.Reset);

            // 2. Reset provider config + delete keys + generated server config (models are kept)
            ResetAiSetup();
            _console.WriteLineColored(_color.Green + "[Reinstall] Setup fully reset — keys, config and models deleted." + _color.Reset);
        }
        catch (Exception ex)
        {
            _console.WriteLineColored(_color.Red + $"[Reinstall] Reset error: {ex.Message} — continuing to setup." + _color.Reset);
        }

        // 3. Back to the installation wizard
        await RunSetupFlowAsync(onlyIfNeeded: false);
    }

    /// <summary>Polls the LLM server health endpoint until it stops responding (server down) or the timeout expires.</summary>
    private async Task<bool> WaitForServerShutdownAsync(int timeoutSec)
    {
        var endpoint = _config.LlmProvider.ResolvedEndpoint.TrimEnd('/');
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await http.GetAsync($"{endpoint}/eca/health");
                await Task.Delay(500); // still up — keep waiting
            }
            catch (HttpRequestException)
            {
                // Connection refused / reset = server down.
                return true;
            }
            catch (TaskCanceledException)
            {
                // M4: HttpClient 2s timeout — endpoint did not answer in time.
                // Treat as "down" so we don't spin forever on a hung server.
                return true;
            }
            catch (Exception ex)
            {
                // M4: only HttpRequestException/TaskCanceledException mean "server down".
                // Any other exception is unexpected — log it and keep polling instead of
                // falsely concluding the server stopped.
                _logger.Warn("Reinstall", $"Unexpected error probing LLM server health: {ex}");
                await Task.Delay(500);
            }
        }
        return false;
    }

    /// <summary>
    /// Reset all AI setup to first-run defaults: clear llm_providers, restore default local
    /// llm_provider, delete the keys/ folder and the generated llm-server.json.
    /// Downloaded GGUF model files are deliberately kept.
    /// </summary>
    private void ResetAiSetup() => _setupResetter.Reset(_userConfigDir);

    private void StopSession(string arg)
    {
        // L3: usage message when the index is missing or non-integer.
        if (_sessionManager == null || string.IsNullOrEmpty(arg) || !int.TryParse(arg, out var idx))
        {
            _console.WriteLineColored(_color.Cyan + "[Session] Usage: session-stop <n>" + _color.Reset);
            return;
        }
        var sessions = _sessionManager.List();
        if (idx >= 1 && idx <= sessions.Count)
        {
            var s = sessions[idx - 1];
            s.Stop();
            _console.WriteLineColored(_color.Yellow + _color.Bold + "[Session] " + $"Stopped [{s.Key}]." + _color.Reset);
        }
        else
        {
            _console.WriteLineColored(_color.Red + _color.Bold + "[Session] " + $"No session at index {idx}" + _color.Reset);
        }
    }
    
    private async Task CloseSessionAsync(string arg)
    {
        // L3: usage message when the index is missing or non-integer.
        if (_sessionManager == null || string.IsNullOrEmpty(arg) || !int.TryParse(arg, out var idx))
        {
            _console.WriteLineColored(_color.Cyan + "[Session] Usage: session-close <n>" + _color.Reset);
            return;
        }

        var sessions = _sessionManager.List();
        if (idx < 1 || idx > sessions.Count)
        {
            _console.WriteLineColored(_color.Red + _color.Bold + "[Session] " + $"No session at index {idx}" + _color.Reset);
            return;
        }

        var key = sessions[idx - 1].Key;

        // Detach the renderer (stop stream polling) and remove it as listener
        if (_renderers.TryGetValue(key, out var renderer))
        {
            sessions[idx - 1].RemoveListener(renderer);
            renderer.Dispose();
            _renderers.TryRemove(key, out _);
        }

        _sessionManager.DeleteSession(key);

        // Remove the layer for this session
        var layerKey = $"session:{key}";
        if (_layers.TryGetValue(layerKey, out var layer))
        {
            layer.UnbindFromConsole();
            _layers.TryRemove(layerKey, out _);
            _sessionLayers.TryRemove(key, out _);
        }

        // M3: if the closed session was active, switch to the new active session's layer
        // (or fall back to the startup/home layer) so the UI stays on a valid layer.
        var wasActive = (_sessionManager?.ActiveSession?.Key == key) ||
                        string.Equals(key, _activeLayer?.Name.Replace("session:", "", StringComparison.Ordinal), StringComparison.Ordinal);
        if (wasActive)
        {
            var newActive = _sessionManager?.ActiveSession;
            if (newActive != null && _layers.TryGetValue($"session:{newActive.Key}", out var nextLayer))
            {
                SwitchToLayer($"session:{newActive.Key}");
                _console.WriteLineColored(_color.Cyan + $"[Session] Now on [{newActive.Key}]." + _color.Reset);
            }
            else
            {
                // No remaining session layer — fall back to the home screen.
                GoHome();
            }
        }

        _console.WriteLineColored(_color.Green + _color.Bold + "[Session] " + $"Closed session {idx}." + _color.Reset);
    }
    
    private void PeekSession(string arg)
    {
        // L3: usage message when the index is missing or non-integer.
        if (_sessionManager == null || string.IsNullOrEmpty(arg) || !int.TryParse(arg, out var idx))
        {
            _console.WriteLineColored(_color.Cyan + "[Session] Usage: session-peek <n>" + _color.Reset);
            return;
        }
        var sessions = _sessionManager.List();
        if (idx < 1 || idx > sessions.Count)
        {
            _console.WriteLineColored(_color.Red + _color.Bold + "[Session] " + $"No session at index {idx}" + _color.Reset);
            return;
        }
        var s = sessions[idx - 1];
        if (s != null)
        {
            _console.BlankLine();
            _console.WriteLineColored(_color.Cyan + _color.Bold + "[Peek] " + $"[{s.Key}] last 5 lines:" + _color.Reset);
            var history = s.ReadOutputHistory(5);
            foreach (var e in history)
                _console.WriteLineColored($"  {e.Text}");
            _console.BlankLine();
        }
    }
    
    private void RenameSession(string arg)
    {
        if (_sessionManager == null) return;
        var renameParts = arg.Split(' ', 2);
        if (renameParts.Length == 2 && int.TryParse(renameParts[0], out var idx))
        {
            var sessions = _sessionManager.List();
            if (idx >= 1 && idx <= sessions.Count)
            {
                sessions[idx - 1].Rename(renameParts[1]);
                _console.WriteLineColored(_color.Green + _color.Bold + "[Session] " + $"Renamed session {idx} to '{renameParts[1]}'" + _color.Reset);
            }
            else
            {
                _console.WriteLineColored(_color.Red + _color.Bold + "[Session] " + $"No session at index {idx}" + _color.Reset);
            }
        }
        else
        {
            _console.WriteLineColored(_color.Cyan + "[Session] Usage: session-rename <n> <label>" + _color.Reset);
        }
    }
    
    private void SessionInfo(string arg)
    {
        if (_sessionManager == null) return;

        // L3: usage message when an index was given but is not a valid integer.
        if (!string.IsNullOrEmpty(arg) && !int.TryParse(arg, out _))
        {
            _console.WriteLineColored(_color.Cyan + "[Session] Usage: session-info [n]" + _color.Reset);
            return;
        }

        AgentSession? infoSession;
        if (int.TryParse(arg, out var idx))
        {
            var sessions = _sessionManager.List();
            infoSession = (idx >= 1 && idx <= sessions.Count) ? sessions[idx - 1] : null;
        }
        else
            infoSession = _sessionManager.ActiveSession;
        if (infoSession != null)
        {
            _console.BlankLine();
            _console.WriteLineColored(infoSession.GetDetailedInfo());
            _console.BlankLine();
        }
        else
        {
            _console.WriteLineColored(_color.Cyan + "[Session] No active session." + _color.Reset);
        }
    }
    
    private void ShowQueue()
    {
        var q = _sessionManager?.ActiveSession?.GetQueue() ?? new List<string>();
        _console.BlankLine();
        if (q.Count == 0)
            _console.WriteLineColored(_color.Cyan + "[Queue] Empty" + _color.Reset);
        else
        {
            _console.WriteLineColored(_color.Cyan + _color.Bold + "[Queue] " + $"{q.Count} pending:" + _color.Reset);
            for (int i = 0; i < q.Count; i++)
                _console.WriteLineColored($"  {i}: {TruncatePrompt(q[i])}");
        }
        _console.BlankLine();
    }
    
    private void RemoveFromQueue(string arg)
    {
        // L3: usage message when the index is missing or non-integer.
        if (string.IsNullOrEmpty(arg) || !int.TryParse(arg, out var qi))
        {
            _console.WriteLineColored(_color.Cyan + "[Queue] Usage: session-queue-remove <index>" + _color.Reset);
            return;
        }
        if (_sessionManager?.ActiveSession?.RemoveFromQueue(qi) == true)
            _console.WriteLineColored(_color.Green + _color.Bold + "[Queue] " + $"Removed prompt {qi}" + _color.Reset);
        else
            _console.WriteLineColored(_color.Red + _color.Bold + "[Queue] " + $"No prompt at index {qi}" + _color.Reset);
    }
    
    private static string TruncatePrompt(string prompt, int maxLen = 60)
    {
        if (string.IsNullOrEmpty(prompt)) return "";
        return prompt.Length <= maxLen ? prompt : prompt.Substring(0, maxLen) + "...";
    }
}