namespace ECAssistant.TUI.UI;

/// <summary>
/// ITerminalOutput implementation using System.Console.
/// This is the default for real terminal/console environments.
/// </summary>
public class ConsoleTerminalOutput : ITerminalOutput, IDisposable
{
    public int WindowWidth => Console.WindowWidth;
    public int WindowHeight => Console.WindowHeight;

    public event EventHandler? OnResize;

    private System.Threading.Timer? _resizePoll;
    private int _lastW;
    private int _lastH;

    public ConsoleTerminalOutput()
    {
        // L1: removed empty Console.CancelKeyPress handler — it did nothing and
        // could suppress the default Ctrl+C behavior.

        // Poll window size and raise OnResize so the interface event is
        // actually fired (instead of the never-raised CS0067 warning).
        try
        {
            _lastW = Console.WindowWidth;
            _lastH = Console.WindowHeight;
        }
        catch { /* M5: benign — Console.WindowWidth/Height throws when output is redirected */ }
        _resizePoll = new System.Threading.Timer(_ =>
        {
            try
            {
                int w = Console.WindowWidth;
                int h = Console.WindowHeight;
                if (w != _lastW || h != _lastH)
                {
                    _lastW = w;
                    _lastH = h;
                    OnResize?.Invoke(this, EventArgs.Empty);
                }
            }
            catch { /* M5: benign — terminal not ready or redirected; resize poll is best-effort */ }
        }, null, 200, 200);
    }

    public void Write(string text) => Console.Write(text);
    public void Flush() => Console.Out.Flush();

    [Obsolete("GuiConsole manages screen clearing via delta rendering; not used via ITerminalOutput.")]
    public void ClearScreen() => Console.Write("\x1b[2J");

    [Obsolete("GuiConsole positions the cursor via _term.Write directly; not used via ITerminalOutput.")]
    public void SetCursorPosition(int row, int col)
        => Console.Write($"\x1b[{row + 1};{col + 1}H");

    [Obsolete("GuiConsole writes cursor sequences directly; not used via ITerminalOutput.")]
    public void ShowCursor() => Console.Write("\x1b[?25h");
    [Obsolete("GuiConsole writes cursor sequences directly; not used via ITerminalOutput.")]
    public void HideCursor() => Console.Write("\x1b[?25l");

    [Obsolete("GuiConsole writes alternate-screen sequences directly; not used via ITerminalOutput.")]
    public void EnableAlternateScreen()
        => Console.Write("\x1b[?1049h\x1b[?1000h");

    [Obsolete("GuiConsole writes alternate-screen sequences directly; not used via ITerminalOutput.")]
    public void DisableAlternateScreen()
        => Console.Write("\x1b[?1000l\x1b[?25h\x1b[?1049l");

    [Obsolete("Mouse tracking is deliberately not enabled — see GuiConsole.InitConsole comment.")]
    public void EnableMouse() => Console.Write("\x1b[?1000h");
    [Obsolete("Mouse tracking is deliberately not enabled — see GuiConsole.InitConsole comment.")]
    public void DisableMouse() => Console.Write("\x1b[?1000l");

    /// <summary>L1: stop the resize-poll timer to prevent leaks when disposed.</summary>
    public void Dispose()
    {
        _resizePoll?.Dispose();
        _resizePoll = null;
    }
}