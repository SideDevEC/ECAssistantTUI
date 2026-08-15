using System.Text;

namespace ECAssistant.UI;

/// <summary>
/// Console-based EGuiBase.
///
/// Simplest possible approach: "> " is always at the start of the current line.
/// Output writes on the current line (overwriting "> "), then the \n scrolls
/// it up, and we immediately write "> " on the new line.
///
/// This is just: write output + \n, then write "> " + buffer.
/// No cursor movement, no clear-line tricks.
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
    /// Write output then reprint "> " + buffer.
    ///
    /// If prompt is showing:
    ///   - \r to go to start of line (where "> " is)
    ///   - \x1b[2K to clear the line (clears "> " + partial input)
    ///   - write the output text (its trailing \n scrolls up)
    ///   - write "> " + buffer on the new line
    ///
    /// If prompt not showing yet: just write.
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

            // Clear current line (where "> " + input is)
            Console.Write("\r\x1b[2K");
            // Write the output — trailing \n scrolls it up into the scrollback
            Console.Write(text);
            // Ensure we end on a newline so "> " starts on a fresh line
            if (!text.EndsWith("\n"))
                Console.Write("\n");
            // Reprint "> " + partial input
            Console.Write(PromptStr);
            Console.Write(_inputBuffer.ToString());
            Console.Out.Flush();
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
            Console.Write(PromptStr);
            _promptShowing = true;
            Console.Out.Flush();
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

                    // Clear line, write "> text", newline scrolls it up
                    Console.Write("\r\x1b[2K");
                    Console.Write(PromptStr);
                    Console.Write(result);
                    Console.Write("\n");

                    // Reprint "> " for next input
                    Console.Write(PromptStr);
                    Console.Out.Flush();

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
                        Console.Write("\r\x1b[2K");
                        Console.Write(PromptStr);
                        Console.Out.Flush();
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