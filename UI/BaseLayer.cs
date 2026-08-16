using System.Text;

namespace ECAssistant.TUI.UI;

/// <summary>
/// Abstract base class for all layers in the EGuiConsole system.
/// 
/// Each layer represents one full screen of content. Only one layer is
/// active at a time (held by EGuiConsole). Layers own their output buffers,
/// scroll position, status bar, and live stream line. They fill continuously
/// even when not the active layer — so when the user switches back, all
/// output is already there.
///
/// Layers do NOT know about the controller or other layers. They only know
/// about the EGuiConsole they are bound to (for repaint requests).
/// </summary>
public abstract class BaseLayer
{
    // ── Output buffer ──
    // Lines stored with ANSI color codes intact.
    internal readonly List<string> _outputLines = new();
    
    // Scroll position: 0 = bottom (newest), N = scrolled up N lines from bottom
    internal int _scrollOffset;
    
    // Status bar content
    protected string _statusBar = "";
    
    // ── Live stream line ──
    // When streaming, this is the current accumulated text. It occupies the
    // last line in _outputLines at index _liveStreamLineIndex.
    protected string? _liveStreamText;
    protected int _liveStreamLineIndex = -1;
    
    // Dirty flag — buffer changed since last render
    internal bool _isDirty = true;
    
    // Console reference (set by Bind/Unbind, used for repaint requests)
    protected IGuiConsole? _console;
    
    // ── Screen dimensions (set by EGuiConsole when bound) ──
    protected int _screenWidth = 80;
    protected int _screenHeight = 24;
    protected int _outputRegionStart = 0;
    protected int _outputRegionEnd = 21;  // screenHeight - 3
    
    // ═══════════════════════════════════════════════════
    //  LIFECYCLE
    // ═══════════════════════════════════════════════════
    
    /// <summary>Bind this layer to an EGuiConsole. Layer can now request repaints.</summary>
    public void BindToConsole(IGuiConsole console)
    {
        _console = console;
        UpdateDimensions(console.ScreenWidth, console.ScreenHeight);
        _isDirty = true;
    }
    
    /// <summary>Unbind from the console. Layer stops requesting repaints but keeps its buffer.</summary>
    public void UnbindFromConsole()
    {
        _console = null;
    }
    
    /// <summary>Update screen dimensions (called by EGuiConsole on resize or bind).</summary>
    public void UpdateDimensions(int width, int height)
    {
        _screenWidth = width;
        _screenHeight = height;
        _outputRegionStart = 0;
        _outputRegionEnd = height - 3;
        _isDirty = true;
    }
    
    // ═══════════════════════════════════════════════════
    //  RENDERING QUERIES (called by EGuiConsole)
    // ═══════════════════════════════════════════════════
    
    /// <summary>Layer name for debugging.</summary>
    public abstract string Name { get; }
    
