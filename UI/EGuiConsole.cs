using ECAssistant.Core;
using System.Text;
using ECAssistant.Core.UI;

namespace ECAssistant.TUI.UI;

/// <summary>
/// Pure terminal engine for ECAssistant.
///
/// Owns: terminal (alt buffer, cursor, ANSI), input buffer, delta rendering,
/// raw key reading. Holds exactly one ActiveLayer (set by the controller).
///
/// Does NOT own: output buffers, scroll state, status bar content, application
/// logic, command parsing, layer dictionaries. Those live on layers / controller.
///
/// Layout:
/// ┌─────────────────────────────────────┐  row 0
/// │ Output region (from ActiveLayer)     │  → layer.GetVisibleRows()
/// ├─────────────────────────────────────┤  row (height-2)
/// │ Status bar (from ActiveLayer)        │  → layer.GetStatusBar()
/// ├─────────────────────────────────────┤  row (height-1)
/// │ > user input (from input buffer)     │  → EGuiConsole owns this
/// └─────────────────────────────────────┘
///
/// Thread safety: all console writes go through _writeLock.
/// </summary>
public sealed class EGuiConsole : EGuiBase, IGuiConsole
{
    private readonly ITerminalOutput _term;
    private bool _ansiSupported;
    private readonly object _writeLock = new();
    private readonly StringBuilder _inputBuffer = new();
    private const string PromptStr = "> ";
    private volatile bool _quitRequested;

    public EGuiConsole() : this(new ConsoleTerminalOutput()) { }

    public EGuiConsole(ITerminalOutput terminal)
    {
        _term = terminal ?? throw new ArgumentNullException(nameof(terminal));
    }
    
    // ── Controller callbacks ──
    private Action<string>? _onPrompt;
    private Action? _onEscape;
    
    // ── Silent mode: typed characters are buffered but not echoed ──
    private volatile bool _silentInput;
    private Func<bool>? _silentInputCheck;
    
    // ── Pending approval: set by inference thread, consumed by input loop ──
    private volatile bool _approvalPending;
    private string? _approvalMessage;
    private bool? _approvalResult;
    private readonly System.Threading.ManualResetEventSlim _approvalAnswered = new();
    private System.Threading.Thread? _inputLoopThread;
    
    // ── Startup buffering ──
    // Before InitConsole(), output is queued here instead of written to terminal.
    private readonly List<string> _startupBuffer = new();
    private bool _bufferingMode = true;
    
    // ── Active layer ──
    private BaseLayer? _activeLayer;
    
    // ── Screen dimensions ──
    public int ScreenWidth { get; private set; }
    public int ScreenHeight { get; private set; }
    private int _outputRegionStart;
    private int _outputRegionEnd;
    private int _statusRow;
    private int _inputRow;

    // ── Composer (chat-style input box) ──
    private const int MaxComposerContentLines = 5;
    private int _composerTopRow;                 // top border row of the input box
    private int _composerContentRows = 1;        // current content height (1..Max)
    private List<string> _composerWrapped = new() { "" };  // wrapped input lines (plain text)
    
    // ── Delta rendering cache ──
    private string?[] _screenRows = Array.Empty<string?>();
    private bool _outputDirty;
    private bool _statusDirty;
    private bool _inputDirty;
    private bool _fullRepaint;
    
    // ── Resize watcher ──
    private Timer? _resizeTimer;
    private int _lastWidth;
    private int _lastHeight;
    
    // ═══════════════════════════════════════════════════
    //  INIT / SHUTDOWN
    // ═══════════════════════════════════════════════════
    
