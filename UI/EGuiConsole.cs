using System.Text;

namespace ECAssistant.UI;

/// <summary>
/// Console-based EGuiBase with ANSI scroll region.
///
/// Terminal layout:
///   ┌─────────────────────────┐
///   │  output scrolls here     │  ← scroll region (lines 1 to height-1)
///   │  [INFO] Session created  │
///   │  token stream...         │
///   │                          │
///   ├─────────────────────────┤
///   │ > type here always_      │  ← input line (pinned, line height)
///   └─────────────────────────┘
///
/// How it works:
/// - ANSI scroll region (\x1b[1;<h-1>r) confines scrolling to the top area
/// - Output: move cursor to last line of scroll region, write text + \n
///   → the \n causes the scroll region to scroll up, pushing content up
/// - Input: cursor stays on the bottom line (outside scroll region)
/// - User can type at any time — output scrolling doesn't touch the input line
/// </summary>
public sealed class EGuiConsole : EGuiBase
{
    private bool _ansiSupported;
    private readonly object _writeLock = new();
    private readonly StringBuilder _inputBuffer = new();
    private string _inputPrompt = "> ";
    private int _consoleHeight = 24;
    private int _scrollBottom;  // last line of scroll region (1-based)
    private volatile bool _escPressed;
    private Action? _onEscape;

    public void InitConsole()
    {
        _ansiSupported = DetectAnsiSupport();
        try { _consoleHeight = Console.WindowHeight; } catch { }
        _scrollBottom = Math.Max(1, _consoleHeight - 1);

        if (_ansiSupported)
        {
            try { Console.CursorVisible = false; } catch { }
            SetupScrollRegion();
        }
    }

    public void ShutdownConsole()
    {
        if (_ansiSupported)
        {
            try
            {
                Console.Write("\x1b[r");       // reset scroll region
                Console.CursorVisible = true;
            }
            catch { }
        }
    }

    /// <summary>Set ESC callback to stop the active session.</summary>
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

    private void RefreshHeight()
    {
        try { _consoleHeight = Console.WindowHeight; } catch { }
        _scrollBottom = Math.Max(1, _consoleHeight - 1);
    }

    /// <summary>
    /// Set the ANSI scroll region to lines 1..(height-1).
    /// The bottom line (height) is outside the scroll region — input lives there.
    /// </summary>
    private void SetupScrollRegion()
    {
        RefreshHeight();
        // Set scroll region: \x1b[1;<scrollBottom>r
        Console.Write($"\x1b[1;{_scrollBottom}r");
        // Move cursor to bottom line for input
        Console.Write($"\x1b[{_consoleHeight};1H");
        Console.Write("\x1b[2K");
        Console.Write("\x1b[36m> \x1b[0m");
        Console.CursorVisible = true;
    }

    // ═══════════════════════════════════════════════════
    //  OUTPUT — writes go to the scroll region, never the input line
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Write text into the scroll region.
    ///
    /// 1. Save cursor (we're on the input line)
    /// 2. Move to the LAST line of the scroll region (line scrollBottom)
    /// 3. Write text — if it contains \n, the scroll region auto-scrolls up
    /// 4. Restore cursor to the input line
    ///
    /// The input line is outside the scroll region so it's never affected.
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
            // Save cursor position (currently on input line)
            Console.Write("\x1b7");

            // Move to last line of scroll region
            Console.Write($"\x1b[{_scrollBottom};1H");

            // Write the text — \n causes the scroll region to scroll up
            Console.Write(text);

            // Restore cursor to input line
            Console.Write("\x1b8");

            // Reprint input prompt + current buffer (in case scroll region output
            // overwrote the input line visually — it shouldn't, but just in case)
            // Actually we DON'T need this — the input line is outside the scroll region.
            // Only reprint if cursor visual got messed up.
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
    //  INPUT — always on the bottom line, outside scroll region
    // ═══════════════════════════════════════════════════

    public override string? PromptColored(string labelAndText) => ReadInputLine(labelAndText);
    public override string? PromptRaw(string label) => ReadInputLine(label);

    private string? ReadInputLine(string prompt)
    {
        _inputPrompt = prompt;
        _inputBuffer.Clear();

        lock (_writeLock)
        {
            if (_ansiSupported)
            {
                RefreshHeight();
                // Move to bottom line, clear it, show prompt
                Console.Write($"\x1b[{_consoleHeight};1H");
                Console.Write("\x1b[2K");
                Console.Write(prompt);
            }
            else
            {
                Console.Write(prompt);
            }
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

                    if (_ansiSupported)
                    {
                        RefreshHeight();

                        // Clear the input line
                        Console.Write($"\x1b[{_consoleHeight};1H");
                        Console.Write("\x1b[2K");

                        // Echo the submitted command into the scroll region
                        if (!string.IsNullOrEmpty(result))
                        {
                            WriteOutput(prompt + result + "\n");
                        }

                        // Reprint fresh prompt on the input line
                        _inputPrompt = "\x1b[36m> \x1b[0m";
                        Console.Write($"\x1b[{_consoleHeight};1H");
                        Console.Write("\x1b[2K");
                        Console.Write(_inputPrompt);
                    }
                    else
                    {
                        Console.WriteLine();
                    }

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
                        // Clear input buffer
                        _inputBuffer.Clear();
                        if (_ansiSupported)
                        {
                            RefreshHeight();
                            Console.Write($"\x1b[{_consoleHeight};1H");
                            Console.Write("\x1b[2K");
                            Console.Write(_inputPrompt);
                        }
                        else
                        {
                            Console.Write("\r\x1b[2K");
                            Console.Write(_inputPrompt);
                        }
                    }
                    else
                    {
                        // Empty input + ESC = stop session
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