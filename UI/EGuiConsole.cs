using System.Text;

namespace ECAssistant.UI;

/// <summary>
/// Console-based EGuiBase with ANSI scroll region.
///
/// Architecture:
/// - Terminal is split into two regions:
///   - Top region: scrollable output (all session output goes here)
///   - Bottom line: always-visible input with "> " prompt
/// - ANSI scroll region (\x1b[<top>;<bottom>r) keeps output scrolling in the top area
///   while the input line stays fixed at the bottom
/// - User can type at ANY time — input is always accepting keys
/// - Output arriving while user types just scrolls above — no cursor tricks needed
/// - ESC stops the active session (handled by Program.cs main loop)
/// </summary>
public sealed class EGuiConsole : EGuiBase
{
    private bool _ansiSupported;
    private readonly object _writeLock = new();

    // Input state — always active, user can type whenever
    private readonly StringBuilder _inputBuffer = new();
    private string _inputPrompt = "> ";
    private int _consoleHeight = 24;

    // Callback for when user submits input (set by Program.cs)
    private Func<string, Task>? _onSubmitAsync;
    private Action? _onEscape;

    //ESC detection (for IsEscapePressed compat)
    private volatile bool _escPressed;

    public void InitConsole()
    {
        _ansiSupported = DetectAnsiSupport();
        try { _consoleHeight = Console.WindowHeight; } catch { }

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
            // Reset scroll region to full terminal
            try
            {
                Console.Write("\x1b[r");           // reset scroll region
                Console.CursorVisible = true;
            }
            catch { }
        }
    }

    /// <summary>Set callbacks for input submission and ESC.</summary>
    public void SetHandlers(Func<string, Task>? onSubmit, Action? onEscape)
    {
        _onSubmitAsync = onSubmit;
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

    /// <summary>
    /// Set the ANSI scroll region to leave the bottom line free for input.
    /// Top region = lines 1 to (height-1), bottom line = input.
    /// </summary>
    private void SetupScrollRegion()
    {
        try { _consoleHeight = Console.WindowHeight; } catch { }
        // \x1b[1;<height-1>r — set scroll region from line 1 to height-1
        var bottom = Math.Max(1, _consoleHeight - 1);
        Console.Write($"\x1b[1;{bottom}r");
        // Move cursor to bottom line for input
        Console.Write($"\x1b[{_consoleHeight};1H");
        Console.Write("\x1b[2K");  // clear the line
        Console.Write(_inputPrompt);
        Console.CursorVisible = true;
    }

    /// <summary>
    /// Write output to the scroll region (above the input line).
    /// This works because the scroll region confines scrolling to the top area.
    /// The input line at the bottom is never touched.
    /// </summary>
    private void WriteToScrollRegion(string text)
    {
        if (!_ansiSupported)
        {
            // Fallback: just write to console
            Console.Write(text);
            return;
        }

        lock (_writeLock)
        {
            // Save cursor position
            Console.Write("\x1b7");

            // Move to top of scroll region, write output
            // The scroll region handles the scrolling automatically
            Console.Write(text);

            // Restore cursor position (back to input line)
            Console.Write("\x1b8");
        }
    }

    // ── EGuiBase output methods ──

    public override void WriteLine(string text) => WriteToScrollRegion(text + "\n");
    public override void WriteLineColored(string coloredText) => WriteToScrollRegion(coloredText + "\n");
    public override void WriteRaw(string text) => WriteToScrollRegion(text);
    public override void BlankLine() => WriteToScrollRegion("\n");
    public override void InfoColored(string coloredText) => WriteToScrollRegion(coloredText + "\n");
    public override void WarningColored(string coloredText) => WriteToScrollRegion(coloredText + "\n");

    public override void WriteRawDirect(string text) => WriteToScrollRegion(text);
    public override void LogInternal(string text) => WriteToScrollRegion(text + "\n");

    // ── Input ──

    public override string? PromptColored(string labelAndText) => ReadInputLine(labelAndText);
    public override string? PromptRaw(string label) => ReadInputLine(label);

    /// <summary>
    /// Read a line of input. Shows the prompt, waits for Enter.
    /// Output can arrive simultaneously via other threads — it goes to the scroll region
    /// and doesn't interfere with the input line.
    /// </summary>
    private string? ReadInputLine(string prompt)
    {
        _inputPrompt = prompt;
        _inputBuffer.Clear();

        lock (_writeLock)
        {
            if (_ansiSupported)
            {
                // Move to bottom line, clear it, show prompt
                try { _consoleHeight = Console.WindowHeight; } catch { }
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
                // No console — fallback to ReadLine
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
                        // Clear the input line, move output to scroll region
                        try { _consoleHeight = Console.WindowHeight; } catch { }
                        Console.Write($"\x1b[{_consoleHeight};1H");
                        Console.Write("\x1b[2K");

                        // Write the submitted input into the scroll region as a log line
                        if (!string.IsNullOrEmpty(result))
                        {
                            WriteToScrollRegion(prompt + result + "\n");
                        }

                        // Reprint prompt for next input
                        _inputPrompt = "> ";
                        Console.Write($"\x1b[{_consoleHeight};1H");
                        Console.Write("\x1b[2K");
                        Console.Write(_colorPrompt());
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
                    // ESC: clear current input OR signal stop
                    if (_inputBuffer.Length > 0)
                    {
                        // Clear input buffer first
                        _inputBuffer.Clear();
                        if (_ansiSupported)
                        {
                            try { _consoleHeight = Console.WindowHeight; } catch { }
                            Console.Write($"\x1b[{_consoleHeight};1H");
                            Console.Write("\x1b[2K");
                            Console.Write(_colorPrompt());
                        }
                        else
                        {
                            Console.Write("\r\x1b[2K");
                            Console.Write(_inputPrompt);
                        }
                    }
                    else
                    {
                        // Input is empty — ESC signals stop
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

    /// <summary>Default prompt with color (cyan "> ").</summary>
    private string _colorPrompt()
    {
        return "\x1b[36m> \x1b[0m";
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