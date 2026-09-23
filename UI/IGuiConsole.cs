namespace ECAssistant.TUI.UI;

/// <summary>
/// Interface for the console terminal that AppController and layers depend on.
///
/// GuiConsole implements this for the standalone terminal app.
/// External hosts (e.g., ECSQL's Avalonia terminal pane) can implement this
/// to embed ECAssistant's TUI logic without using System.Console directly.
///
/// This interface covers both GuiBase output methods and GuiConsole
/// terminal-specific methods, so implementers provide everything in one shot.
/// </summary>
public interface IGuiConsole
{
    // ── Controller wiring ──
    void SetCallbacks(Action<string> onPrompt, Action onEscape);
    void SetActiveLayer(BaseLayer? layer);
    void SetSilentInputCheck(Func<bool>? check);
    void SetSilentInputInitial(bool silent);

    // ── Lifecycle ──
    void InitConsole();
    void ShutdownConsole();
    void Quit();
    bool IsQuitRequested { get; }

    // ── Rendering ──
    void RequestRepaint();

    // ── Screen dimensions ──
    int ScreenWidth { get; }
    int ScreenHeight { get; }

    // ── Output methods (from GuiBase) ──
    void WriteLine(string text);
    void WriteLineColored(string coloredText);
    void WriteLineColored(string coloredText, int maxChars);
    void WriteLine(string text, int maxChars);
    void WriteRaw(string text);
    void BlankLine();
    string? PromptColored(string labelAndText);
    string? PromptRaw(string label);
    void InfoColored(string coloredText);
    void WarningColored(string coloredText);
    void WriteRawDirect(string text);
    void ClearCanvas();
    bool IsEscapePressed();
    void LogInternal(string text);
}