    public void InitConsole()
    {
        _ansiSupported = DetectAnsiSupport();
        if (!_ansiSupported)
        {
            _bufferingMode = false;
            FlushStartupBuffer();
            return;
        }
        
        // Enter alternate screen buffer + hide cursor + enable mouse wheel tracking
        _term.Write("\x1b[?1049h\x1b[?25l\x1b[?1000h");
        _term.Flush();
        
        UpdateDimensions();
        
        // Flush all buffered startup output into the active layer, then paint once
        _bufferingMode = false;
        lock (_writeLock)
        {
            if (_activeLayer != null)
            {
                foreach (var line in _startupBuffer)
                    _activeLayer.AddOutputLine(line);
            }
            _startupBuffer.Clear();
            _fullRepaint = true;
            Repaint();
            PositionCursorAtInput();
            _term.Flush();
        }
        
        StartResizeWatcher();
    }
    
    /// <summary>Flush buffered startup output to the terminal (non-ANSI path).</summary>
    private void FlushStartupBuffer()
    {
        foreach (var line in _startupBuffer)
            _term.Write(line);
        _term.Flush();
        _startupBuffer.Clear();
    }

    public void ShutdownConsole()
    {
        _resizeTimer?.Dispose();
        _resizeTimer = null;
        
        if (!_ansiSupported) return;
        
        _term.Write("\x1b[?1000l\x1b[?25h\x1b[?1049l");
        _term.Flush();
    }
    
    // ═══════════════════════════════════════════════════
    //  CONTROLLER WIRING
    // ═══════════════════════════════════════════════════
    
    /// <summary>Set the callbacks for when user submits input (Enter) or presses ESC.</summary>
    public void SetCallbacks(Action<string> onPrompt, Action onEscape)
    {
        _onPrompt = onPrompt;
        _onEscape = onEscape;
    }
    
    /// <summary>Set the active layer. EGuiConsole will render this layer's content.</summary>
    public void SetActiveLayer(BaseLayer? layer)
    {
        lock (_writeLock)
        {
            _activeLayer = layer;
            if (layer != null)
            {
                layer.UpdateDimensions(ScreenWidth, ScreenHeight);
                layer._isDirty = true;
            }
            _fullRepaint = true;
            Repaint();
            PositionCursorAtInput();
            _term.Flush();
        }
    }
    
    /// <summary>Set the silent input check function (used during session running).</summary>
    public void SetSilentInputCheck(Func<bool>? check)
    {
        _silentInputCheck = check;
    }
    
    /// <summary>Set initial silent input state for the next input cycle.</summary>
    public void SetSilentInputInitial(bool silent)
    {
        _silentInput = silent;
        _inputDirty = true;
    }
    
    /// <summary>Signal the console to quit the input loop.</summary>
    public void Quit() => _quitRequested = true;
    
