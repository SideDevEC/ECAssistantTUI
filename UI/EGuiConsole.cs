using System.Text;

namespace ECAssistant.UI;

/// <summary>
/// Console-based EGuiBase with always-visible input prompt and silent buffering.
///
/// Design:
///   - "> " is ALWAYS shown at the bottom of the terminal.
///   - When the session is running (silent mode):
///     * Typed characters are buffered but NOT shown on screen
///     * "> " stays visible — never disappears
///     * On Enter, the buffered input is submitted
///     * Buffer is never written to the output stream
///   - When the session is idle (normal mode):
///     * "> " + typed characters are visible
///     * Standard interactive input
///   - Output always writes ABOVE the prompt line, then reprints "> "
///
/// Thread safety: all console writes go through _writeLock.
/// </summary>
public sealed class EGuiConsole : EGuiBase
{
    private bool _ansiSupported;
    private readonly object _writeLock = new();
    private readonly StringBuilder _inputBuffer = new();
    private const string PromptStr = "> ";
    private volatile bool _escPressed;
    private Action? _onEscape;

    private volatile bool _promptActive;

    // Silent mode: typed characters are buffered but not echoed.
    // Set by the main loop when the session is running.
    private volatile bool _silentInput;

    // Callback to check if silent mode should still be active.
    private Func<bool>? _silentInputCheck;

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

    public void SetSilentInputCheck(Func<bool>? check)
    {
        _silentInputCheck = check;
    }

    /// <summary>Set the initial silent state at the start of ReadInputLine.</summary>
    public void SetSilentInputInitial(bool silent)
    {
        _silentInput = silent;
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
    //  PROMPT RENDER
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Render the prompt line. In silent mode: just "> ".
    /// In normal mode: "> " + buffer. Must be inside _writeLock.
    /// </summary>
    private void RenderPrompt()
    {
        Console.Write("\r\x1b[2K");
        Console.Write(PromptStr);
        if (!_silentInput)
            Console.Write(_inputBuffer.ToString());
        Console.Out.Flush();
    }

    // ═══════════════════════════════════════════════════
    //  OUTPUT
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Write output above the prompt line, then reprint "> ".
    ///
    /// 1. Clear current line (removes "> " from screen)
    /// 2. Write output text (scrolls up)
    /// 3. Ensure newline at end
    /// 4. Reprint "> " (or "> " + buffer in normal mode)
    ///
    /// The prompt is ALWAYS reprinted after output — it never disappears.
    /// In silent mode, the buffer is NOT shown (just "> ").
    /// </summary>
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

            // Reprint the prompt — ALWAYS "> " visible
            // In silent mode: just "> " (no buffer shown)
            // In normal mode: "> " + buffer
            RenderPrompt();
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
            _promptActive = true;
            RenderPrompt();
        }

        while (true)
        {
            ConsoleKeyInfo key;
            try
            {
                // Poll: check if silent mode should turn off
                // (session finished while we're waiting for input)
                if (_silentInput && _silentInputCheck != null && !_silentInputCheck())
                {
                    _silentInput = false;
                    lock (_writeLock) { RenderPrompt(); }
                }

                if (!Console.KeyAvailable)
                {
                    Thread.Sleep(10);
                    continue;
                }
                key = Console.ReadKey(true); // intercept: don't auto-echo
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

                    // Print the submitted input: "> text\n"
                    // This shows what was entered even in silent mode
                    Console.Write("\r\x1b[2K");
                    Console.Write(PromptStr);
                    Console.Write(result);
                    Console.Write("\n");
                    Console.Out.Flush();

                    // Reprint "> " for next input
                    RenderPrompt();

                    return result;
                }
                else if (key.Key == ConsoleKey.Backspace)
                {
                    if (_inputBuffer.Length > 0)
                    {
                        _inputBuffer.Remove(_inputBuffer.Length - 1, 1);
                        if (!_silentInput)
                            RenderPrompt();
                    }
                }
                else if (key.Key == ConsoleKey.Escape)
                {
                    if (_inputBuffer.Length > 0)
                    {
                        _inputBuffer.Clear();
                        if (!_silentInput)
                            RenderPrompt();
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
                        RenderPrompt();
                }
                else if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
                {
                    _inputBuffer.Append(key.KeyChar);
                    // Silent mode: don't echo, just buffer
                    // Normal mode: echo at cursor
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