using System.Text;

namespace ECAssistant.UI;

/// <summary>
/// Console-based EGuiBase with always-visible input prompt.
///
/// Design:
///   - "> " + buffer is ALWAYS shown at the bottom of the terminal.
///   - Output writes ABOVE the prompt line: clear last line, write output,
///     reprint "> " + buffer. The buffer is always preserved and restored.
///   - In silent mode (session running): keystrokes update the buffer and
///     redraw the prompt line via RenderPrompt(). No direct Console.Write
///     of characters — everything goes through the locked render path.
///     This prevents typed text from leaking into the output stream.
///   - In normal mode (session idle): same behavior, same render path.
///   - The difference between silent and normal mode is only in how
///     we render — but both show "> " + buffer on the last line.
///
/// The critical rule: there is only ONE way to write to the screen —
/// through RenderPrompt() or WriteOutput(), both inside _writeLock.
/// Never Console.Write(key.KeyChar) directly.
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

    // Silent mode: when true, we still show "> " + buffer but ALL
    // keystroke rendering goes through RenderPrompt() (no direct echo).
    // This ensures typed text can't leak into output streams.
    private volatile bool _silentInput;
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
    //  PROMPT RENDER — the ONLY way to draw the input line
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Clear current line and write "> " + buffer.
    /// Always shows the buffer — silent or normal mode.
    /// Must be called inside _writeLock.
    /// </summary>
    private void RenderPrompt()
    {
        Console.Write("\r\x1b[2K");
        Console.Write(PromptStr);
        Console.Write(_inputBuffer.ToString());
        Console.Out.Flush();
    }

    // ═══════════════════════════════════════════════════
    //  OUTPUT — clear prompt line, write output, restore prompt
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// 1. Clear current line (removes "> " + buffer from screen)
    /// 2. Write output text (scrolls up into scrollback)
    /// 3. Ensure newline at end
    /// 4. Reprint "> " + buffer on the new last line
    ///
    /// The buffer is ALWAYS restored after output — it never disappears.
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

            // Clear current line (where "> " + buffer is)
            Console.Write("\r\x1b[2K");

            // Write the output
            Console.Write(text);

            // Ensure we end on a newline
            if (!text.EndsWith("\n"))
                Console.Write("\n");

            // Reprint "> " + buffer — always, silent or not
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
                // Check if silent mode should turn off (session finished)
                if (_silentInput && _silentInputCheck != null && !_silentInputCheck())
                {
                    _silentInput = false;
                    // No need to redraw — RenderPrompt already shows buffer
                    // in both modes. The only difference is no more direct echo.
                }

                if (!Console.KeyAvailable)
                {
                    Thread.Sleep(10);
                    continue;
                }
                key = Console.ReadKey(true); // intercept: never auto-echo
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

                    // Print submitted input: "> text\n"
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
                        RenderPrompt();
                    }
                }
                else if (key.Key == ConsoleKey.Escape)
                {
                    if (_inputBuffer.Length > 0)
                    {
                        _inputBuffer.Clear();
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
                    RenderPrompt();
                }
                else if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
                {
                    _inputBuffer.Append(key.KeyChar);
                    // ALWAYS render through RenderPrompt — never direct echo.
                    // This is the key fix: in both silent and normal mode,
                    // every keystroke redraws the full prompt line through
                    // the locked render path. WriteOutput also uses RenderPrompt
                    // to restore the buffer after output. So the buffer is
                    // always correctly positioned and never leaks into output.
                    RenderPrompt();
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