    public bool IsQuitRequested => _quitRequested;
    
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
            ScreenWidth = _term.WindowWidth;
            ScreenHeight = _term.WindowHeight;
        }
        catch
        {
            ScreenWidth = 80;
            ScreenHeight = 24;
        }
        
        if (ScreenWidth < 10) ScreenWidth = 10;
        if (ScreenHeight < 5) ScreenHeight = 5;
        
        _outputRegionStart = 0;
        _outputRegionEnd = ScreenHeight - 3;
        _statusRow = ScreenHeight - 2;
        _inputRow = ScreenHeight - 1;
        UpdateComposerLayout(_inputBuffer.ToString());

        _screenRows = new string?[ScreenHeight];
        _fullRepaint = true;

        // Layer must know the (composer-aware) output region end
        _activeLayer?.UpdateDimensions(ScreenWidth, ScreenHeight, _outputRegionEnd);
    }

    /// <summary>
    /// Wrap the input text to the composer inner width and derive box geometry.
    /// Pure layout computation — no terminal writes.
    /// </summary>
    private void UpdateComposerLayout(string text)
    {
        int innerWidth = Math.Max(1, ScreenWidth - 4); // borders + "› " prompt on first line
        _composerWrapped = WrapPlainText(text, innerWidth);

        int maxRows = Math.Max(1, Math.Min(MaxComposerContentLines, (ScreenHeight - 2) / 3));
        _composerContentRows = Math.Max(1, Math.Min(_composerWrapped.Count, maxRows));

        // Box: top border, content rows, bottom border (bottom border sits on _inputRow)
        _composerTopRow = _inputRow - _composerContentRows - 1;
        _statusRow = _composerTopRow - 1;
        _outputRegionEnd = _composerTopRow - 2;
    }

    /// <summary>Word-wrap plain text to the given width (long words hard-broken). Stateless utility.</summary>
    private static List<string> WrapPlainText(string text, int width)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text)) { lines.Add(""); return lines; }

        foreach (var para in text.Split('\n'))
        {
            var current = new StringBuilder();
            foreach (var word in para.Split(' '))
            {
                // Hard-break any word (or word remainder) longer than the width
                var piece = word;
                while (piece.Length > width)
                {
                    FlushCurrent(current, lines);
                    lines.Add(piece.Substring(0, width));
                    piece = piece.Substring(width);
                }
                if (piece.Length == 0) continue;

                if (current.Length == 0)
                    current.Append(piece);
                else if (current.Length + 1 + piece.Length <= width)
                    current.Append(' ').Append(piece);
                else
                {
                    lines.Add(current.ToString());
                    current.Clear().Append(piece);
                }
            }
            lines.Add(current.ToString());
        }
        return lines;

        static void FlushCurrent(StringBuilder current, List<string> lines)
        {
            if (current.Length > 0)
            {
                lines.Add(current.ToString());
                current.Clear();
            }
        }
    }
    
    private bool CheckResize()
    {
        try
        {
            int w = _term.WindowWidth;
            int h = _term.WindowHeight;
            if (w != ScreenWidth || h != ScreenHeight)
            {
                UpdateDimensions();
                return true;
            }
        }
        catch { }
        return false;
    }
    
    private void StartResizeWatcher()
    {
        _lastWidth = ScreenWidth;
        _lastHeight = ScreenHeight;
        _resizeTimer = new Timer(OnResizeCheck, null, 200, 200);
    }
    
    private void OnResizeCheck(object? state)
    {
        if (!_ansiSupported) return;
        
        try
        {
            int w = _term.WindowWidth;
            int h = _term.WindowHeight;
            if (w != _lastWidth || h != _lastHeight)
            {
                _lastWidth = w;
                _lastHeight = h;
                lock (_writeLock)
                {
                    UpdateDimensions();
                    // Notify the active layer about resize
                    _activeLayer?.UpdateDimensions(ScreenWidth, ScreenHeight);
                    _fullRepaint = true;
                    Repaint();
                    PositionCursorAtInput();
                    _term.Flush();
                }
            }
        }
        catch { }
    }
    
    // ═══════════════════════════════════════════════════
    //  RENDER ENGINE
    // ═══════════════════════════════════════════════════
    
    /// <summary>
    /// Request a repaint. Called by layers when their buffer changes.
    /// Only repaints if there is an active layer.
    /// </summary>
    public void RequestRepaint()
    {
        if (!_ansiSupported || _activeLayer == null) return;
        
        lock (_writeLock)
        {
            _outputDirty = true;
            Repaint();
            PositionCursorAtInput();
            _term.Flush();
        }
    }
    
    private void Repaint()
    {
        if (!_ansiSupported || _activeLayer == null) return;
        
        if (_fullRepaint)
        {
            _term.Write("\x1b[2J");
            Array.Fill(_screenRows, null);
            
            PaintOutputRegion();
            PaintStatusBar();
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
        
        _term.Flush();
    }
    
    /// <summary>
    /// Paint the output region using the active layer's visible rows.
    /// Delta rendering: only writes rows that differ from the cache.
    /// </summary>
    private void PaintOutputRegion()
    {
        if (_activeLayer == null) return;
        
        var visibleRows = _activeLayer.GetVisibleRows();
        int regionHeight = _outputRegionEnd - _outputRegionStart + 1;
        
        for (int i = 0; i < regionHeight; i++)
        {
            string? newRow = i < visibleRows.Count ? visibleRows[i] : null;
            string? oldRow = i < _screenRows.Length ? _screenRows[i] : null;
            
            if (newRow != oldRow)
            {
                int row = _outputRegionStart + i;
                _term.Write($"\x1b[{row + 1};1H\x1b[2K");
                if (newRow != null)
                    _term.Write(newRow);
                if (i < _screenRows.Length)
                    _screenRows[i] = newRow;
            }
        }
    }
    
    private void PaintStatusBar()
    {
        _term.Write($"\x1b[{_statusRow + 1};1H\x1b[2K");
        if (_activeLayer != null)
        {
            string bar = _activeLayer.GetStatusBar();
            if (!string.IsNullOrEmpty(bar))
            {
                int visibleLen = BaseLayer.StripAnsi(bar).Length;
                if (visibleLen > ScreenWidth)
                    bar = BaseLayer.TruncateAnsi(bar, ScreenWidth);
                _term.Write(bar);
            }
        }
    }
    
    private void PaintInputLine()
    {
        var text = _inputBuffer.ToString();
        UpdateComposerLayout(text);

        int innerWidth = Math.Max(1, ScreenWidth - 2);
        int maxRows = Math.Max(1, Math.Min(MaxComposerContentLines, (ScreenHeight - 2) / 3));
        var contentLines = _composerWrapped.Skip(Math.Max(0, _composerWrapped.Count - maxRows)).ToList();

        // When the box grows, output/status shift up — repaint them too
        _outputDirty = true;
        _statusDirty = true;
        PaintOutputRegion();
        _outputDirty = false;
        PaintStatusBar();
        _statusDirty = false;

        const string dim = "\x1b[2m";
        const string reset = "\x1b[0m";

        // Top border
        _term.Write($"\x1b[{_composerTopRow + 1};1H\x1b[2K");
        _term.Write(dim + "╭" + new string('─', ScreenWidth - 2) + "╮" + reset);

        // Content rows
        for (int i = 0; i < _composerContentRows; i++)
        {
            _term.Write($"\x1b[{_composerTopRow + 2 + i};1H\x1b[2K");
            string line = i < contentLines.Count ? contentLines[i] : "";
            if (line.Length > innerWidth) line = line.Substring(0, innerWidth);
            string prefix = i == 0 ? $"{dim}› {reset}" : "  ";
            int pad = Math.Max(0, ScreenWidth - 2 - BaseLayer.StripAnsi(prefix).Length - BaseLayer.StripAnsi(line).Length);
            _term.Write(dim + "│" + reset + prefix + line + new string(' ', pad) + dim + "│" + reset);
        }

        // Bottom border
        _term.Write($"\x1b[{_inputRow + 1};1H\x1b[2K");
        _term.Write(dim + "╰" + new string('─', ScreenWidth - 2) + "╯" + reset);

        PositionCursorAtInput();
    }
    
    private void PositionCursorAtInput()
    {
        // Cursor always sits after the last character → on the last visible wrapped line
        int visibleLineIdx = _composerContentRows - 1;
        int firstVisible = Math.Max(0, _composerWrapped.Count - _composerContentRows);
        string lastLine = _composerWrapped[Math.Min(firstVisible + visibleLineIdx, _composerWrapped.Count - 1)];
        int col = 1 /*left border*/ + 2 /*"› " or indent*/ + lastLine.Length + 1; // 1-based
        if (col > ScreenWidth) col = ScreenWidth;
        _term.Write($"\x1b[{_composerTopRow + 2 + visibleLineIdx};{col}H\x1b[?25h");
    }
    
    // ═══════════════════════════════════════════════════
    //  OUTPUT (for startup buffering + EGuiBase interface)
    // ═══════════════════════════════════════════════════
    
    private void WriteOutput(string text)
    {
        if (!_ansiSupported)
        {
            lock (_writeLock) { _term.Write(text); _term.Flush(); }
            return;
        }
        
        // Buffering mode: queue output until InitConsole() enters alternate buffer
        if (_bufferingMode)
        {
            lock (_writeLock) { _startupBuffer.Add(text); }
            return;
        }
        
        // Route output to the active layer's buffer
        lock (_writeLock)
        {
            _activeLayer?.AddOutputLine(text);
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
    //  CLEAR CANVAS
    // ═══════════════════════════════════════════════════
    
    public override void ClearCanvas()
    {
        if (!_ansiSupported)
        {
            lock (_writeLock)
            {
                try { Console.Clear(); } catch { }
                _term.Flush();
            }
            return;
        }
        
        lock (_writeLock)
        {
            _activeLayer?.Clear();
            _fullRepaint = true;
            Repaint();
            PositionCursorAtInput();
            _term.Flush();
        }
    }
    
    public override void LogInternal(string text) => WriteOutput(text + "\n");
    
    // ═══════════════════════════════════════════════════
    //  INPUT LOOP
    // ═══════════════════════════════════════════════════
    
    public override string? PromptColored(string labelAndText)
    {
        // If this is called from the inference thread (not the main input loop),
        // use the pending approval mechanism to avoid console input conflict.
        if (!object.ReferenceEquals(System.Threading.Thread.CurrentThread, _inputLoopThread))
        {
            // Queue approval for the main input loop
            _approvalAnswered.Reset();
            _approvalMessage = labelAndText;
            _approvalPending = true;
            _approvalResult = null;
            
            // Turn off silent input so the prompt is visible
            _silentInput = false;
            
            // Wait for the main input loop to deliver the answer
            _approvalAnswered.Wait();
            
            return _approvalResult == true ? "y" : "n";
        }
        return ReadInputLine();
    }
    public override string? PromptRaw(string label) => ReadInputLine();
    
    /// <summary>
    /// Main input loop. Reads keys, handles input buffer locally, forwards
    /// Enter → controller.OnPrompt(), ESC → controller.OnEscapePressed(),
    /// scroll keys / mouse wheel → activeLayer.HandleScroll().
    /// </summary>
    private string? ReadInputLine()
    {
        _inputLoopThread = System.Threading.Thread.CurrentThread;
        _inputBuffer.Clear();
        
        if (_ansiSupported)
        {
            lock (_writeLock)
            {
                if (CheckResize()) _fullRepaint = true;
                _inputDirty = true;
                Repaint();
                PositionCursorAtInput();
                _term.Flush();
            }
        }
        
        while (!_quitRequested)
        {
            ConsoleKeyInfo key;
            try
            {
                // Check for pending approval from inference thread
                if (_approvalPending)
                {
                    lock (_writeLock)
                    {
                        var msg = _approvalMessage ?? "Approve?";
                        _activeLayer?.AddOutputLine($"\x1b[33m\x1b[1m⚠ APPROVAL REQUIRED\x1b[0m");
                        _activeLayer?.AddOutputLine($"\x1b[33m{msg}\x1b[0m");
                        _outputDirty = true;
                        Repaint();
                        // Show prompt at input line
                        _term.Write($"\x1b[{_composerTopRow + 2};1H\x1b[2K\x1b[33mApprove? (y/n): \x1b[0m");
                        _term.Flush();
                    }
                    
                    // Read y/n directly (we're on the input loop thread)
                    _inputBuffer.Clear();
                    while (_approvalPending)
                    {
                        if (!Console.KeyAvailable)
                        {
                            System.Threading.Thread.Sleep(10);
                            continue;
                        }
                        var approvalKey = Console.ReadKey(true);
                        if (approvalKey.Key == ConsoleKey.Y)
                        {
                            _approvalResult = true;
                            _approvalPending = false;
                        }
                        else if (approvalKey.Key == ConsoleKey.N || approvalKey.Key == ConsoleKey.Escape)
                        {
                            _approvalResult = false;
                            _approvalPending = false;
                        }
                        else if (approvalKey.Key == ConsoleKey.Enter)
                        {
                            _approvalResult = _inputBuffer.ToString().Trim().ToLowerInvariant() == "y" || _inputBuffer.ToString().Trim().ToLowerInvariant() == "yes";
                            _approvalPending = false;
                            _inputBuffer.Clear();
                        }
                        else if (approvalKey.KeyChar == 'y' || approvalKey.KeyChar == 'Y' || approvalKey.KeyChar == 'n' || approvalKey.KeyChar == 'N')
                        {
                            _inputBuffer.Append(approvalKey.KeyChar);
                            lock (_writeLock)
                            {
                                _term.Write($"\x1b[{_composerTopRow + 2};1H\x1b[2K\x1b[33mApprove? (y/n): {approvalKey.KeyChar}\x1b[0m");
                                _term.Flush();
                            }
                        }
                    }
                    
                    lock (_writeLock)
                    {
                        var approved = _approvalResult == true;
                        _activeLayer?.AddOutputLine(approved
                            ? "\x1b[32m✓ Approved\x1b[0m"
                            : "\x1b[31m✗ Denied\x1b[0m");
                        _outputDirty = true;
                        _inputDirty = true;
                        Repaint();
                        PositionCursorAtInput();
                        _term.Flush();
                    }
                    _approvalAnswered.Set();
                    continue;
                }
                
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
                            _term.Flush();
                        }
                    }
                }
                
                if (!Console.KeyAvailable)
                {
                    Thread.Sleep(10);
                    // Re-check for pending approval while waiting for key input
                    // This prevents deadlock when inference thread requests approval
                    // while the main loop is sleeping here waiting for a key.
                    if (_approvalPending)
                        continue;
                    continue;
                }
                key = Console.ReadKey(true); // intercept: don't auto-echo
                
                // ── Mouse wheel events (X10 mode: \x1b[M + 3 bytes) ──
                if (key.KeyChar == '\x1b' && Console.KeyAvailable)
                {
                    var next = Console.ReadKey(true);
                    if (next.KeyChar == '[' && Console.KeyAvailable)
                    {
                        var m = Console.ReadKey(true);
                        if (m.KeyChar == 'M' && Console.KeyAvailable)
                        {
                            var b = Console.ReadKey(true);
                            var cx = Console.ReadKey(true);
                            var cy = Console.ReadKey(true);
                            int button = b.KeyChar - 32;
                            
                            if (button == 64) // scroll up
                            {
                                lock (_writeLock)
                                {
                                    _activeLayer?.HandleScroll(1, 3);
                                    _outputDirty = true;
                                    Repaint();
                                    PositionCursorAtInput();
                                    _term.Flush();
                                }
                                continue;
                            }
                            else if (button == 65) // scroll down
                            {
                                lock (_writeLock)
                                {
                                    _activeLayer?.HandleScroll(-1, 3);
                                    _outputDirty = true;
                                    Repaint();
                                    PositionCursorAtInput();
                                    _term.Flush();
                                }
                                continue;
                            }
                            continue;
                        }
                        continue;
                    }
                    // Lone ESC
                    key = new ConsoleKeyInfo('\x1b', ConsoleKey.Escape, false, false, false);
                }
            }
            catch (InvalidOperationException)
            {
                return Console.ReadLine();
            }
            
            if (!_ansiSupported)
            {
                return ReadInputLineFallback(key);
            }
            
            lock (_writeLock)
            {
                if (CheckResize()) _fullRepaint = true;
                
                if (key.Key == ConsoleKey.Enter)
                {
                    var result = _inputBuffer.ToString();
                    _inputBuffer.Clear();
                    _silentInput = false;
                    
                    // Add submitted input to active layer's buffer (skip commands)
                    if (!result.StartsWith("/") && _activeLayer != null)
                    {
                        string prompt = _activeLayer.GetInputPrompt();
                        _activeLayer.AddOutputLine(prompt + result);
                    }
                    
                    _inputDirty = true;
                    Repaint();
                    PositionCursorAtInput();
                    _term.Flush();
                    
                    // Forward to controller
                    _onPrompt?.Invoke(result);
                    return result;
                }
                else if (key.Key == ConsoleKey.Escape)
                {
                    if (_inputBuffer.Length > 0)
                    {
                        // ESC with text in buffer: clear the buffer
                        _inputBuffer.Clear();
                        _inputDirty = true;
                        Repaint();
                        PositionCursorAtInput();
                        _term.Flush();
                    }
                    else
                    {
                        // ESC with empty buffer: forward to controller
                        _onEscape?.Invoke();
                    }
                    continue;
                }
                else if (key.Key == ConsoleKey.PageUp)
                {
                    int regionHeight = _outputRegionEnd - _outputRegionStart + 1;
                    _activeLayer?.ScrollUp(regionHeight);
                    _outputDirty = true;
                    Repaint();
                    PositionCursorAtInput();
                    _term.Flush();
                }
                else if (key.Key == ConsoleKey.PageDown)
                {
                    int regionHeight = _outputRegionEnd - _outputRegionStart + 1;
                    _activeLayer?.ScrollDown(regionHeight);
                    _outputDirty = true;
                    Repaint();
                    PositionCursorAtInput();
                    _term.Flush();
                }
                else if (key.Key == ConsoleKey.UpArrow)
                {
                    _activeLayer?.ScrollUp(1);
                    _outputDirty = true;
                    Repaint();
                    PositionCursorAtInput();
                    _term.Flush();
                }
                else if (key.Key == ConsoleKey.DownArrow)
                {
                    _activeLayer?.ScrollDown(1);
                    _outputDirty = true;
                    Repaint();
                    PositionCursorAtInput();
                    _term.Flush();
                }
                else if (key.Key == ConsoleKey.Home)
                {
                    int regionHeight = _outputRegionEnd - _outputRegionStart + 1;
                    int maxScroll = Math.Max(0, (_activeLayer?._outputLines.Count ?? 0) - regionHeight);
                    _activeLayer?.ScrollUp(maxScroll - (_activeLayer?._scrollOffset ?? 0));
                    _outputDirty = true;
                    Repaint();
                    PositionCursorAtInput();
                    _term.Flush();
                }
                else if (key.Key == ConsoleKey.End)
                {
                    _activeLayer?.ScrollToBottom();
                    _outputDirty = true;
                    Repaint();
                    PositionCursorAtInput();
                    _term.Flush();
                }
                else if (key.Key == ConsoleKey.Backspace)
                {
                    if (_inputBuffer.Length > 0)
                    {
                        _inputBuffer.Remove(_inputBuffer.Length - 1, 1);
                        _inputDirty = true;
                        Repaint();
                        PositionCursorAtInput();
                        _term.Flush();
                    }
                }
                else if (key.Key == ConsoleKey.Tab)
                {
                    _inputBuffer.Append("    ");
                    _inputDirty = true;
                    Repaint();
                    PositionCursorAtInput();
                    _term.Flush();
                }
                else if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
                {
                    _inputBuffer.Append(key.KeyChar);
                    _inputDirty = true;
                    Repaint();
                    PositionCursorAtInput();
                    _term.Flush();
                }
            }
        }
        
        return null;
    }
    
    /// <summary>Fallback input for non-ANSI terminals.</summary>
    private string? ReadInputLineFallback(ConsoleKeyInfo firstKey)
    {
        if (firstKey.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            return "";
        }
        var sb = new StringBuilder();
        sb.Append(firstKey.KeyChar);
        _term.Write(firstKey.KeyChar.ToString());
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
                    _term.Write("\b \b");
                }
                else if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
                {
                    sb.Append(key.KeyChar);
                    _term.Write(key.KeyChar.ToString());
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