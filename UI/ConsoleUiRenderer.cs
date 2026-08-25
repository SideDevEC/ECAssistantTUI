using ECAssistant.Core;
using ECAssistant.Core.Session;
using ECAssistant.TUI.UI;

namespace ECAssistant.TUI.Session;

/// <summary>
/// Bridges Core's IOutputListener to a SessionLayer's buffer.
///
/// v11.0: Writes to the SessionLayer's buffer, NOT to EGuiConsole directly.
/// The layer owns the output buffer. When the layer is active, its buffer
/// is what gets rendered. When it's not active, the buffer keeps filling
/// silently — so when the user switches back, all output is there.
///
/// Stream polling (80ms) updates the layer's LiveStreamLine.
/// </summary>
public class ConsoleUiRenderer : IOutputListener, IDisposable
{
    private readonly SessionLayer _layer;
    private readonly EColor _color;
    private readonly Func<string>? _streamBufferGetter;
    private Timer? _streamPollTimer;
    private string _lastStreamSnapshot = "";
    private readonly Func<string, bool>? _approvalPrompt;

    public ConsoleUiRenderer(SessionLayer layer, EColor color, Func<string>? streamBufferGetter = null, Func<string, bool>? approvalPrompt = null)
    {
        _layer = layer;
        _color = color;
        _streamBufferGetter = streamBufferGetter;
        _approvalPrompt = approvalPrompt;
    }

    /// <summary>Map output states to ANSI color codes.</summary>
    private string StateToColor(OutputState state) => state switch
    {
        OutputState.Info    => _color.Cyan,
        OutputState.Success => _color.Green,
        OutputState.Warning => _color.Yellow,
        OutputState.Error   => _color.Red,
        OutputState.Dim     => _color.Dim,
        OutputState.Bold    => _color.Bold,
        OutputState.Raw     => _color.Reset,
        OutputState.System  => _color.Magenta,
        _ => _color.Reset
    };

    /// <summary>Map output states to a tag prefix for display.</summary>
    private string? StateToTag(OutputState state) => state switch
    {
        OutputState.Success => "OK",
        OutputState.Warning => "WARN",
        OutputState.Error   => "ERR",
        OutputState.System  => "SYS",
        _ => null
    };

    // ── IOutputListener implementation ──

    public void OnOutput(string text, OutputState state)
    {
        if (string.IsNullOrEmpty(text))
        {
            _layer.AddOutputLine("");
            return;
        }

        var color = StateToColor(state);
        var tag = StateToTag(state);
        if (tag != null)
            _layer.AddOutputLine($"{color}[{tag}] {text}{_color.Reset}");
        else
            _layer.AddOutputLine($"{color}{text}{_color.Reset}");
    }

    public void OnStreamStart()
    {
        _streamPollTimer?.Dispose();
        _lastStreamSnapshot = "";

        if (_streamBufferGetter == null) return;

        // Start polling the stream buffer every 80ms
        _streamPollTimer = new Timer(_ =>
        {
            try
            {
                var current = _streamBufferGetter();
                if (current == _lastStreamSnapshot) return;
                _lastStreamSnapshot = current;

                // Write the live stream content to the layer's buffer
                _layer.UpdateLiveStreamLine(current);
            }
            catch { }
        }, null, 80, 80);
    }

    public void OnStreamStop()
    {
        _streamPollTimer?.Dispose();
        _streamPollTimer = null;
        _layer.ClearLiveStreamLine();
    }

    public bool OnRequestApproval(string message)
    {
        if (_approvalPrompt != null)
            return _approvalPrompt(message);
        // No approval callback — auto-deny
        return false;
    }

    /// <summary>Render output history when switching to a session.</summary>
    public void RenderHistory(List<OutputEntry> entries)
    {
        foreach (var entry in entries)
        {
            switch (entry.Type)
            {
                case "stream":
                    if (!string.IsNullOrEmpty(entry.Text))
                    {
                        var c = StateToColor(entry.State);
                        _layer.AddOutputLine(c + entry.Text + _color.Reset);
                    }
                    break;

                case "line":
                    if (string.IsNullOrEmpty(entry.Text))
                        _layer.AddOutputLine("");
                    else
                    {
                        var c = StateToColor(entry.State);
                        var tag = StateToTag(entry.State);
                        if (tag != null)
                            _layer.AddOutputLine($"{c}[{tag}] {entry.Text}{_color.Reset}");
                        else
                            _layer.AddOutputLine($"{c}{entry.Text}{_color.Reset}");
                    }
                    break;
            }
        }
    }

    public void Dispose()
    {
        _streamPollTimer?.Dispose();
    }
}