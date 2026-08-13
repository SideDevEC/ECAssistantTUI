using System.Text;
using static ECAssistant.EColor;

namespace ECAssistant.UI;

/// <summary>
/// Console-based EGuiBase with ANSI scroll region.
/// Output scrolls above a fixed input line at the bottom.
/// 
/// Strategy: Track only the output ROW (not column). Before writing output
/// when returning from input line, position cursor at (outputRow, 1). Write
/// text and let the terminal flow the cursor. Count newlines to update outputRow.
/// Don't count columns — ANSI color codes break column tracking.
/// 
/// For token streaming (WriteRaw, no newlines): the cursor stays on the same
/// row, text appends naturally. We just need to know which row to return to.
/// </summary>
public sealed class EGuiConsole : EGuiBase
{
    private static bool _ansiSupported;
    private static int _scrollBottom;
    private static int _inputRow;

    private static readonly object _cursorLock = new();
    private static StringBuilder _inputBuffer = new();
    private static string _inputPrompt = "> ";
    private static bool _inputActive;

    // Output cursor row tracking (1-based, within scroll region)
    private static int _outputRow = 1;
    private static bool _cursorOnInputLine;

    public static void InitConsole()
    {
        _ansiSupported = DetectAnsiSupport();
        if (_ansiSupported)
        {
            UpdateTerminalSize();
            SetupScrollRegion();
        }
    }

    public static void ShutdownConsole()
    {
        if (!_ansiSupported) return;
        Console.Write("\x1b[r");
        Console.Write("\x1b[2J");
        Console.Write("\x1b[1;1H");
    }

    private static bool DetectAnsiSupport()
    {
        var term = Environment.GetEnvironmentVariable("TERM");
        if (string.IsNullOrEmpty(term) || term == "dumb") return false;
        try { if (Console.IsOutputRedirected) return false; } catch { return false; }
        if (OperatingSystem.IsWindows()) { try { EnableWindowsAnsi(); } catch { } }
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

    private static void EnableWindowsAnsi()
    {
        if (!OperatingSystem.IsWindows()) return;
        var handle = GetStdHandle(STD_OUTPUT_HANDLE);
        if (GetConsoleMode(handle, out uint mode))
        {
            mode |= ENABLE_VIRTUAL_TERMINAL_PROCESSING;
            SetConsoleMode(handle, mode);
        }
    }

    private static void UpdateTerminalSize()
    {
        try
        {
            _scrollBottom = Console.WindowHeight - 1;
            _inputRow = Console.WindowHeight;
        }
        catch
        {
            _scrollBottom = 23;
            _inputRow = 24;
        }
    }

    private static void SetupScrollRegion()
    {
        Console.Write($"\x1b[1;{_scrollBottom}r");
        Console.Write("\x1b[2J");
        Console.Write("\x1b[1;1H");
        _outputRow = 1;
        _cursorOnInputLine = false;
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
            if (_cursorOnInputLine)
            {
                // Return cursor to scroll region at the output row, column 1
                Console.Write($"\x1b[{_outputRow};1H");
                _cursorOnInputLine = false;
            }
            // If not on input line, cursor is already where output left off — just write

            Console.Write(text);

            // Update output row: count newlines in text
            int newlines = 0;
            foreach (char c in text)
                if (c == '\n') newlines++;
            _outputRow += newlines;
            if (_outputRow > _scrollBottom)
                _outputRow = _scrollBottom;  // scrolled — cursor at bottom of region

            if (_inputActive)
            {
                RedrawInputLine();  // moves cursor to input row, sets _cursorOnInputLine
            }
        }
    }

    private void WriteOutputLine(string text) => WriteOutput(text + "\n");

    private static void RedrawInputLine()
    {
        if (!_ansiSupported) return;
        Console.Write($"\x1b[{_inputRow};1H");
        Console.Write("\x1b[2K");
        Console.Write(_inputPrompt);
        Console.Write(_inputBuffer.ToString());
        _cursorOnInputLine = true;
    }

    // ── EGuiBase ──

    public override void WriteLine(string text) => WriteOutputLine(text);
    public override void WriteLineColored(string coloredText) => WriteOutputLine(coloredText);
    public override void WriteRaw(string text) => WriteOutput(text);
    public override void BlankLine() => WriteOutputLine("");
    public override void InfoColored(string coloredText) => WriteOutputLine(coloredText);
    public override void WarningColored(string coloredText) => WriteOutputLine(coloredText);
    public override void WriteRawDirect(string text) => WriteOutput(text);
    public override void LogInternal(string text) => WriteOutputLine(text);

    // ── User Input ──

    public override string? PromptColored(string labelAndText) => ReadInputLine(labelAndText);
    public override string? PromptRaw(string label) => ReadInputLine(label);

    private static string? ReadInputLine(string prompt)
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

        // ANSI mode
        lock (_cursorLock)
        {
            _inputActive = true;
            RedrawInputLine();
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

                    // Clear input line
                    Console.Write($"\x1b[{_inputRow};1H");
                    Console.Write("\x1b[2K");
                    _cursorOnInputLine = false;

                    // Echo submitted line into scroll region at current output position
                    Console.Write($"\x1b[{_outputRow};1H");
                    Console.WriteLine($"{prompt}{result}");
                    _outputRow++;
                    if (_outputRow > _scrollBottom) _outputRow = _scrollBottom;

                    return result;
                }
                else if (key.Key == ConsoleKey.Backspace)
                {
                    if (_inputBuffer.Length > 0)
                    {
                        _inputBuffer.Remove(_inputBuffer.Length - 1, 1);
                        RedrawInputLine();
                    }
                }
                else if (key.Key == ConsoleKey.Escape)
                {
                    _inputBuffer.Clear();
                    RedrawInputLine();
                }
                else if (key.KeyChar != '\0')
                {
                    _inputBuffer.Append(key.KeyChar);
                    RedrawInputLine();
                }
            }
        }
    }
}