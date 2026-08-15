using System.Text;

namespace ECAssistant.UI;

/// <summary>
/// Console-based EGuiBase.
///
/// "> " prompt is ALWAYS the last line on screen.
///
/// Output: clear input line → move up → newline → write output → reprint "> "
/// Enter:  clear input line → move up → newline → write "> text" → reprint "> "
///
/// The trick: after any output, we ALWAYS reprint "> " + buffer.
/// The prompt never goes away.
/// </summary>
public sealed class EGuiConsole : EGuiBase
{
    private bool _ansiSupported;
    private readonly object _writeLock = new();
    private readonly StringBuilder _inputBuffer = new();
    private const string PromptStr = "> ";
    private volatile bool _escPressed;
    private Action? _onEscape;
    private bool _promptShowing;

    public void InitConsole()
    {
        _ansiSupported = DetectAnsiSupport();
        if (_ansiSupported) { try { Console.CursorVisible = true; } catch { } }
    }

    public void ShutdownConsole()
    {
        try { Console.CursorVisible = true; } catch { }
    }

    public void SetHandlers(Func<string, Task>? onSubmit, Action? onEscape)
    {
        _onEscape = onEscape;
    }

    private bool DetectAnsiSupport()
    {
        if (OperatingSystem.IsWindows())
        {
            try { EnableWindowsAnsi(); } catch { }
            try { if (Console.IsOutputRedirected) return false; } catch { return false; }
            return true;
        }
        var term = Environment.GetEnvironmentVariable("TERM");
        if (string.IsNullOrEmpty(term) || term == "dumb") return false;
        try { if (Console.IsOutputRedirected) return false; } catch { return false; }
        return true;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);
    private const int STD_OUTPUT_HANDLE = -11;
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;

    private void EnableWindowsAnsi()
    {
        if (!OperatingSystem.IsWindows()) return;
        var handle = GetStdHandle(STD_OUTPUT_HANDLE);
        if (GetConsoleMode(handle, out uint mode))
        {
            mode |= ENABLE_VIRTUAL_TERMINAL_PROCESSING;
            SetConsoleMode(handle, mode);
        }
    }

    // ═══════════════════════════════════════════════════
    //  OUTPUT
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Reprint "> " + input buffer. Must hold lock.
    /// </summary>
    private void ReprintPrompt()
    {
        Console.Write("\r\x1b[2K");
        Console.Write(PromptStr);
        Console.Write(_inputBuffer.ToString());
    }

    /// <summary>
    /// Write output above the input line.
    ///
    /// If prompt is showing: clear line, move up, newline, write output,
    /// then reprint "> " + buffer on the new bottom line.
    /// </summary>
    private void WriteOutput(string text)
    {
        if (!_ansiSupported)
        {
            Console.Write(text);
            return;
        }

        lock (_writeLock)
        {
            if (!_promptShowing)
            {
                Console.Write(text);
                return;
            }

            // Clear input line
            Console.Write("\r\x1b[2K");
            // Move up one line
            Console.Write("\x1b[A");
            // Newline: creates a fresh line below the last output
            Console.Write("\n");
            // Write the output (scrolls up)
            Console.Write(text);
            if (!text.EndsWith("\n"))
                Console.Write("\n");
            // Reprint prompt + partial input
            ReprintPrompt();
        }
    }

    public override void WriteLine(string text) => WriteOutput(text + "\n");
    public override void WriteLineColored(string coloredText) => WriteOutput(coloredText + "\n");
    public override void WriteRaw(string text) => WriteOutput(text);
    public override void BlankLine() => WriteOutput("\n");
    public override void InfoColored(string coloredText) => WriteOutput(coloredText + "\n");
    public override void WarningColored(string coloredText) => WriteOutput(coloredText + "\n");
    public override void WriteRawDirect(string text) => WriteOutput(text);
    public override void LogInternal(string text) => WriteOutput(text + "\n");

    // ═══════════════════════════════════════════════════
    //  INPUT
    // ═══════════════════════════════════════════════════

    public override string? PromptColored(string labelAndText) => ReadInputLine();
    public override string? PromptRaw(string label) => ReadInputLine();

    private string? ReadInputLine()
    {
        _inputBuffer.Clear();

        lock (_writeLock)
        {
            ReprintPrompt();
            _promptShowing = true;
        }

        while (true)
        {
            ConsoleKeyInfo key;
            try
            {
                if (!Console.KeyAvailable) { Thread.Sleep(10); continue; }
                key = Console.ReadKey(true);
            }
            catch (InvalidOperationException)
            {
                return Console.ReadLine();
            }

            lock (_writeLock)
            {
                if (key.Key == ConsoleKey.Enter)
                {
                    var result = _inputBuffer.ToString();
                    _inputBuffer.Clear();

                    // Put the typed command into the scrollback:
                    // clear line, move up, newline, write "> text"
                    Console.Write("\r\x1b[2K");
                    Console.Write("\x1b[A");
                    Console.Write("\n");
                    Console.Write(PromptStr);
                    Console.Write(result);
                    Console.Write("\n");

                    // Reprint "> " for next input
                    ReprintPrompt();

                    return result;
                }
                else if (key.Key == ConsoleKey.Backspace)
                {
                    if (_inputBuffer.Length > 0)
                    {
                        _inputBuffer.Remove(_inputBuffer.Length - 1, 1);
                        Console.Write("\b \b");
                    }
                }
                else if (key.Key == ConsoleKey.Escape)
                {
                    if (_inputBuffer.Length > 0)
                    {
                        _inputBuffer.Clear();
                        ReprintPrompt();
                    }
                    else
                    {
                        _escPressed = true;
                        _onEscape?.Invoke();
                    }
                }
                else if (key.KeyChar != '\0')
                {
                    _inputBuffer.Append(key.KeyChar);
                    Console.Write(key.KeyChar);
                }
            }
        }
    }

    public override bool IsEscapePressed()
    {
        if (_escPressed)
        {
            _escPressed = false;
            return true;
        }

        try
        {
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Escape)
                {
                    _onEscape?.Invoke();
                    return true;
                }
            }
        }
        catch (InvalidOperationException) { }

        return false;
    }
}