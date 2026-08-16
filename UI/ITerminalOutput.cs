namespace ECAssistant.TUI.UI;

/// <summary>
/// Abstraction for low-level terminal output operations.
/// EGuiConsole uses this instead of Console.Write directly,
/// enabling the TUI to be hosted in non-console environments
/// (e.g., ECSQL's AvaloniaGuiConsole provides its own implementation).
/// </summary>
public interface ITerminalOutput
{
    void Write(string text);
    void Flush();
    void ClearScreen();
    void SetCursorPosition(int row, int col);
    void ShowCursor();
    void HideCursor();
    void EnableAlternateScreen();
    void DisableAlternateScreen();
    void EnableMouse();
    void DisableMouse();
    int WindowWidth { get; }
    int WindowHeight { get; }
    event EventHandler? OnResize;
}