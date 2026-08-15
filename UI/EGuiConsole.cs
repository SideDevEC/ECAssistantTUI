using System.Text;

namespace ECAssistant.UI;

/// <summary>
/// Console-based EGuiBase.
///
/// Approach: no ANSI scroll region (unreliable across terminals).
/// Instead: the input line is always at the bottom. When output arrives:
/// 1. Clear the current input line
/// 2. Move cursor up one line
/// 3. Write the output (scrolls normally)
/// 4. Move to bottom, reprint "> " + whatever user had typed
///
/// This is the same pattern tmux, screen, and other TUIs use when they
/// can't rely on scroll regions.
/// </summary>
public sealed class EGuiConsole : EGuiBase
{
    private bool _ansiSupported;
    private readonly object _writeLock = new();
    private readonly StringBuilder _inputBuffer = new();
    private string _inputPrompt = "> ";
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
    /// Write output. If user is typing (input active), we:
    /// 1. Clear the input line
    /// 2. Move up one line so output appears ABOVE the input
    /// 3. Write the output (normal scroll)
    /// 4. On a fresh line, reprint "> " + user's partial input
    ///
    /// If user is NOT typing, just write normally.
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
                // We're on the input line. Clear it, move up, write output, reprint input.
                Console.Write("\r\x1b[2K");       // clear current line
                Console.Write("\x1b[A");            // move up one line
                Console.Write("\n");                // newline (creates space below for our output)
                Console.Write(text);                // write the output
                // Ensure output ends with a newline so we get a fresh line for input
                if (!text.EndsWith("\n"))
                    Console.Write("\n");
                // Reprint prompt + partial input on the new bottom line
                Console.Write(_inputPrompt);
                Console.Write(_inputBuffer.ToString());
            }
            else
            {
                // No input active — just write
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
        _inputPrompt = prompt;
        _inputBuffer.Clear();

        lock (_writeLock)
        {
            Console.Write(prompt);
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
                    Console.WriteLine();
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
                        // Clear input buffer, keep prompt
                        _inputBuffer.Clear();
                        Console.Write("\r\x1b[2K");
                        Console.Write(_inputPrompt);
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