using ECAssistant.Core;
using ECAssistant.TUI.UI;

namespace ECAssistant.TUI.UI;

/// <summary>
/// v14.10.1: animated processing-status indicator (spinner) shown while the
/// agent is working — structured inference is non-streamed (up to 240s), so
/// without this the TUI is silent between "enter" and the answer.
/// Braille frames + phase label + elapsed seconds, redrawn in place via the
/// GuiConsole loading-line path (same replace-last-line mechanics as
/// LoadingIndicator — layer-safe, never pollutes the buffer).
/// One instance per session, driven by IOutputListener.OnStatus.
/// </summary>
public class StatusIndicator : IDisposable
{
    private static readonly string[] Frames = { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };
    private const int TickMs = 90;

    private readonly IGuiConsole _gui;
    private readonly GuiConsole? _egui;
    private readonly AnsiColor _color;
    private readonly object _lock = new();
    private System.Threading.Timer? _timer;
    private string? _label;
    private string? _lastLine;
    private DateTime _started;
    private int _frame;
    private bool _disposed;

    public StatusIndicator(IGuiConsole gui, AnsiColor color)
    {
        _gui = gui;
        _egui = gui as GuiConsole;
        _color = color;
    }

    /// <summary>Show/replace the spinner with a new phase label.</summary>
    public void Start(string label) => Update(label);

    /// <summary>Show/replace the spinner with a new phase label.</summary>
    public void Update(string label)
    {
        lock (_lock)
        {
            if (_disposed) return;
            var restart = _timer == null;
            _label = label;
            if (restart)
            {
                _started = DateTime.UtcNow;
                _frame = 0;
                _timer = new System.Threading.Timer(_ => Tick(), null, TickMs, TickMs);
            }
            Render();
        }
    }

    /// <summary>Hide the spinner (output is arriving / turn ended).</summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _timer?.Dispose();
            _timer = null;
            _label = null;
            var line = _lastLine;
            _lastLine = null;
            if (line != null)
            {
                try
                {
                    if (_egui != null) _egui.ClearLoadingLine(line);
                    else _gui.WriteRaw("\r\x1b[2K");
                }
                catch { /* clear is best-effort */ }
            }
        }
    }

    private void Render()
    {
        // Caller holds _lock.
        if (_label == null) return;
        var elapsed = (int)(DateTime.UtcNow - _started).TotalSeconds;
        var line = $"{_color.Cyan}{Frames[_frame]} {_label} {_color.Dim}· {elapsed}s{_color.Reset}";
        _frame = (_frame + 1) % Frames.Length;
        var previous = _lastLine;
        _lastLine = line;
        try
        {
            if (_egui != null) _egui.WriteLoadingLine(previous, line);
            else
            {
                if (previous != null) _gui.WriteRaw("\r\x1b[2K");
                _gui.WriteLineColored(line);
            }
        }
        catch { /* render races with shutdown — ignore tick */ }
    }

    private void Tick()
    {
        lock (_lock)
        {
            if (_disposed || _timer == null || _label == null) return;
            Render();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}