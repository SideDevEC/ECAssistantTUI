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
    private string? _approvalAnswer;
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
        
        // Enter alternate screen buffer + hide cursor. Mouse tracking is deliberately NOT
        // enabled — the terminal handles the mouse natively (native scrollback), so no
        // mouse escape sequences ever arrive in the input stream.
        _term.Write("\x1b[?1049h\x1b[?25l");
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
                layer.MarkDirty();
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
        
        _screenRows = new string?[ScreenHeight];
        _fullRepaint = true;
        
        _activeLayer?.UpdateDimensions(ScreenWidth, ScreenHeight);
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
            FlushRepaint(outputDirty: true);
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
    /// Repaint + cursor reposition + flush under the write lock.
    /// Single shared tail for the key-handler branches — must be called while
    /// holding _writeLock.
    /// </summary>
    private void FlushRepaint(bool outputDirty = false, bool inputDirty = false)
    {
        if (outputDirty) _outputDirty = true;
        if (inputDirty) _inputDirty = true;
        Repaint();
        PositionCursorAtInput();
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
        _term.Write($"\x1b[{_inputRow + 1};1H\x1b[2K");
        
        // Use the active layer's prompt prefix (or default "> ")
        string prompt = _activeLayer?.GetInputPrompt() ?? PromptStr;
        _term.Write(prompt);
        _term.Write(_inputBuffer.ToString());
        
        // Position cursor right after the last typed character
        PositionCursorAtInput();
    }
    
    private void PositionCursorAtInput()
    {
        string prompt = _activeLayer?.GetInputPrompt() ?? PromptStr;
        int col = prompt.Length + _inputBuffer.Length;
        // Position cursor at end of input, show it
        _term.Write($"\x1b[{_inputRow + 1};{col + 1}H\x1b[?25h");
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
        // If this is called from another thread while the input loop is running,
        // use the pending prompt mechanism to avoid console input conflict.
        if (_inputLoopThread != null && !object.ReferenceEquals(System.Threading.Thread.CurrentThread, _inputLoopThread))
        {
            return PromptViaInputLoop(labelAndText);
        }
        return ReadInputLine();
    }

    public override string? PromptRaw(string label)
    {
        // Cross-thread prompt (e.g. installer continuation after await) → route through the input loop
        if (_inputLoopThread != null && !object.ReferenceEquals(System.Threading.Thread.CurrentThread, _inputLoopThread))
        {
            return PromptViaInputLoop(label);
        }
        return ReadInputLine();
    }

    /// <summary>
    /// Queue a prompt for the main input loop and block until the user answers.
    /// Generic: any text input, not just y/n. ESC returns null.
    /// </summary>
    private string? PromptViaInputLoop(string label)
    {
        _approvalAnswered.Reset();
        _approvalMessage = label;
        _approvalPending = true;
        _approvalResult = null;
        _approvalAnswer = null;

        // Turn off silent input so the prompt is visible
        _silentInput = false;

        // Wait for the main input loop to deliver the answer
        _approvalAnswered.Wait();

        return _approvalAnswer;
    }
    
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
                FlushRepaint(inputDirty: true);
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
                        var msg = _approvalMessage ?? "? ";
                        _outputDirty = true;
                        Repaint();
                        // Show prompt at input line — the message IS the prompt label
                        _term.Write($"\x1b[{_inputRow + 1};1H\x1b[2K\x1b[33m{msg}\x1b[0m");
                        _term.Flush();
                    }

                    // Generic line read (we're on the input loop thread):
                    // printable chars append, Enter submits, ESC cancels (null).
                    _inputBuffer.Clear();
                    while (_approvalPending)
                    {
                        if (!Console.KeyAvailable)
                        {
                            System.Threading.Thread.Sleep(10);
                            continue;
                        }
                        var keyInfo = Console.ReadKey(true);
                        if (keyInfo.Key == ConsoleKey.Enter)
                        {
                            _approvalAnswer = _inputBuffer.ToString().Trim();
                            _approvalPending = false;
                            _inputBuffer.Clear();
                        }
                        else if (keyInfo.Key == ConsoleKey.Escape)
                        {
                            _approvalAnswer = null;
                            _approvalPending = false;
                            _inputBuffer.Clear();
                        }
                        else if (keyInfo.Key == ConsoleKey.Backspace)
                        {
                            if (_inputBuffer.Length > 0)
                            {
                                _inputBuffer.Remove(_inputBuffer.Length - 1, 1);
                                lock (_writeLock)
                                {
                                    _term.Write($"\x1b[{_inputRow + 1};1H\x1b[2K\x1b[33m{_approvalMessage}\x1b[0m{_inputBuffer}\x1b[0m");
                                    _term.Flush();
                                }
                            }
                        }
                        else if (!char.IsControl(keyInfo.KeyChar))
                        {
                            _inputBuffer.Append(keyInfo.KeyChar);
                            lock (_writeLock)
                            {
                                _term.Write($"\x1b[{_inputRow + 1};1H\x1b[2K\x1b[33m{_approvalMessage}\x1b[0m{_inputBuffer}\x1b[0m");
                                _term.Flush();
                            }
                        }
                    }

                    lock (_writeLock)
                    {
                        FlushRepaint(outputDirty: true, inputDirty: true);
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
                            FlushRepaint(inputDirty: true);
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
                
                // ── Escape sequences: mouse wheel (X10 + SGR), arrow keys, lone ESC ──
                if (key.KeyChar == '\x1b')
                {
                    var mapped = TryMapEscapeSequence();
                    if (mapped == null)
                        continue; // sequence fully consumed (mouse event / unknown CSI)
                    key = mapped.Value;
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
                    
                    FlushRepaint(inputDirty: true);
                    
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
                        FlushRepaint(inputDirty: true);
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
                    FlushRepaint(outputDirty: true);
                }
                else if (key.Key == ConsoleKey.PageDown)
                {
                    int regionHeight = _outputRegionEnd - _outputRegionStart + 1;
                    _activeLayer?.ScrollDown(regionHeight);
                    FlushRepaint(outputDirty: true);
                }
                else if (key.Key == ConsoleKey.UpArrow)
                {
                    _activeLayer?.ScrollUp(1);
                    FlushRepaint(outputDirty: true);
                }
                else if (key.Key == ConsoleKey.DownArrow)
                {
                    _activeLayer?.ScrollDown(1);
                    FlushRepaint(outputDirty: true);
                }
                else if (key.Key == ConsoleKey.Home)
                {
                    int regionHeight = _outputRegionEnd - _outputRegionStart + 1;
                    int maxScroll = Math.Max(0, (_activeLayer?.OutputLines.Count ?? 0) - regionHeight);
                    _activeLayer?.ScrollUp(maxScroll - (_activeLayer?.ScrollOffset ?? 0));
                    FlushRepaint(outputDirty: true);
                }
                else if (key.Key == ConsoleKey.End)
                {
                    _activeLayer?.ScrollToBottom();
                    FlushRepaint(outputDirty: true);
                }
                else if (key.Key == ConsoleKey.Backspace)
                {
                    if (_inputBuffer.Length > 0)
                    {
                        _inputBuffer.Remove(_inputBuffer.Length - 1, 1);
                        FlushRepaint(inputDirty: true);
                    }
                }
                else if (key.Key == ConsoleKey.Tab)
                {
                    _inputBuffer.Append("    ");
                    FlushRepaint(inputDirty: true);
                }
                else if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
                {
                    _inputBuffer.Append(key.KeyChar);
                    FlushRepaint(inputDirty: true);
                }
            }
        }
        
        return null;
    }
    
    /// <summary>
    /// Reads a full escape sequence after the ESC byte and maps it to a ConsoleKeyInfo.
    /// Returns null when the sequence was fully consumed (mouse event, unknown CSI) —
    /// the caller must not treat any of its bytes as user input.
    /// Waits briefly for sequence bytes so fast event bursts are never split mid-sequence.
    /// </summary>
    private ConsoleKeyInfo? TryMapEscapeSequence()
    {
        var next = WaitForNextKey(50);
        if (next == null)
            return EscapeKeyInfo(); // lone ESC
        if (next.Value.KeyChar != '[')
        {
            if (next.Value.KeyChar != 'O')
                DebugLogUnmapped($"After ESC: 0x{((int)next.Value.KeyChar):X2} '{next.Value.KeyChar}'");
            if (next.Value.KeyChar == 'O')
            {
                // SS3 sequences (ESC O P/S etc.): consume the final byte, treat as Escape for now.
                var ss3Tail = WaitForNextKey(50);
                if (ss3Tail != null)
                    DebugLogUnmapped($"SS3 {ss3Tail.Value.KeyChar}");
                return null;
            }
            return EscapeKeyInfo();
        }

        var csi = WaitForNextKey(50);
        if (csi == null)
            return EscapeKeyInfo();

        switch (csi.Value.KeyChar)
        {
            case 'O':
                // ESC O A/B — arrows on terminals using SS3; map like CSI arrows.
                var ss3 = WaitForNextKey(50);
                if (ss3 == null) return EscapeKeyInfo();
                if (ss3.Value.KeyChar == 'A') return new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, false, false, false);
                if (ss3.Value.KeyChar == 'B') return new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, false);
                return null;
            case 'M':
                ReadX10Mouse();
                return null;
            case '<':
                ReadSgrMouse();
                return null;
            case 'A':
                return new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, false, false, false);
            case 'B':
                return new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, false);
            default:
                DrainCsi();
                return null;
        }
    }

    /// <summary>X10 mouse event: ESC [ M + 3 raw bytes (button, x, y). Wheel = buttons 64/65.</summary>
    private void ReadX10Mouse()
    {
        var b = WaitForNextKey(50);
        var cx = WaitForNextKey(50);
        var cy = WaitForNextKey(50);
        if (b == null || cx == null || cy == null)
        {
            DebugLogUnmapped("X10 mouse truncated");
            return;
        }

        int button = b.Value.KeyChar - 32;
        if (button == 64) ScrollByWheel(1);
        else if (button == 65) ScrollByWheel(-1);
    }

    /// <summary>SGR mouse event: ESC [ &lt; button ; x ; y (M|m). Wheel = buttons 64/65.</summary>
    private void ReadSgrMouse()
    {
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var ch = WaitForNextKey(50);
            if (ch == null) return; // truncated — dropped
            if (ch.Value.KeyChar == 'M' || ch.Value.KeyChar == 'm') break;
            sb.Append(ch.Value.KeyChar);
        }

        var parts = sb.ToString().Split(';');
        if (parts.Length < 1 || !int.TryParse(parts[0], out var button)) return;
        if (button == 64) ScrollByWheel(1);
        else if (button == 65) ScrollByWheel(-1);
    }

    /// <summary>Drains the remainder of an unrecognized CSI sequence up to its final byte (@-~).</summary>
    private void DrainCsi()
    {
        var drained = new System.Text.StringBuilder();
        while (true)
        {
            var ch = WaitForNextKey(50);
            if (ch == null) break;
            var c = ch.Value.KeyChar;
            drained.Append(c);
            if (c >= '@' && c <= '~') break; // CSI final byte
        }
        DebugLogUnmapped($"CSI drained: {drained}");
    }

    /// <summary>Appends unmapped input bytes to a debug file — helps diagnose terminal-specific escape sequences.</summary>
    private static void DebugLogUnmapped(string message)
    {
        try
        {
            File.AppendAllText("/tmp/ecassistant-input-debug.log",
                $"{DateTime.Now:HH:mm:ss.fff} {message}\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void ScrollByWheel(int direction)
    {
        lock (_writeLock)
        {
            _activeLayer?.HandleScroll(direction, 3);
            FlushRepaint(outputDirty: true);
        }
    }

    /// <summary>Waits up to timeoutMs for the next key; null when none arrives (sequence ended early).</summary>
    private ConsoleKeyInfo? WaitForNextKey(int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!Console.KeyAvailable)
        {
            if (DateTime.UtcNow >= deadline) return null;
            Thread.Sleep(5);
        }
        return Console.ReadKey(true);
    }

    // Stateless utility — no mutable state.
    private static ConsoleKeyInfo EscapeKeyInfo() =>
        new('\x1b', ConsoleKey.Escape, false, false, false);

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
        bool escapePressed = false;
        // Console input is a shared resource with the input loop — read under
        // _writeLock so we never race ReadKey/KeyAvailable against rendering
        // or the input loop.
        lock (_writeLock)
        {
            try
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true);
                    escapePressed = key.Key == ConsoleKey.Escape;
                }
            }
            catch (InvalidOperationException) { }
        }

        if (escapePressed)
            _onEscape?.Invoke();

        return escapePressed;
    }
}