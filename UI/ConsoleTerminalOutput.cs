namespace ECAssistant.TUI.UI;

/// <summary>
/// ITerminalOutput implementation using System.Console.
/// This is the default for real terminal/console environments.
/// </summary>
public class ConsoleTerminalOutput : ITerminalOutput
{
    public int WindowWidth => Console.WindowWidth;
    public int WindowHeight => Console.WindowHeight;

    public event EventHandler? OnResize;

    public ConsoleTerminalOutput()
    {
        Console.CancelKeyPress += (s, e) => { };
    }

    public void Write(string text) => Console.Write(text);
    public void Flush() => Console.Out.Flush();

    public void ClearScreen() => Console.Write("\x1b[2J");

    public void SetCursorPosition(int row, int col)
        => Console.Write($"\x1b[{row + 1};{col + 1}H");

    public void ShowCursor() => Console.Write("\x1b[?25h");
    public void HideCursor() => Console.Write("\x1b[?25l");

    public void EnableAlternateScreen()
        => Console.Write("\x1b[?1049h\x1b[?1000h");

    public void DisableAlternateScreen()
        => Console.Write("\x1b[?1000l\x1b[?25h\x1b[?1049l");

    public void EnableMouse() => Console.Write("\x1b[?1000h");
    public void DisableMouse() => Console.Write("\x1b[?1000l");
}