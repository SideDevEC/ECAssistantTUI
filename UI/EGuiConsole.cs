using System.Text;

namespace ECAssistant.UI;

/// <summary>
/// Console-based EGuiBase.
///
/// The "> " prompt is ALWAYS visible. When output arrives while the
/// user is typing, we clear the input line, move up, write the output,
/// then reprint "> " + whatever the user had typed so far.
///
/// After the user submits (Enter), the typed text scrolls up as output
/// and "> " immediately reappears for the next input.
/// </summary>
public sealed class EGuiConsole : EGuiBase
{
    private bool _ansiSupported;
    private readonly object _writeLock = new();
    private readonly StringBuilder _inputBuffer = new();

    // The prompt is always "> " — stored without ANSI codes so reprint is clean
    private const string PromptStr = "> ";
    private string _inputPrompt = PromptStr;
    private bool _inputActive;
    private volatile bool _escPressed;
    private Action? _onEscape;

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
    //  OUTPUT
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Reprint the prompt + current input buffer on the current line.
    /// Caller must hold _writeLock.
    /// </summary>
    private void ReprintInput()
    {
        Console.Write("\r\x1b[2K");  // clear line
        Console.Write(_inputPrompt);
        Console.Write(_inputBuffer.ToString());
    }

    /// <summary>
    /// Write output. If user is typing (input active):
    /// 1. Clear input line
    /// 2. Move up one line, newline to make room
    /// 3. Write the output (scrolls up)
    /// 4. Reprint "> " + partial input on the new bottom line
    ///
    /// If not typing, just write normally. The "> " is always reprinted
    /// after output in case it got scrolled away.
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
            if (_inputActive)
            {
                // Clear input line, move up, make room, write output, reprint input
                Console.Write("\r\x1b[2K");       // clear current line
                Console.Write("\x1b[A");            // move up one line
                Console.Write("\n");                // newline — now we're on a fresh line
                Console.Write(text);                // write the output
                if (!text.EndsWith("\n"))
                    Console.Write("\n");
                // Reprint prompt + partial input
                ReprintInput();
            }
            else
            {
                // Not typing — just write output
                Console.Write(text);
            }
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
        // Strip ANSI codes from prompt for internal storage — we reprint it ourselves
        _inputPrompt = StripAnsi(prompt);
        _inputBuffer.Clear();

        lock (_writeLock)
        {
            // Show the prompt
            ReprintInput();
            _inputActive = true;
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
                lock (_writeLock) { _inputActive = false; }
                return Console.ReadLine();
            }

            lock (_writeLock)
            {
                if (key.Key == ConsoleKey.Enter)
                {
                    var result = _inputBuffer.ToString();
                    _inputBuffer.Clear();
                    _inputActive = false;

                    // Move the typed text up into the scroll area as a log line
                    Console.Write("\r\x1b[2K");     // clear current line
                    Console.Write(_inputPrompt);     // write prompt + input as output
                    Console.Write(result);
                    Console.Write("\n");             // newline — scrolls up

                    // Immediately reprint fresh "> " for next input
                    _inputPrompt = PromptStr;
                    _inputBuffer.Clear();
                    ReprintInput();
                    _inputActive = true;

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
                        ReprintInput();
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

    /// <summary>Strip ANSI escape sequences from a string.</summary>
    private static string StripAnsi(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var sb = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '\x1b' && i + 1 < text.Length && text[i + 1] == '[')
            {
                // Skip ANSI escape sequence: ESC [ ... letter
                i += 2;
                while (i < text.Length && !char.IsLetter(text[i]))
                    i++;
                i++; // skip the final letter
            }
            else
            {
                sb.Append(text[i]);
                i++;
            }
        }
        return sb.ToString();
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