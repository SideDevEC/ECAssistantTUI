using System.Text;

namespace ECAssistant.UI;

/// <summary>
/// Console-based EGuiBase.
/// Uses a simple approach: output writes to console normally,
/// input prompt is shown before blocking on ReadLine.
/// 
/// For concurrent input while streaming: the output thread writes
/// tokens directly to stdout. The main thread blocks on ReadKey in
/// the input loop. A lock prevents interleaving.
/// </summary>
public sealed class EGuiConsole : EGuiBase
{
    private bool _ansiSupported;
    private readonly object _cursorLock = new();
    private StringBuilder _inputBuffer = new();
    private string _inputPrompt = "> ";
    private bool _inputActive;

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
        if (!_ansiSupported) return;
        try { Console.CursorVisible = true; } catch { }
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

    // ── Output ──

    private void WriteOutput(string text)
    {
        if (!_ansiSupported)
        {
            Console.Write(text);
            return;
        }

        lock (_cursorLock)
        {
            // If input is active, temporarily move cursor to a new line for output
            if (_inputActive)
            {
                // Save cursor, move to next line after input, write output
                // Then redraw input on its own line
                Console.Write("\x1b[s");  // save cursor position
                Console.Write("\r");       // return to start of current line
                Console.Write("\x1b[A");   // move up one line (above input)
                Console.Write("\n");       // new line — pushes input down
                Console.Write(text);
                Console.Write("\n");       // ensure output ends on its own line
                // Redraw input prompt on the new line below
                Console.Write(_inputPrompt);
                Console.Write(_inputBuffer.ToString());
                Console.Write("\x1b[u");  // restore cursor
            }
            else
            {
                Console.Write(text);
            }
        }
    }

    private void WriteOutputLine(string text) => WriteOutput(text + "\n");

    // ── EGuiBase ──

    public override void WriteLine(string text) => WriteOutputLine(text);
    public override void WriteLineColored(string coloredText) => WriteOutputLine(coloredText);
    public override void WriteRaw(string text) => WriteOutput(text);
    public override void BlankLine() => WriteOutputLine("");
    public override void InfoColored(string coloredText) => WriteOutputLine(coloredText);
    public override void WarningColored(string coloredText) => WriteOutputLine(coloredText);
    public override void WriteRawDirect(string text)
    {
        // Direct streaming — same as WriteOutput but without input line management
        // This prevents line break issues during token streaming
        if (!_ansiSupported)
        {
            Console.Write(text);
            return;
        }

        lock (_cursorLock)
        {
            if (_inputActive)
            {
                // Write output on the line above the input, then restore input
                Console.Write("\x1b[s");
                Console.Write("\r\x1b[A\n");
                Console.Write(text);
                Console.Write("\x1b[u");
            }
            else
            {
                Console.Write(text);
            }
        }
    }
    public override void LogInternal(string text) => WriteOutputLine(text);

    public override bool IsEscapePressed()
    {
        try
        {
            return Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    // ── User Input ──

    public override string? PromptColored(string labelAndText) => ReadInputLine(labelAndText);
    public override string? PromptRaw(string label) => ReadInputLine(label);

    private string? ReadInputLine(string prompt)
    {
        _inputPrompt = prompt;
        _inputBuffer.Clear();

        if (!_ansiSupported)
        {
            Console.Write(prompt);
            var sb = new StringBuilder();
            while (true)
            {
                try { if (!Console.KeyAvailable) { Thread.Sleep(10); continue; } }
                catch (InvalidOperationException) { return Console.ReadLine(); }
                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return sb.ToString(); }
                else if (key.Key == ConsoleKey.Backspace)
                {
                    if (sb.Length > 0) { sb.Remove(sb.Length - 1, 1); Console.Write("\b \b"); }
                }
                else if (key.KeyChar != '\0') { sb.Append(key.KeyChar); Console.Write(key.KeyChar); }
            }
        }

        // ANSI mode — simple approach: just print prompt and read
        lock (_cursorLock)
        {
            _inputActive = true;
            Console.Write(prompt);
        }

        while (true)
        {
            try
            {
                if (!Console.KeyAvailable) { Thread.Sleep(10); continue; }
            }
            catch (InvalidOperationException)
            {
                lock (_cursorLock) { _inputActive = false; }
                return Console.ReadLine();
            }

            var key = Console.ReadKey(true);

            lock (_cursorLock)
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
                    _inputBuffer.Clear();
                    // Clear current line and redraw prompt
                    Console.Write("\r\x1b[2K");
                    Console.Write(prompt);
                }
                else if (key.KeyChar != '\0')
                {
                    _inputBuffer.Append(key.KeyChar);
                    Console.Write(key.KeyChar);
                }
            }
        }
    }
}