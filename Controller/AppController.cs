using ECAssistant.Config;
using ECAssistant.Engine;
using ECAssistant.Orchestration;
using ECAssistant.Tools;
using ECAssistant.Tools.Shell;
using ECAssistant.Tools.Background;
using ECAssistant.UI;
using ECAssistant.Session;
using ECAssistant.Services;
using ECAssistant.Analysis;
using ECAssistant.Interfaces;

namespace ECAssistant.Controller;

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
public sealed class AppController
{
    // ── Dependencies (injected from Program.cs) ──
    private readonly EAgentConfig _config;
    private readonly string _modelPath;
    private readonly string _workingDir;
    private readonly string _userConfigDir;
    private readonly ILogger _logger;
    private readonly EColor _color;
    private readonly List<EToolBase>? _externalTools;
    
    // ── Owned objects ──
    private readonly IGuiConsole _console;
    private readonly Dictionary<string, BaseLayer> _layers = new();
    private BaseLayer? _activeLayer;
    private string? _previousLayerKey; // for help → return to prior layer
    
    // ── Core objects ──
    private SessionManager? _sessionManager;
    private BackgroundProcessManager _bgMgr = null!;
    private FileWatcherService? _fileWatcher;
    
    // ── Session → Layer mapping ──
    // Each Core session has a corresponding SessionLayer
    private readonly Dictionary<string, SessionLayer> _sessionLayers = new();
    
    // ── Startup layer (always present, never deleted) ──
    private StartupLayer? _startupLayer;
    
    private const string VersionString = "v11.0";
    
