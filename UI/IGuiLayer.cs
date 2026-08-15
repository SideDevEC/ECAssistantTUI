namespace ECAssistant.UI;

/// <summary>
/// A layer in the EGuiConsole view stack.
/// Each layer owns the full screen and handles its own input.
/// When pushed, it paints itself. When popped, the previous layer is restored.
/// </summary>
public interface IGuiLayer
{
    /// <summary>Layer name for debugging.</summary>
    string Name { get; }

    /// <summary>Called when the layer becomes active. Paint the full screen.</summary>
    void OnActivate(EGuiConsole console);

    /// <summary>Called on terminal resize while this layer is active.</summary>
    void OnResize(EGuiConsole console);

    /// <summary>
    /// Called when a key is pressed while this layer is active.
    /// Return true if the key was handled, false to pop the layer.
    /// </summary>
    bool OnKey(EGuiConsole console, ConsoleKeyInfo key);
}