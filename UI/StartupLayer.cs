namespace ECAssistant.UI;

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
    public void UpdateStatus(string version, string modelPath, string workingDir, string configPath, int sessionCount, string activeSessionKey)
    {
        _version = version;
        _modelPath = modelPath;
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
        // Clear and rebuild with current status
        _outputLines.Clear();
        _scrollOffset = 0;
        
        _outputLines.Add($"{_color.Cyan}{_color.Bold}  ECAssistant — Home{_color.Reset}");
        _outputLines.Add($"{_color.Dim}  ════════════════════════════════════════{_color.Reset}");
        _outputLines.Add("");
        _outputLines.Add($"{_color.Cyan}  Version:    {_color.Reset}{_version}");
        _outputLines.Add($"{_color.Cyan}  Model:      {_color.Reset}{Path.GetFileName(_modelPath)}");
        _outputLines.Add($"{_color.Cyan}  Config:     {_color.Reset}{_configPath}");
        _outputLines.Add($"{_color.Cyan}  WorkingDir: {_color.Reset}{_workingDir}");
        _outputLines.Add("");
        _outputLines.Add($"{_color.Cyan}  Sessions:   {_color.Reset}{_sessionCount} active" + 
            (_sessionCount > 0 && !string.IsNullOrEmpty(_activeSessionKey) ? $" (current: {_color.Green}{_activeSessionKey}{_color.Reset})" : ""));
        _outputLines.Add("");
        _outputLines.Add($"{_color.Dim}  ────────────────────────────────────────{_color.Reset}");
        _outputLines.Add("");
        _outputLines.Add($"{_color.Yellow}{_color.Bold}  Quick Start:{_color.Reset}");
        _outputLines.Add($"{_color.Dim}  • Type a message to chat with the active session{_color.Reset}");
        _outputLines.Add($"{_color.Dim}  • /help     — Show all commands{_color.Reset}");
        _outputLines.Add($"{_color.Dim}  • /sessions — List all sessions{_color.Reset}");
        _outputLines.Add($"{_color.Dim}  • /session-new <name> — Create a new session{_color.Reset}");
        _outputLines.Add($"{_color.Dim}  • /home     — Return to this screen{_color.Reset}");
        _outputLines.Add($"{_color.Dim}  • /quit     — Exit ECAssistant{_color.Reset}");
        _outputLines.Add("");
        _outputLines.Add($"{_color.Dim}  ────────────────────────────────────────{_color.Reset}");
        _outputLines.Add("");
        _outputLines.Add($"{_color.Dim}  Boot log below ↓{_color.Reset}");
        _outputLines.Add("");
        
        _isDirty = true;
        RequestRepaint();
    }
}