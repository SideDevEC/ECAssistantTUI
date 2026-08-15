using System.Text;

namespace ECAssistant.UI;

/// <summary>
/// Console-based EGuiBase.
///
/// Architecture:
/// - Output writes to console normally (scrolls up)
/// - Input line at the bottom with "> " prompt, always visible
/// - When output arrives while user is typing, output is written on a new line
///   above the input, and the input line + current typed text is reprinted below
/// - No ANSI scroll region — just save/restore cursor around the input line
/// </summary>
public sealed class EGuiConsole : EGuiBase
{
    private bool _ansiSupported;
    private readonly object _writeLock = new();
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
    // All output methods write to the console. If input is active (user is typing),
    // output is written on the line above the input, then the input line is reprinted.

    private void WriteToConsole(string text)
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
                // Clear current input line, move cursor up, write output, then reprint input below
                Console.Write("\r\x1b[2K");          // clear input line
                Console.Write("\x1b[A");               // move up one line
                Console.Write("\n");                   // new line (creates space)
                Console.Write(text);                   // write output
                Console.Write("\n");                   // ensure output ends on its own line
                // Reprint input prompt + buffer on the new line
                Console.Write(_inputPrompt);
                Console.Write(_inputBuffer.ToString());
            }
            else
            {
                Console.Write(text);
            }
        }
    }

    public override void WriteLine(string text) => WriteToConsole(text + "\n");
    public override void WriteLineColored(string coloredText) => WriteToConsole(coloredText + "\n");
    public override void WriteRaw(string text) => WriteToConsole(text);
    public override void BlankLine() => WriteToConsole("\n");
    public override void InfoColored(string coloredText) => WriteToConsole(coloredText + "\n");
    public override void WarningColored(string coloredText) => WriteToConsole(coloredText + "\n");

    // WriteRawDirect — for token streaming. Same approach: write above input line.
    public override void WriteRawDirect(string text) => WriteToConsole(text);

    public override void LogInternal(string text) => WriteToConsole(text + "\n");

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

    // ── Input ──
    // The input field is always visible with "> " prompt.
    // ReadInputLine prints the prompt and reads keys until Enter.
    // Output arriving while input is active is handled by WriteToConsole.

    public override string? PromptColored(string labelAndText) => ReadInputLine(labelAndText);
    public override string? PromptRaw(string label) => ReadInputLine(label);

    private string? ReadInputLine(string prompt)
    {
        _inputPrompt = prompt;
        _inputBuffer.Clear();

        // Print the prompt — it stays visible until user presses Enter
        lock (_writeLock)
        {
            Console.Write(prompt);
            _inputActive = true;
        }

        while (true)
        {
            try
            {
                if (!Console.KeyAvailable) { Thread.Sleep(10); continue; }
            }
            catch (InvalidOperationException)
            {
                lock (_writeLock) { _inputActive = false; }
                return Console.ReadLine();
            }

            var key = Console.ReadKey(true);

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
                    _inputBuffer.Clear();
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