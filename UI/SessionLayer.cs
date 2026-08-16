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
        // Returning false would cause ReadInputLine to PopLayer (removing the base),
        // so instead we return true BUT this should never be reached.
        // The real fix is in ReadInputLine: it skips layer routing for SessionLayer.
        // If we ever get here, something is wrong — log it.
        Console.Error.WriteLine("[WARN] SessionLayer.OnKey called — ReadInputLine should skip SessionLayer routing");
        return true;
    }
}