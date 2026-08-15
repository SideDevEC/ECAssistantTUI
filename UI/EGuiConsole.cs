using System.Text;

namespace ECAssistant.UI;

/// <summary>
/// Console-based EGuiBase.
///
/// The "> " prompt is ALWAYS on the current line. There is no
/// "input active" vs "input inactive" distinction. The prompt
/// never goes away.
///
/// When output arrives:
///   1. Clear the current line (removes "> " + partial input)
///   2. Move up one line
///   3. Write a newline (creates room below the last output line)
///   4. Write the output text
///   5. Reprint "> " + partial input on the new current line
///
/// When user presses Enter:
///   1. Clear the current line
///   2. Write "> " + submitted text + newline (it becomes part of the scrollback)
///   3. Reprint "> " for the next input
///
/// This means the "> " is always the last thing on screen.
/// </summary>
public sealed class EGuiConsole : EGuiBase
{
    private bool _ansiSupported;
    private readonly object _writeLock = new();
    private readonly StringBuilder _inputBuffer = new();
    private const string PromptStr = "> ";
    private volatile bool _escPressed;
    private Action? _onEscape;

    // True from InitConsole until ShutdownConsole — prompt is always showing
    private bool _promptShowing;

    public void InitConsole()
    {
        _ansiSupported = DetectAnsiSupport();
        if (_ansiSupported)
        {
            try { Console.CursorVisible = true; } catch { }
        }
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
    //  OUTPUT — always clears prompt, writes above, reprints prompt
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Reprint "> " + current input buffer. Must hold _writeLock.
    /// </summary>
    private void ReprintPrompt()
    {
        Console.Write("\r\x1b[2K");  // clear line
        Console.Write(PromptStr);
        Console.Write(_inputBuffer.ToString());
    }

    /// <summary>
    /// Write output above the input line.
    ///
    /// 1. Clear current line (prompt + partial input gone)
    /// 2. Move up one line
    /// 3. Newline (creates a blank line below the last output)
    /// 4. Write the output text (scrolls up)
    /// 5. Reprint "> " + partial input
    ///
    /// If prompt isn't showing yet (before first ReadInputLine), just write.
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

            // Clear the input line, move up, make room, write output, reprint prompt
            Console.Write("\r\x1b[2K");       // clear current line
            Console.Write("\x1b[A");            // move up one line
            Console.Write("\n");                // newline — now on a fresh blank line
            Console.Write(text);                // write output (scrolls up)
            if (!text.EndsWith("\n"))
                Console.Write("\n");
            ReprintPrompt();                    // "> " + partial input back on bottom
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

    public override string? PromptColored(string labelAndText) => ReadInputLine(labelAndText);
    public override string? PromptRaw(string label) => ReadInputLine(label);

    private string? ReadInputLine(string prompt)
    {
        // We ignore the passed-in prompt — we always use "> "
        // (it may contain ANSI codes from Program.cs, we don't want that)
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

                    // Log the submitted input into scrollback
                    Console.Write("\r\x1b[2K");     // clear line
                    Console.Write(PromptStr);        // "> "
                    Console.Write(result);            // what user typed
                    Console.Write("\n");             // newline — scrolls up

                    // Immediately reprint "> " for next input
                    ReprintPrompt();

                    // _promptShowing stays true — never goes false
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
                        // Clear buffer, keep prompt
                        _inputBuffer.Clear();
                        ReprintPrompt();
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