    /// <summary>
    /// Return the visible rows for the output region, after scroll + wrap.
    /// Each string is one terminal row with ANSI codes intact.
    /// </summary>
    public List<string> GetVisibleRows()
    {
        int regionHeight = _outputRegionEnd - _outputRegionStart + 1;
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
                var wrapped = WrapLine(line, _screenWidth);
                for (int w = wrapped.Count - 1; w >= 0; w--)
                {
                    if (linesUsed >= regionHeight) break;
                    visibleRows.Insert(0, wrapped[w]);
                    linesUsed++;
                }
            }
        }
        
        return visibleRows;
    }
    
    /// <summary>Return the status bar content (ANSI codes intact). Empty string = no status bar.</summary>
    public string GetStatusBar() => _statusBar;
    
    /// <summary>Return the input prompt prefix shown before user input. Default is "> " — override to change.</summary>
    public virtual string GetInputPrompt() => "> ";
    
    // ═══════════════════════════════════════════════════
    //  OUTPUT BUFFER MANAGEMENT
    // ═══════════════════════════════════════════════════
    
    /// <summary>
    /// Add a line to the output buffer. Splits on \n, strips trailing \r.
    /// Does NOT create an empty trailing entry for text ending with \n.
    /// Resets scroll to bottom (newest).
    /// </summary>
    public void AddOutputLine(string text)
    {
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string clean = lines[i].EndsWith('\r') ? lines[i][..^1] : lines[i];
            if (i == lines.Length - 1 && string.IsNullOrEmpty(clean) && lines.Length > 1)
                continue;
            _outputLines.Add(clean);
        }
        _scrollOffset = 0; // snap to bottom
        _isDirty = true;
        RequestRepaint();
    }
    
    /// <summary>Clear all output, reset scroll.</summary>
    public virtual void Clear()
    {
        _outputLines.Clear();
        _scrollOffset = 0;
        _liveStreamLineIndex = -1;
        _liveStreamText = null;
        _statusBar = "";
        _isDirty = true;
        RequestRepaint();
    }
    
    // ═══════════════════════════════════════════════════
    //  LIVE STREAM LINE
    // ═══════════════════════════════════════════════════
    
    /// <summary>
    /// Update or create a live stream line at the bottom of the output.
    /// Called by ConsoleUiRenderer's stream poll timer.
    /// </summary>
    public void UpdateLiveStreamLine(string text)
    {
        if (_liveStreamLineIndex < 0 || _liveStreamLineIndex >= _outputLines.Count)
        {
            _outputLines.Add(text);
            _liveStreamLineIndex = _outputLines.Count - 1;
        }
        else
        {
            _outputLines[_liveStreamLineIndex] = text;
        }
        _liveStreamText = text;
        _scrollOffset = 0;
        _isDirty = true;
        RequestRepaint();
    }
    
    /// <summary>
    /// Clear the live stream line. The line is removed from output (it will be
    /// replaced by the flush). Called when streaming stops.
    /// </summary>
    public void ClearLiveStreamLine()
    {
        if (_liveStreamLineIndex >= 0 && _liveStreamLineIndex < _outputLines.Count)
        {
            _outputLines.RemoveAt(_liveStreamLineIndex);
            _liveStreamLineIndex = -1;
        }
        _liveStreamText = null;
        _isDirty = true;
        RequestRepaint();
    }
    
    // ═══════════════════════════════════════════════════
    //  SCROLL NAVIGATION (called by EGuiConsole)
    // ═══════════════════════════════════════════════════
    
    /// <summary>Scroll up (away from newest) by the given number of lines.</summary>
    public void HandleScroll(int direction, int lines)
    {
        if (direction > 0)
            ScrollUp(lines);
        else
            ScrollDown(lines);
    }
    
    /// <summary>Scroll up (away from newest). Positive = scroll back in history.</summary>
    public void ScrollUp(int lines)
    {
        int regionHeight = _outputRegionEnd - _outputRegionStart + 1;
        int maxScroll = Math.Max(0, _outputLines.Count - regionHeight);
        int newOffset = Math.Min(maxScroll, _scrollOffset + lines);
        if (newOffset == _scrollOffset) return;
        _scrollOffset = newOffset;
        UpdateScrollStatus();
        _isDirty = true;
        RequestRepaint();
    }
    
    /// <summary>Scroll down (toward newest). Positive = scroll toward recent.</summary>
    public void ScrollDown(int lines)
    {
        int newOffset = Math.Max(0, _scrollOffset - lines);
        if (newOffset == _scrollOffset) return;
        _scrollOffset = newOffset;
        UpdateScrollStatus();
        _isDirty = true;
        RequestRepaint();
    }
    
    /// <summary>Reset scroll to bottom (newest content).</summary>
    public void ScrollToBottom()
    {
        if (_scrollOffset == 0) return;
        _scrollOffset = 0;
        UpdateScrollStatus();
        _isDirty = true;
        RequestRepaint();
    }
    
    /// <summary>Update the status bar when scroll position changes.</summary>
    protected void UpdateScrollStatus()
    {
        if (_scrollOffset > 0)
        {
            int totalLines = _outputLines.Count;
            _statusBar = $"\x1b[2m\x1b[36m↑ Scrolled up {_scrollOffset} line(s) — PageDown to return ({totalLines} total lines)\x1b[0m";
        }
        else if (!string.IsNullOrEmpty(_statusBar) && _statusBar.StartsWith("\x1b[2m\x1b[36m↑"))
        {
            _statusBar = "";
        }
    }
    
    // ═══════════════════════════════════════════════════
    //  REPAINT
    // ═══════════════════════════════════════════════════
    
    /// <summary>
    /// Request a repaint from the bound EGuiConsole.
    /// Only fires if the layer is bound to a console (i.e., is active).
    /// No-op if not bound — background layers don't trigger repaints.
    /// </summary>
    protected void RequestRepaint()
    {
        _console?.RequestRepaint();
    }
    
    // ═══════════════════════════════════════════════════
    //  INPUT PROCESSING
    // ═══════════════════════════════════════════════════
    
    /// <summary>
    /// Process user input. Called by the controller after it has handled
    /// layer-switching commands (/help, /home, /quit, /session, /session-new).
    /// 
    /// Base implementation handles common commands that work on any layer:
    /// /clear — clears the output buffer.
    /// Returns true if handled, false if the layer didn't recognize it.
    /// Override in subclasses for layer-specific commands and prompt routing.
    /// </summary>
    public virtual bool ProcessInput(string input)
    {
        if (string.IsNullOrEmpty(input)) return true;
        
        bool isCommand = input.StartsWith("/");
        if (!isCommand) return false;
        
        var cmd = input[1..].ToLower().Trim();
        
        switch (cmd)
        {
            case "clear":
                Clear();
                return true;
            default:
                return false;
        }
    }
    
    // ═══════════════════════════════════════════════════
    //  ANSI HELPERS (moved from EGuiConsole)
    // ═══════════════════════════════════════════════════
    
    /// <summary>Remove ANSI escape sequences from a string to get visible length.</summary>
    public static string StripAnsi(string text)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '\x1b')
            {
                i++;
                if (i < text.Length && text[i] == '[')
                {
                    i++;
                    while (i < text.Length && text[i] >= 0x30 && text[i] <= 0x3F) i++;
                    while (i < text.Length && text[i] >= 0x20 && text[i] <= 0x2F) i++;
                    if (i < text.Length && text[i] >= 0x40 && text[i] <= 0x7E) i++;
                }
                else if (i < text.Length && text[i] == ']')
                {
                    i++;
                    while (i < text.Length && text[i] != '\x07' && text[i] != '\x1b') i++;
                    if (i < text.Length && text[i] == '\x1b' && i + 1 < text.Length && text[i + 1] == '\\') i += 2;
                    else if (i < text.Length) i++;
                }
                else if (i < text.Length)
                {
                    i++;
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
    public static string TruncateAnsi(string text, int maxWidth)
    {
        var visible = StripAnsi(text);
        if (visible.Length <= maxWidth) return text;
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
    
    /// <summary>Wrap a line with ANSI codes into multiple rows, each ≤ maxCols visible width.</summary>
    public static List<string> WrapLine(string text, int maxCols)
    {
        var result = new List<string>();
        var visible = StripAnsi(text);
        if (visible.Length <= maxCols)
        {
            result.Add(text);
            return result;
        }
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
}