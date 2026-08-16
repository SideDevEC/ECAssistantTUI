namespace ECAssistant.UI;

/// <summary>
/// Session layer — the base layer of the view stack.
/// Renders the normal session output + input prompt.
/// This is layer 1 — always at the bottom of the stack.
/// </summary>
public sealed class SessionLayer : IGuiLayer
{
    public string Name => "session";

    public void OnActivate(EGuiConsole console)
    {
        // Session view is painted by the normal Repaint() path.
        // Just trigger a full repaint.
        console.TriggerFullRepaint();
    }

    public void OnResize(EGuiConsole console)
    {
        console.TriggerFullRepaint();
    }

    public bool OnKey(EGuiConsole console, ConsoleKeyInfo key)
    {
        // SessionLayer is the base layer — it never intercepts keys.
        // ReadInputLine skips layer routing when this layer is on top.
        return true;
    }
}