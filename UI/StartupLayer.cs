using ECAssistant.Core;
namespace ECAssistant.TUI.UI;

/// <summary>
/// The startup/home layer — always present, never deleted.
/// 
/// During boot: shows loading messages (model weights, session discovery, config).
/// After boot: serves as a home screen with app status, model info, and session summary.
/// Accessible via /home command.
/// 
/// If no sessions exist, this is the default active layer.
/// </summary>
public sealed class StartupLayer : BaseLayer
{
    private readonly EColor _color;
    
    // ── Status info (updated by controller) ──
    private string _version = "";
    private string _modelPath = "";
    private string _secondaryModelPath = "";  // repurposed: background tasks status string
    private bool _secondaryEnabled;  // repurposed: background tasks enabled
    private string _workingDir = "";
    private string _configPath = "";
    private int _sessionCount;
    private string _activeSessionKey = "";
    
    public override string Name => "startup";
    
    public StartupLayer(EColor color)
    {
        _color = color;
    }
    
    /// <summary>Update the status info and refresh the home screen content.</summary>
    public void UpdateStatus(string version, string modelPath, string secondaryModelPath, bool secondaryEnabled, string workingDir, string configPath, int sessionCount, string activeSessionKey)
    {
        _version = version;
        _modelPath = modelPath;
        _secondaryModelPath = secondaryModelPath;
        _secondaryEnabled = secondaryEnabled;
        _workingDir = workingDir;
        _configPath = configPath;
        _sessionCount = sessionCount;
        _activeSessionKey = activeSessionKey;
        RebuildHomeScreen();
    }
    
    /// <summary>
    /// Rebuild the home screen content from status info.
    /// Keeps any loading messages that were added during boot (they stay in the buffer
    /// above the home screen content).
    /// </summary>
    private void RebuildHomeScreen()
    {
        _outputLines.Clear();
        _scrollOffset = 0;
        
        _outputLines.Add($"{_color.Cyan}{_color.Bold}  ECAssistant — Home{_color.Reset}");
        _outputLines.Add($"{_color.Dim}  ════════════════════════════════════════{_color.Reset}");
        _outputLines.Add("");
        _outputLines.Add($"{_color.Cyan}  Version:    {_color.Reset}{_version}");
        _outputLines.Add($"{_color.Cyan}  Model:      {_color.Reset}{Path.GetFileName(_modelPath)}");
        if (_secondaryEnabled && !string.IsNullOrEmpty(_secondaryModelPath))
            _outputLines.Add($"{_color.Cyan}  Bg Tasks:   {_color.Reset}{_secondaryModelPath} ✓");
        else
            _outputLines.Add($"{_color.Cyan}  Bg Tasks:   {_color.Dim}disabled{_color.Reset}");
        _outputLines.Add($"{_color.Cyan}  Config:     {_color.Reset}{_configPath}");
        _outputLines.Add($"{_color.Cyan}  WorkingDir: {_color.Reset}{_workingDir}");
        _outputLines.Add("");
        _outputLines.Add($"{_color.Cyan}  Sessions:   {_color.Reset}{_sessionCount} active" + 
            (_sessionCount > 0 && !string.IsNullOrEmpty(_activeSessionKey) ? $" (current: {_color.Green}{_activeSessionKey}{_color.Reset})" : ""));
        _outputLines.Add("");
        _outputLines.Add($"{_color.Dim}  ────────────────────────────────────────{_color.Reset}");
        _outputLines.Add("");
        _outputLines.Add($"{_color.Yellow}{_color.Bold}  Quick Start:{_color.Reset}");
        _outputLines.Add($"{_color.Dim}  • /session \u003cn\u003e       Switch to session n{_color.Reset}");
        _outputLines.Add($"{_color.Dim}  • /session-new \u003cname\u003e  Create a new session{_color.Reset}");
        _outputLines.Add($"{_color.Dim}  • /sessions         List all sessions{_color.Reset}");
        _outputLines.Add($"{_color.Dim}  • /menu             Show command menu{_color.Reset}");
        _outputLines.Add($"{_color.Dim}  • /home             Return to this screen{_color.Reset}");
        _outputLines.Add($"{_color.Dim}  • /quit             Exit ECAssistant{_color.Reset}");
        _outputLines.Add("");
        _outputLines.Add($"{_color.Dim}  ────────────────────────────────────────{_color.Reset}");
        _outputLines.Add("");
        _outputLines.Add($"{_color.Dim}  Boot log below ↓{_color.Reset}");
        _outputLines.Add("");
        
        _isDirty = true;
        RequestRepaint();
    }
    
    public override bool ProcessInput(string input)
    {
        // Let base handle common commands (/clear)
        if (base.ProcessInput(input)) return true;
        
        bool isCommand = input.StartsWith("/");
        if (!isCommand)
        {
            // Non-command on startup → show hint
            AddOutputLine($"{_color.Cyan}[Hint] Switch to a session first — use /session <n> or /session-new <name>{_color.Reset}");
            return true;
        }
        
        // Sessions listing and session info need SessionManager — controller handles them
        var cmd = input[1..].ToLower().Trim();
        var parts = cmd.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        switch (parts[0])
        {
            case "sessions":
            case "session-peek":
            case "session-stop":
            case "session-close":
            case "session-rename":
            case "session-info":
            case "session-queue":
            case "session-queue-remove":
            case "session-queue-clear":
            case "tools":
                // These need SessionManager or active session — controller handles them
                return false;
            default:
                // Unknown command
                AddOutputLine($"{_color.Red}[Error] Unknown command: {input}{_color.Reset}");
                return true;
        }
    }
}