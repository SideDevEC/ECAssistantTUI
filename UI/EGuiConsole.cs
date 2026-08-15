using System.Text;

namespace ECAssistant.UI;

/// <summary>
/// Console-based EGuiBase with always-visible input prompt.
///
/// Design:
///   - "> " is ALWAYS shown at the bottom of the terminal.
///   - Output writes ABOVE the prompt line, then the prompt is reprinted.
///   - User can type while the orchestrator is streaming output.
///   - Input buffer is preserved across output writes — WriteOutput clears
///     the prompt line, writes output, then reprints "> " + buffer.
///   - Commands like "stop", "session <n>", etc. work while orchestrator runs.
///
/// Thread safety: all console writes go through _writeLock.
/// Input is read on the main thread; output comes from session background threads.
///
/// Rendering:
///   - Regular keystrokes: echoed directly at cursor (end of "> " + buffer).
///     No full redraw — avoids flashing. WriteOutput restores the buffer
///     when output arrives between keystrokes.
///   - Backspace/Tab/Escape: full RedrawPrompt (need line rewrite).
///   - WriteOutput: clear line, write output, reprint "> " + buffer.
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
    //  PROMPT REDRAW
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Redraw the prompt line: clear current line, write "> " + buffer.
    /// Must be called inside _writeLock.
    /// </summary>
    private void RedrawPrompt()
    {
        Console.Write("\r\x1b[2K");
        Console.Write(PromptStr);
        Console.Write(_inputBuffer.ToString());
        Console.Out.Flush();
    }

    // ═══════════════════════════════════════════════════
    //  OUTPUT — writes above the prompt line
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Write output above the prompt line, then reprint "> " + input buffer.
    ///
    /// 1. Clear current line (removes "> " + partial input from screen)
    /// 2. Write output text (scrolls up into scrollback)
    /// 3. Ensure output ends with newline
    /// 4. Reprint "> " + current input buffer on the new line
    ///
    /// This means: user's typed text is temporarily removed, output appears,
    /// then the typed text is restored on the new prompt line.
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

            // Clear current line (where "> " + input is)
            Console.Write("\r\x1b[2K");

            // Write the output
            Console.Write(text);

            // Ensure we end on a newline
            if (!text.EndsWith("\n"))
                Console.Write("\n");

            // Reprint "> " + current input buffer
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
    /// The "> " prompt is always visible. While the user types, output from
    /// background sessions may arrive — WriteOutput clears the prompt line,
    /// writes output, then reprints "> " + buffer. The user's typed text
    /// is always preserved and correctly positioned.
    ///
    /// Keystroke rendering:
    ///   - Regular chars: echoed directly at cursor (end of buffer).
    ///     No full redraw — avoids flashing between output steps.
    ///   - Backspace/Tab/Escape: full RedrawPrompt (need line rewrite).
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
                        RedrawPrompt();
                    }
                }
                else if (key.Key == ConsoleKey.Escape)
                {
                    if (_inputBuffer.Length > 0)
                    {
                        _inputBuffer.Clear();
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
                    RedrawPrompt();
                }
                else if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
                {
                    // Echo directly at cursor position (end of buffer).
                    // WriteOutput will clear and reprint "> " + full buffer
                    // when output arrives, keeping everything consistent.
                    _inputBuffer.Append(key.KeyChar);
                    Console.Write(key.KeyChar);
                    Console.Out.Flush();
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