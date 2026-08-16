namespace ECAssistant.UI;

/// <summary>
/// One instance per Core session. Owns the output buffer for that session.
/// 
/// The buffer fills continuously — even when this layer is not the active one.
/// ConsoleUiRenderer writes to this layer's buffer, not to EGuiConsole.
/// When the user switches back to this layer, all output is already there.
/// </summary>
public sealed class SessionLayer : BaseLayer
{
    /// <summary>The Core session key (e.g., "main", "session-1").</summary>
    public string SessionKey { get; }
    
    /// <summary>The label shown in status info (optional, user-provided).</summary>
    public string Label { get; set; }
    
    public override string Name => $"session:{SessionKey}";
    
    public override string GetInputPrompt() => "> ";
    
    public SessionLayer(string sessionKey, string label = "")
    {
        SessionKey = sessionKey;
        Label = label;
    }
    
    /// <summary>Read output history entries (for testing or controller queries).</summary>
    public IReadOnlyList<string> OutputLines => _outputLines;
    
    /// <summary>Current scroll offset (for testing).</summary>
    public int ScrollOffset => _scrollOffset;
}