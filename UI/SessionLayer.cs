using ECAssistant.Engine;
using ECAssistant.Session;
using ECAssistant.TUI.Session;

namespace ECAssistant.TUI.UI;

/// <summary>
/// One instance per Core session. Owns the output buffer for that session.
/// 
/// The buffer fills continuously — even when this layer is not the active one.
/// ConsoleUiRenderer writes to this layer's buffer, not to EGuiConsole.
/// When the user switches back to this layer, all output is already there.
/// 
/// ProcessInput handles session-specific commands and forwards non-command
/// input as prompts to the Core session.
/// </summary>
public sealed class SessionLayer : BaseLayer
{
    /// <summary>The Core session key (e.g., "main", "session-1").</summary>
    public string SessionKey { get; }
    
    /// <summary>The label shown in status info (optional, user-provided).</summary>
    public string Label { get; set; }
    
    /// <summary>The Core session reference (set by controller).</summary>
    internal AgentSession? CoreSession { get; set; }
    
    /// <summary>Color formatter for output.</summary>
    internal EColor? Color { get; set; }
    
    public override string Name => $"session:{SessionKey}";
    
    /// <summary>The working directory for saving transcripts (set by controller).</summary>
    internal string WorkingDir { get; set; } = "";
    
    public SessionLayer(string sessionKey, string label = "")
    {
        SessionKey = sessionKey;
        Label = label;
    }
    
    /// <summary>Read output history entries (for testing or controller queries).</summary>
    public IReadOnlyList<string> OutputLines => _outputLines;
    
    /// <summary>Current scroll offset (for testing).</summary>
    public int ScrollOffset => _scrollOffset;
    
    public override bool ProcessInput(string input)
    {
        if (string.IsNullOrEmpty(input)) return true;
        
        // Let base handle common commands (/clear)
        if (base.ProcessInput(input)) return true;
        
        bool isCommand = input.StartsWith("/");
        if (!isCommand)
        {
            // Not a command — forward as prompt to Core session
            CoreSession?.Prompt(input);
            return true;
        }
        
        // Session-specific commands
        var lowerInput = input[1..].ToLower().Trim();
        var parts = lowerInput.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0];
        var arg = parts.Length > 1 ? parts[1].Trim() : "";
        var color = Color ?? new EColor();
        
        switch (cmd)
        {
            case "clear-history":
                CoreSession?.Engine.ClearHistory();
                return true;
            
            case "save-context":
                if (CoreSession != null)
                {
                    var p = System.IO.Path.Combine(WorkingDir, ".sessions", CoreSession.Key, "transcript.json");
                    CoreSession.Engine.SaveTranscript(p);
                    AddOutputLine("");
                }
                return true;
            
            case "context-status":
                if (CoreSession != null)
                {
                    AddOutputLine("");
                    AddOutputLine($"{color.Cyan}{color.Bold}[Context] {CoreSession.Engine.ContextStatusSummary}{color.Reset}");
                    AddOutputLine("");
                }
                return true;
            
            case "stop":
                if (CoreSession != null && CoreSession.RunState == SessionRunState.Running)
                {
                    CoreSession.Stop();
                    AddOutputLine($"{color.Red}{color.Bold}[Stop] Cancelling active session...{color.Reset}");
                }
                else
                {
                    AddOutputLine($"{color.Cyan}[Stop] Nothing is running.{color.Reset}");
                }
                return true;
            
            case "tools":
                if (CoreSession != null)
                {
                    var agent = CoreSession.Engine;
                    AddOutputLine("");
                    AddOutputLine($"{color.Cyan}{color.Bold}[Tools] {agent.Tools.Count} registered:{color.Reset}");
                    AddOutputLine("");
                    foreach (var t in agent.Tools)
                    {
                        AddOutputLine($"{color.Yellow}{color.Bold}  {t.Name}{color.Reset}");
                        AddOutputLine($"{color.Dim}    {t.Description}{color.Reset}");
                        AddOutputLine($"{color.Dim}    Example: {t.UsageExample}{color.Reset}");
                        AddOutputLine("");
                    }
                }
                return true;
            
            case "single":
                if (CoreSession != null)
                {
                    CoreSession.Orchestrator.Reset();
                    AddOutputLine($"{color.Cyan}[Mode] Single-turn mode reset.{color.Reset}");
                }
                return true;
            
            case "sessions":
                // Session listing is handled by the controller (it owns SessionManager)
                // Return false so the controller can handle it
                return false;
            
            case "session-peek":
            case "session-stop":
            case "session-close":
            case "session-rename":
            case "session-info":
            case "session-queue":
            case "session-queue-remove":
            case "session-queue-clear":
                // These need the SessionManager — controller handles them
                return false;
            
            default:
                // Unknown command — show error
                AddOutputLine($"{color.Red}[Error] Unknown command: {input}{color.Reset}");
                return true;
        }
    }
}