    public AppController(
        IGuiConsole console,
        EAgentConfig config,
        string modelPath,
        string workingDir,
        string userConfigDir,
        ILogger logger)
        : this(console, config, modelPath, workingDir, userConfigDir, logger, null)
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
    {
        _config = config;
        _modelPath = modelPath;
        _workingDir = workingDir;
        _userConfigDir = userConfigDir;
        _logger = logger;
        _color = new EColor();
        _externalTools = externalTools;
        
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
        // ── Bind startup layer as active BEFORE any output ──
        // This ensures all startup messages go into the startup layer's buffer
        _startupLayer!.BindToConsole(_console);
        _console.SetActiveLayer(_startupLayer);
        _activeLayer = _startupLayer;
        
        // ── Startup output (goes to startup layer's buffer) ──
        _console.WriteLineColored(_color.Cyan + _color.Bold + $"[ECAssistant] {VersionString}" + _color.Reset);
        
        // ── Background process manager ──
        _bgMgr = new BackgroundProcessManager();
        _console.WriteLineColored(_color.Cyan + _color.Bold + "[Background] Process manager ready." + _color.Reset);
        
        // ── File watcher ──
        _fileWatcher = new FileWatcherService(_workingDir, logger: _logger);
        _fileWatcher.Start();
        
        // ── Session manager (loads model weights ONCE) ──
        _console.WriteLineColored(_color.Cyan + _color.Bold + "[Sessions] Discovering sessions..." + _color.Reset);
        
        var discovered = new SessionDiscovery().DiscoverSessions(_workingDir);
        if (discovered.Count > 0)
            _console.WriteLineColored(_color.Cyan + "[Sessions] " + $"Found {discovered.Count} session(s): {string.Join(", ", discovered)}" + _color.Reset);
        else
            _console.WriteLineColored(_color.Cyan + "[Sessions] No existing sessions found — creating new 'main' session." + _color.Reset);
        
        _sessionManager = new SessionManager(_config, _modelPath, _workingDir, _logger);
        
        var loading = new LoadingIndicator(_console, _color);
        loading.Start("Loading model weights");
        
        string activeKey = await _sessionManager.LoadSessionsFromDiskAsync(async (session) =>
        {
            loading.UpdateLabel($"Initializing session '{session.Key}'");
            var builder = new SessionBuilder(_config, _workingDir, _userConfigDir, _logger, _bgMgr);
            
            // Create a SessionLayer for this session
            var layer = new SessionLayer(session.Key);
            layer.CoreSession = session;
            layer.Color = _color;
            layer.WorkingDir = _workingDir;
            _layers[layer.Name] = layer;
            _sessionLayers[session.Key] = layer;
            
            // Create ConsoleUiRenderer that writes to the layer's buffer
            var renderer = new ConsoleUiRenderer(layer, _color, session.GetStreamBuffer);
            session.AddListener(renderer);
            
            await builder.BuildAsync(session, _externalTools);
        });
        
        loading.Stop();
        
        var mainSession = _sessionManager.Main;
        var activeSession = _sessionManager.ActiveSession ?? mainSession;
        _console.WriteLineColored(_color.Green + _color.Bold + "[Ready] " + $"Active session: {activeKey} ({_sessionManager.List().Count} total)" + _color.Reset);
        _console.BlankLine();
        
        // ── Enter alternate buffer + flush startup output into startup layer ──
        _console.InitConsole();
        
        // Update startup layer with current status info
        UpdateStartupLayerStatus();
        
        // ── Decide which layer to show first ──
        if (_sessionManager != null && _sessionManager.List().Count > 0)
        {
            // Sessions exist — switch to the active session's layer
            var activeLayerKey = $"session:{activeKey}";
            if (_layers.TryGetValue(activeLayerKey, out var layerToBind))
            {
                SwitchToLayer(activeLayerKey);
                
                // Show initial prompt on the session layer
                _console.WriteLineColored(_color.Cyan + _color.Bold + $"[{activeSession.Key}] Type your request (/help for commands)" + _color.Reset);
                _console.WriteLine("===========================================");
                _console.WriteLineColored(_color.Cyan + _color.Bold + "[Mode] The agent decides tools automatically." + _color.Reset);
                _console.BlankLine();
            }
        }
        else
        {
            // No sessions — stay on the startup layer as the default
            // User can create a session with /session-new
        }
        
        // ── Set up silent input check ──
        _console.SetSilentInputCheck(() =>
        {
            var s = _sessionManager?.ActiveSession;
            return s != null && s.RunState == SessionRunState.Running;
        });
        
        // ── Input loop — blocks here until quit ──
        while (!_console.IsQuitRequested)
        {
            // Set silent input state based on current session
            var s = _sessionManager.ActiveSession;
            _console.SetSilentInputInitial(s != null && s.RunState == SessionRunState.Running);
            
            _console.PromptRaw("");
        }
        
        // ── Shutdown ──
        if (_sessionManager != null)
            await _sessionManager.StopAllAsync();
        
        _console.ShutdownConsole();
        return 0;
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
    private void OnPrompt(string input)
    {
        input = input.Trim();
        if (string.IsNullOrEmpty(input)) return;
        
        // ── Controller handles layer-switching commands only ──
        if (input.StartsWith("/"))
        {
            var lowerInput = input[1..].ToLower().Trim();
            var parts = lowerInput.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var cmd = parts[0];
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
                
                case "help":
                    ShowHelp();
                    return;
                
                case "config":
                    ShowConfig();
                    return;
                
                case "session":
                    SwitchSession(arg);
                    return;
                
                case "session-new":
                    CreateNewSession(arg);
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
        if (_activeLayer is HelpLayer or ConfigLayer)
        {
            // ESC on help/config → return to prior layer
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
        
        var lowerInput = input[1..].ToLower().Trim();
        var parts = lowerInput.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0];
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
                CloseSession(arg);
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
        
        // Secondary model info from config
        string secondaryPath = _config.SecondaryModel?.ModelPath ?? "";
        bool secondaryEnabled = _config.SecondaryModel?.Enabled ?? false;
        
        _startupLayer.UpdateStatus(VersionString, _modelPath, secondaryPath, secondaryEnabled, _workingDir, configPath, sessionCount, activeKey);
    }
    
    private void ShowHelp()
    {
        var helpLines = BuildHelpLines();
        var helpLayer = new HelpLayer(_color, helpLines);
        helpLayer.BuildContent();
        _layers["help"] = helpLayer;
        PushOverlayLayer("help");
    }
    
    private void ShowConfig()
    {
        var configLayer = new ConfigLayer(_color);
        configLayer.BuildFromConfig(_config, _modelPath);
        _layers["config"] = configLayer;
        PushOverlayLayer("config");
    }
    
    private string[] BuildHelpLines()
    {
        return new[]
        {
            $"{_color.Cyan}{_color.Bold}  Commands (use / prefix){_color.Reset}",
            "",
            $"{_color.Yellow}{_color.Bold}  <type request>       Multi-step agent execution (no / prefix){_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  /stop                 Stop the running session{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  ESC                   Stop generation mid-stream{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  /clear                Clear console output{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  /quit or /exit        Stop all sessions and exit{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  /help                 Show this help{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  /config               Show configuration values{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  /home                 Go to home/startup screen{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  /tools                List registered tools{_color.Reset}",
            "",
            $"{_color.Dim}  Context:{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  /clear-history        Clear conversation history{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  /save-context         Save transcript to disk{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  /context-status       Show context window usage{_color.Reset}",
            "",
            $"{_color.Dim}  Sessions:{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  /sessions             List all sessions with status{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  /session <n>          Switch to session n{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  /session-new <name>   Create a new session{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  /session-stop <n>     Stop session n's execution{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  /session-close <n>    Close and delete session n{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  /session-peek <n>     Quick glance at session n's output{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  /session-rename <n> <label>  Rename session n{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  /session-info [n]     Detailed session info{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  /session-queue         Show prompt queue{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  /session-queue-remove <i>  Remove prompt i from queue{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  /session-queue-clear  Clear active session's queue{_color.Reset}",
            "",
            $"{_color.Dim}  Background:{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  /bg-run <cmd>         Start a background process{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  /bg-status            List background processes{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  /bg-output <id>       Get output from a process{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  /bg-kill <id>         Kill a background process{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  /bg-cleanup           Remove finished processes{_color.Reset}",
            "",
            $"{_color.Dim}  Files & Watch:{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  /watch                Show recent file changes{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  /watch-start          Start watching for changes{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  /watch-stop           Stop watching{_color.Reset}",
            "",
            $"{_color.Dim}  System:{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  /reload-config        Reload appsettings.json{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  /swap-model           Switch GGUF model at runtime{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  /clipboard-read       Read from clipboard{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  /clipboard-write      Write to clipboard{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  /log                  Show recent log entries{_color.Reset}",
            $"{_color.Yellow}{_color.Bold}  /log-level            Set log level{_color.Reset}",
        };
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
        _console.WriteLineColored(_sessionManager.GetStatusReport());
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
        string newKey;
        if (int.TryParse(arg, out var idx))
        {
            switched = _sessionManager.SwitchTo(idx);
            newKey = _sessionManager.ActiveSession?.Key ?? "";
        }
        else
        {
            switched = _sessionManager.SwitchTo(arg);
            newKey = arg;
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
        }
        else
        {
            _console.WriteLineColored(_color.Red + _color.Bold + "[Session] " + $"No session '{arg}'" + _color.Reset);
        }
    }
    
    private async void CreateNewSession(string arg)
    {
        if (_sessionManager == null) return;
        
        var name = string.IsNullOrEmpty(arg) ? $"session-{DateTime.UtcNow:HHmmss}" : arg;
        try
        {
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
            var renderer = new ConsoleUiRenderer(layer, _color, newSession.GetStreamBuffer);
            newSession.AddListener(renderer);
            
            await builder.BuildAsync(newSession, _externalTools);
            _console.BlankLine();
            _console.WriteLineColored(_color.Green + _color.Bold + "[Session] " + $"Created [{name}]. Use '/session <index>' to switch." + _color.Reset);
            _console.BlankLine();
        }
        catch (Exception ex)
        {
            _console.WriteLineColored(_color.Red + _color.Bold + "[Session] " + $"Failed: {ex.Message}" + _color.Reset);
        }
    }
    
    private void StopSession(string arg)
    {
        if (_sessionManager == null || !int.TryParse(arg, out var idx)) return;
        var s = _sessionManager.GetByIndex(idx);
        if (s != null)
        {
            s.Stop();
            _console.WriteLineColored(_color.Yellow + _color.Bold + "[Session] " + $"Stopped [{s.Key}]." + _color.Reset);
        }
        else
        {
            _console.WriteLineColored(_color.Red + _color.Bold + "[Session] " + $"No session at index {idx}" + _color.Reset);
        }
    }
    
    private async void CloseSession(string arg)
    {
        if (_sessionManager == null || !int.TryParse(arg, out var idx)) return;
        try
        {
            var key = _sessionManager.GetByIndex(idx)?.Key ?? "";
            await _sessionManager.CloseSessionAsync(key);
            
            // Remove the layer for this session
            var layerKey = $"session:{key}";
            if (_layers.TryGetValue(layerKey, out var layer))
            {
                layer.UnbindFromConsole();
                _layers.Remove(layerKey);
                _sessionLayers.Remove(key);
            }
            
            _console.WriteLineColored(_color.Green + _color.Bold + "[Session] " + $"Closed session {idx}." + _color.Reset);
        }
        catch (Exception ex)
        {
            _console.WriteLineColored(_color.Red + _color.Bold + "[Session] " + $"Failed: {ex.Message}" + _color.Reset);
        }
    }
    
    private void PeekSession(string arg)
    {
        if (_sessionManager == null || !int.TryParse(arg, out var idx)) return;
        var s = _sessionManager.GetByIndex(idx);
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
            if (_sessionManager.RenameSession(idx, renameParts[1]))
                _console.WriteLineColored(_color.Green + _color.Bold + "[Session] " + $"Renamed session {idx} to '{renameParts[1]}'" + _color.Reset);
            else
                _console.WriteLineColored(_color.Red + _color.Bold + "[Session] " + $"No session at index {idx}" + _color.Reset);
        }
        else
        {
            _console.WriteLineColored(_color.Cyan + "[Session] Usage: session-rename <n> <label>" + _color.Reset);
        }
    }
    
    private void SessionInfo(string arg)
    {
        if (_sessionManager == null) return;
        AgentSession? infoSession;
        if (int.TryParse(arg, out var idx))
            infoSession = _sessionManager.GetByIndex(idx);
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
        if (int.TryParse(arg, out var qi))
        {
            if (_sessionManager?.ActiveSession?.RemoveFromQueue(qi) == true)
                _console.WriteLineColored(_color.Green + _color.Bold + "[Queue] " + $"Removed prompt {qi}" + _color.Reset);
            else
                _console.WriteLineColored(_color.Red + _color.Bold + "[Queue] " + $"No prompt at index {qi}" + _color.Reset);
        }
    }
    
    private static string TruncatePrompt(string prompt, int maxLen = 60)
    {
        if (string.IsNullOrEmpty(prompt)) return "";
        return prompt.Length <= maxLen ? prompt : prompt.Substring(0, maxLen) + "...";
    }
}