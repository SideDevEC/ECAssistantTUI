using System.Text;

namespace ECAssistant.UI;

/// <summary>
/// Console-based EGuiBase with always-visible input prompt.
///
/// Design:
///   - "> " is ALWAYS shown at the bottom of the terminal.
///   - Output writes ABOVE the prompt line, then the prompt is reprinted.
///   - User can type while the orchestrator is streaming output.
///   - Input is buffered silently — typed characters do NOT appear on screen
///     until Enter is pressed. This prevents input from mixing into output.
///   - Commands like "stop", "session <n>", etc. work while orchestrator runs.
///
/// Thread safety: all console writes go through _writeLock.
/// Input is read on the main thread; output comes from session background threads.
/// </summary>
public sealed class EGuiConsole : EGuiBase
{
    private bool _ansiSupported;
    private readonly object _writeLock = new();
    private readonly StringBuilder _inputBuffer = new();
    private const string PromptStr = "> ";
    private volatile bool _escPressed;
    private Action? _onEscape;

    // _promptActive = true once ReadInputLine is first called, stays true forever.
    private volatile bool _promptActive;

    // _silentInput = true when input should be buffered but NOT echoed.
    // Set when the session is running (output is streaming). The user can
    // still type and press Enter — characters just don't appear on screen.
    // This prevents typed text from mixing into output.
    private volatile bool _silentInput;

    public void InitConsole()
    {
        _ansiSupported = DetectAnsiSupport();
        if (_ansiSupported) { try { Console.CursorVisible = true; } catch { } }
    }

    public void ShutdownConsole()
    {
        try { Console.CursorVisible = true; } catch { }
    }

    /// <summary>Enable/disable silent input mode.</summary>
    /// <param name="silent">When true, typed characters are buffered but not echoed.</param>
    public void SetSilentInput(bool silent)
    {
        _silentInput = silent;
        // If turning off, redraw prompt to reveal buffered input
        if (!silent)
        {
            lock (_writeLock) { RedrawPrompt(); }
        }
    }

    // Callback to check if silent mode should still be active.
    // Called by ReadInputLine on each poll cycle. If it returns false,
    // silent mode is turned off and the buffer is revealed.
    private Func<bool>? _silentInputCheck;

    /// <summary>Set a callback that returns true if silent input should be active.</summary>
    public void SetSilentInputCheck(Func<bool>? check)
    {
        _silentInputCheck = check;
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
    //  PROMPT REDRAW
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Redraw the prompt line: clear current line, write "> " + buffer.
    /// In silent mode, only "> " is shown (buffer is hidden).
    /// Must be called inside _writeLock.
    /// </summary>
    private void RedrawPrompt()
    {
        Console.Write("\r\x1b[2K");
        Console.Write(PromptStr);
        if (!_silentInput)
            Console.Write(_inputBuffer.ToString());
        Console.Out.Flush();
    }

    // ═══════════════════════════════════════════════════
    //  OUTPUT — writes above the prompt line
    // ═══════════════════════════════════════════════════

    private void WriteOutput(string text)
    {
        if (!_ansiSupported)
        {
            lock (_writeLock) { Console.Write(text); Console.Out.Flush(); }
            return;
        }

        lock (_writeLock)
        {
            if (!_promptActive)
            {
                Console.Write(text);
                Console.Out.Flush();
                return;
            }

            // Clear current line (where "> " is)
            Console.Write("\r\x1b[2K");

            // Write the output
            Console.Write(text);

            // Ensure we end on a newline
            if (!text.EndsWith("\n"))
                Console.Write("\n");

            // Reprint "> " + buffer (or just "> " in silent mode)
            RedrawPrompt();
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

    /// <summary>
    /// Read a line of input from the console.
    ///
    /// In silent mode (_silentInput = true):
    ///   - "> " is shown but typed characters are NOT echoed to the console
    ///   - Input is buffered silently
    ///   - Only when Enter is pressed is the input revealed and processed
    ///   - This prevents typed text from appearing in output streams
    ///
    /// In normal mode (_silentInput = false):
    ///   - "> " + typed characters are visible
    ///   - Standard interactive input
    /// </summary>
    private string? ReadInputLine()
    {
        _inputBuffer.Clear();

        lock (_writeLock)
        {
            _promptActive = true;
            RedrawPrompt();
        }

        while (true)
        {
            ConsoleKeyInfo key;
            try
            {
                // Check if silent mode should be turned off
                if (_silentInput && _silentInputCheck != null && !_silentInputCheck())
                {
                    _silentInput = false;
                    lock (_writeLock) { RedrawPrompt(); }
                }

                if (!Console.KeyAvailable)
                {
                    Thread.Sleep(10);
                    continue;
                }
                key = Console.ReadKey(true); // intercept = true: don't echo
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

                    // Echo the submitted input: "> text\n"
                    Console.Write("\r\x1b[2K");
                    Console.Write(PromptStr);
                    Console.Write(result);
                    Console.Write("\n");
                    Console.Out.Flush();

                    // Reprint "> " for next input
                    RedrawPrompt();

                    return result;
                }
                else if (key.Key == ConsoleKey.Backspace)
                {
                    if (_inputBuffer.Length > 0)
                    {
                        _inputBuffer.Remove(_inputBuffer.Length - 1, 1);
                        // Only redraw in non-silent mode (in silent mode,
                        // the user can't see what they typed anyway)
                        if (!_silentInput)
                            RedrawPrompt();
                    }
                }
                else if (key.Key == ConsoleKey.Escape)
                {
                    if (_inputBuffer.Length > 0)
                    {
                        _inputBuffer.Clear();
                        if (!_silentInput)
                            RedrawPrompt();
                    }
                    else
                    {
                        _escPressed = true;
                        _onEscape?.Invoke();
                    }
                }
                else if (key.Key == ConsoleKey.Tab)
                {
                    _inputBuffer.Append("    ");
                    if (!_silentInput)
                        RedrawPrompt();
                }
                else if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
                {
                    _inputBuffer.Append(key.KeyChar);
                    // In silent mode: don't echo — just buffer silently
                    // In normal mode: echo at cursor position
                    if (!_silentInput)
                    {
                        Console.Write(key.KeyChar);
                        Console.Out.Flush();
                    }
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