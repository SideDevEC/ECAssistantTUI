using System.Text;

namespace ECAssistant.UI;

/// <summary>
/// Full-screen alternate-buffer TUI for ECAssistant.
///
/// Layout (fixed, no scrollback — like nano/vim):
/// ┌─────────────────────────────────────┐  row 0
/// │ Output region (scrolls internally)   │
/// │ ...                                  │
/// │                                      │
/// ├─────────────────────────────────────┤  row (height-2)
/// │ Status bar                           │
/// ├─────────────────────────────────────┤  row (height-1)
/// │ > user input here                    │
/// └─────────────────────────────────────┘
///
/// Uses the terminal alternate screen buffer (\x1b[?1049h).
/// No scrollback — we maintain our own output line history.
/// Input is always at the bottom row — never moves, never gets overwritten.
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

    // Silent mode: typed characters are buffered but not echoed.
    private volatile bool _silentInput;
    private Func<bool>? _silentInputCheck;

    // ── Startup buffering ──
    // Before InitConsole(), output is queued here instead of written to terminal.
    // InitConsole() flushes all queued lines in one paint after entering alternate buffer.
    private readonly List<string> _startupBuffer = new();
    private bool _bufferingMode = true; // true until InitConsole() is called

    // ── Screen model ──
    // Output lines stored with ANSI color codes already embedded.
    internal readonly List<string> _outputLines = new();
    private string _statusBar = "";

    // Scroll position: 0 = bottom (newest), N = scrolled up N lines from bottom
    private int _scrollOffset;
    private bool _isScrolledUp => _scrollOffset > 0;

    // Screen dimensions (recalculated on resize)
    private int _screenWidth;
    private int _screenHeight;
    private int _outputRegionStart; // usually 0
    private int _outputRegionEnd;   // screenHeight - 3 (inclusive)
    private int _statusRow;         // screenHeight - 2
    private int _inputRow;          // screenHeight - 1

    // Dirty tracking
    private bool _outputDirty;
    private bool _statusDirty;
    private bool _inputDirty;
    private bool _fullRepaint;

    // Track what's currently on screen to avoid redundant writes
    private string?[] _screenRows = Array.Empty<string?>();

    // ── Resize watcher ──
    private Timer? _resizeTimer;
    private int _lastWidth;
    private int _lastHeight;

    // ── Layer stack ──
    // When non-empty, the top layer owns the screen.
    // Output/input go to the layer instead of the normal session view.
    private readonly Stack<IGuiLayer> _layerStack = new();
    private bool _inLayerMode => _layerStack.Count > 0;

    // ═══════════════════════════════════════════════════
    //  INIT / SHUTDOWN
    // ═══════════════════════════════════════════════════

    public void InitConsole()
    {
        _ansiSupported = DetectAnsiSupport();
        if (!_ansiSupported)
        {
            // No ANSI: flush buffered output to primary terminal
            _bufferingMode = false;
            FlushStartupBuffer();
            return;
        }

        // Enter alternate screen buffer + hide cursor
        Console.Write("\x1b[?1049h\x1b[?25l");
        Console.Out.Flush();

        UpdateDimensions();

        // Flush all buffered startup output into the output model, then paint once
        _bufferingMode = false;
        lock (_writeLock)
        {
            foreach (var line in _startupBuffer)
                AddOutputLine(line);
            _startupBuffer.Clear();
            _fullRepaint = true;
            Repaint();
            PositionCursorAtInput();
            Console.Out.Flush();
        }

        // Start background resize watcher
        StartResizeWatcher();

        // NOTE: SessionLayer removed from stack — it was blocking input and output
        // routing in ReadInputLine/WriteOutput because _inLayerMode was always true.
        // The session view is painted by the normal Repaint() path without layers.
        // Overlay layers (HelpLayer) are pushed dynamically when needed.
    }

    /// <summary>Flush buffered startup output to the terminal (non-ANSI path).</summary>
    private void FlushStartupBuffer()
    {
        foreach (var line in _startupBuffer)
            Console.Write(line);
        Console.Out.Flush();
        _startupBuffer.Clear();
    }

    public void ShutdownConsole()
    {
        _resizeTimer?.Dispose();
        _resizeTimer = null;

        if (!_ansiSupported) return;

        // Leave alternate screen buffer + show cursor
        Console.Write("\x1b[?25h\x1b[?1049l");
        Console.Out.Flush();
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
        _inputDirty = true;
    }

    // ═══════════════════════════════════════════════════
    //  ANSI / PLATFORM
    // ═══════════════════════════════════════════════════

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
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static extern IntPtr GetStdHandle(int nStdHandle);
    private const int STD_OUTPUT_HANDLE = -11;
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
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
    //  SCREEN DIMENSIONS
    // ═══════════════════════════════════════════════════

    private void UpdateDimensions()
    {
        try
        {
            _screenWidth = Console.WindowWidth;
            _screenHeight = Console.WindowHeight;
        }
        catch
        {
            _screenWidth = 80;
            _screenHeight = 24;
        }

        if (_screenWidth < 10) _screenWidth = 10;
        if (_screenHeight < 5) _screenHeight = 5;

        _outputRegionStart = 0;
        _outputRegionEnd = _screenHeight - 3;
        _statusRow = _screenHeight - 2;
        _inputRow = _screenHeight - 1;

        _screenRows = new string?[_screenHeight];
        _fullRepaint = true;
    }

    private bool CheckResize()
    {
        try
        {
            int w = Console.WindowWidth;
            int h = Console.WindowHeight;
            if (w != _screenWidth || h != _screenHeight)
            {
                UpdateDimensions();
                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Background timer that detects terminal resize and triggers a full repaint.
    /// Polls every 200ms — lightweight, no console writes unless size actually changed.
    /// </summary>
    private void StartResizeWatcher()
    {
        _lastWidth = _screenWidth;
        _lastHeight = _screenHeight;
        _resizeTimer = new Timer(OnResizeCheck, null, 200, 200);
    }

    private void OnResizeCheck(object? state)
    {
        if (!_ansiSupported) return;

        try
        {
            int w = Console.WindowWidth;
            int h = Console.WindowHeight;
            if (w != _lastWidth || h != _lastHeight)
            {
 _lastWidth = w;
                _lastHeight = h;
                lock (_writeLock)
                {
                    UpdateDimensions();
                    if (_inLayerMode)
                        _layerStack.Peek().OnResize(this);
                    else
                    {
                        _fullRepaint = true;
                        Repaint();
                        PositionCursorAtInput();
                    }
                    Console.Out.Flush();
                }
            }
        }
        catch { }
    }

    // ═══════════════════════════════════════════════════
    //  RENDER ENGINE
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Repaint dirty regions to the terminal. Only writes what changed.
    /// Must be called inside _writeLock.
    /// </summary>
    private void Repaint()
    {
        if (!_ansiSupported) return;

        if (_fullRepaint)
        {
            // Full repaint: clear screen, invalidate cache, draw everything
            Console.Write("\x1b[2J");
            Array.Fill(_screenRows, null);

            // Paint output region
            PaintOutputRegion();
            // Paint status bar
            PaintStatusBar();
            // Paint input line
            PaintInputLine();

            _fullRepaint = false;
            _outputDirty = false;
            _statusDirty = false;
            _inputDirty = false;
            return;
        }

        if (_outputDirty)
        {
            PaintOutputRegion();
            _outputDirty = false;
        }

        if (_statusDirty)
        {
            PaintStatusBar();
            _statusDirty = false;
        }

        if (_inputDirty)
        {
            PaintInputLine();
            _inputDirty = false;
        }

        Console.Out.Flush();
    }

    /// <summary>
    /// Paint the output region (rows 0 to _outputRegionEnd).
    /// Shows lines from _outputLines based on _scrollOffset.
    /// 0 = bottom (newest lines visible), N = scrolled up N lines.
    /// Long lines are wrapped to fit screen width.
    ///
    /// Delta rendering: compares each visible row against _screenRows cache.
    /// Only clears+writes rows that actually changed. Full clear only on resize/fullRepaint.
    /// </summary>
    private void PaintOutputRegion()
    {
        int regionHeight = _outputRegionEnd - _outputRegionStart + 1;

        // Build the list of wrapped visible lines (bottom-up, then reverse)
        // Start from the bottom of the output and work upward
        var visibleRows = new List<string>();
        int linesUsed = 0;
        int maxVisibleLine = _outputLines.Count - 1 - _scrollOffset;

        for (int i = maxVisibleLine; i >= 0 && linesUsed < regionHeight; i--)
        {
            string line = _outputLines[i];
            int visibleLen = StripAnsi(line).Length;
            if (visibleLen <= _screenWidth)
            {
                visibleRows.Insert(0, line);
                linesUsed++;
            }
            else
            {
                // Wrap long line into multiple rows
                var wrapped = WrapLine(line, _screenWidth);
                for (int w = wrapped.Count - 1; w >= 0; w--)
                {
                    if (linesUsed >= regionHeight) break;
                    visibleRows.Insert(0, wrapped[w]);
                    linesUsed++;
                }
            }
        }

        // Delta render: only clear+write rows that differ from _screenRows cache
        for (int i = 0; i < regionHeight; i++)
        {
            string? newRow = i < visibleRows.Count ? visibleRows[i] : null;
            string? oldRow = i < _screenRows.Length ? _screenRows[i] : null;

            if (newRow != oldRow)
            {
                int row = _outputRegionStart + i;
                Console.Write($"\x1b[{row + 1};1H\x1b[2K");
                if (newRow != null)
                    Console.Write(newRow);
                if (i < _screenRows.Length)
                    _screenRows[i] = newRow;
            }
        }
    }

    /// <summary>
    /// Paint the status bar row.
    /// </summary>
    private void PaintStatusBar()
    {
        Console.Write($"\x1b[{_statusRow + 1};1H\x1b[2K");
        if (!string.IsNullOrEmpty(_statusBar))
        {
            string bar = _statusBar;
            int visibleLen = StripAnsi(bar).Length;
            if (visibleLen > _screenWidth)
                bar = TruncateAnsi(bar, _screenWidth);
            Console.Write(bar);
        }
    }

    /// <summary>
    /// Paint the input row: "> " + buffer (or just "> " in silent mode) + steady cursor.
    /// </summary>
    private void PaintInputLine()
    {
        Console.Write($"\x1b[{_inputRow + 1};1H\x1b[2K");
        Console.Write(PromptStr);
        Console.Write(_inputBuffer.ToString());

        // Steady cursor at end of input
        int col = PromptStr.Length + _inputBuffer.Length;
        if (col < _screenWidth)
            Console.Write("\x1b[7m \x1b[0m"); // reverse-video space = block cursor
    }

    /// <summary>
    /// Move cursor to the input row, just after the prompt + typed text.
    /// </summary>
    private void PositionCursorAtInput()
    {
        // Cursor is drawn as part of PaintInputLine (block cursor)
        // Just position after it — the block cursor is at the end of input
        int col = PromptStr.Length + _inputBuffer.Length;
        Console.Write($"\x1b[{_inputRow + 1};{col + 1}H");
    }

    // ═══════════════════════════════════════════════════
    //  ANSI HELPERS
    // ═══════════════════════════════════════════════════

    /// <summary>Remove ANSI escape sequences from a string to get visible length.</summary>
    internal static string StripAnsi(string text)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '\x1b')
            {
                i++;
                // Skip CSI sequence: \x1b[ params intermediates final
                if (i < text.Length && text[i] == '[')
                {
                    i++; // skip '['
                    // Skip parameter bytes 0x30-0x3F (digits, ;, :, etc.)
                    while (i < text.Length && text[i] >= 0x30 && text[i] <= 0x3F) i++;
                    // Skip intermediate bytes 0x20-0x2F
                    while (i < text.Length && text[i] >= 0x20 && text[i] <= 0x2F) i++;
                    // Skip final byte 0x40-0x7E
                    if (i < text.Length && text[i] >= 0x40 && text[i] <= 0x7E) i++;
                }
                // Skip OSC sequence: \x1b] ... BEL or ST
                else if (i < text.Length && text[i] == ']')
                {
                    i++; // skip ']'
                    while (i < text.Length && text[i] != '\x07' && text[i] != '\x1b') i++;
                    if (i < text.Length && text[i] == '\x1b' && i + 1 < text.Length && text[i + 1] == '\\') i += 2;
                    else if (i < text.Length) i++; // skip BEL
                }
                // Other escape sequences: skip next char
                else if (i < text.Length)
                {
                    i++; // skip single char after ESC
                }
            }
            else
            {
                sb.Append(text[i]);
                i++;
            }
        }
        return sb.ToString();
    }

    /// <summary>Truncate a string with ANSI codes to a max visible width.</summary>
    internal static string TruncateAnsi(string text, int maxWidth)
    {
        var visible = StripAnsi(text);
        if (visible.Length <= maxWidth) return text;
        // Simple approach: find the cut point in the original string
        int visibleCount = 0;
        int cutIdx = 0;
        bool inEscape = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\x1b') { inEscape = true; continue; }
            if (inEscape)
            {
                if (c >= 0x40 && c <= 0x7E) inEscape = false;
                continue;
            }
            visibleCount++;
            if (visibleCount > maxWidth)
            {
                cutIdx = i;
                break;
            }
        }
        return text.Substring(0, cutIdx) + "\x1b[0m [...]\x1b[0m";
    }

    // ═══════════════════════════════════════════════════
    //  OUTPUT
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Add a line to the output buffer and mark the output region dirty.
    /// Lines are stored with ANSI color codes intact.
    /// </summary>
    /// <summary>Wrap a line with ANSI codes into multiple rows, each ≤ maxCols visible width.</summary>
    internal static List<string> WrapLine(string text, int maxCols)
    {
        var result = new List<string>();
        var visible = StripAnsi(text);
        if (visible.Length <= maxCols)
        {
            result.Add(text);
            return result;
        }

        // Simple wrapping: split visible text into chunks, preserving ANSI by re-scanning
        // For simplicity, strip ANSI, chunk, and re-add reset after each chunk
        int idx = 0;
        while (idx < visible.Length)
        {
            int len = Math.Min(maxCols, visible.Length - idx);
            string chunk = visible.Substring(idx, len);
            result.Add(chunk);
            idx += len;
        }
        return result;
    }

    internal void AddOutputLine(string text)
    {
        // Split multi-line text into individual lines
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            // Remove trailing \r if present (Windows line endings)
            string clean = lines[i].EndsWith('\r') ? lines[i][..^1] : lines[i];
            // Skip empty trailing entry from text ending with \n
            // (split of "text\n" gives ["text", ""] — don't add the trailing "")
            if (i == lines.Length - 1 && string.IsNullOrEmpty(clean) && lines.Length > 1)
                continue;
            _outputLines.Add(clean);
        }
        // New output = snap to bottom
        _scrollOffset = 0;
        _outputDirty = true;
    }

    private void WriteOutput(string text)
    {
        if (!_ansiSupported)
        {
            lock (_writeLock) { Console.Write(text); Console.Out.Flush(); }
            return;
        }

        // Buffering mode: queue output until InitConsole() enters alternate buffer
        if (_bufferingMode)
        {
            lock (_writeLock) { _startupBuffer.Add(text); }
            return;
        }

        lock (_writeLock)
        {
            if (CheckResize()) _fullRepaint = true;
            AddOutputLine(text);
            // Repaint when no overlay layer is active.
            // The base SessionLayer (name="session") doesn't block output rendering.
            var topForOutput = _inLayerMode ? _layerStack.Peek() : null;
            if (!_inLayerMode || (topForOutput != null && topForOutput.Name == "session"))
            {
                UpdateScrollStatus();
                Repaint();
                PositionCursorAtInput();
            }
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

    // ═══════════════════════════════════════════════════
    //  SCROLL NAVIGATION
    // ═══════════════════════════════════════════════════

    private void ScrollUp(int lines)
    {
        int regionHeight = _outputRegionEnd - _outputRegionStart + 1;
        int maxScroll = Math.Max(0, _outputLines.Count - regionHeight);
        int newOffset = Math.Min(maxScroll, _scrollOffset + lines);
        if (newOffset == _scrollOffset) return;
        _scrollOffset = newOffset;
        _outputDirty = true;
        UpdateScrollStatus();
        Repaint();
        PositionCursorAtInput();
        Console.Out.Flush();
    }

    private void ScrollDown(int lines)
    {
        int newOffset = Math.Max(0, _scrollOffset - lines);
        if (newOffset == _scrollOffset) return;
        _scrollOffset = newOffset;
        _outputDirty = true;
        UpdateScrollStatus();
        Repaint();
        PositionCursorAtInput();
        Console.Out.Flush();
    }

    private void ScrollToBottom()
    {
        if (_scrollOffset == 0) return;
        _scrollOffset = 0;
        _outputDirty = true;
        UpdateScrollStatus();
        Repaint();
        PositionCursorAtInput();
        Console.Out.Flush();
    }

    private void UpdateScrollStatus()
    {
        if (_scrollOffset > 0)
        {
            int totalLines = _outputLines.Count;
            _statusBar = $"\x1b[2m\x1b[36m↑ Scrolled up {_scrollOffset} line(s) — PageDown to return ({totalLines} total lines)\x1b[0m";
            _statusDirty = true;
        }
        else if (!string.IsNullOrEmpty(_statusBar) && _statusBar.StartsWith("\x1b[2m\x1b[36m↑"))
        {
            _statusBar = "";
            _statusDirty = true;
        }
    }

    // ═══════════════════════════════════════════════════
    //  STATUS BAR
    // ═══════════════════════════════════════════════════

    /// <summary>Update the status bar content. Pass empty string to clear.</summary>
    public void SetStatusBar(string text)
    {
        lock (_writeLock)
        {
            _statusBar = text;
            _statusDirty = true;
            if (_ansiSupported)
            {
                if (CheckResize()) _fullRepaint = true;
                Repaint();
                PositionCursorAtInput();
                Console.Out.Flush();
            }
        }
    }

    // ════════════════════════════════════════════════════════
    //  LAYER STACK
    // ════════════════════════════════════════════════════════

    /// <summary>Trigger a full repaint of the session view. Used by SessionLayer.</summary>
    public void TriggerFullRepaint()
    {
        lock (_writeLock)
        {
            _fullRepaint = true;
            Repaint();
            PositionCursorAtInput();
            Console.Out.Flush();
        }
    }

    /// <summary>Push a new layer onto the view stack. The layer takes over the screen.</summary>
    public void PushLayer(IGuiLayer layer)
    {
        if (!_ansiSupported) return;
        lock (_writeLock)
        {
            _layerStack.Push(layer);
            layer.OnActivate(this);
            Console.Out.Flush();
        }
    }

    /// <summary>Pop the current layer and restore the previous view.</summary>
    public void PopLayer()
    {
        if (!_ansiSupported) return;
        lock (_writeLock)
        {
            if (_layerStack.Count > 0)
                _layerStack.Pop();

            if (_layerStack.Count > 0)
            {
                _layerStack.Peek().OnActivate(this);
            }
            else
            {
                // No layers left — restore the session view with a full repaint
                _fullRepaint = true;
                Repaint();
                PositionCursorAtInput();
            }
            Console.Out.Flush();
        }
    }

    /// <summary>
    /// Paint a full-screen content array for a layer.
    /// Each string is one line, written top to bottom.
    /// </summary>
    public void PaintLayerScreen(string[] lines)
    {
        if (!_ansiSupported) return;

        lock (_writeLock)
        {
            Console.Write("\x1b[2J");

            int startRow = 0;
            // Center vertically if content is shorter than screen
            int contentHeight = lines.Length;
            if (contentHeight < _screenHeight)
                startRow = (_screenHeight - contentHeight) / 4; // near top, not dead center

            for (int i = 0; i < lines.Length && i < _screenHeight; i++)
            {
                int row = startRow + i + 1;
                string line = lines[i];
                int visibleLen = StripAnsi(line).Length;
                if (visibleLen > _screenWidth)
                    line = TruncateAnsi(line, _screenWidth);
                Console.Write($"\x1b[{row};1H{line}");
            }

            // Footer hint at bottom
            Console.Write($"\x1b[{_screenHeight};1H\x1b[2K{_color_Dim}  Press Enter to return{_color_Reset}");
            Console.Out.Flush();
        }
    }

    // ── ANSI color shortcuts for layer rendering ──
    private static string _color_Dim => "\x1b[2m";
    private static string _color_Reset => "\x1b[0m";

    // ════════════════════════════════════════════════════════
    //  CLEAR CANVAS
    // ════════════════════════════════════════════════════════

    public override void ClearCanvas()
    {
        if (!_ansiSupported)
        {
            lock (_writeLock)
            {
                try { Console.Clear(); } catch { }
                Console.Out.Flush();
            }
            return;
        }

        lock (_writeLock)
        {
            _outputLines.Clear();
            _scrollOffset = 0;
            UpdateScrollStatus();
            _fullRepaint = true;
            Repaint();
            PositionCursorAtInput();
            Console.Out.Flush();
        }
    }

    public override void LogInternal(string text) => WriteOutput(text + "\n");

    // ═══════════════════════════════════════════════════
    //  INPUT
    // ═══════════════════════════════════════════════════

    public override string? PromptColored(string labelAndText) => ReadInputLine();
    public override string? PromptRaw(string label) => ReadInputLine();

    private string? ReadInputLine()
    {
        _inputBuffer.Clear();

        if (_ansiSupported)
        {
            lock (_writeLock)
            {
                if (CheckResize()) _fullRepaint = true;
                _inputDirty = true;
                Repaint();
                PositionCursorAtInput();
                Console.Out.Flush();
            }
        }

        while (true)
        {
            ConsoleKeyInfo key;
            try
            {
                // Poll: check if silent mode should turn off
                if (_silentInput && _silentInputCheck != null && !_silentInputCheck())
                {
                    _silentInput = false;
                    if (_ansiSupported)
                    {
                        lock (_writeLock)
                        {
                            _inputDirty = true;
                            Repaint();
                            PositionCursorAtInput();
                            Console.Out.Flush();
                        }
                    }
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

            if (!_ansiSupported)
            {
                // Fallback: old behavior for non-ANSI terminals
                return ReadInputLineFallback(key);
            }

            // ── Layer mode: route keys to overlay layers only ──
            // The base SessionLayer (name="session") never intercepts keys —
            // keys fall through to the normal input handler below.
            // Only overlay layers (HelpLayer, etc.) get key routing.
            var topLayer = _inLayerMode ? _layerStack.Peek() : null;
            if (topLayer != null && topLayer.Name != "session")
            {
                lock (_writeLock)
                {
                    var activeLayer = _layerStack.Peek();
                    bool handled = activeLayer.OnKey(this, key);
                    if (!handled)
                    {
                        // Layer requested pop
                        PopLayer();
                    }
                    Console.Out.Flush();
                }
                continue; // don't return — keep reading input for next prompt
            }

            lock (_writeLock)
            {
                if (CheckResize()) _fullRepaint = true;

                if (key.Key == ConsoleKey.Enter)
                {
                    var result = _inputBuffer.ToString();
                    _inputBuffer.Clear();

                    // Add the submitted input as an output line: "> text"
                    // But skip commands (starting with /) — they're not conversation
                    if (!result.StartsWith("/"))
                        AddOutputLine(PromptStr + result);
                    _outputDirty = true;

                    // Reset silent mode for next input
                    _silentInput = false;

                    Repaint();
                    PositionCursorAtInput();
                    Console.Out.Flush();

                    return result;
                }
                else if (key.Key == ConsoleKey.PageUp)
                {
                    int regionHeight = _outputRegionEnd - _outputRegionStart + 1;
                    ScrollUp(regionHeight);
                }
                else if (key.Key == ConsoleKey.PageDown)
                {
                    int regionHeight = _outputRegionEnd - _outputRegionStart + 1;
                    ScrollDown(regionHeight);
                }
                else if (key.Key == ConsoleKey.UpArrow)
                {
                    ScrollUp(1);
                }
                else if (key.Key == ConsoleKey.DownArrow)
                {
                    ScrollDown(1);
                }
                else if (key.Key == ConsoleKey.Home)
                {
                    int regionHeight = _outputRegionEnd - _outputRegionStart + 1;
                    int maxScroll = Math.Max(0, _outputLines.Count - regionHeight);
                    ScrollUp(maxScroll - _scrollOffset);
                }
                else if (key.Key == ConsoleKey.End)
                {
                    ScrollToBottom();
                }
                else if (key.Key == ConsoleKey.Backspace)
                {
                    if (_inputBuffer.Length > 0)
                    {
                        _inputBuffer.Remove(_inputBuffer.Length - 1, 1);
                        _inputDirty = true;
                        Repaint();
                        PositionCursorAtInput();
                        Console.Out.Flush();
                    }
                }
                else if (key.Key == ConsoleKey.Escape)
                {
                    if (_inputBuffer.Length > 0)
                    {
                        _inputBuffer.Clear();
                        _inputDirty = true;
                        Repaint();
                        PositionCursorAtInput();
                        Console.Out.Flush();
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
                    _inputDirty = true;
                    Repaint();
                    PositionCursorAtInput();
                    Console.Out.Flush();
                }
                else if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
                {
                    _inputBuffer.Append(key.KeyChar);
                    _inputDirty = true;
                    Repaint();
                    PositionCursorAtInput();
                    Console.Out.Flush();
                }
            }
        }
    }

    /// <summary>
    /// Fallback input for non-ANSI terminals. Uses the old scroll-based approach.
    /// </summary>
    private string? ReadInputLineFallback(ConsoleKeyInfo firstKey)
    {
        // For non-ANSI, just do simple line reading
        if (firstKey.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            return "";
        }
        var sb = new StringBuilder();
        sb.Append(firstKey.KeyChar);
        Console.Write(firstKey.KeyChar);
        while (true)
        {
            try
            {
                if (!Console.KeyAvailable) { Thread.Sleep(10); continue; }
                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    return sb.ToString();
                }
                if (key.Key == ConsoleKey.Backspace && sb.Length > 0)
                {
                    sb.Remove(sb.Length - 1, 1);
                    Console.Write("\b \b");
                }
                else if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
                {
                    sb.Append(key.KeyChar);
                    Console.Write(key.KeyChar);
                }
            }
            catch (InvalidOperationException)
            {
                return Console.ReadLine();
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